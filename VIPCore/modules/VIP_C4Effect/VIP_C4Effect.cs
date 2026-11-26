using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_C4Effect;

public class VipC4Effect : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] C4 Effect";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleDescription => "Selectable particle effects for planting and defusing the bomb";

    private C4Effect? _feature;
    private IVipCoreApi? _api;
    private Timer? _retry;
    private bool _unloaded;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload)
    {
        // Particle files must be precached on map load, so this listener lives on the plugin itself.
        RegisterListener<Listeners.OnServerPrecacheResources>(PrecacheParticles);
        Server.NextWorldUpdate(Initialize);
    }

    public override void OnAllPluginsLoaded(bool hotReload) => Initialize();

    private void Initialize()
    {
        if (_unloaded || _api != null) return;
        try
        {
            _api = PluginCapability.Get();
        }
        catch (KeyNotFoundException)
        {
            // Older CSS builds throw while the capability has no provider.
        }

        if (_api == null)
        {
            _retry ??= AddTimer(5f, Initialize, TimerFlags.REPEAT);
            return;
        }

        StopRetry();
        _api.OnCoreReady += RegisterFeature;
        // The core-ready event has already fired when loading this module on a live server.
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (_api == null || _feature != null) return;
        _feature = new C4Effect(this, _api);
        _api.RegisterFeature(_feature, FeatureType.Selectable);
        _feature.SchedulePlayerRestore();
    }

    private void StopRetry()
    {
        if (_retry == null) return;
        _retry.Kill();
        Timers.Remove(_retry);
        _retry = null;
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;
        StopRetry();
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
        if (_feature == null) return;

        _feature.Stop();
        _api.UnRegisterFeature(_feature);
        _feature = null;
    }

    /// <summary>
    /// The core API only exposes group values for a given player, so particle files are collected
    /// straight from vip.json to have them precached before anyone joins.
    /// </summary>
    private void PrecacheParticles(ResourceManifest manifest)
    {
        var dir = _api?.CoreConfigDirectory ?? Path.Combine(Application.RootDirectory, "configs/plugins/VIPCore/");
        var path = Path.Combine(dir, "vip.json");
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Object)
                return;

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Object ||
                    !group.Value.TryGetProperty("Values", out var values) || values.ValueKind != JsonValueKind.Object ||
                    !values.TryGetProperty(C4Effect.FeatureName, out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("particle", out var file) &&
                        file.ValueKind == JsonValueKind.String && file.GetString() is { Length: > 0 } name)
                        files.Add(name);
            }

            foreach (var file in files)
                manifest.AddResource(file);
        }
        catch (Exception e)
        {
            Logger.LogError("Failed to read particles from {Path}: {Error}", path, e.Message);
        }
    }
}

public class C4EffectEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Particle file (.vpcf).</summary>
    [JsonPropertyName("particle")] public string Particle { get; set; } = "";

    /// <summary>
    /// Seconds the effect stays. 0 on a plant effect keeps it until the bomb explodes, is defused or the round
    /// restarts; on a defuse effect it falls back to <see cref="C4Effect.DefaultDefuseLifetime"/>.
    /// </summary>
    [JsonPropertyName("lifetime")] public float Lifetime { get; set; }

    /// <summary>false = effect of the planted bomb, true = effect of the defuse.</summary>
    [JsonPropertyName("defuse")] public bool Defuse { get; set; }
}

public class C4Effect : VipFeatureBase
{
    public const string FeatureName = "C4Effect";
    public const float DefaultDefuseLifetime = 3f;

    private const string CookieKey = "c4effect_prefs";
    private const string Off = "off";
    private const int MaxPlayers = 64;

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Who may see the owner's effect.</summary>
    private enum MineMode { All, Team, Self }

    /// <summary>Whose effects a viewer receives.</summary>
    private enum OthersMode { All, Team, None }

    /// <summary>Per-player settings, stored as one JSON cookie.</summary>
    private class Prefs
    {
        /// <summary>null = never chosen (first entry of the group), "off" = disabled, otherwise an entry name.</summary>
        [JsonPropertyName("p")] public string? Plant { get; set; }
        [JsonPropertyName("d")] public string? Defuse { get; set; }
        [JsonPropertyName("m")] public MineMode Mine { get; set; } = MineMode.All;
        [JsonPropertyName("o")] public OthersMode Others { get; set; } = OthersMode.All;
        [JsonPropertyName("s")] public bool SeeOwn { get; set; } = true;
    }

