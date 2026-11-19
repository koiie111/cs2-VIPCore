using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_PlayerParticle;

public class VipPlayerParticle : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Player Particle";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleDescription => "Attaches a selectable particle effect to VIP players";

    private PlayerParticle? _feature;
    private IVipCoreApi? _api;
    private HashSet<string>? _precached;
    private bool _unloaded;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload)
    {
        // Particle files must be precached on map load, so this listener lives on the plugin itself.
        RegisterListener<Listeners.OnServerPrecacheResources>(PrecacheParticles);
        RegisterListener<Listeners.OnMapEnd>(() => _precached = null);
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
            return;
        }
        if (_api == null) return;
        _api.OnCoreReady += RegisterFeature;
        // The core-ready event has already fired when loading this module on a live server.
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (_api == null || _feature != null) return;
        _feature = new PlayerParticle(this, _api, () => _precached);
        _api.RegisterFeature(_feature, FeatureType.Selectable);
        _feature.RestorePlayers();
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;
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
        _precached = null;
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
                    !group.Value.TryGetProperty("Values", out var values) ||
                    values.ValueKind != JsonValueKind.Object ||
                    !values.TryGetProperty(PlayerParticle.FeatureName, out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object &&
                        entry.TryGetProperty("particle", out var file) && file.ValueKind == JsonValueKind.String &&
                        file.GetString() is { Length: > 0 } name)
                        files.Add(name);
            }

            foreach (var file in files)
                manifest.AddResource(file);
            _precached = files;
        }
        catch (Exception e)
        {
            Logger.LogError("Failed to read particles from {Path}: {Error}", path, e.Message);
        }
    }
}

public class PlayerParticleEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Particle file (.vpcf).</summary>
    [JsonPropertyName("particle")] public string Particle { get; set; } = "";

    /// <summary>Height above the player's feet. Players can override it in the menu.</summary>
    [JsonPropertyName("offset")] public float Offset { get; set; } = 15f;
}

public class PlayerParticle : VipFeatureBase
{
    public const string FeatureName = "PlayerParticle";
    private const string CookieKey = "player_particle_prefs";
    private const string Off = "off";

    private static readonly float[] HeightSteps = { 0f, 10f, 15f, 25f, 40f, 60f, 80f, 100f };

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Per-player settings, stored as one JSON cookie. A null value means "use the config default".</summary>
    private class Prefs
    {
        /// <summary>null = never chosen (disabled), "off" = disabled, otherwise an explicitly selected entry name.</summary>
        [JsonPropertyName("p")] public string? Particle { get; set; }
        [JsonPropertyName("h")] public float? Height { get; set; }
        [JsonPropertyName("m")] public MineMode Mine { get; set; } = MineMode.All;
        [JsonPropertyName("o")] public OthersMode Others { get; set; } = OthersMode.All;
        [JsonPropertyName("s")] public bool SeeOwn { get; set; } = true;
    }

    public override string Feature => FeatureName;

    private sealed record ActiveParticle(uint Handle, uint Pawn, string Resource, float Height);

    private readonly Prefs?[] _prefs = new Prefs?[ParticleVisibility.Slots];
    private readonly ActiveParticle?[] _active = new ActiveParticle?[ParticleVisibility.Slots];
    private readonly SpawnRequests _spawns = new();
    private readonly Dictionary<uint, int> _particleOwners = new();
    private readonly ParticleVisibility _visibility = new();
    private readonly BasePlugin _plugin;
    private readonly Func<HashSet<string>?> _getPrecached;
    private readonly Listeners.CheckTransmit _checkTransmit;
    private readonly Listeners.OnEntityDeleted _entityDeleted;
    private bool _transmitRegistered;
    private bool _deletionRegistered;
    private bool _viewersInitialized;
    private bool _stopped;
    private uint _mapGeneration;
    private bool _reloadQueued;

