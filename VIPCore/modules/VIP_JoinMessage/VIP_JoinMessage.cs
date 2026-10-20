using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_JoinMessage;

public class VipJoinMessage : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Join Message";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Selectable VIP join and leave announcements";

    private JoinMessage? _feature;
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload) => Server.NextWorldUpdate(Initialize);

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
        _feature = new JoinMessage(this, _api);
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
}

public class JoinMessageEntry
{
    /// <summary>Name shown in the menu; also the identifier of the saved choice.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Chat text. Supports {name}, {group} and colour tags such as {red} or {blue}.</summary>
    [JsonPropertyName("message")] public string Message { get; set; } = "";

    /// <summary>false = shown when the player joins, true = shown when the player leaves.</summary>
    [JsonPropertyName("leave")] public bool Leave { get; set; }
}

public class JoinMessage : VipFeatureBase
{
    public const string FeatureName = "JoinMessage";

    private const string CookieKey = "joinmessage_prefs";
    private const int MaxAttempts = 6;

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Who receives the owner's announcement.</summary>
    private enum MineMode { All, Team, Self }

    /// <summary>Whose announcements a viewer receives.</summary>
    private enum OthersMode { All, Team, None }

    /// <summary>Per-player settings, stored as one JSON cookie.</summary>
    private class Prefs
    {
        [JsonPropertyName("j")] public bool Join { get; set; } = true;
        [JsonPropertyName("l")] public bool Leave { get; set; } = true;

        /// <summary>null = never chosen (first entry of the group), otherwise an entry name.</summary>
        [JsonPropertyName("jn")] public string? JoinName { get; set; }
        [JsonPropertyName("ln")] public string? LeaveName { get; set; }
        [JsonPropertyName("m")] public MineMode Mine { get; set; } = MineMode.All;
        [JsonPropertyName("o")] public OthersMode Others { get; set; } = OthersMode.All;
    }

    public override string Feature => FeatureName;

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Prefs> _prefs = new();
    private bool _stopped;