    private sealed class Spawned(CHandle<CParticleSystem> handle, int owner, MineMode mine, bool seeOwn)
    {
        public CHandle<CParticleSystem> Handle { get; } = handle;
        public int Index { get; } = (int)handle.Index;
        public int Owner = owner;
        public MineMode Mine { get; } = mine;
        public bool SeeOwn { get; } = seeOwn;
        public ulong Hidden;
        public Timer? Timer;
    }

    private struct Viewer
    {
        public uint Controller;
        public ulong Connection;
        public int Team;
        public Prefs? Prefs;
    }

    public override string Feature => FeatureName;

    private readonly BasePlugin _plugin;
    private readonly Viewer[] _viewers = new Viewer[MaxPlayers];
    private readonly List<Spawned> _active = new();
    private readonly Dictionary<string, List<C4EffectEntry>>? _groupEntries;
    private readonly System.Reflection.EventInfo? _reloadEvent;
    private readonly Action? _reloadHandler;
    private ulong _present;
    private ulong _hidden;
    private ulong _connection;
    private uint _plantGeneration;
    private uint _mapGeneration;
    private CHandle<CPlantedC4>? _bomb;
    private Spawned? _plant;
    private bool _transmitRegistered;
    private bool _deleteRegistered;
    private bool _deleteSyncPending;
    private bool _stopped;

