using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_PlayerParticle;

public class VipPlayerParticle : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Player Particle";
    public override string ModuleVersion => "1.0.2";
    public override string ModuleDescription => "Attaches a selectable particle effect to VIP players";

    private PlayerParticle? _feature;
    private IVipCoreApi? _api;
    private bool _resourcesReady;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload)
    {
        // Particle files must be precached on map load, so this listener lives on the plugin itself.
        RegisterListener<Listeners.OnServerPrecacheResources>(PrecacheParticles);
        RegisterListener<Listeners.OnMapEnd>(() => _resourcesReady = false);
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
        _feature = new PlayerParticle(this, _api, () => _resourcesReady);
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
        _resourcesReady = false;
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
                    !values.TryGetProperty(PlayerParticle.FeatureName, out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("particle", out var file) && file.GetString() is { Length: > 0 } name)
                        files.Add(name);
            }

            foreach (var file in files)
                manifest.AddResource(file);
            _resourcesReady = true;
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
    private const int MaxPlayers = 64;

    private static readonly float[] HeightSteps = { 0f, 10f, 15f, 25f, 40f, 60f, 80f, 100f };

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Who may see the owner's particle.</summary>
    private enum MineMode { All, Team, Self }

    /// <summary>Whose particles a viewer receives.</summary>
    private enum OthersMode { All, Team, None }

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

    private readonly Dictionary<int, Prefs> _prefs = new();
    private readonly CParticleSystem?[] _active = new CParticleSystem?[MaxPlayers];
    private readonly int[] _team = new int[MaxPlayers];
    private bool _stopped;
    private readonly Func<bool> _resourcesReady;

    public PlayerParticle(BasePlugin plugin, IVipCoreApi api, Func<bool> resourcesReady) : base(api)
    {
        _resourcesReady = resourcesReady;
        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            var player = ev.Userid;
            if (player is not { IsValid: true }) return HookResult.Continue;
            Remove(player.Slot);
            Server.NextFrame(() => Apply(player));
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            Remove(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            Remove(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            RemoveAll();
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            RemoveAll();
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => Array.Clear(_active));
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            Remove(slot);
            _prefs.Remove(slot);
        });

        // Module loaded on a live server: pick up VIPs already in game.
        foreach (var player in Utilities.GetPlayers())
            if (!player.IsBot && IsClientVip(player))
                Apply(player);
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
        if (_stopped) return;
        _prefs.Remove(player.Slot);
        Apply(player);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        Remove(player.Slot);
        _prefs.Remove(player.Slot);
    }

    // ---- menu -------------------------------------------------------------------------------

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped) return;

        var entries = TryGetEntries(player);
        if (entries == null) return;

        var prefs = Load(player);
        var entry = Current(player, entries);
        var menu = CreateMenu(GetTranslatedText(Feature));

        menu.AddMenuOption(GetTranslatedText("playerparticle.Particle",
                entry?.Name ?? GetTranslatedText("playerparticle.Disable")),
            (controller, _) => OpenParticles(controller, entries, state));

        if (entry != null)
        {
            // Settings cycle on click, are applied at once and reopen the menu so the new value is shown.
            menu.AddMenuOption(GetTranslatedText("playerparticle.Height", Format(prefs.Height ?? entry.Offset)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Height = Next(HeightSteps, p.Height ?? entry.Offset);
                    Save(controller);
                    Apply(controller);
                    OnSelectItem(controller, state);
                });

            menu.AddMenuOption(GetTranslatedText("playerparticle.Self", YesNo(prefs.SeeOwn)), (controller, _) =>
            {
                var p = Load(controller);
                p.SeeOwn = !p.SeeOwn;
                Save(controller);
                OnSelectItem(controller, state);
            });

            menu.AddMenuOption(
                GetTranslatedText("playerparticle.Mine", GetTranslatedText($"playerparticle.mine.{prefs.Mine}")),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Mine = (MineMode)(((int)p.Mine + 1) % 3);
                    Save(controller);
                    OnSelectItem(controller, state);
                });
        }

        // Applies to particles of other players, so it stays available while the own effect is off.
        menu.AddMenuOption(
            GetTranslatedText("playerparticle.Others", GetTranslatedText($"playerparticle.others.{prefs.Others}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Others = (OthersMode)(((int)p.Others + 1) % 3);
                Save(controller);
                OnSelectItem(controller, state);
            });

        menu.AddMenuOption(GetTranslatedText("playerparticle.Reset"), (controller, _) =>
        {
            var p = Load(controller);
            p.Height = null;
            p.Mine = MineMode.All;
            p.Others = OthersMode.All;
            p.SeeOwn = true;
            Save(controller);
            Apply(controller);
            OnSelectItem(controller, state);
        });

        menu.Open(player);
    }

    private void OpenParticles(CCSPlayerController player, List<PlayerParticleEntry> entries, FeatureState state)
    {
        var current = Current(player, entries)?.Name ?? Off;
        var menu = CreateMenu(GetTranslatedText("playerparticle.Select"));

        menu.AddMenuOption(GetTranslatedText("playerparticle.Disable"), (controller, _) =>
        {
            Load(controller).Particle = Off;
            Save(controller);
            Remove(controller.Slot);
            PrintToChat(controller, GetTranslatedText("playerparticle.Off"));
        }, current == Off);

        foreach (var entry in entries)
        {
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                Load(controller).Particle = entry.Name;
                Save(controller);
                Apply(controller);
                PrintToChat(controller, GetTranslatedText("playerparticle.On", entry.Name));
            }, current == entry.Name);
        }

        menu.Open(player);
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
    /// The entry the player picked if the group still offers it, otherwise the first entry of the group
    /// after an explicit selection. No saved selection means disabled.
    /// </summary>
    private PlayerParticleEntry? Current(CCSPlayerController player, List<PlayerParticleEntry> entries)
    {
        var saved = Load(player).Particle;
        if (string.IsNullOrEmpty(saved) || saved == Off) return null;
        return entries.FirstOrDefault(e => e.Name == saved) ?? entries.FirstOrDefault();
    }

    private List<PlayerParticleEntry>? TryGetEntries(CCSPlayerController player)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is not FeatureState.Enabled) return null;

        try
        {
            var entries = GetFeatureValue<List<PlayerParticleEntry>>(player)
                .Where(e => e.Name.Length > 0 && e.Particle.Length > 0)
                .ToList();
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- particle ---------------------------------------------------------------------------

    private void Apply(CCSPlayerController? player)
    {
        if (_stopped || player is not { IsValid: true } || player.IsBot || player.Slot >= MaxPlayers) return;

        Remove(player.Slot);

        // A live load missed this map's precache callback. Wait for the next map
        // instead of creating entities with resources clients have not received.
        if (!_resourcesReady()) return;

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

        var entries = TryGetEntries(player);
        if (entries == null) return;

        var entry = Current(player, entries);
        var origin = pawn.AbsOrigin;
        if (entry == null || origin == null) return;

        var height = Load(player).Height ?? entry.Offset;

        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle == null || !particle.IsValid) return;

        particle.EffectName = entry.Particle;
        particle.StartActive = true;
        particle.Teleport(new Vector(origin.X, origin.Y, origin.Z + height), new QAngle(), new Vector());
        particle.DispatchSpawn();
        // Parenting keeps the world position and makes the particle follow the player.
        particle.AcceptInput("SetParent", pawn, pawn, "!activator");
        particle.AcceptInput("Start");

        _active[player.Slot] = particle;
    }

    private void RemoveAll()
    {
        for (var slot = 0; slot < MaxPlayers; slot++)
            Remove(slot);
    }

    private void Remove(int slot)
    {
        if (slot < 0 || slot >= MaxPlayers) return;

        var particle = _active[slot];
        _active[slot] = null;

        if (particle == null || !particle.IsValid || particle.DesignerName != "info_particle_system") return;

        particle.AcceptInput("DestroyImmediately");
        if (particle.IsValid) particle.Remove();
    }

    // ---- visibility -------------------------------------------------------------------------

    /// <summary>Strips particles from each viewer's snapshot according to both visibility settings.</summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_stopped) return;

        var any = false;
        foreach (var particle in _active)
            if (particle != null) { any = true; break; }
        if (!any) return;

        Array.Clear(_team);
        foreach (var player in Utilities.GetPlayers())
            if (player.Slot < MaxPlayers)
                _team[player.Slot] = player.TeamNum;

        foreach (var (info, viewer) in infoList)
        {
            if (viewer is not { IsValid: true } || viewer.Slot >= MaxPlayers) continue;

            var viewerPrefs = _prefs.GetValueOrDefault(viewer.Slot);
            for (var owner = 0; owner < MaxPlayers; owner++)
            {
                var particle = _active[owner];
                if (particle == null || !particle.IsValid) continue;

                var ownerPrefs = _prefs.GetValueOrDefault(owner);
                if (owner == viewer.Slot)
                {
                    if (ownerPrefs is { SeeOwn: false })
                        info.TransmitEntities.Remove((int)particle.Index);
                    continue;
                }

                var sameTeam = _team[owner] > 1 && _team[owner] == _team[viewer.Slot];

                var ownerAllows = ownerPrefs?.Mine switch
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
