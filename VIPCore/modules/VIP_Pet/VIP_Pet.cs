using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace VIP_Pet;

public class VipPet : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Pet";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Selectable companion pet that follows VIP players";

    private Pet? _feature;
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload)
    {
        // Models must be precached on map load, so this listener lives on the plugin itself.
        RegisterListener<Listeners.OnServerPrecacheResources>(PrecacheModels);
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
        _feature = new Pet(this, _api);
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
    /// The core API only exposes group values for a given player, so models are collected
    /// straight from vip.json to have them precached before anyone joins.
    /// </summary>
    private void PrecacheModels(ResourceManifest manifest)
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
                    !values.TryGetProperty(Pet.FeatureName, out var entries) ||
                    entries.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var entry in entries.EnumerateArray())
                    if (entry.TryGetProperty("model", out var file) && file.GetString() is { Length: > 0 } name)
                        files.Add(name);
            }

            foreach (var file in files)
                manifest.AddResource(file);
        }
        catch (Exception e)
        {
            Logger.LogError("Failed to read pet models from {Path}: {Error}", path, e.Message);
        }
    }
}

public class PetAnimations
{
    [JsonPropertyName("idle")] public string Idle { get; set; } = "";
    [JsonPropertyName("run")] public string Run { get; set; } = "";
    [JsonPropertyName("death")] public string Death { get; set; } = "";
    [JsonPropertyName("spawn")] public string Spawn { get; set; } = "";
}

public class PetEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";

    /// <summary>Hovers above the ground and bobs instead of walking.</summary>
    [JsonPropertyName("flying")] public bool Flying { get; set; }

    [JsonPropertyName("speed")] public float Speed { get; set; } = 260f;

    /// <summary>Distance the pet keeps from its owner. Players can override it in the menu.</summary>
    [JsonPropertyName("distance")] public float Distance { get; set; } = 55f;

    /// <summary>Hover height of a flying pet. Players can override it in the menu.</summary>
    [JsonPropertyName("height")] public float Height { get; set; } = 45f;

    /// <summary>Model scale. Players can multiply it in the menu.</summary>
    [JsonPropertyName("scale")] public float Scale { get; set; } = 1f;

    /// <summary>Extra yaw added to the model so it looks the way it moves.</summary>
    [JsonPropertyName("yaw")] public float Yaw { get; set; }

    /// <summary>"t", "ct" or empty for both teams.</summary>
    [JsonPropertyName("team")] public string Team { get; set; } = "";

    [JsonPropertyName("animations")] public PetAnimations Animations { get; set; } = new();
}

public class Pet : VipFeatureBase
{
    public const string FeatureName = "Pet";
    private const string CookieKey = "pet_prefs";
    private const string Off = "off";
    private const int MaxPlayers = 64;

    private const float RunThreshold = 40f;
    private const float BobSpeed = 3.4f;
    private const float BobSize = 3f;
    private const float Smoothing = 6f;
    private const float TeleportJump = 300f;
    private const float SnapDistance = 1500f;

    private static readonly float[] SizeSteps = { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f };
    private static readonly float[] DistanceSteps = { 30f, 45f, 55f, 80f, 120f, 200f, 300f };
    private static readonly float[] HeightSteps = { 0f, 20f, 45f, 70f, 100f, 150f };

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Who may see the owner's pet.</summary>
    private enum MineMode { All, Team, Self }

    /// <summary>Whose pets a viewer receives.</summary>
    private enum OthersMode { All, Team, None }

    /// <summary>Per-player settings, stored as one JSON cookie. A null value means "use the config default".</summary>
    private class Prefs
    {
        /// <summary>null = never chosen (first entry of the group), "off" = disabled, otherwise an entry name.</summary>
        [JsonPropertyName("p")] public string? Pet { get; set; }
        [JsonPropertyName("z")] public float? Size { get; set; }
        [JsonPropertyName("d")] public float? Distance { get; set; }
        [JsonPropertyName("h")] public float? Height { get; set; }
        [JsonPropertyName("m")] public MineMode Mine { get; set; } = MineMode.All;
        [JsonPropertyName("o")] public OthersMode Others { get; set; } = OthersMode.All;
        [JsonPropertyName("s")] public bool SeeOwn { get; set; } = true;
    }