    public C4Effect(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;

        // The bundled reference DLL predates IVipConfigReloadApi.
        _reloadEvent = api.GetType().GetEvent("ConfigReloaded");
        if (_reloadEvent?.EventHandlerType == typeof(Action))
        {
            _groupEntries = new(StringComparer.Ordinal);
            _reloadHandler = _groupEntries.Clear;
            _reloadEvent.AddEventHandler(api, _reloadHandler);
        }

        plugin.RegisterEventHandler<EventBombPlanted>(OnPlanted);
        plugin.RegisterEventHandler<EventBombDefused>(OnDefused);
        plugin.RegisterEventHandler<EventBombExploded>((_, _) =>
        {
            _plantGeneration++;
            _bomb = null;
            RemovePlant();
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _plantGeneration++;
            _bomb = null;
            RemoveAll();
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            if (!ev.Disconnect && ev.Userid is { IsValid: true } player)
                UpdateViewer(player, ev.Team);
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.OnMapEnd>(ResetMap);
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            ResetMap();
            SchedulePlayerRestore();
        });
        plugin.RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            if (ValidSlot(slot) && Utilities.GetPlayerFromSlot(slot) is { IsValid: true } player)
                UpdateViewer(player, player.TeamNum);
        });
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(ForgetViewer);
    }

    internal void SchedulePlayerRestore()
    {
        // OnAllPluginsLoaded can run before CSS's game globals are initialized.
        var generation = _mapGeneration;
        Server.NextWorldUpdate(() =>
        {
            if (!_stopped && generation == _mapGeneration) RestorePlayers();
        });
    }

    private void RestorePlayers()
    {
        foreach (var player in Utilities.GetPlayers())
            if (player.IsValid && ValidSlot(player.Slot))
            {
                if (!player.IsBot && !player.IsHLTV && IsClientVip(player))
                    OnPlayerLoaded(player, "");
                else
                    UpdateViewer(player, player.TeamNum);
            }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        Api.OnPlayerSpawn -= OnPlayerSpawn;
        Api.PlayerLoaded -= OnPlayerLoaded;
        Api.PlayerRemoved -= OnPlayerRemoved;
        if (_reloadHandler != null) _reloadEvent!.RemoveEventHandler(Api, _reloadHandler);
        _groupEntries?.Clear();
        ResetMap();
    }

    private void ResetMap()
    {
        _mapGeneration++;
        _plantGeneration++;
        _bomb = null;
        RemoveAll();
        _present = 0;
        Array.Clear(_viewers);
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (_stopped || !player.IsValid || !ValidSlot(player.Slot) || player.IsBot || player.IsHLTV) return;
        UpdateViewer(player, player.TeamNum, ReadPrefs(player), replacePrefs: true);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        if (_stopped || !player.IsValid) return;
        var slot = player.Slot;
        if (ValidSlot(slot) && _viewers[slot].Controller == player.EntityHandle.Raw)
            UpdateViewer(player, player.TeamNum, replacePrefs: true);
    }

    // ---- menu -------------------------------------------------------------------------------

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped || !player.IsValid || !ValidSlot(player.Slot)) return;

        var prefs = Load(player);
        var entries = HasAccess(player) ? GetEntries(player) : null;
        var plant = Current(entries, prefs.Plant, false, honorOff: false);
        var defuse = Current(entries, prefs.Defuse, true, honorOff: false);
        var menu = CreateMenu(GetTranslatedText(Feature));

        if (plant != null)
            menu.AddMenuOption(GetTranslatedText("c4effect.Plant", Name(prefs.Plant == Off ? null : plant)),
                (controller, _) => OpenEffects(controller, false));

        if (defuse != null)
            menu.AddMenuOption(GetTranslatedText("c4effect.Defuse", Name(prefs.Defuse == Off ? null : defuse)),
                (controller, _) => OpenEffects(controller, true));

        // Settings cycle on click and reopen the menu so the new value is shown.
        menu.AddMenuOption(GetTranslatedText("c4effect.Self", YesNo(prefs.SeeOwn)), (controller, _) =>
        {
            if (!CanEdit(controller)) return;
            var p = Load(controller);
            p.SeeOwn = !p.SeeOwn;
            Save(controller, p);
            OnSelectItem(controller, state);
        });

        menu.AddMenuOption(
            GetTranslatedText("c4effect.Mine", GetTranslatedText($"c4effect.mine.{prefs.Mine}")),
            (controller, _) =>
            {
                if (!CanEdit(controller)) return;
                var p = Load(controller);
                p.Mine = (MineMode)(((int)p.Mine + 1) % 3);
                Save(controller, p);
                OnSelectItem(controller, state);
            });

        // Applies to effects of other players, so it stays available while the own effects are off.
        menu.AddMenuOption(
            GetTranslatedText("c4effect.Others", GetTranslatedText($"c4effect.others.{prefs.Others}")),
            (controller, _) =>
            {
                if (!CanEdit(controller)) return;
                var p = Load(controller);
                p.Others = (OthersMode)(((int)p.Others + 1) % 3);
                Save(controller, p);
                OnSelectItem(controller, state);
            });

        menu.AddMenuOption(GetTranslatedText("c4effect.Reset"), (controller, _) =>
        {
            if (!CanEdit(controller)) return;
            var p = Load(controller);
            p.Plant = null;
            p.Defuse = null;
            p.Mine = MineMode.All;
            p.Others = OthersMode.All;
            p.SeeOwn = true;
            Save(controller, p);
            OnSelectItem(controller, state);
        });

        menu.Open(player);
    }

    private void OpenEffects(CCSPlayerController player, bool defuse)
    {
        if (!CanEdit(player)) return;
        var entries = GetEntries(player);
        var prefs = Load(player);
        var saved = defuse ? prefs.Defuse : prefs.Plant;
        if (Current(entries, saved, defuse, honorOff: false) == null) return;

        var current = Current(entries, saved, defuse)?.Name ?? Off;
        var menu = CreateMenu(GetTranslatedText(defuse ? "c4effect.SelectDefuse" : "c4effect.SelectPlant"));

        menu.AddMenuOption(GetTranslatedText("c4effect.Disable"), (controller, _) =>
        {
            if (!CanEdit(controller)) return;
            var prefs = Load(controller);
            Set(prefs, defuse, Off);
            Save(controller, prefs);
            PrintToChat(controller, GetTranslatedText(defuse ? "c4effect.DefuseOff" : "c4effect.PlantOff"));
        }, current == Off);

        foreach (var entry in entries)
        {
            if (entry.Defuse != defuse) continue;
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                if (!CanEdit(controller)) return;
                var prefs = Load(controller);
                Set(prefs, defuse, entry.Name);
                Save(controller, prefs);
                PrintToChat(controller,
                    GetTranslatedText(defuse ? "c4effect.DefuseOn" : "c4effect.PlantOn", entry.Name));
            }, current == entry.Name);
        }

        menu.Open(player);
    }

    private string Name(C4EffectEntry? entry) => entry?.Name ?? GetTranslatedText("c4effect.Disable");

    private string YesNo(bool value) => GetTranslatedText(value ? "c4effect.Yes" : "c4effect.No");

    private static void Set(Prefs prefs, bool defuse, string value)
    {
        if (defuse) prefs.Defuse = value;
        else prefs.Plant = value;
    }

    // ---- settings ---------------------------------------------------------------------------

    private static bool ValidSlot(int slot) => (uint)slot < MaxPlayers;

    private bool CanEdit(CCSPlayerController player) =>
        !_stopped && player.IsValid && ValidSlot(player.Slot) && HasAccess(player);

    private Prefs Load(CCSPlayerController player)
    {
        ref var viewer = ref _viewers[player.Slot];
        if (viewer.Controller == player.EntityHandle.Raw && viewer.Prefs is { } cached) return cached;

        var prefs = ReadPrefs(player);
        UpdateViewer(player, player.TeamNum, prefs, replacePrefs: true);
        return prefs;
    }

    private Prefs ReadPrefs(CCSPlayerController player)
    {
        var loaded = new Prefs();
        var raw = GetPlayerCookie<string>(player.SteamID, CookieKey);
        if (!string.IsNullOrEmpty(raw))
        {
            try
            {
                loaded = JsonSerializer.Deserialize<Prefs>(raw, PrefsJson) ?? loaded;
            }
            catch (JsonException)
            {
                // A damaged cookie falls back to the defaults.
            }
        }

        if (!Enum.IsDefined(loaded.Mine)) loaded.Mine = MineMode.All;
        if (!Enum.IsDefined(loaded.Others)) loaded.Others = OthersMode.All;

        return loaded;
    }

    private void Save(CCSPlayerController player, Prefs prefs)
    {
        SetPlayerCookie(player.SteamID, CookieKey, JsonSerializer.Serialize(prefs, PrefsJson));
        RefreshViewer(player.Slot);
    }

    private static C4EffectEntry? Current(List<C4EffectEntry>? entries, string? saved, bool defuse,
        bool honorOff = true)
    {
        if (entries == null || (honorOff && saved == Off)) return null;
        C4EffectEntry? first = null;
        foreach (var entry in entries)
        {
            if (entry.Defuse != defuse) continue;
            first ??= entry;
            if (entry.Name == saved) return entry;
        }
        return first;
    }

    private bool HasAccess(CCSPlayerController player) =>
        IsClientVip(player) && PlayerHasFeature(player) && GetPlayerFeatureState(player) == FeatureState.Enabled;

    private List<C4EffectEntry> GetEntries(CCSPlayerController player)
    {
        var group = _groupEntries != null ? GetClientVipGroup(player) : null;
        if (group != null && _groupEntries!.TryGetValue(group, out var cached)) return cached;

        try
        {
            var list = GetFeatureValue<JsonElement>(player);
            List<C4EffectEntry> entries = [];
            if (list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    if (ReadEntry(item) is { } entry)
                        entries.Add(entry);
            if (group != null) _groupEntries![group] = entries;
            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (KeyNotFoundException)
        {
            return [];
        }
    }

    private static C4EffectEntry? ReadEntry(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var entry = item.Deserialize<C4EffectEntry>();
            return entry != null && !string.IsNullOrEmpty(entry.Name) && !string.IsNullOrEmpty(entry.Particle)
                ? entry
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private C4EffectEntry? Pick(CCSPlayerController? player, bool defuse, out Prefs? prefs)
    {
        prefs = null;
        if (_stopped || player is not { IsValid: true, IsBot: false, IsHLTV: false } ||
            !ValidSlot(player.Slot) || !HasAccess(player)) return null;

        prefs = Load(player);
        var saved = defuse ? prefs.Defuse : prefs.Plant;
        return saved == Off ? null : Current(GetEntries(player), saved, defuse);
    }

    private HookResult OnPlanted(EventBombPlanted ev, GameEventInfo info)
    {
        var generation = ++_plantGeneration;
        _bomb = null;
        var planter = ev.Userid;
        var entry = Pick(planter, false, out var prefs);
        if (entry == null) return HookResult.Continue;

        var slot = planter!.Slot;
        var connection = _viewers[slot].Connection;
        var mine = prefs!.Mine;
        var seeOwn = prefs.SeeOwn;

        Server.NextFrame(() =>
        {
            if (_stopped || generation != _plantGeneration) return;
            var bomb = FindBomb();
            if (bomb?.AbsOrigin is not { } origin) return;

            _bomb = new CHandle<CPlantedC4>(bomb.EntityHandle.Raw);
            if (_plant is { } previous) Remove(previous, refresh: false);
            var particle = Spawn(entry.Particle, new Vector(origin.X, origin.Y, origin.Z), bomb);
            if (particle != null)
            {
                var owner = _viewers[slot].Connection == connection ? slot : -1;
                _plant = Track(particle, owner, mine, seeOwn, entry.Lifetime);
            }
            RefreshListeners();
        });
        return HookResult.Continue;
    }

    private HookResult OnDefused(EventBombDefused ev, GameEventInfo info)
    {
        _plantGeneration++;
        var bomb = _bomb;
        _bomb = null;
        var defuser = ev.Userid;
        var entry = Pick(defuser, true, out var prefs);
        if (entry == null)
        {
            RemovePlant();
            return HookResult.Continue;
        }

        var origin = (_plant is { } plant ? Resolve(plant)?.AbsOrigin : null)
            ?? (bomb is null ? FindBomb()?.AbsOrigin : bomb.Value?.AbsOrigin)
            ?? defuser!.PlayerPawn.Value?.AbsOrigin;
        var position = origin == null ? null : new Vector(origin.X, origin.Y, origin.Z + 10f);
        if (_plant is { } previous) Remove(previous, refresh: false);
        if (position != null && Spawn(entry.Particle, position, null) is { } particle)
            Track(particle, defuser!.Slot, prefs!.Mine, prefs.SeeOwn,
                entry.Lifetime > 0 ? entry.Lifetime : DefaultDefuseLifetime);
        RefreshListeners();
        return HookResult.Continue;
    }

    private static CPlantedC4? FindBomb()
    {
        foreach (var bomb in Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4"))
            if (bomb.IsValid && bomb.DesignerName == "planted_c4") return bomb;
        return null;
    }

    private static CParticleSystem? Spawn(string effect, Vector position, CBaseEntity? follow)
    {
        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle == null || !particle.IsValid) return null;

        particle.EffectName = effect;
        particle.StartActive = true;
        particle.Teleport(position, new QAngle(), new Vector());
        particle.DispatchSpawn();
        particle.AcceptInput("Start");

        if (follow is { IsValid: true })
            particle.AcceptInput("FollowEntity", follow, particle, "!activator");

        return particle.IsValid ? particle : null;
    }

    private Spawned Track(CParticleSystem particle, int owner, MineMode mine, bool seeOwn, float lifetime)
    {
        var handle = new CHandle<CParticleSystem>(particle.EntityHandle.Raw);
        var spawned = new Spawned(handle, owner, mine, seeOwn);
        spawned.Hidden = HiddenViewers(spawned);
        _active.Add(spawned);
        if (lifetime > 0)
            spawned.Timer = _plugin.AddTimer(lifetime, () => Expire(spawned), TimerFlags.STOP_ON_MAPCHANGE);
        return spawned;
    }

    private static CParticleSystem? Resolve(Spawned spawned)
    {
        var particle = spawned.Handle.Value;
        return particle is { IsValid: true } ? particle : null;
    }

    private void Expire(Spawned spawned)
    {
        if (spawned.Timer is not { } timer) return;
        spawned.Timer = null;
        _plugin.Timers.Remove(timer);
        Remove(spawned);
    }

    private void RemovePlant()
    {
        if (_plant is { } plant) Remove(plant);
    }

    private void RemoveAll()
    {
        while (_active.Count != 0) Remove(_active[^1], refresh: false);
        RefreshListeners();
    }

    private bool Detach(Spawned spawned)
    {
        if (!_active.Remove(spawned)) return false;
        if (ReferenceEquals(_plant, spawned)) _plant = null;
        if (spawned.Timer is { } timer)
        {
            spawned.Timer = null;
            timer.Kill();
            _plugin.Timers.Remove(timer);
        }
        return true;
    }

    private void Remove(Spawned spawned, bool refresh = true)
    {
        if (!Detach(spawned)) return;
        if (Resolve(spawned) is { } particle)
        {
            particle.AcceptInput("DestroyImmediately");
            Resolve(spawned)?.Remove();
        }
        if (refresh) RefreshListeners();
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (_active.Count == 0) return;
        var handle = entity.EntityHandle.Raw;
        for (var i = 0; i < _active.Count; i++)
        {
            if (_active[i].Handle.Raw != handle) continue;
            Detach(_active[i]);
            // Keep iteration of CSS's native deletion listeners intact.
            RefreshListeners(fromDeletion: true);
            return;
        }
    }

    private void UpdateViewer(CCSPlayerController player, int team, Prefs? prefs = null, bool replacePrefs = false)
    {
        var slot = player.Slot;
        if (_stopped || !ValidSlot(slot)) return;
        var bit = 1UL << slot;
        var handle = player.EntityHandle.Raw;
        ref var viewer = ref _viewers[slot];
        if ((_present & bit) == 0 || viewer.Controller != handle)
        {
            ClearViewer(slot);
            viewer.Controller = handle;
            viewer.Connection = ++_connection;
            _present |= bit;
        }
        viewer.Team = team;
        if (replacePrefs) viewer.Prefs = prefs;
        RefreshViewer(slot);
    }

    private void ClearViewer(int slot)
    {
        _viewers[slot] = default;
        _present &= ~(1UL << slot);
        foreach (var spawned in _active)
            if (spawned.Owner == slot)
            {
                spawned.Owner = -1;
                spawned.Hidden = HiddenViewers(spawned);
            }
    }

    private void ForgetViewer(int slot)
    {
        if (!ValidSlot(slot)) return;
        ClearViewer(slot);
        RefreshViewer(slot);
    }

    private void RefreshViewer(int slot)
    {
        var bit = 1UL << slot;
        foreach (var spawned in _active)
        {
            if (spawned.Owner == slot)
                spawned.Hidden = HiddenViewers(spawned);
            else if ((_present & bit) != 0 && !CanSee(slot, spawned))
                spawned.Hidden |= bit;
            else
                spawned.Hidden &= ~bit;
        }
        RefreshListeners();
    }

    private ulong HiddenViewers(Spawned spawned)
    {
        ulong hidden = 0;
        for (var present = _present; present != 0; present &= present - 1)
        {
            var slot = System.Numerics.BitOperations.TrailingZeroCount(present);
            if (!CanSee(slot, spawned)) hidden |= 1UL << slot;
        }
        return hidden;
    }

    private bool CanSee(int viewer, Spawned spawned)
    {
        if (viewer == spawned.Owner) return spawned.SeeOwn;
        var others = _viewers[viewer].Prefs?.Others ?? OthersMode.All;
        if (spawned.Mine == MineMode.Self || others == OthersMode.None) return false;
        if (spawned.Mine != MineMode.Team && others != OthersMode.Team) return true;
        var team = _viewers[viewer].Team;
        return spawned.Owner >= 0 && team > 1 && team == _viewers[spawned.Owner].Team;
    }

    private void RefreshListeners(bool fromDeletion = false)
    {
        _hidden = 0;
        foreach (var spawned in _active)
            _hidden |= spawned.Hidden;
        var transmit = _hidden != 0;
        if (_transmitRegistered != transmit)
        {
            if (transmit) _plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
            else _plugin.RemoveListener<Listeners.CheckTransmit>(OnCheckTransmit);
            _transmitRegistered = transmit;
        }

        var watchDeletion = _active.Count != 0;
        if (_deleteRegistered == watchDeletion) return;
        if (fromDeletion)
        {
            if (_deleteSyncPending) return;
            _deleteSyncPending = true;
            Server.NextWorldUpdate(() =>
            {
                _deleteSyncPending = false;
                if (!_stopped) RefreshListeners();
            });
            return;
        }
        if (watchDeletion) _plugin.RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        else _plugin.RemoveListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        _deleteRegistered = watchDeletion;
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        var count = infoList.Count;
        for (var i = 0; i < count; i++)
        {
            var (info, viewer) = infoList[i];
            if (viewer is not { IsValid: true }) continue;
            var slot = viewer.Slot;
            if (!ValidSlot(slot)) continue;
            var bit = 1UL << slot;
            if ((_hidden & bit) == 0) continue;
            foreach (var spawned in _active)
                if ((spawned.Hidden & bit) != 0)
                    info.TransmitEntities.Remove(spawned.Index);
        }
    }
}
