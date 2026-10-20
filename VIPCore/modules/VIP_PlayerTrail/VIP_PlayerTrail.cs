using System.Drawing;
using System.Globalization;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_PlayerTrail;

public class VipPlayerTrail : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Player Trail";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Draws a beam or particle trail behind VIP players";

    private PlayerTrail? _trail;
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
        if (_trail != null) return;
        _api = PluginCapability.Get();
        if (_api == null) return;
        _api.OnCoreReady += RegisterFeature;
        // The core-ready event has already fired when loading this module on a live server.
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (_api == null || _trail != null) return;
        _trail = new PlayerTrail(this, _api);
        _api.RegisterFeature(_trail, FeatureType.Selectable);
    }

    public override void Unload(bool hotReload)
    {
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
        if (_trail == null) return;
        _trail.Stop();
        _api.UnRegisterFeature(_trail);
        _trail = null;
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
                    !values.TryGetProperty(PlayerTrail.FeatureName, out var trail) ||
                    !trail.TryGetProperty("Particles", out var particles) ||
                    particles.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var particle in particles.EnumerateArray())
                    if (particle.TryGetProperty("File", out var file) && file.GetString() is { Length: > 0 } name)
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

public class TrailConfig
{
    public float Width { get; set; } = 1.0f;
    public float Lifetime { get; set; } = 2.0f;

    /// <summary>Entries of the form "Name #RRGGBB", "Name rainbow" or "Name random".</summary>
    public List<string> Colors { get; set; } = new();

    public List<ParticleEntry> Particles { get; set; } = new();
}

public class ParticleEntry
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string Tint { get; set; } = "";
    public float Radius { get; set; } = 0f;
    public float Lifetime { get; set; } = 0f;
    public float Offset { get; set; } = 0f;
    public bool Follow { get; set; } = true;
}

public class PlayerTrail : VipFeatureBase
{
    public const string FeatureName = "PlayerTrail";
    private const string CookieKey = "player_trail";
    private const string Off = "off";
    private const char ParticleMarker = '@';
    private const string BeamSprite = "materials/sprites/laserbeam.vmat";
    private const float MinSegment = 12f;
    private const float MaxSegment = 250f;
    private const float RainbowDegreesPerSecond = 60f;

    private static readonly QAngle ZeroAngle = new();
    private static readonly Vector ZeroVector = new();

    /// <summary>What a player's trail resolved to; rebuilt on spawn and menu selection, read every tick.</summary>
    private sealed class TrailState
    {
        public required CCSPlayerController Player;
        public required TrailConfig Config;
        public string Value = "";
        public ParticleEntry? Particle;
        public CParticleSystem? Carried;
        public Vector3 Last;
        public bool HasLast;
    }

