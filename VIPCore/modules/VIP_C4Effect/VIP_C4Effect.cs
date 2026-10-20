using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_C4Effect;

public class VipC4Effect : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] C4 Effect";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Selectable particle effects for planting and defusing the bomb";

    private C4Effect? _feature;
    private IVipCoreApi? _api;
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
        if (_api != null) return;
        _api = PluginCapability.Get();
        if (_api == null) return;
        _api.OnCoreReady += RegisterFeature;
        // The core-ready event has already fired when loading this module on a live server.
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (_api == null || _feature != null) return;
        _feature = new C4Effect(this, _api);
        _api.RegisterFeature(_feature, FeatureType.Selectable);
    }

    public override void Unload(bool hotReload)
    {
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

            if (!doc.RootElement.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Object)
                return;

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups.EnumerateObject())
            {
                if (!group.Value.TryGetProperty("Values", out var values) ||
                    !values.TryGetProperty(C4Effect.FeatureName, out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("particle", out var file) && file.GetString() is { Length: > 0 } name)
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

    /// <summary>A spawned particle with the visibility rules of its owner, fixed at spawn time.</summary>
    private class Spawned
    {
        public CParticleSystem Particle = null!;
        public int Owner;
        public MineMode Mine;
        public bool SeeOwn;
    }

    public override string Feature => FeatureName;

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Prefs> _prefs = new();
    private readonly List<Spawned> _active = new();
    private readonly int[] _team = new int[MaxPlayers];
    private Spawned? _plant;
    private bool _stopped;

    public C4Effect(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;

        plugin.RegisterEventHandler<EventBombPlanted>(OnPlanted);
        plugin.RegisterEventHandler<EventBombDefused>(OnDefused);
        plugin.RegisterEventHandler<EventBombExploded>((_, _) =>
        {
            RemovePlant();
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            RemoveAll();
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _active.Clear();
            _plant = null;
        });
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            _prefs.Remove(slot);
            // The slot can be taken over by another player while the effect is still running.
            foreach (var spawned in _active)
                if (spawned.Owner == slot)
                    spawned.Owner = -1;
        });
    }

    /// <summary>Called on plugin unload; CounterStrikeSharp drops handlers and listeners itself.</summary>
    public void Stop()
    {
        _stopped = true;
        RemoveAll();
        _prefs.Clear();
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (!_stopped) _prefs.Remove(player.Slot);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group) => _prefs.Remove(player.Slot);

    // ---- menu -------------------------------------------------------------------------------

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped) return;

        var prefs = Load(player);
        var plant = TryGetEntries(player, false);
        var defuse = TryGetEntries(player, true);
        var menu = CreateMenu(GetTranslatedText(Feature));

        if (plant != null)
            menu.AddMenuOption(GetTranslatedText("c4effect.Plant", Name(Current(player, plant, false))),
                (controller, _) => OpenEffects(controller, false, state));

        if (defuse != null)
            menu.AddMenuOption(GetTranslatedText("c4effect.Defuse", Name(Current(player, defuse, true))),
                (controller, _) => OpenEffects(controller, true, state));

        // Settings cycle on click and reopen the menu so the new value is shown.
        menu.AddMenuOption(GetTranslatedText("c4effect.Self", YesNo(prefs.SeeOwn)), (controller, _) =>
        {
            var p = Load(controller);
            p.SeeOwn = !p.SeeOwn;
            Save(controller);
            OnSelectItem(controller, state);
        });

        menu.AddMenuOption(
            GetTranslatedText("c4effect.Mine", GetTranslatedText($"c4effect.mine.{prefs.Mine}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Mine = (MineMode)(((int)p.Mine + 1) % 3);
                Save(controller);
                OnSelectItem(controller, state);
            });

        // Applies to effects of other players, so it stays available while the own effects are off.
        menu.AddMenuOption(
            GetTranslatedText("c4effect.Others", GetTranslatedText($"c4effect.others.{prefs.Others}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Others = (OthersMode)(((int)p.Others + 1) % 3);
                Save(controller);
                OnSelectItem(controller, state);
            });

        menu.AddMenuOption(GetTranslatedText("c4effect.Reset"), (controller, _) =>
        {
            var p = Load(controller);
            p.Plant = null;
            p.Defuse = null;
            p.Mine = MineMode.All;
            p.Others = OthersMode.All;
            p.SeeOwn = true;
            Save(controller);
            OnSelectItem(controller, state);
        });

        menu.Open(player);
    }

    private void OpenEffects(CCSPlayerController player, bool defuse, FeatureState state)
    {
        var entries = TryGetEntries(player, defuse);
        if (entries == null) return;

        var current = Current(player, entries, defuse)?.Name ?? Off;
        var menu = CreateMenu(GetTranslatedText(defuse ? "c4effect.SelectDefuse" : "c4effect.SelectPlant"));

        menu.AddMenuOption(GetTranslatedText("c4effect.Disable"), (controller, _) =>
        {
            Set(Load(controller), defuse, Off);
            Save(controller);
            PrintToChat(controller, GetTranslatedText(defuse ? "c4effect.DefuseOff" : "c4effect.PlantOff"));
        }, current == Off);

        foreach (var entry in entries)
        {
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                Set(Load(controller), defuse, entry.Name);
                Save(controller);
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

    private Prefs Load(CCSPlayerController player)
    {
        if (_prefs.TryGetValue(player.Slot, out var cached)) return cached;

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

        _prefs[player.Slot] = loaded;
        return loaded;
    }

    private void Save(CCSPlayerController player) =>
        SetPlayerCookie(player.SteamID, CookieKey, JsonSerializer.Serialize(Load(player), PrefsJson));

    /// <summary>
    /// The entry the player picked if the group still offers it, otherwise the first entry of the category
    /// (so a fresh VIP gets an effect without opening the menu). Null when disabled.
    /// </summary>
    private C4EffectEntry? Current(CCSPlayerController player, List<C4EffectEntry> entries, bool defuse)
    {
        var prefs = Load(player);
        var saved = defuse ? prefs.Defuse : prefs.Plant;
        if (saved == Off) return null;
        return entries.FirstOrDefault(e => e.Name == saved) ?? entries.FirstOrDefault();
    }

    /// <summary>Effects of the player's group for one category; null when the player has none.</summary>
    private List<C4EffectEntry>? TryGetEntries(CCSPlayerController player, bool defuse)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is not FeatureState.Enabled) return null;

        try
        {
            var entries = GetFeatureValue<List<C4EffectEntry>>(player)
                .Where(e => e.Name.Length > 0 && e.Particle.Length > 0 && e.Defuse == defuse)
                .ToList();
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The effect the player will see for this event, or null if they have none or switched it off.</summary>
    private C4EffectEntry? Pick(CCSPlayerController? player, bool defuse)
    {
        if (_stopped || player is not { IsValid: true } || player.IsBot) return null;

        var entries = TryGetEntries(player, defuse);
        return entries == null ? null : Current(player, entries, defuse);
    }

    // ---- bomb events ------------------------------------------------------------------------

    private HookResult OnPlanted(EventBombPlanted ev, GameEventInfo info)
    {
        var planter = ev.Userid;
        var entry = Pick(planter, false);
        if (entry == null) return HookResult.Continue;

        var slot = planter!.Slot;
        var prefs = Load(planter);
        var mine = prefs.Mine;
        var seeOwn = prefs.SeeOwn;

        // The planted bomb entity exists only after the event has been processed.
        Server.NextFrame(() =>
        {
            if (_stopped) return;

            var c4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault(c => c is { IsValid: true });
            if (c4?.AbsOrigin == null) return;

            RemovePlant();

            var origin = c4.AbsOrigin;
            var particle = Spawn(entry.Particle, new Vector(origin.X, origin.Y, origin.Z), c4);
            if (particle == null) return;

            var spawned = Track(particle, slot, mine, seeOwn);
            _plant = spawned;

            if (entry.Lifetime > 0)
                _plugin.AddTimer(entry.Lifetime, () =>
                {
                    if (ReferenceEquals(_plant, spawned)) RemovePlant();
                }, TimerFlags.STOP_ON_MAPCHANGE);
        });

        return HookResult.Continue;
    }

    private HookResult OnDefused(EventBombDefused ev, GameEventInfo info)
    {
        // The plant effect marks where the bomb was; take its position before it is removed.
        Vector? bombPos = null;
        if (_plant?.Particle is { IsValid: true, AbsOrigin: { } at })
            bombPos = new Vector(at.X, at.Y, at.Z);

        if (bombPos == null)
        {
            var c4 = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
                .FirstOrDefault(c => c is { IsValid: true });
            if (c4?.AbsOrigin is { } at2)
                bombPos = new Vector(at2.X, at2.Y, at2.Z);
        }

        RemovePlant();

        var defuser = ev.Userid;
        var entry = Pick(defuser, true);
        if (entry == null) return HookResult.Continue;

        if (bombPos == null)
        {
            var origin = defuser!.PlayerPawn.Value?.AbsOrigin;
            if (origin == null) return HookResult.Continue;
            bombPos = new Vector(origin.X, origin.Y, origin.Z);
        }

        var particle = Spawn(entry.Particle, new Vector(bombPos.X, bombPos.Y, bombPos.Z + 10f), null);
        if (particle == null) return HookResult.Continue;

        var prefs = Load(defuser!);
        var spawned = Track(particle, defuser!.Slot, prefs.Mine, prefs.SeeOwn);
        _plugin.AddTimer(entry.Lifetime > 0 ? entry.Lifetime : DefaultDefuseLifetime,
            () => Remove(spawned), TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    // ---- particle ---------------------------------------------------------------------------

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

        return particle;
    }

    private Spawned Track(CParticleSystem particle, int owner, MineMode mine, bool seeOwn)
    {
        var spawned = new Spawned { Particle = particle, Owner = owner, Mine = mine, SeeOwn = seeOwn };
        _active.Add(spawned);
        return spawned;
    }

    private void RemovePlant()
    {
        var plant = _plant;
        _plant = null;
        if (plant != null) Remove(plant);
    }

    private void RemoveAll()
    {
        foreach (var spawned in _active.ToArray())
            Remove(spawned);
        _plant = null;
    }

    private void Remove(Spawned spawned)
    {
        _active.Remove(spawned);

        var particle = spawned.Particle;
        if (!particle.IsValid || particle.DesignerName != "info_particle_system") return;

        particle.AcceptInput("DestroyImmediately");
        if (particle.IsValid) particle.Remove();
    }

    // ---- visibility -------------------------------------------------------------------------

    /// <summary>Strips effects from each viewer's snapshot according to both visibility settings.</summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_stopped || _active.Count == 0) return;

        Array.Clear(_team);
        foreach (var player in Utilities.GetPlayers())
            if (player.Slot < MaxPlayers)
                _team[player.Slot] = player.TeamNum;

        foreach (var (info, viewer) in infoList)
        {
            if (viewer is not { IsValid: true } || viewer.Slot >= MaxPlayers) continue;

            var viewerPrefs = _prefs.GetValueOrDefault(viewer.Slot);
            foreach (var spawned in _active)
            {
                var particle = spawned.Particle;
                if (!particle.IsValid) continue;

                var owner = spawned.Owner;
                if (owner == viewer.Slot)
                {
                    if (!spawned.SeeOwn)
                        info.TransmitEntities.Remove((int)particle.Index);
                    continue;
                }

                var sameTeam = owner >= 0 && _team[owner] > 1 && _team[owner] == _team[viewer.Slot];

                var ownerAllows = spawned.Mine switch
                {
                    MineMode.Team => sameTeam,
                    MineMode.Self => false,
                    _ => true
                };
                var viewerAllows = viewerPrefs?.Others switch
                {
                    OthersMode.Team => sameTeam,
                    OthersMode.None => false,
                    _ => true
                };

                if (!ownerAllows || !viewerAllows)
                    info.TransmitEntities.Remove((int)particle.Index);
            }
        }
    }
}