    private class Companion
    {
        public required CDynamicProp Prop;
        public required PetEntry Def;
        public required int UserId;
        public required float Distance;
        public required float Height;
        public Vector3 Position;
        public Vector3 Owner;
        public float Yaw;
        public bool Running;
        public float LastTime;
        public bool Dying;
    }

    public override string Feature => FeatureName;

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Prefs> _prefs = new();
    private readonly Companion?[] _pets = new Companion?[MaxPlayers];
    private readonly int[] _team = new int[MaxPlayers];
    private readonly Vector _draw = new();
    private readonly QAngle _angle = new();
    private readonly Vector _zero = new();
    private bool _stopped;

    public Pet(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;

        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            var player = ev.Userid;
            if (player is not { IsValid: true }) return HookResult.Continue;
            Remove(player.Slot);
            Server.NextFrame(() => Apply(player, false));
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } player)
                Die(player.Slot);
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerTeam>((ev, _) =>
        {
            Remove(ev.Userid?.Slot ?? -1);
            return HookResult.Continue;
        });
        plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        plugin.RegisterListener<Listeners.OnTick>(OnTick);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => Array.Clear(_pets));
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot =>
        {
            Remove(slot);
            _prefs.Remove(slot);
        });

        // Module loaded on a live server: pick up VIPs already in game.
        foreach (var player in Utilities.GetPlayers())
            if (!player.IsBot && IsClientVip(player))
                Apply(player, true);
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
        Apply(player, true);
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

        menu.AddMenuOption(GetTranslatedText("pet.Pet", entry?.Name ?? GetTranslatedText("pet.Disable")),
            (controller, _) => OpenPets(controller, entries, state));

        if (entry != null)
        {
            // Settings cycle on click, are applied at once and reopen the menu so the new value is shown.
            menu.AddMenuOption(GetTranslatedText("pet.Size", Format(prefs.Size ?? 1f)), (controller, _) =>
            {
                var p = Load(controller);
                p.Size = Next(SizeSteps, p.Size ?? 1f);
                Save(controller);
                Apply(controller, true);
                OnSelectItem(controller, state);
            });

            menu.AddMenuOption(GetTranslatedText("pet.Distance", Format(prefs.Distance ?? entry.Distance)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Distance = Next(DistanceSteps, p.Distance ?? entry.Distance);
                    Save(controller);
                    Apply(controller, true);
                    OnSelectItem(controller, state);
                });

            if (entry.Flying)
            {
                menu.AddMenuOption(GetTranslatedText("pet.Height", Format(prefs.Height ?? entry.Height)),
                    (controller, _) =>
                    {
                        var p = Load(controller);
                        p.Height = Next(HeightSteps, p.Height ?? entry.Height);
                        Save(controller);
                        Apply(controller, true);
                        OnSelectItem(controller, state);
                    });
            }

            menu.AddMenuOption(GetTranslatedText("pet.Self", YesNo(prefs.SeeOwn)), (controller, _) =>
            {
                var p = Load(controller);
                p.SeeOwn = !p.SeeOwn;
                Save(controller);
                OnSelectItem(controller, state);
            });

            menu.AddMenuOption(GetTranslatedText("pet.Mine", GetTranslatedText($"pet.mine.{prefs.Mine}")),
                (controller, _) =>
                {
                    var p = Load(controller);
                    p.Mine = (MineMode)(((int)p.Mine + 1) % 3);
                    Save(controller);
                    OnSelectItem(controller, state);
                });
        }

        // Applies to pets of other players, so it stays available while the own pet is off.
        menu.AddMenuOption(GetTranslatedText("pet.Others", GetTranslatedText($"pet.others.{prefs.Others}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Others = (OthersMode)(((int)p.Others + 1) % 3);
                Save(controller);
                OnSelectItem(controller, state);
            });

        menu.AddMenuOption(GetTranslatedText("pet.Reset"), (controller, _) =>
        {
            var p = Load(controller);
            p.Size = null;
            p.Distance = null;
            p.Height = null;
            p.Mine = MineMode.All;
            p.Others = OthersMode.All;
            p.SeeOwn = true;
            Save(controller);
            Apply(controller, true);
            OnSelectItem(controller, state);
        });

        menu.Open(player);
    }

    private void OpenPets(CCSPlayerController player, List<PetEntry> entries, FeatureState state)
    {
        var current = Current(player, entries)?.Name ?? Off;
        var menu = CreateMenu(GetTranslatedText("pet.Select"));

        menu.AddMenuOption(GetTranslatedText("pet.Disable"), (controller, _) =>
        {
            Load(controller).Pet = Off;
            Save(controller);
            Remove(controller.Slot);
            PrintToChat(controller, GetTranslatedText("pet.Off"));
        }, current == Off);

        foreach (var entry in entries)
        {
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                Load(controller).Pet = entry.Name;
                Save(controller);
                Apply(controller, true);
                PrintToChat(controller, GetTranslatedText("pet.On", entry.Name));
            }, current == entry.Name);
        }

        menu.Open(player);
    }

    private string YesNo(bool value) => GetTranslatedText(value ? "pet.Yes" : "pet.No");

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
    /// (so a fresh VIP gets a pet without opening the menu). Null when disabled.
    /// </summary>
    private PetEntry? Current(CCSPlayerController player, List<PetEntry> entries)
    {
        var saved = Load(player).Pet;
        if (saved == Off) return null;
        return entries.FirstOrDefault(e => e.Name == saved) ?? entries.FirstOrDefault();
    }

    private List<PetEntry>? TryGetEntries(CCSPlayerController player)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is not FeatureState.Enabled) return null;

        try
        {
            var entries = GetFeatureValue<List<PetEntry>>(player)
                .Where(e => e.Name.Length > 0 && e.Model.Length > 0 && TeamMatches(player, e.Team))
                .ToList();
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TeamMatches(CCSPlayerController player, string team) => team.ToLowerInvariant() switch
    {
        "t" => player.TeamNum == (int)CsTeam.Terrorist,
        "ct" => player.TeamNum == (int)CsTeam.CounterTerrorist,
        _ => true
    };

    // ---- pet --------------------------------------------------------------------------------

    private static string PetName(CCSPlayerController player) => $"pet_{player.SteamID}";

    private static bool IsAlive(CCSPlayerController player) =>
        player.PlayerPawn.Value is { IsValid: true } pawn && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;

    private void RemoveAll()
    {
        for (var slot = 0; slot < MaxPlayers; slot++)
            Remove(slot);
    }

    private void Remove(int slot)
    {
        if (slot < 0 || slot >= MaxPlayers) return;

        var pet = _pets[slot];
        _pets[slot] = null;

        if (pet is { Prop.IsValid: true })
            pet.Prop.Remove();
    }

    private void Die(int slot)
    {
        if (slot < 0 || slot >= MaxPlayers) return;
        var pet = _pets[slot];
        if (pet == null || pet.Dying) return;

        pet.Dying = true;
        pet.Running = false;
        if (pet.Def.Animations.Death.Length > 0 && pet.Prop.IsValid)
            pet.Prop.AcceptInput("SetAnimation", value: pet.Def.Animations.Death);

        _plugin.AddTimer(1.2f, () =>
        {
            if (ReferenceEquals(_pets[slot], pet))
                Remove(slot);
        });
    }

    /// <summary>Removes stray props left by a previous spawn (hot reload, map change glitches).</summary>
    private static void RemoveNamed(string name)
    {
        foreach (var prop in Utilities.FindAllEntitiesByDesignerName<CDynamicProp>("prop_dynamic"))
            if (prop.IsValid && prop.Entity?.Name == name)
                prop.Remove();
    }

    private void Apply(CCSPlayerController? player, bool instant)
    {
        if (_stopped || player is not { IsValid: true } || player.IsBot || player.Slot >= MaxPlayers) return;

        Remove(player.Slot);
        if (!IsAlive(player)) return;

        var entries = TryGetEntries(player);
        var def = entries == null ? null : Current(player, entries);
        if (def == null) return;

        var pawn = player.PlayerPawn.Value;
        var origin = pawn?.AbsOrigin;
        if (pawn == null || origin == null) return;

        var prefs = Load(player);
        float distance = prefs.Distance ?? def.Distance;
        float height = prefs.Height ?? def.Height;
        float scale = def.Scale * (prefs.Size ?? 1f);

        string name = PetName(player);
        RemoveNamed(name);

        var start = new Vector(origin.X, origin.Y, origin.Z + (def.Flying ? height : 0f));

        var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
        if (prop == null || !prop.IsValid) return;

        prop.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags &= ~(uint)(1 << 2);

        var keys = new CEntityKeyValues();
        keys.SetString("model", def.Model);
        keys.SetString("targetname", name);
        keys.SetInt("spawnflags", 256);
        keys.SetVector("origin", start);
        prop.DispatchSpawn(keys);
        keys.Dispose();

        if (!prop.IsValid) return;

        var collision = prop.Collision;
        if (collision != null)
        {
            collision.SolidType = SolidType_t.SOLID_NONE;
            collision.SolidFlags = 12;
        }

        prop.Teleport(start, new QAngle(0, def.Yaw, 0), new Vector());

        var pet = new Companion
        {
            Prop = prop,
            Def = def,
            UserId = player.UserId ?? -1,
            Distance = distance,
            Height = height,
            Position = new Vector3(start.X, start.Y, start.Z),
            Owner = new Vector3(origin.X, origin.Y, origin.Z),
            LastTime = Server.CurrentTime
        };
        _pets[player.Slot] = pet;

        Server.NextWorldUpdate(() =>
        {
            if (!prop.IsValid) return;

            if (Math.Abs(scale - 1f) > 0.01f)
            {
                var skeleton = prop.CBodyComponent?.SceneNode?.GetSkeletonInstance();
                if (skeleton != null)
                    skeleton.Scale = scale;
                prop.AcceptInput("SetScale", null, null, scale.ToString(CultureInfo.InvariantCulture));
            }

            bool intro = !instant && def.Animations.Spawn.Length > 0;
            string first = intro ? def.Animations.Spawn : def.Animations.Idle;
            if (first.Length > 0)
                prop.AcceptInput("SetAnimation", value: first);

            if (intro && def.Animations.Idle.Length > 0)
                _plugin.AddTimer(1.1f, () =>
                {
                    if (prop.IsValid && !pet.Dying)
                        prop.AcceptInput("SetAnimation", value: def.Animations.Idle);
                });
        });
    }

    private void OnTick()
    {
        if (_stopped) return;

        float now = Server.CurrentTime;

        for (var slot = 0; slot < MaxPlayers; slot++)
        {
            var pet = _pets[slot];
            if (pet == null) continue;

            var owner = Utilities.GetPlayerFromSlot(slot);
            if (owner is not { IsValid: true } || owner.UserId != pet.UserId)
            {
                Remove(slot);
                continue;
            }

            if (!pet.Prop.IsValid)
            {
                // Round restarts wipe map entities: bring the pet back for a living owner.
                _pets[slot] = null;
                if (!pet.Dying && IsAlive(owner))
                    Server.NextFrame(() => Apply(owner, true));
                continue;
            }

            if (pet.Dying) continue;

            var pawn = owner.PlayerPawn.Value;
            if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                Die(slot);
                continue;
            }

            var originVec = pawn.AbsOrigin;
            if (originVec == null) continue;
            var origin = new Vector3(originVec.X, originVec.Y, originVec.Z);

            float dt = Math.Clamp(now - pet.LastTime, 0f, 0.2f);
            pet.LastTime = now;

            var velocity = pawn.AbsVelocity;
            float ownerSpeed = new Vector3(velocity.X, velocity.Y, velocity.Z).Length();
            float jump = Vector3.Distance(pet.Owner, origin);
            pet.Owner = origin;

            var target = Chase(pet, origin);
            bool teleported = jump > TeleportJump && jump > ownerSpeed * dt * 3f + 64f;
            if (teleported || Vector3.Distance(pet.Position, origin) > SnapDistance)
            {
                pet.Position = target;
                pet.Yaw = MathF.Atan2(origin.Y - target.Y, origin.X - target.X) * (180f / MathF.PI);
            }

            Step(pet, target, dt, MathF.Max(pet.Def.Speed, ownerSpeed * 1.1f));
        }
    }

    private static Vector3 Chase(Companion pet, Vector3 origin)
    {
        var here = pet.Position;
        float height = origin.Z + (pet.Def.Flying ? pet.Height : 0f);

        float dx = origin.X - here.X;
        float dy = origin.Y - here.Y;
        float flat = MathF.Sqrt(dx * dx + dy * dy);

        if (flat <= pet.Distance)
            return new Vector3(here.X, here.Y, height);

        float keep = pet.Distance / flat;
        return new Vector3(origin.X - dx * keep, origin.Y - dy * keep, height);
    }

    private void Step(Companion pet, Vector3 target, float dt, float speed)
    {
        var delta = target - pet.Position;
        float dist = delta.Length();

        float ease = MathF.Min(1f, Smoothing * dt);
        float step = MathF.Min(speed * dt, dist * ease + speed * dt * 0.15f);
        float moved = 0f;

        if (dist > 0.5f)
        {
            moved = MathF.Min(step, dist);
            pet.Position = step >= dist ? target : pet.Position + delta * (step / dist);

            float want = MathF.Atan2(delta.Y, delta.X) * (180f / MathF.PI);
            float turn = ((want - pet.Yaw + 540f) % 360f) - 180f;
            pet.Yaw += turn * MathF.Min(1f, 10f * dt);
        }

        bool running = dt > 0f && moved / dt > RunThreshold;

        float bob = pet.Def.Flying ? MathF.Sin(Server.CurrentTime * BobSpeed) * BobSize : 0f;
        _draw.X = pet.Position.X;
        _draw.Y = pet.Position.Y;
        _draw.Z = pet.Position.Z + bob;
        _angle.Y = pet.Yaw + pet.Def.Yaw;

        pet.Prop.Teleport(_draw, _angle, _zero);

        if (running == pet.Running) return;

        pet.Running = running;
        string anim = running ? pet.Def.Animations.Run : pet.Def.Animations.Idle;
        if (anim.Length > 0)
            pet.Prop.AcceptInput("SetAnimation", value: anim);
    }

    // ---- visibility -------------------------------------------------------------------------

    /// <summary>Strips pets from each viewer's snapshot according to both visibility settings.</summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_stopped) return;

        var any = false;
        foreach (var pet in _pets)
            if (pet != null) { any = true; break; }
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
                var pet = _pets[owner];
                if (pet == null || !pet.Prop.IsValid) continue;

                var ownerPrefs = _prefs.GetValueOrDefault(owner);
                if (owner == viewer.Slot)
                {
                    if (ownerPrefs is { SeeOwn: false })
                        info.TransmitEntities.Remove((int)pet.Prop.Index);
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
                    info.TransmitEntities.Remove((int)pet.Prop.Index);
            }
        }
    }
}