    public JoinMessage(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;

        plugin.RegisterEventHandler<EventPlayerConnectFull>(OnConnect);
        plugin.RegisterEventHandler<EventPlayerDisconnect>(OnDisconnect);
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot => _prefs.Remove(slot));
    }

    /// <summary>Called on plugin unload; CounterStrikeSharp drops handlers and listeners itself.</summary>
    public void Stop()
    {
        _stopped = true;
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
        var join = TryGetEntries(player, false);
        var leave = TryGetEntries(player, true);
        var menu = CreateMenu(GetTranslatedText(Feature));

        // Settings cycle on click and reopen the menu so the new value is shown.
        menu.AddMenuOption(GetTranslatedText("joinmessage.Join", OnOff(prefs.Join)), (controller, _) =>
        {
            var p = Load(controller);
            p.Join = !p.Join;
            Save(controller);
            OnSelectItem(controller, state);
        });

        if (join != null)
            menu.AddMenuOption(GetTranslatedText("joinmessage.JoinText", Current(player, join, false)?.Name ?? ""),
                (controller, _) => OpenMessages(controller, false, state));

        menu.AddMenuOption(GetTranslatedText("joinmessage.Leave", OnOff(prefs.Leave)), (controller, _) =>
        {
            var p = Load(controller);
            p.Leave = !p.Leave;
            Save(controller);
            OnSelectItem(controller, state);
        });

        if (leave != null)
            menu.AddMenuOption(GetTranslatedText("joinmessage.LeaveText", Current(player, leave, true)?.Name ?? ""),
                (controller, _) => OpenMessages(controller, true, state));

        menu.AddMenuOption(
            GetTranslatedText("joinmessage.Mine", GetTranslatedText($"joinmessage.mine.{prefs.Mine}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Mine = (MineMode)(((int)p.Mine + 1) % 3);
                Save(controller);
                OnSelectItem(controller, state);
            });

        menu.AddMenuOption(
            GetTranslatedText("joinmessage.Others", GetTranslatedText($"joinmessage.others.{prefs.Others}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Others = (OthersMode)(((int)p.Others + 1) % 3);
                Save(controller);
                OnSelectItem(controller, state);
            });

        // Shows both texts to the player only, so a chosen message can be checked without reconnecting.
        menu.AddMenuOption(GetTranslatedText("joinmessage.Preview"), (controller, _) =>
        {
            PrintToChat(controller, BuildMessage(controller, false));
            PrintToChat(controller, BuildMessage(controller, true));
            OnSelectItem(controller, state);
        });

        menu.AddMenuOption(GetTranslatedText("joinmessage.Reset"), (controller, _) =>
        {
            _prefs[controller.Slot] = new Prefs();
            Save(controller);
            OnSelectItem(controller, state);
        });

        menu.Open(player);
    }

    private void OpenMessages(CCSPlayerController player, bool leave, FeatureState state)
    {
        var entries = TryGetEntries(player, leave);
        if (entries == null) return;

        var current = Current(player, entries, leave)?.Name;
        var menu = CreateMenu(GetTranslatedText(leave ? "joinmessage.SelectLeave" : "joinmessage.SelectJoin"));

        foreach (var entry in entries)
        {
            menu.AddMenuOption(entry.Name, (controller, _) =>
            {
                var prefs = Load(controller);
                if (leave) prefs.LeaveName = entry.Name;
                else prefs.JoinName = entry.Name;
                Save(controller);
                PrintToChat(controller,
                    GetTranslatedText(leave ? "joinmessage.LeaveSet" : "joinmessage.JoinSet", entry.Name));
            }, current == entry.Name);
        }

        menu.Open(player);
    }

    private string OnOff(bool value) => GetTranslatedText(value ? "joinmessage.On" : "joinmessage.Off");

    // ---- settings ---------------------------------------------------------------------------

    private Prefs Load(CCSPlayerController player)
    {
        if (_prefs.TryGetValue(player.Slot, out var cached)) return cached;

        var loaded = new Prefs();
        try
        {
            var raw = GetPlayerCookie<string>(player.SteamID, CookieKey);
            if (!string.IsNullOrEmpty(raw))
                loaded = JsonSerializer.Deserialize<Prefs>(raw, PrefsJson) ?? loaded;
        }
        catch (Exception)
        {
            // A damaged or unavailable cookie falls back to the defaults.
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
    /// (so a fresh VIP gets a message without opening the menu).
    /// </summary>
    private JoinMessageEntry? Current(CCSPlayerController player, List<JoinMessageEntry> entries, bool leave)
    {
        var prefs = Load(player);
        var saved = leave ? prefs.LeaveName : prefs.JoinName;
        return entries.FirstOrDefault(e => e.Name == saved) ?? entries.FirstOrDefault();
    }

    /// <summary>Messages of the player's group for one category; null when the player has none.</summary>
    private List<JoinMessageEntry>? TryGetEntries(CCSPlayerController player, bool leave)
    {
        if (!IsEligible(player)) return null;

        try
        {
            var entries = GetFeatureValue<List<JoinMessageEntry>>(player)
                .Where(e => e.Name.Length > 0 && e.Message.Length > 0 && e.Leave == leave)
                .ToList();
            return entries.Count > 0 ? entries : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool IsEligible(CCSPlayerController player) =>
        IsClientVip(player) && PlayerHasFeature(player) && GetPlayerFeatureState(player) is FeatureState.Enabled;

    // ---- announcements ----------------------------------------------------------------------

    private HookResult OnConnect(EventPlayerConnectFull ev, GameEventInfo info)
    {
        var player = ev.Userid;
        if (player is { IsValid: true, IsBot: false })
            _plugin.AddTimer(2f, () => TryAnnounceJoin(player, 1), TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    /// <summary>The VIP record is loaded asynchronously, so retry for a few seconds before giving up.</summary>
    private void TryAnnounceJoin(CCSPlayerController player, int attempt)
    {
        if (_stopped || !player.IsValid) return;

        if (IsEligible(player))
        {
            Announce(player, false);
            return;
        }

        if (attempt < MaxAttempts)
            _plugin.AddTimer(2f, () => TryAnnounceJoin(player, attempt + 1), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult OnDisconnect(EventPlayerDisconnect ev, GameEventInfo info)
    {
        var player = ev.Userid;
        if (!_stopped && player is { IsValid: true, IsBot: false } && IsEligible(player))
            Announce(player, true);

        return HookResult.Continue;
    }

    private static readonly Regex ColorTag = new(@"\{([a-zA-Z]+)\}", RegexOptions.Compiled);

    /// <summary>Chat colour characters by lower-case name (red, blue, lightyellow...).</summary>
    private static readonly Dictionary<string, char> Colors = typeof(ChatColors)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(char))
        .ToDictionary(f => f.Name.ToLowerInvariant(), f => (char)f.GetValue(null)!);

    /// <summary>Turns {red}-style tags into chat colour characters; unknown tags are left as they are.</summary>
    private static string ReplaceColorTags(string text) =>
        ColorTag.Replace(text, m => Colors.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var c)
            ? c.ToString()
            : m.Value);

    /// <summary>Text the owner's settings select: their chosen preset, or the localized default.</summary>
    private string BuildMessage(CCSPlayerController owner, bool leave)
    {
        var entries = TryGetEntries(owner, leave);
        var entry = entries == null ? null : Current(owner, entries, leave);

        if (entry == null)
            return GetTranslatedText(leave ? "joinmessage.DefaultLeave" : "joinmessage.DefaultJoin", owner.PlayerName);

        // Colour tags are resolved first so a player name containing "{red}" cannot recolour the line.
        return ReplaceColorTags(entry.Message
                .Replace("{group}", GetClientVipGroup(owner), StringComparison.OrdinalIgnoreCase))
            .Replace("{name}", owner.PlayerName, StringComparison.OrdinalIgnoreCase);
    }

    private void Announce(CCSPlayerController owner, bool leave)
    {
        var prefs = Load(owner);
        if (leave ? !prefs.Leave : !prefs.Join) return;

        var message = BuildMessage(owner, leave);
        var ownerTeam = owner.TeamNum;

        foreach (var viewer in Utilities.GetPlayers())
        {
            if (!viewer.IsValid || viewer.IsBot) continue;

            if (viewer.Slot == owner.Slot)
            {
                // Nobody is left to read their own leave message.
                if (!leave) PrintToChat(viewer, message);
                continue;
            }

            var sameTeam = ownerTeam > 1 && ownerTeam == viewer.TeamNum;

            var ownerAllows = prefs.Mine switch
            {
                MineMode.Team => sameTeam,
                MineMode.Self => false,
                _ => true
            };
            var viewerAllows = Load(viewer).Others switch
            {
                OthersMode.Team => sameTeam,
                OthersMode.None => false,
                _ => true
            };

            if (ownerAllows && viewerAllows)
                PrintToChat(viewer, message);
        }
    }
}