    private record struct Vector3(float X, float Y, float Z)
    {
        public float DistanceTo(Vector3 o) =>
            MathF.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y) + (Z - o.Z) * (Z - o.Z));
    }

    public override string Feature => FeatureName;

    private readonly BasePlugin _plugin;
    private readonly TrailState?[] _states = new TrailState?[65];
    private readonly string?[] _selected = new string?[65];
    private readonly Color[] _roundColors = new Color[65];
    private int _tick;
    private bool _stopped;

    public PlayerTrail(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;
        RollRoundColors();

        plugin.RegisterListener<Listeners.OnTick>(OnTick);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => Array.Clear(_states));
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            Remove(slot);
            _selected[slot] = null;
        });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            RollRoundColors();
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } player) Remove(player.Slot);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } player) Remove(player.Slot);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            var player = ev.Userid;
            if (player is not { IsValid: true }) return HookResult.Continue;
            Remove(player.Slot);
            Server.NextFrame(() => Apply(player));
            return HookResult.Continue;
        });

        // Module loaded on a live server: pick up VIPs already in game.
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsBot || !IsClientVip(player)) continue;
            LoadSelection(player);
            Apply(player);
        }
    }

    public void Stop()
    {
        _stopped = true;
        for (var slot = 0; slot < _states.Length; slot++)
            Remove(slot);
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (_stopped) return;
        LoadSelection(player);
        Apply(player);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        if (_stopped) return;
        Remove(player.Slot);
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped) return;

        var cfg = TryGetConfig(player);
        if (cfg == null) return;

        var current = CurrentValue(player, cfg);
        var menu = CreateMenu(GetTranslatedText(Feature));

        menu.AddMenuOption(GetTranslatedText("playertrail.Disable"), (controller, _) =>
        {
            Select(controller, Off);
            PrintToChat(controller, GetTranslatedText("playertrail.Off"));
        }, current == Off);

        foreach (var (name, value) in Options(cfg))
        {
            menu.AddMenuOption(name, (controller, _) =>
            {
                Select(controller, value);
                PrintToChat(controller, GetTranslatedText("playertrail.On", name));
            }, current.Equals(value, StringComparison.OrdinalIgnoreCase));
        }

        menu.Open(player);
    }

    private void Select(CCSPlayerController player, string value)
    {
        _selected[player.Slot] = value;
        SetPlayerCookie(player.SteamID, CookieKey, value);
        Remove(player.Slot);
        Apply(player);
    }

    private void LoadSelection(CCSPlayerController player)
    {
        var cookie = GetPlayerCookie<string>(player.SteamID, CookieKey);
        _selected[player.Slot] = string.IsNullOrEmpty(cookie) ? null : cookie;
    }

    /// <summary>Menu entries available to the player's group: beam colors first, then particles.</summary>
    private static List<(string Name, string Value)> Options(TrailConfig cfg)
    {
        var options = new List<(string, string)>();

        foreach (var entry in cfg.Colors)
        {
            var tokens = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0) continue;

            var value = tokens[^1];
            var name = tokens.Length > 1 ? string.Join(' ', tokens[..^1]) : value;
            options.Add((name, value));
        }

        foreach (var particle in cfg.Particles)
            if (particle.Name.Length > 0 && particle.File.Length > 0)
                options.Add((particle.Name, ParticleMarker + particle.Name));

        return options;
    }

    /// <summary>
    /// The saved choice if the group still offers it, otherwise the first option of the group
    /// (so a fresh VIP gets a trail without opening the menu).
    /// </summary>
    private string CurrentValue(CCSPlayerController player, TrailConfig cfg)
    {
        var options = Options(cfg);
        var saved = _selected[player.Slot];

        if (saved == Off) return Off;
        if (saved != null && options.Any(o => o.Value.Equals(saved, StringComparison.OrdinalIgnoreCase)))
            return saved;

        return options.Count > 0 ? options[0].Value : Off;
    }

    private TrailConfig? TryGetConfig(CCSPlayerController player)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is FeatureState.NoAccess) return null;

        try
        {
            return GetFeatureValue<TrailConfig>(player);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Apply(CCSPlayerController? player)
    {
        if (_stopped || player is not { IsValid: true } || player.IsBot || player.Slot >= _states.Length) return;

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

        var cfg = TryGetConfig(player);
        if (cfg == null) return;

        var value = CurrentValue(player, cfg);
        if (value == Off) return;

        Remove(player.Slot);

        var state = new TrailState { Player = player, Config = cfg, Value = value };
        if (value[0] == ParticleMarker)
        {
            var name = value[1..];
            state.Particle = cfg.Particles.FirstOrDefault(p =>
                p.File.Length > 0 && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (state.Particle == null) return;

            if (state.Particle.Follow)
                state.Carried = Carry(pawn, state.Particle);
        }

        _states[player.Slot] = state;
    }

    private void Remove(int slot)
    {
        if (slot < 0 || slot >= _states.Length) return;

        var state = _states[slot];
        _states[slot] = null;
        StopParticle(state?.Carried);
    }

    private void OnTick()
    {
        if (_stopped || ++_tick % 2 != 0) return;

        for (var slot = 0; slot < _states.Length; slot++)
        {
            var state = _states[slot];
            if (state == null) continue;

            var player = state.Player;
            var pawn = player.IsValid ? player.PlayerPawn.Value : null;
            var abs = pawn is { IsValid: true } ? pawn.AbsOrigin : null;
            if (abs == null || pawn!.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                Remove(slot);
                continue;
            }

            var origin = new Vector3(abs.X, abs.Y, abs.Z);

            if (state.Carried != null)
            {
                if (!state.Carried.IsValid)
                {
                    state.Carried = null;
                    continue;
                }

                state.Carried.Teleport(new Vector(origin.X, origin.Y, origin.Z + state.Particle!.Offset), ZeroAngle,
                    ZeroVector);
                continue;
            }

            if (!state.HasLast)
            {
                state.Last = origin;
                state.HasLast = true;
                continue;
            }

            var dist = state.Last.DistanceTo(origin);
            if (dist < MinSegment) continue;

            // Longer jumps are teleports/respawns: skip the segment instead of drawing a line across the map.
            if (dist < MaxSegment)
            {
                var last = state.Last;
                if (state.Particle != null)
                {
                    var offset = state.Particle.Offset;
                    Tracer(new Vector(last.X, last.Y, last.Z + offset),
                        new Vector(origin.X, origin.Y, origin.Z + offset), state.Particle, state.Config.Lifetime);
                }
                else
                {
                    var color = state.Value.Equals("random", StringComparison.OrdinalIgnoreCase)
                        ? _roundColors[slot]
                        : ResolveColor(state.Value);
                    CreateBeam(new Vector(origin.X, origin.Y, origin.Z), new Vector(last.X, last.Y, last.Z), color,
                        state.Config.Width, state.Config.Lifetime);
                }
            }

            state.Last = origin;
        }
    }

    private void RollRoundColors()
    {
        for (var i = 0; i < _roundColors.Length; i++)
            _roundColors[i] = FromHue(Random.Shared.NextDouble() * 360.0);
    }

    private void CreateBeam(Vector from, Vector to, Color color, float width, float lifetime)
    {
        var beam = Utilities.CreateEntityByName<CEnvBeam>("env_beam");
        if (beam == null || !beam.IsValid) return;

        beam.Width = width;
        beam.Render = color;
        beam.SetModel(BeamSprite);
        beam.Teleport(from, ZeroAngle, ZeroVector);

        beam.EndPos.X = to.X;
        beam.EndPos.Y = to.Y;
        beam.EndPos.Z = to.Z;
        Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");

        _plugin.AddTimer(lifetime, () =>
        {
            if (beam.IsValid) beam.Remove();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void Tracer(Vector start, Vector end, ParticleEntry entry, float lifetime)
    {
        var particle = CreateParticle(entry, start, Aim(start, end));
        if (particle == null) return;

        particle.DispatchSpawn();

        if (entry.Tint.Length == 0)
            SetControlPoint(particle, 1, end.X, end.Y, end.Z);

        SetControlPoint(particle, 5, start.X, start.Y, start.Z);
        SetControlPoint(particle, 6, end.X, end.Y, end.Z);
        if (entry.Radius > 0)
            SetControlPoint(particle, 4, 0, entry.Radius, 0);

        particle.AcceptInput("Start");

        var life = entry.Lifetime > 0 ? entry.Lifetime : lifetime;
        _plugin.AddTimer(life, () =>
        {
            if (particle.IsValid) particle.Remove();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static CParticleSystem? Carry(CBaseEntity owner, ParticleEntry entry)
    {
        var origin = owner.AbsOrigin;
        if (origin == null) return null;

        var particle = CreateParticle(entry, new Vector(origin.X, origin.Y, origin.Z + entry.Offset), ZeroAngle);
        if (particle == null) return null;

        particle.DispatchSpawn();
        particle.AcceptInput("Start");
        return particle;
    }

    private static CParticleSystem? CreateParticle(ParticleEntry entry, Vector start, QAngle angles)
    {
        if (entry.File.Length == 0) return null;

        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle == null || !particle.IsValid) return null;

        particle.EffectName = entry.File;
        particle.StartActive = true;
        particle.Teleport(start, angles, ZeroVector);

        if (entry.Tint.Length > 0)
        {
            particle.TintCP = 1;
            particle.Tint = ResolveColor(entry.Tint);
        }

        return particle;
    }

    private static void StopParticle(CParticleSystem? particle)
    {
        if (particle == null || !particle.IsValid || particle.DesignerName != "info_particle_system") return;

        particle.AcceptInput("DestroyImmediately");
        if (particle.IsValid) particle.Remove();
    }

    private static void SetControlPoint(CParticleSystem particle, int index, float x, float y, float z) =>
        particle.AcceptInput("SetControlPoint", value: string.Create(CultureInfo.InvariantCulture,
            $"{index}: {x} {y} {z}"));

    private static QAngle Aim(Vector from, Vector to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y, dz = to.Z - from.Z;
        var flat = MathF.Sqrt(dx * dx + dy * dy);

        var yaw = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        var pitch = -MathF.Atan2(dz, flat) * (180f / MathF.PI);

        return new QAngle(pitch, yaw, 0);
    }

    /// <summary>"#RRGGBB", "R G B" or "rainbow" (hue cycles over time); anything else is white.</summary>
    private static Color ResolveColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color) || color.Equals("rainbow", StringComparison.OrdinalIgnoreCase))
            return FromHue(Server.CurrentTime * RainbowDegreesPerSecond % 360.0);

        if (color.StartsWith('#') && color.Length == 7
            && int.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hr)
            && int.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hg)
            && int.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hb))
            return Color.FromArgb(255, hr, hg, hb);

        var parts = color.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && int.TryParse(parts[0], out var r) && int.TryParse(parts[1], out var g) &&
            int.TryParse(parts[2], out var b))
            return Color.FromArgb(255, r, g, b);

        return Color.White;
    }

    private static Color FromHue(double h)
    {
        var x = 1 - Math.Abs(h / 60.0 % 2 - 1);
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = 1; g = x; }
        else if (h < 120) { r = x; g = 1; }
        else if (h < 180) { g = 1; b = x; }
        else if (h < 240) { g = x; b = 1; }
        else if (h < 300) { r = x; b = 1; }
        else { r = 1; b = x; }
        return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }
}
