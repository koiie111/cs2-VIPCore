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

namespace VIP_Outfit;

public class VipOutfit : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Outfit";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Selectable wearable models (hats, glasses, backpacks...) for VIP players";

    private Outfit? _feature;
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
        _feature = new Outfit(this, _api);
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
    /// The core API only exposes group values for a given player, so model files are collected
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
                    !values.TryGetProperty(Outfit.FeatureName, out var categories) ||
                    categories.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var category in categories.EnumerateObject())
                {
                    if (category.Value.ValueKind != JsonValueKind.Array) continue;

                    foreach (var entry in category.Value.EnumerateArray())
                        if (entry.ValueKind == JsonValueKind.Object &&
                            entry.TryGetProperty("model", out var file) && file.GetString() is { Length: > 0 } name)
                            files.Add(name);
                }
            }

            foreach (var file in files)
                manifest.AddResource(file);
        }
        catch (Exception e)
        {
            Logger.LogError("Failed to read models from {Path}: {Error}", path, e.Message);
        }
    }
}

public class OutfitEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Model file (.vmdl).</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "";

    /// <summary>Optional: "T" or "CT" restricts the item to that team.</summary>
    [JsonPropertyName("team")] public string Team { get; set; } = "";

    /// <summary>Optional CounterStrikeSharp permission or group required for this item.</summary>
    [JsonPropertyName("required")] public string Required { get; set; } = "";
}

public class Outfit : VipFeatureBase
{
    public const string FeatureName = "Outfit";
    private const string CookieKey = "outfit_prefs";
    private const string Off = "off";
    private const int MaxPlayers = 64;

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Who may see the owner's outfit.</summary>
    private enum MineMode { All, Team, Self }

    /// <summary>Whose outfits a viewer receives.</summary>
    private enum OthersMode { All, Team, None }

    /// <summary>Per-player settings, stored as one JSON cookie.</summary>
    private class Prefs
    {
        /// <summary>Category name -> selected entry name. A missing category or "off" means nothing is worn.</summary>
        [JsonPropertyName("c")] public Dictionary<string, string> Items { get; set; } = new();
        [JsonPropertyName("m")] public MineMode Mine { get; set; } = MineMode.All;
        [JsonPropertyName("o")] public OthersMode Others { get; set; } = OthersMode.All;
        [JsonPropertyName("s")] public bool SeeOwn { get; set; } = true;
    }

    public override string Feature => FeatureName;

    private readonly Dictionary<int, Prefs> _prefs = new();
    private readonly List<CDynamicProp>?[] _worn = new List<CDynamicProp>?[MaxPlayers];
    private readonly int[] _team = new int[MaxPlayers];
    private bool _stopped;

    public Outfit(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
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
        plugin.RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => Array.Clear(_worn));
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

        var categories = TryGetCategories(player);
        if (categories == null) return;

        var prefs = Load(player);
        var menu = CreateMenu(GetTranslatedText(Feature));

        foreach (var (category, entries) in categories)
        {
            var name = category;
            var list = entries;
            menu.AddMenuOption(
                GetTranslatedText("outfit.Category", Label(name), Current(player, name, list)?.Name ?? GetTranslatedText("outfit.None")),
                (controller, _) => OpenCategory(controller, name, state));
        }

        // Settings cycle on click, are applied at once and reopen the menu so the new value is shown.
        menu.AddMenuOption(GetTranslatedText("outfit.Self", YesNo(prefs.SeeOwn)), (controller, _) =>
        {
            var p = Load(controller);
            p.SeeOwn = !p.SeeOwn;
            Save(controller);
            OnSelectItem(controller, state);
        });

        menu.AddMenuOption(
            GetTranslatedText("outfit.Mine", GetTranslatedText($"outfit.mine.{prefs.Mine}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Mine = (MineMode)(((int)p.Mine + 1) % 3);
                Save(controller);
                OnSelectItem(controller, state);
            });

        // Applies to outfits of other players, so it stays available while nothing is worn.
        menu.AddMenuOption(
            GetTranslatedText("outfit.Others", GetTranslatedText($"outfit.others.{prefs.Others}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Others = (OthersMode)(((int)p.Others + 1) % 3);
                Save(controller);
                OnSelectItem(controller, state);
            });

        menu.AddMenuOption(GetTranslatedText("outfit.TakeOffAll"), (controller, _) =>
        {
            var p = Load(controller);
            foreach (var key in categories.Keys)
                p.Items[key] = Off;
            Save(controller);
            Apply(controller);
            PrintToChat(controller, GetTranslatedText("outfit.AllOff"));
            OnSelectItem(controller, state);
        });

        menu.AddMenuOption(GetTranslatedText("outfit.Reset"), (controller, _) =>
        {
            var p = Load(controller);
            p.Mine = MineMode.All;
            p.Others = OthersMode.All;
            p.SeeOwn = true;
            Save(controller);
            OnSelectItem(controller, state);
        });

        menu.Open(player);
    }

    private void OpenCategory(CCSPlayerController player, string category, FeatureState state)
    {
        var categories = TryGetCategories(player);
        if (categories == null || !categories.TryGetValue(category, out var entries)) return;

        var current = Current(player, category, entries)?.Name ?? Off;
        var menu = CreateMenu(GetTranslatedText("outfit.Select", Label(category)));

        menu.AddMenuOption(GetTranslatedText("outfit.Disable"), (controller, _) =>
        {
            Load(controller).Items[category] = Off;
            Save(controller);
            Apply(controller);
            PrintToChat(controller, GetTranslatedText("outfit.Off", Label(category)));
        }, current == Off);

        foreach (var entry in entries)
        {
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                Load(controller).Items[category] = entry.Name;
                Save(controller);
                Apply(controller);
                PrintToChat(controller, GetTranslatedText("outfit.On", entry.Name, Label(category)));
            }, current == entry.Name);
        }