    internal PlayerParticle(BasePlugin plugin, IVipCoreApi api, Func<HashSet<string>?> getPrecached) : base(api)
    {
        _plugin = plugin;
        _getPrecached = getPrecached;
        _checkTransmit = OnCheckTransmit;
        _entityDeleted = OnEntityDeleted;

        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } player && ValidSlot(player.Slot))
            {
                var slot = player.Slot;
                QueueSpawn(player, slot);
                UpdateViewer(slot, player.TeamNum);
            }
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            EndSpawn(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            var slot = ev.Userid?.Slot ?? -1;
            EndSpawn(slot);
            if (ValidSlot(slot) && !ev.Disconnect)
                UpdateViewer(slot, ev.Team);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            RemoveAll();
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            _spawns.Clear();
            RemoveAll();
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.OnMapEnd>(EndMap);
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            // At map start the previous map's entities have already been destroyed.
            ClearMap();
        });
        plugin.RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            if (_viewersInitialized && ValidSlot(slot) && Utilities.GetPlayerFromSlot(slot) is { IsValid: true } player)
                UpdateViewer(slot, player.TeamNum);
        });
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            if (!ValidSlot(slot)) return;
            EndSpawn(slot);
            _prefs[slot] = null;
            _visibility.SetViewer(slot, false, 0, MineMode.All, OthersMode.All, true);
            SyncTransmit();
        });
        plugin.AddCommandListener("css_vip_reload", (player, _) =>
        {
            if (!_reloadQueued && _getPrecached() != null &&
                (player == null || AdminManager.PlayerHasPermissions(player, "@css/root")))
            {
                _reloadQueued = true;
                var map = _mapGeneration;
                Server.NextFrame(() =>
                {
                    if (_stopped || map != _mapGeneration) return;
                    _reloadQueued = false;
                    RestorePlayers();
                });
            }
            return HookResult.Continue;
        }, HookMode.Post);
    }

    internal void RestorePlayers()
    {
        if (_getPrecached() == null) return;

        var players = Utilities.GetPlayers();
        InitializeViewers(players);
        foreach (var player in players)
            if (!player.IsBot && ValidSlot(player.Slot) && (_active[player.Slot] != null || IsClientVip(player)))
                Apply(player);
    }

    private void InitializeViewers(IEnumerable<CCSPlayerController> players)
    {
        if (_viewersInitialized) return;
        _viewersInitialized = true;
        foreach (var player in players)
        {
            var slot = player.Slot;
            if (!ValidSlot(slot)) continue;
            if (!player.IsBot && IsClientVip(player))
                Load(player, slot);
            UpdateViewer(slot, player.TeamNum);
        }
    }

    private void EndMap()
    {
        RemoveAll();
        ClearMap();
    }

    private void ClearMap()
    {
        _mapGeneration++;
        _reloadQueued = false;
        _spawns.Clear();
        Array.Clear(_active);
        _particleOwners.Clear();
        _viewersInitialized = false;
        _visibility.Clear();
        SyncTransmit();
        StopWatchingDeletes();
    }

    public void Stop()
    {
        _stopped = true;
        Api.OnPlayerSpawn -= OnPlayerSpawn;
        Api.PlayerLoaded -= OnPlayerLoaded;
        Api.PlayerRemoved -= OnPlayerRemoved;
        EndMap();
        Array.Clear(_prefs);
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (_stopped || !player.IsValid || player.IsBot || !ValidSlot(player.Slot)) return;
        var slot = player.Slot;
        _prefs[slot] = null;
        var prefs = Load(player, slot);
        UpdateViewer(slot, player.TeamNum);
        if (_getPrecached() != null)
            Apply(player, prefs);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        if (_stopped || !player.IsValid || !ValidSlot(player.Slot)) return;
        var slot = player.Slot;
        EndSpawn(slot);
        _prefs[slot] = null;
        UpdateViewer(slot, player.TeamNum);
    }

    // ---- menu -------------------------------------------------------------------------------

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (!CanEdit(player)) return;
        var entries = GetEntries(player);
        if (entries == null || FindEntry(entries, null) == null) return;
        ShowSettings(player, entries);
    }

    private void ShowSettings(CCSPlayerController player, List<PlayerParticleEntry> entries)
    {
        var prefs = Load(player, player.Slot);
        var entry = Current(prefs, entries);
        var menu = CreateMenu(GetTranslatedText(Feature));

        menu.AddMenuOption(GetTranslatedText("playerparticle.Particle",
                entry?.Name ?? GetTranslatedText("playerparticle.Disable")),
            (controller, _) => OpenParticles(controller));

        if (entry != null)
        {
            menu.AddMenuOption(GetTranslatedText("playerparticle.Height", Format(prefs.Height ?? entry.Offset)),
                (controller, _) => Edit(controller, (p, current) =>
                {
                    if (current != null) p.Height = Next(HeightSteps, p.Height ?? current.Offset);
                }, apply: true));

            menu.AddMenuOption(GetTranslatedText("playerparticle.Self", YesNo(prefs.SeeOwn)),
                (controller, _) => Edit(controller, (p, _) => p.SeeOwn = !p.SeeOwn));

            menu.AddMenuOption(
                GetTranslatedText("playerparticle.Mine", GetTranslatedText($"playerparticle.mine.{prefs.Mine}")),
                (controller, _) => Edit(controller, (p, _) => p.Mine = (MineMode)(((int)p.Mine + 1) % 3)));
        }

        menu.AddMenuOption(
            GetTranslatedText("playerparticle.Others", GetTranslatedText($"playerparticle.others.{prefs.Others}")),
            (controller, _) => Edit(controller, (p, _) => p.Others = (OthersMode)(((int)p.Others + 1) % 3)));

        menu.AddMenuOption(GetTranslatedText("playerparticle.Reset"), (controller, _) => Edit(controller, (p, _) =>
        {
            p.Height = null;
            p.Mine = MineMode.All;
            p.Others = OthersMode.All;
            p.SeeOwn = true;
        }, apply: true));

        menu.Open(player);
    }

    private void Edit(CCSPlayerController player, Action<Prefs, PlayerParticleEntry?> change, bool apply = false)
    {
        if (!CanEdit(player)) return;
        var entries = GetEntries(player);
        if (entries == null || FindEntry(entries, null) == null) return;
        var prefs = Load(player, player.Slot);
        change(prefs, Current(prefs, entries));
        Save(player, prefs);
        if (apply)
            ApplySelection(player, prefs, Current(prefs, entries));
        ShowSettings(player, entries);
    }

    private void OpenParticles(CCSPlayerController player)
    {
        if (!CanEdit(player)) return;
        var entries = GetEntries(player);
        if (entries == null || FindEntry(entries, null) == null) return;
        var current = Current(Load(player, player.Slot), entries)?.Name ?? Off;
        var menu = CreateMenu(GetTranslatedText("playerparticle.Select"));

        menu.AddMenuOption(GetTranslatedText("playerparticle.Disable"),
            (controller, _) => SelectParticle(controller, Off), current == Off);

        foreach (var entry in entries)
        {
            if (!ValidEntry(entry)) continue;
            menu.AddMenuOption(entry.Name, (controller, _) => SelectParticle(controller, entry.Name),
                current == entry.Name);
        }

        menu.Open(player);
    }

    private void SelectParticle(CCSPlayerController player, string name)
    {
        if (!CanEdit(player)) return;
        var entry = name == Off ? null : FindEntry(GetEntries(player), name);
        if (name != Off && entry == null) return;
        var prefs = Load(player, player.Slot);
        prefs.Particle = name;
        Save(player, prefs);
        if (entry == null) _spawns.Cancel(player.Slot);
        ApplySelection(player, prefs, entry);
        PrintToChat(player, entry == null
            ? GetTranslatedText("playerparticle.Off")
            : GetTranslatedText("playerparticle.On", entry.Name));
    }

    private string YesNo(bool value) => GetTranslatedText(value ? "playerparticle.Yes" : "playerparticle.No");

    private static string Format(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The first step above <paramref name="current"/>, wrapping to the first one.</summary>
    private static float Next(float[] steps, float current)
    {
        foreach (var step in steps)
            if (step > current + 0.001f)
                return step;
        return steps[0];
    }

    // ---- settings ---------------------------------------------------------------------------

    private Prefs Load(CCSPlayerController player, int slot)
    {
        if (_prefs[slot] is { } cached) return cached;

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

        _prefs[slot] = loaded;
        return loaded;
    }

    private void Save(CCSPlayerController player, Prefs prefs)
    {
        UpdateViewer(player.Slot, player.TeamNum);
        SetPlayerCookie(player.SteamID, CookieKey, JsonSerializer.Serialize(prefs, PrefsJson));
    }

    private static bool ValidEntry(PlayerParticleEntry? entry) =>
        entry is { Name.Length: > 0, Particle.Length: > 0 };

    private static PlayerParticleEntry? FindEntry(List<PlayerParticleEntry>? entries, string? name)
    {
        if (entries == null) return null;
        foreach (var entry in entries)
            if (ValidEntry(entry) && (name == null || entry.Name == name))
                return entry;
        return null;
    }

    private static bool HasSelection(Prefs prefs) =>
        !string.IsNullOrEmpty(prefs.Particle) && prefs.Particle != Off;

    private static PlayerParticleEntry? Current(Prefs prefs, List<PlayerParticleEntry>? entries) =>
        HasSelection(prefs) ? FindEntry(entries, prefs.Particle) ?? FindEntry(entries, null) : null;

    private bool CanEdit(CCSPlayerController player) =>
        !_stopped && player.IsValid && !player.IsBot && ValidSlot(player.Slot) && HasAccess(player);

    private bool HasAccess(CCSPlayerController player) =>
        IsClientVip(player) && PlayerHasFeature(player) && GetPlayerFeatureState(player) == FeatureState.Enabled;

    private List<PlayerParticleEntry>? GetEntries(CCSPlayerController player)
    {
        try
        {
            return GetFeatureValue<List<PlayerParticleEntry>>(player);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool ValidSlot(int slot) => (uint)slot < ParticleVisibility.Slots;

    private void QueueSpawn(CCSPlayerController player, int slot)
    {
        if (_getPrecached() == null || player.IsBot || !IsClientVip(player))
        {
            EndSpawn(slot);
            return;
        }
        var prefs = Load(player, slot);
        if (!HasSelection(prefs) || !PlayerHasFeature(player) || GetPlayerFeatureState(player) != FeatureState.Enabled)
        {
            EndSpawn(slot);
            return;
        }

        var pawn = player.PlayerPawn.Raw;
        if (_active[slot] is { } active && active.Pawn != pawn)
            Remove(slot);
        var controller = player.EntityHandle.Raw;
        if (!_spawns.TryQueue(slot, pawn, out var request)) return;
        Server.NextFrame(() =>
        {
            if (_stopped || !_spawns.Take(slot, request)) return;
            if (Utilities.GetPlayerFromSlot(slot) is { IsValid: true } current &&
                current.EntityHandle.Raw == controller && current.PlayerPawn.Raw == pawn)
                Apply(current);
        });
    }

    private void EndSpawn(int slot)
    {
        if (!ValidSlot(slot)) return;
        _spawns.Cancel(slot);
        Remove(slot);
    }

    private void Apply(CCSPlayerController player, Prefs? prefs = null)
    {
        var slot = player.Slot;
        if (!HasAccess(player))
        {
            Remove(slot);
            return;
        }

        prefs ??= Load(player, slot);
        ApplySelection(player, prefs, HasSelection(prefs) ? Current(prefs, GetEntries(player)) : null);
    }

    private void ApplySelection(CCSPlayerController player, Prefs prefs, PlayerParticleEntry? entry)
    {
        var slot = player.Slot;
        var precached = _getPrecached();
        if (entry == null || precached == null || !precached.Contains(entry.Particle))
        {
            Remove(slot);
            return;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
        {
            Remove(slot);
            return;
        }

        var height = prefs.Height ?? entry.Offset;
        var pawnHandle = pawn.EntityHandle.Raw;
        if (_active[slot] is { } active && active.Pawn == pawnHandle && active.Resource == entry.Particle &&
            active.Height == height)
            return;

        Remove(slot);
        var origin = pawn.AbsOrigin;
        if (origin == null) return;

        if (!_viewersInitialized)
            InitializeViewers(Utilities.GetPlayers());
        UpdateViewer(slot, player.TeamNum);

        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle == null || !particle.IsValid) return;

        particle.EffectName = entry.Particle;
        particle.StartActive = true;
        particle.Teleport(new Vector(origin.X, origin.Y, origin.Z + height), new QAngle(), new Vector());
        particle.DispatchSpawn();
        // Parenting keeps the world position and makes the particle follow the player.
        particle.AcceptInput("SetParent", pawn, pawn, "!activator");
        particle.AcceptInput("Start");
        if (!particle.IsValid) return;

        var handle = particle.EntityHandle;
        _active[slot] = new ActiveParticle(handle.Raw, pawnHandle, entry.Particle, height);
        _particleOwners.Add(handle.Raw, slot);
        if (!_deletionRegistered)
        {
            _plugin.RegisterListener(_entityDeleted);
            _deletionRegistered = true;
        }
        _visibility.SetParticle(slot, (int)handle.Index);
        SyncTransmit();
    }

    private void RemoveAll()
    {
        if (_particleOwners.Count == 0) return;
        for (var slot = 0; slot < _active.Length; slot++)
            Remove(slot);
    }

    private void Remove(int slot)
    {
        if (_active[slot] is not { } active) return;
        ForgetParticle(slot, active.Handle);
        if (_particleOwners.Count == 0)
            StopWatchingDeletes();

        var particle = new CHandle<CParticleSystem>(active.Handle).Value;
        if (particle is not { IsValid: true }) return;

        particle.AcceptInput("DestroyImmediately");
        if (particle.IsValid) particle.Remove();
    }

    private void ForgetParticle(int slot, uint handle)
    {
        _active[slot] = null;
        _particleOwners.Remove(handle);
        _visibility.SetParticle(slot, 0);
        SyncTransmit();
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (_particleOwners.Count == 0) return;
        var handle = entity.EntityHandle.Raw;
        if (!_particleOwners.TryGetValue(handle, out var slot)) return;
        ForgetParticle(slot, handle);
        if (_particleOwners.Count == 0)
        {
            // Remove the listener outside its own native dispatch.
            var map = _mapGeneration;
            Server.NextWorldUpdate(() =>
            {
                if (!_stopped && map == _mapGeneration && _particleOwners.Count == 0)
                    StopWatchingDeletes();
            });
        }
    }

    private void StopWatchingDeletes()
    {
        if (!_deletionRegistered) return;
        _plugin.RemoveListener(_entityDeleted);
        _deletionRegistered = false;
    }

    private void UpdateViewer(int slot, int team)
    {
        if (!_viewersInitialized) return;
        var prefs = _prefs[slot];
        _visibility.SetViewer(slot, true, team, prefs?.Mine ?? MineMode.All, prefs?.Others ?? OthersMode.All,
            prefs?.SeeOwn ?? true);
        SyncTransmit();
    }

    private void SyncTransmit()
    {
        var needed = !_stopped && _visibility.NeedsFilter;
        if (needed == _transmitRegistered) return;
        if (needed) _plugin.RegisterListener(_checkTransmit);
        else _plugin.RemoveListener(_checkTransmit);
        _transmitRegistered = needed;
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

            var hidden = _visibility.Hidden(slot);
            while (hidden != 0)
                info.TransmitEntities.Remove(_visibility.EntityIndex(ParticleVisibility.PopSlot(ref hidden)));
        }
    }
}
