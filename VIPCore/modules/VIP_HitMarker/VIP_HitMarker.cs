using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_HitMarker;

public class VipHitMarker : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Hit Marker";
    public override string ModuleVersion => "1.0.1";
    public override string ModuleDescription => "Shows a selectable particle hit marker in front of a VIP player's crosshair";

    private HitMarker? _hitMarker;
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
        if (_api == null || _hitMarker != null) return;
        _hitMarker = new HitMarker(this, _api);
        _api.RegisterFeature(_hitMarker, FeatureType.Selectable);
    }

    public override void Unload(bool hotReload)
    {
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
        if (_hitMarker == null) return;

        _hitMarker.Stop();
        _api.UnRegisterFeature(_hitMarker);
        _hitMarker = null;
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
                    !values.TryGetProperty(HitMarker.FeatureName, out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    foreach (var key in new[] { "particle", "headshot" })
                        if (entry.TryGetProperty(key, out var file) && file.GetString() is { Length: > 0 } name)
                            files.Add(name);
                }
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

public class HitMarkerEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Particle file (.vpcf) shown on a normal hit.</summary>
    [JsonPropertyName("particle")] public string Particle { get; set; } = "";

    /// <summary>Optional particle file shown instead on a headshot.</summary>
    [JsonPropertyName("headshot")] public string Headshot { get; set; } = "";

    /// <summary>How long the marker stays, in seconds. Players can override it in the menu.</summary>
    [JsonPropertyName("duration")] public float Duration { get; set; } = 0.25f;

    /// <summary>Distance from the eyes along the view direction. Players can override it in the menu.</summary>
    [JsonPropertyName("distance")] public float Distance { get; set; } = 60f;

    /// <summary>Sideways shift relative to the view direction (positive = right).</summary>
    [JsonPropertyName("offset_x")] public float OffsetX { get; set; }

    /// <summary>Vertical shift (positive = up).</summary>
    [JsonPropertyName("offset_y")] public float OffsetY { get; set; }
}

public class HitMarker : VipFeatureBase
{
    public const string FeatureName = "HitMarker";
    private const string CookieKey = "hitmarker_prefs";
    private const string Off = "off";
    private const int MaxPlayers = 64;

    private static readonly float[] DurationSteps = { 0.1f, 0.15f, 0.25f, 0.4f, 0.6f, 1f };
    private static readonly float[] DistanceSteps = { 0f, 30f, 45f, 60f, 80f, 100f, 130f, 160f };
    private static readonly float[] OffsetSteps = { -40f, -30f, -20f, -10f, 0f, 10f, 20f, 30f, 40f };

    private static ConVar? _cvFfa;

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Per-player settings, stored as one JSON cookie. A null value means "use the config default".</summary>
    private class Prefs
    {
        /// <summary>null = never chosen (disabled by default), "off" = disabled, otherwise an entry name.</summary>
        [JsonPropertyName("m")] public string? Marker { get; set; }
        [JsonPropertyName("hs")] public bool Headshot { get; set; } = true;
        [JsonPropertyName("sp")] public bool Spectators { get; set; } = true;
        [JsonPropertyName("t")] public float? Duration { get; set; }
        [JsonPropertyName("d")] public float? Distance { get; set; }
        [JsonPropertyName("x")] public float? OffsetX { get; set; }
        [JsonPropertyName("y")] public float? OffsetY { get; set; }
    }

    private class Live
    {
        public required CParticleSystem Particle;
        public required CCSPlayerController Owner;
        public required float Distance;
        public required float OffsetX;
        public required float OffsetY;
        public ulong Viewers;
    }

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Prefs> _prefs = new();
    private readonly Live?[] _active = new Live?[MaxPlayers];
    private int _count;
    private bool _stopped;

    public override string Feature => FeatureName;