        menu.Open(player);
    }

    private string YesNo(bool value) => GetTranslatedText(value ? "outfit.Yes" : "outfit.No");

    private static string Label(string name) =>
        name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;

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

        loaded.Items ??= new Dictionary<string, string>();
        if (!Enum.IsDefined(loaded.Mine)) loaded.Mine = MineMode.All;
        if (!Enum.IsDefined(loaded.Others)) loaded.Others = OthersMode.All;

        _prefs[player.Slot] = loaded;
        return loaded;
    }

    private void Save(CCSPlayerController player) =>
        SetPlayerCookie(player.SteamID, CookieKey, JsonSerializer.Serialize(Load(player), PrefsJson));

    /// <summary>The entry the player picked in a category if they may still use it; null when nothing is worn.</summary>
    private OutfitEntry? Current(CCSPlayerController player, string category, List<OutfitEntry> entries)
    {
        if (!Load(player).Items.TryGetValue(category, out var saved) || saved == Off) return null;
        return entries.FirstOrDefault(e => e.Name == saved);
    }

    /// <summary>Categories with at least one item the player may use right now (group, permission, team).</summary>
    private Dictionary<string, List<OutfitEntry>>? TryGetCategories(CCSPlayerController player)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is not FeatureState.Enabled) return null;

        try
        {
            var config = GetFeatureValue<Dictionary<string, List<OutfitEntry>>>(player);
            var result = new Dictionary<string, List<OutfitEntry>>();
            foreach (var (category, entries) in config)
            {
                if (category.Length == 0 || entries == null) continue;

                var usable = entries
                    .Where(e => e.Name.Length > 0 && e.Model.Length > 0 && Allowed(player, e))
                    .ToList();
                if (usable.Count > 0)
                    result[category] = usable;
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Allowed(CCSPlayerController player, OutfitEntry entry)
    {
        if (entry.Required.Length > 0 && !AdminManager.PlayerHasPermissions(player, entry.Required))
            return false;

        return entry.Team.ToUpperInvariant() switch
        {
            "T" => player.TeamNum == 2,
            "CT" => player.TeamNum == 3,
            _ => true
        };
    }

    // ---- outfit -----------------------------------------------------------------------------

    private void Apply(CCSPlayerController? player)
    {
        if (_stopped || player is not { IsValid: true } || player.IsBot || player.Slot >= MaxPlayers) return;

        Remove(player.Slot);

        var pawn = player.PlayerPawn.Value;
        if (pawn is not { IsValid: true } || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

        var categories = TryGetCategories(player);
        if (categories == null || pawn.AbsOrigin == null) return;

        var worn = new List<CDynamicProp>();
        foreach (var (category, entries) in categories)
        {
            var entry = Current(player, category, entries);
            if (entry == null) continue;

            var prop = Wear(pawn, entry.Model);
            if (prop != null)
                worn.Add(prop);
        }

        if (worn.Count > 0)
            _worn[player.Slot] = worn;
    }

    private static CDynamicProp? Wear(CCSPlayerPawn pawn, string model)
    {
        var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic_override");
        if (prop == null || !prop.IsValid) return null;

        // Clears the entity flag the original module also clears so the prop is networked normally.
        prop.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags &= ~(uint)(1 << 2);
        prop.SetModel(model);
        prop.Teleport(pawn.AbsOrigin, new QAngle(), new Vector());
        prop.DispatchSpawn();
        // FollowEntity bonemerges the prop onto the player model.
        prop.AcceptInput("FollowEntity", pawn, prop, "!activator");

        return prop;
    }

    private void RemoveAll()
    {
        for (var slot = 0; slot < MaxPlayers; slot++)
            Remove(slot);
    }

    private void Remove(int slot)
    {
        if (slot < 0 || slot >= MaxPlayers) return;

        var worn = _worn[slot];
        _worn[slot] = null;
        if (worn == null) return;

        foreach (var prop in worn)
            if (prop.IsValid && prop.DesignerName == "prop_dynamic_override")
                prop.Remove();
    }

    // ---- visibility -------------------------------------------------------------------------

    /// <summary>Strips outfits from each viewer's snapshot according to both visibility settings.</summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_stopped) return;

        var any = false;
        foreach (var worn in _worn)
            if (worn != null) { any = true; break; }
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
                var worn = _worn[owner];
                if (worn == null) continue;

                var ownerPrefs = _prefs.GetValueOrDefault(owner);
                bool hide;
                if (owner == viewer.Slot)
                {
                    hide = ownerPrefs is { SeeOwn: false };
                }
                else
                {
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
                    hide = !ownerAllows || !viewerAllows;
                }

                if (!hide) continue;

                foreach (var prop in worn)
                    if (prop.IsValid)
                        info.TransmitEntities.Remove((int)prop.Index);
            }
        }
    }
}
