using System.Drawing;
using System.Globalization;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using Vector3 = System.Numerics.Vector3;
using static VipCoreApi.IVipCoreApi;

namespace VIP_PlayerTrail;

public class VipPlayerTrail : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Player Trail";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleDescription => "Draws a beam or particle trail behind VIP players";

    private bool _unloaded;
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
        if (_unloaded || _trail != null) return;
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
        _unloaded = true;
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
        if (_trail == null) return;
        _api.OnPlayerSpawn -= _trail.OnPlayerSpawn;
        _api.PlayerLoaded -= _trail.OnPlayerLoaded;
        _api.PlayerRemoved -= _trail.OnPlayerRemoved;
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
    private const string MineCookieKey = "player_trail_mine";
    private const string OthersCookieKey = "player_trail_others";
    private const string Off = TrailSelection.Off;
    private const char ParticleMarker = '@';
    private const string BeamSprite = "materials/sprites/laserbeam.vmat";
    private const float RainbowDegreesPerSecond = 60f;

    private enum ColorMode { Fixed, Rainbow, Random }

    private sealed class TrailState
    {
        public required CCSPlayerPawn Pawn;
        public float Width;
        public float Lifetime;
        public ColorMode ColorMode;
        public Color Color;
        public ParticleEntry? Particle;
        public CParticleSystem? Carried;
        public Vector3 Last;
        public bool HasLast;
    }

    public override string Feature => FeatureName;

    private readonly TrailState?[] _states = new TrailState?[TrailVisibility.Slots];
    private readonly string?[] _selected = new string?[TrailVisibility.Slots];
    private readonly uint[] _spawnGeneration = new uint[TrailVisibility.Slots];
    private readonly Color[] _roundColors = new Color[TrailVisibility.Slots];
    private readonly TrailVisibility _visibility = new();
    private readonly TrailEntities _entities = new();

    private UInt128 _activeOwners;
    private bool _sampleMovement = true;
    private bool _stopped;

    public PlayerTrail(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        RollRoundColors();

        plugin.RegisterListener<Listeners.OnTick>(OnTick);
        plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        plugin.RegisterListener<Listeners.OnEntityDeleted>(entity =>
        {
            if (_entities.Owners == 0) return;
            var handle = entity.EntityHandle;
            _entities.Remove(handle.Index, handle.Raw);
        });
        plugin.RegisterListener<Listeners.OnMapEnd>(ResetMap);
        plugin.RegisterListener<Listeners.OnMapStart>(_ =>
        {
            ResetMap();
            foreach (var player in Utilities.GetPlayers())
                if (ValidSlot(player.Slot)) _visibility.SetTeam(player.Slot, player.TeamNum);
        });
        plugin.RegisterListener<Listeners.OnClientPutInServer>(slot =>
        {
            if (ValidSlot(slot) && Utilities.GetPlayerFromSlot(slot) is { IsValid: true } player)
                _visibility.SetTeam(slot, player.TeamNum);
        });
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            if (!ValidSlot(slot)) return;
            _spawnGeneration[slot]++;
            Remove(slot, destroyCarried: false);
            RemoveOwnedEffects(slot);
            _selected[slot] = null;
            _visibility.ResetSlot(slot);
        });
        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            RollRoundColors();
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } player) CancelSpawn(player.Slot);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } player && ValidSlot(player.Slot))
            {
                _visibility.SetTeam(player.Slot, ev.Team);
                CancelSpawn(player.Slot);
            }
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            var player = ev.Userid;
            if (player is not { IsValid: true } || !ValidSlot(player.Slot)) return HookResult.Continue;
            var slot = player.Slot;
            CancelSpawn(slot);
            _visibility.SetTeam(slot, player.TeamNum);
            var generation = _spawnGeneration[slot];
            Server.NextFrame(() =>
            {
                if (generation == _spawnGeneration[slot]) Apply(player);
            });
            return HookResult.Continue;
        });

        // Module loaded on a live server: pick up VIPs already in game.
        foreach (var player in Utilities.GetPlayers())
        {
            if (!ValidSlot(player.Slot)) continue;
            _visibility.SetTeam(player.Slot, player.TeamNum);
            if (player.IsBot || !IsClientVip(player)) continue;
            LoadSelection(player);
            Apply(player);
        }
    }

    public void Stop()
    {
        _stopped = true;
        var owners = _entities.Owners;
        while (owners != 0)
            RemoveOwnedEffects(TakeSlot(ref owners));
        ResetMap();
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (_stopped || !ValidSlot(player.Slot)) return;
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

        var options = Options(cfg);
        var current = CurrentValue(player, options);
        var menu = CreateMenu(GetTranslatedText(Feature));

        // Visibility settings cycle on click and reopen the menu so the new value is shown.
        var others = _visibility.Others(player.Slot);
        menu.AddMenuOption(GetTranslatedText("playertrail.Others", GetTranslatedText($"playertrail.others.{others}")),
            (controller, _) =>
            {
                var next = (OthersMode)(((int)others + 1) % 3);
                _visibility.SetPreferences(controller.Slot, _visibility.Mine(controller.Slot), next);
                SetPlayerCookie(controller.SteamID, OthersCookieKey, (int)next);
                OnSelectItem(controller, state);
            });

        var mine = _visibility.Mine(player.Slot);
        menu.AddMenuOption(GetTranslatedText("playertrail.Mine", GetTranslatedText($"playertrail.mine.{mine}")),
            (controller, _) =>
            {
                var next = (MineMode)(((int)mine + 1) % 3);
                _visibility.SetPreferences(controller.Slot, next, _visibility.Others(controller.Slot));
                SetPlayerCookie(controller.SteamID, MineCookieKey, (int)next);
                OnSelectItem(controller, state);
            });

        menu.AddMenuOption(GetTranslatedText("playertrail.Disable"), (controller, _) =>
        {
            Select(controller, Off);
            PrintToChat(controller, GetTranslatedText("playertrail.Off"));
        }, current == Off);

        foreach (var (name, value) in options)
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
        Apply(player);
    }

    private void LoadSelection(CCSPlayerController player)
    {
        var cookie = GetPlayerCookie<string>(player.SteamID, CookieKey);
        _selected[player.Slot] = string.IsNullOrEmpty(cookie) ? null : cookie;

        var mine = GetPlayerCookie<int>(player.SteamID, MineCookieKey);
        var others = GetPlayerCookie<int>(player.SteamID, OthersCookieKey);
        _visibility.SetTeam(player.Slot, player.TeamNum);
        _visibility.SetPreferences(player.Slot,
            Enum.IsDefined((MineMode)mine) ? (MineMode)mine : MineMode.All,
            Enum.IsDefined((OthersMode)others) ? (OthersMode)others : OthersMode.All);
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
    private string CurrentValue(CCSPlayerController player, List<(string Name, string Value)> options)
    {
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
        if (_stopped || player is not { IsValid: true } || player.IsBot || !ValidSlot(player.Slot)) return;
        var slot = player.Slot;
        Remove(slot);

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
        var cfg = TryGetConfig(player);
        if (cfg == null) return;
        var value = CurrentValue(player, Options(cfg));
        if (value == Off) return;

        var state = new TrailState { Pawn = pawn, Width = cfg.Width, Lifetime = cfg.Lifetime };
        if (value[0] == ParticleMarker)
        {
            var name = value[1..];
            var entry = cfg.Particles.FirstOrDefault(p =>
                p.File.Length > 0 && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return;
            state.Particle = entry;
            if (entry.Lifetime > 0) state.Lifetime = entry.Lifetime;
            if (entry.Tint.Length > 0)
            {
                state.ColorMode = IsRainbow(entry.Tint) ? ColorMode.Rainbow : ColorMode.Fixed;
                if (state.ColorMode == ColorMode.Fixed) state.Color = ParseColor(entry.Tint);
            }
            if (entry.Follow && pawn.AbsOrigin is { } abs)
            {
                state.Last = (Vector3)abs;
                state.Carried = Carry(slot, state, state.Last);
                state.HasLast = state.Carried is not null;
            }
        }
        else if (value.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            state.ColorMode = ColorMode.Random;
        }
        else if (IsRainbow(value))
        {
            state.ColorMode = ColorMode.Rainbow;
        }
        else
        {
            state.Color = ParseColor(value);
        }

        _states[slot] = state;
        _activeOwners |= TrailVisibility.Bit(slot);
    }

    private static bool ValidSlot(int slot) => (uint)slot < TrailVisibility.Slots;

    private static int TakeSlot(ref UInt128 slots)
    {
        var slot = (int)UInt128.TrailingZeroCount(slots);
        slots &= slots - 1;
        return slot;
    }

    private void ResetMap()
    {
        for (var slot = 0; slot < _spawnGeneration.Length; slot++) _spawnGeneration[slot]++;
        Array.Clear(_states);
        _activeOwners = 0;
        _entities.Clear();
        _visibility.ResetTeams();
    }

    private void CancelSpawn(int slot)
    {
        if (!ValidSlot(slot)) return;
        _spawnGeneration[slot]++;
        Remove(slot);
    }

    private void Remove(int slot, bool destroyCarried = true)
    {
        if (!ValidSlot(slot)) return;
        var state = _states[slot];
        if (state == null) return;
        _states[slot] = null;
        _activeOwners &= ~TrailVisibility.Bit(slot);
        if (destroyCarried) DestroyEffect(state.Carried, carried: true);
    }

    private void RemoveOwnedEffects(int slot)
    {
        // Removal can synchronously invoke OnEntityDeleted and change the collection.
        foreach (var (handle, carried) in _entities.OwnedBy(slot).ToArray())
            DestroyEffect(new CEntityInstance(handle), carried);
    }

    private void OnTick()
    {
        if (_stopped) return;
        _sampleMovement = !_sampleMovement;
        double? now = null;
        if (_entities.HasExpirations)
        {
            now = Server.TickedTime;
            while (_entities.TryTakeExpired(now.Value, out var handle))
                DestroyEffect(new CEntityInstance(handle), carried: false);
        }

        if (!_sampleMovement) return;
        Color? rainbow = null;
        var owners = _activeOwners;
        while (owners != 0)
        {
            var slot = TakeSlot(ref owners);
            var state = _states[slot]!;
            var pawn = state.Pawn;
            if (!pawn.IsValid || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.AbsOrigin is not { } abs)
            {
                Remove(slot);
                continue;
            }

            var origin = (Vector3)abs;
            if (state.Carried is { } carried)
            {
                if (!carried.IsValid)
                {
                    state.Carried = null;
                    state.HasLast = false;
                }
                else if (origin != state.Last)
                {
                    carried.Teleport(WithOffset(origin, state.Particle!.Offset), Vector3.Zero, Vector3.Zero);
                    state.Last = origin;
                }
                continue;
            }

            if (!state.HasLast)
            {
                state.Last = origin;
                state.HasLast = true;
                continue;
            }

            if (!TrailMovement.Advance(ref state.Last, origin, out var last)) continue;
            var color = state.ColorMode switch
            {
                ColorMode.Rainbow => rainbow ??= RainbowColor(),
                ColorMode.Random => _roundColors[slot],
                _ => state.Color
            };
            var deadline = (now ??= Server.TickedTime) + state.Lifetime;
            if (state.Particle is { } particle)
                Tracer(slot, last, origin, particle, color, deadline);
            else
                CreateBeam(slot, origin, last, color, state.Width, deadline);
        }
    }

    private void Track(int owner, CEntityInstance entity, double? deadline)
    {
        var handle = entity.EntityHandle;
        _entities.Add(owner, handle.Index, handle.Raw, deadline);
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        var owners = _entities.Owners;
        if (_stopped || owners == 0 || !_visibility.IsRestricted) return;

        var count = infoList.Count;
        for (var i = 0; i < count; i++)
        {
            var (info, viewer) = infoList[i];
            if (viewer is not { IsValid: true }) continue;
            var slot = viewer.Slot;
            if (!ValidSlot(slot)) continue;

            var hidden = owners & _visibility.HiddenOwners(slot);
            while (hidden != 0)
            {
                var indices = _entities.Indices(TakeSlot(ref hidden))!;
                foreach (var index in indices)
                    info.TransmitEntities.Remove(index);
            }
        }
    }

    private void RollRoundColors()
    {
        for (var i = 0; i < _roundColors.Length; i++)
            _roundColors[i] = FromHue(Random.Shared.NextDouble() * 360.0);
    }

    private void CreateBeam(int owner, Vector3 from, Vector3 to, Color color, float width, double deadline)
    {
        var beam = Utilities.CreateEntityByName<CEnvBeam>("env_beam");
        if (beam is not { IsValid: true }) return;

        Track(owner, beam, deadline);
        beam.Width = width;
        beam.Render = color;
        beam.SetModel(BeamSprite);
        beam.Teleport(from, Vector3.Zero, Vector3.Zero);

        var end = beam.EndPos;
        end.X = to.X;
        end.Y = to.Y;
        end.Z = to.Z;
        Utilities.SetStateChanged(beam, "CBeam", "m_vecEndPos");
    }

    private void Tracer(int owner, Vector3 start, Vector3 end, ParticleEntry entry, Color tint, double deadline)
    {
        start = WithOffset(start, entry.Offset);
        end = WithOffset(end, entry.Offset);
        var angles = Aim(end - start);
        var particle = CreateParticle(entry, start, angles, tint);
        if (particle == null) return;
        Track(owner, particle, deadline);
        particle.DispatchSpawn();

        if (entry.Tint.Length == 0)
            SetControlPoint(particle, 1, end);
        SetControlPoint(particle, 5, start);
        SetControlPoint(particle, 6, end);
        if (entry.Radius > 0)
            SetControlPoint(particle, 4, new Vector3(0, entry.Radius, 0));

        particle.AcceptInput("Start");
    }

    private CParticleSystem? Carry(int owner, TrailState state, Vector3 origin)
    {
        var entry = state.Particle!;
        var tint = state.ColorMode == ColorMode.Rainbow ? RainbowColor() : state.Color;
        var particle = CreateParticle(entry, WithOffset(origin, entry.Offset), Vector3.Zero, tint);
        if (particle == null) return null;
        Track(owner, particle, null);
        particle.DispatchSpawn();
        particle.AcceptInput("Start");
        return particle;
    }

    private static CParticleSystem? CreateParticle(ParticleEntry entry, Vector3 start, Vector3 angles, Color tint)
    {
        var particle = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system");
        if (particle is not { IsValid: true }) return null;
        particle.EffectName = entry.File;
        particle.StartActive = true;
        particle.Teleport(start, angles, Vector3.Zero);
        if (entry.Tint.Length > 0)
        {
            particle.TintCP = 1;
            particle.Tint = tint;
        }
        return particle;
    }

    private static void DestroyEffect(CEntityInstance? entity, bool carried)
    {
        if (entity is not { IsValid: true }) return;
        if (carried)
        {
            entity.AcceptInput("DestroyImmediately");
            if (!entity.IsValid) return;
        }
        entity.Remove();
    }

    private static Vector3 WithOffset(Vector3 position, float offset)
    {
        position.Z += offset;
        return position;
    }

    private static void SetControlPoint(CParticleSystem particle, int index, Vector3 point) =>
        particle.AcceptInput("SetControlPoint", value: string.Create(CultureInfo.InvariantCulture,
            $"{index}: {point.X} {point.Y} {point.Z}"));

    private static Vector3 Aim(Vector3 delta)
    {
        var flat = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        var yaw = MathF.Atan2(delta.Y, delta.X) * (180f / MathF.PI);
        var pitch = -MathF.Atan2(delta.Z, flat) * (180f / MathF.PI);
        return new Vector3(pitch, yaw, 0);
    }

    private static bool IsRainbow(string color) =>
        string.IsNullOrWhiteSpace(color) || color.Equals("rainbow", StringComparison.OrdinalIgnoreCase);

    private static Color RainbowColor() =>
        FromHue(Server.CurrentTime * RainbowDegreesPerSecond % 360.0);

    private static Color ParseColor(string color)
    {
        if (color.StartsWith('#') && color.Length == 7
            && int.TryParse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hr)
            && int.TryParse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hg)
            && int.TryParse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hb))
            return Color.FromArgb(255, hr, hg, hb);

        var parts = color.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) &&
            byte.TryParse(parts[2], out var b))
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