    public HitMarker(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;

        plugin.RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            Remove(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventRoundEnd>((_, _) =>
        {
            RemoveAll();
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.OnTick>(OnTick);
        plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            Array.Clear(_active);
            _count = 0;
        });
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            Remove(slot);
            _prefs.Remove(slot);
        });
    }

    /// <summary>Called on plugin unload; CounterStrikeSharp drops handlers and listeners itself.</summary>
    public void Stop()
    {
        _stopped = true;
        RemoveAll();
        _prefs.Clear();
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group) => _prefs.Remove(player.Slot);

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        Remove(player.Slot);
        _prefs.Remove(player.Slot);
    }

    // ---- hit --------------------------------------------------------------------------------

    private HookResult OnPlayerHurt(EventPlayerHurt ev, GameEventInfo info)
    {
        if (_stopped) return HookResult.Continue;

        var attacker = ev.Attacker;
        var victim = ev.Userid;
        if (attacker is not { IsValid: true, IsBot: false } || victim is not { IsValid: true })
            return HookResult.Continue;
        if (attacker.Slot == victim.Slot) return HookResult.Continue;

        // Burning ticks would respawn the marker every few frames.
        if (ev.Weapon == "inferno") return HookResult.Continue;

        var entries = TryGetEntries(attacker);
        if (entries == null) return HookResult.Continue;

        _cvFfa ??= ConVar.Find("mp_teammates_are_enemies");
        var ffa = _cvFfa?.GetPrimitiveValue<bool>() ?? false;
        if (!ffa && victim.Team == attacker.Team) return HookResult.Continue;

        var entry = Current(attacker, entries);
        if (entry == null) return HookResult.Continue;

        Show(attacker, entry, ev.Hitgroup == (int)HitGroup_t.HITGROUP_HEAD);
        return HookResult.Continue;
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

        menu.AddMenuOption(GetTranslatedText("hitmarker.Marker", entry?.Name ?? GetTranslatedText("hitmarker.Disable")),
            (controller, _) => OpenMarkers(controller, entries, state));

        if (entry != null)
        {
            // Settings cycle on click and reopen the menu so the new value is shown.
            if (entries.Any(e => e.Headshot.Length > 0))
                menu.AddMenuOption(GetTranslatedText("hitmarker.Headshot", YesNo(prefs.Headshot)), (controller, _) =>
                {
                    var p = Load(controller);
                    p.Headshot = !p.Headshot;
                    Save(controller);
                    OnSelectItem(controller, state);
                });

            menu.AddMenuOption(GetTranslatedText("hitmarker.Duration", Format(prefs.Duration ?? entry.Duration)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Duration = Next(DurationSteps, p.Duration ?? entry.Duration);
                    Save(controller);
                    Preview(controller, entries);
                    OnSelectItem(controller, state);
                });

            menu.AddMenuOption(GetTranslatedText("hitmarker.Position"),
                (controller, _) => OpenPosition(controller, entries, state));

            menu.AddMenuOption(GetTranslatedText("hitmarker.Spectators", YesNo(prefs.Spectators)), (controller, _) =>
            {
                var p = Load(controller);
                p.Spectators = !p.Spectators;
                Save(controller);
                OnSelectItem(controller, state);
            });

            menu.AddMenuOption(GetTranslatedText("hitmarker.Preview"), (controller, _) =>
            {
                Preview(controller, entries);
                OnSelectItem(controller, state);
            });
        }

        menu.Open(player);
    }

    private void OpenMarkers(CCSPlayerController player, List<HitMarkerEntry> entries, FeatureState state)
    {
        var current = Current(player, entries)?.Name ?? Off;
        var menu = CreateMenu(GetTranslatedText("hitmarker.Select"));

        menu.AddMenuOption(GetTranslatedText("hitmarker.Disable"), (controller, _) =>
        {
            Remove(controller.Slot);
            Load(controller).Marker = Off;
            Save(controller);
            PrintToChat(controller, GetTranslatedText("hitmarker.Off"));
        }, current == Off);

        foreach (var entry in entries)
        {
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                Load(controller).Marker = entry.Name;
                Save(controller);
                PrintToChat(controller, GetTranslatedText("hitmarker.On", entry.Name));
                Preview(controller, entries);
            }, current == entry.Name);
        }

        menu.Open(player);
    }

    private void OpenPosition(CCSPlayerController player, List<HitMarkerEntry> entries, FeatureState state)
    {
        var prefs = Load(player);
        var entry = Current(player, entries);
        if (entry == null)
        {
            OnSelectItem(player, state);
            return;
        }

        var menu = CreateMenu(GetTranslatedText("hitmarker.Position"));

        menu.AddMenuOption(GetTranslatedText("hitmarker.Distance", Format(prefs.Distance ?? entry.Distance)),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Distance = Next(DistanceSteps, p.Distance ?? entry.Distance);
                Save(controller);
                Preview(controller, entries);
                OpenPosition(controller, entries, state);
            });

        menu.AddMenuOption(GetTranslatedText("hitmarker.OffsetX", Format(prefs.OffsetX ?? entry.OffsetX)),
            (controller, _) =>
            {
                var p = Load(controller);
                p.OffsetX = Next(OffsetSteps, p.OffsetX ?? entry.OffsetX);
                Save(controller);
                Preview(controller, entries);
                OpenPosition(controller, entries, state);
            });

        menu.AddMenuOption(GetTranslatedText("hitmarker.OffsetY", Format(prefs.OffsetY ?? entry.OffsetY)),
            (controller, _) =>
            {
                var p = Load(controller);
                p.OffsetY = Next(OffsetSteps, p.OffsetY ?? entry.OffsetY);
                Save(controller);
                Preview(controller, entries);
                OpenPosition(controller, entries, state);
            });

        menu.AddMenuOption(GetTranslatedText("hitmarker.Reset"), (controller, _) =>
        {
            var p = Load(controller);
            p.Distance = p.OffsetX = p.OffsetY = null;
            Save(controller);
            Preview(controller, entries);
            OpenPosition(controller, entries, state);
        });

        menu.Open(player);
    }

    private string YesNo(bool value) => GetTranslatedText(value ? "hitmarker.Yes" : "hitmarker.No");

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

        _prefs[player.Slot] = loaded;
        return loaded;
    }

    private void Save(CCSPlayerController player) =>
        SetPlayerCookie(player.SteamID, CookieKey, JsonSerializer.Serialize(Load(player), PrefsJson));

    /// <summary>
    /// The entry explicitly picked by the player, falling back to the first group entry if it was removed.
    /// Null when disabled or when the player has never selected a marker.
    /// </summary>
    private HitMarkerEntry? Current(CCSPlayerController player, List<HitMarkerEntry> entries)
    {
        var saved = Load(player).Marker;
        if (string.IsNullOrEmpty(saved) || saved == Off) return null;
        return entries.FirstOrDefault(e => e.Name == saved) ?? entries.FirstOrDefault();
    }

    private List<HitMarkerEntry>? TryGetEntries(CCSPlayerController player)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is not FeatureState.Enabled) return null;

        try
        {
            var entries = GetFeatureValue<List<HitMarkerEntry>>(player)
                .Where(e => e.Name.Length > 0 && e.Particle.Length > 0)
                .ToList();
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- marker -----------------------------------------------------------------------------

    /// <summary>Shows the marker the player would see on a normal hit, so settings can be judged by eye.</summary>
    private void Preview(CCSPlayerController player, List<HitMarkerEntry> entries)
    {
        var entry = Current(player, entries);
        if (entry != null) Show(player, entry, false);
    }

    private void Show(CCSPlayerController player, HitMarkerEntry entry, bool headshot)
    {
        var slot = player.Slot;
        if (slot < 0 || slot >= MaxPlayers) return;

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true, AbsOrigin: not null }) return;

        var prefs = Load(player);
        var file = headshot && prefs.Headshot && entry.Headshot.Length > 0 ? entry.Headshot : entry.Particle;

        Remove(slot);

        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle == null || !particle.IsValid) return;

        var live = new Live
        {
            Particle = particle,
            Owner = player,
            Distance = MathF.Max(prefs.Distance ?? entry.Distance, 0f),
            OffsetX = prefs.OffsetX ?? entry.OffsetX,
            OffsetY = prefs.OffsetY ?? entry.OffsetY,
            Viewers = 1UL << slot
        };

        if (prefs.Spectators)
        {
            var watchers = new List<CCSPlayerController>();
            AddSpectators(player, watchers);
            foreach (var watcher in watchers)
                if (watcher.Slot is >= 0 and < MaxPlayers)
                    live.Viewers |= 1UL << watcher.Slot;
        }

        particle.EffectName = file;
        particle.StartActive = true;
        particle.Teleport(Aim(pawn, live), Look(pawn), new Vector());
        particle.DispatchSpawn();
        particle.AcceptInput("Start");

        _active[slot] = live;
        _count++;

        var duration = MathF.Max(prefs.Duration ?? entry.Duration, 0.05f);
        _plugin.AddTimer(duration, () =>
        {
            if (_active[slot] is { } current && ReferenceEquals(current.Particle, particle))
            {
                _active[slot] = null;
                _count--;
            }

            StopParticle(particle);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>Adds every dead player who is currently spectating <paramref name="player"/>.</summary>
    private static void AddSpectators(CCSPlayerController player, List<CCSPlayerController> listeners)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true }) return;

        foreach (var other in Utilities.GetPlayers())
        {
            if (!other.IsValid || other.IsBot || other.Slot == player.Slot || other.PawnIsAlive) continue;

            var target = other.Pawn.Value?.ObserverServices?.ObserverTarget.Value;
            if (target is { IsValid: true } && target.Index == pawn.Index)
                listeners.Add(other);
        }
    }

    private static QAngle Look(CCSPlayerPawn pawn)
    {
        var eye = pawn.EyeAngles;
        return new QAngle(eye.X, eye.Y, eye.Z);
    }

    private static Vector Aim(CCSPlayerPawn pawn, Live live)
    {
        var origin = pawn.AbsOrigin!;
        var view = pawn.ViewOffset;
        var eye = pawn.EyeAngles;

        var pitch = eye.X * (MathF.PI / 180f);
        var yaw = eye.Y * (MathF.PI / 180f);
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);

        return new Vector(
            origin.X + view.X + cp * cy * live.Distance + sy * live.OffsetX,
            origin.Y + view.Y + cp * sy * live.Distance - cy * live.OffsetX,
            origin.Z + view.Z - sp * live.Distance + live.OffsetY);
    }

    /// <summary>Keeps each marker in front of its owner's crosshair.</summary>
    private void OnTick()
    {
        if (_stopped || _count == 0) return;

        for (var slot = 0; slot < _active.Length; slot++)
        {
            var live = _active[slot];
            if (live == null) continue;

            if (!live.Particle.IsValid)
            {
                _active[slot] = null;
                _count--;
                continue;
            }

            var pawn = live.Owner.IsValid ? live.Owner.PlayerPawn.Value : null;
            if (pawn is not { IsValid: true, AbsOrigin: not null }) continue;

            live.Particle.Teleport(Aim(pawn, live), Look(pawn), new Vector());
        }
    }

    /// <summary>Strips markers from the snapshot of everyone but the owner and (optionally) their spectators.</summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_stopped || _count == 0) return;

        foreach (var (info, viewer) in infoList)
        {
            if (viewer is not { IsValid: true } || viewer.Slot is < 0 or >= MaxPlayers) continue;

            var bit = 1UL << viewer.Slot;
            foreach (var live in _active)
                if (live != null && (live.Viewers & bit) == 0)
                    info.TransmitEntities.Remove((int)live.Particle.Index);
        }
    }

    private void RemoveAll()
    {
        for (var slot = 0; slot < _active.Length; slot++)
            Remove(slot);
    }

    private void Remove(int slot)
    {
        if (slot < 0 || slot >= _active.Length) return;

        var live = _active[slot];
        if (live == null) return;

        _active[slot] = null;
        _count--;
        StopParticle(live.Particle);
    }

    private static void StopParticle(CParticleSystem? particle)
    {
        if (particle == null || !particle.IsValid || particle.DesignerName != "info_particle_system") return;

        particle.AcceptInput("DestroyImmediately");
        if (particle.IsValid) particle.Remove();
    }
}
