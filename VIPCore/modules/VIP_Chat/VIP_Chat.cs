using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Chat;

public class VipChat : BasePlugin
{
    public override string ModuleAuthor => "ByDexterTR (original), ported for cs2-VIPCore";
    public override string ModuleName => "[VIP] Chat";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription => "Private chat between VIP players";

    private Chat? _feature;
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
        _feature = new Chat(this, _api);
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

/// <summary>
/// The "VipChat" feature value from vip.json. Every field is optional.
/// </summary>
public class ChatConfig
{
    /// <summary>Label shown before the name. Empty = no label.</summary>
    [JsonPropertyName("tag")] public string Tag { get; set; } = "[VIP]";

    /// <summary>Characters a message must start with to go to the VIP chat. Players pick one in the menu.</summary>
    [JsonPropertyName("prefixes")] public List<string> Prefixes { get; set; } = new() { "#" };

    /// <summary>Chat colors players may choose from (names from the palette). Empty = the whole palette.</summary>
    [JsonPropertyName("colors")] public List<string> Colors { get; set; } = new();
}

public class Chat : VipFeatureBase
{
    public const string FeatureName = "VipChat";
    private const string CookieKey = "vip_chat_prefs";
    private const int SayText2 = 118;
    private const int MaxLength = 200;

    private static readonly string[] DefaultPrefixes = { "#" };

    /// <summary>Colors a player can pick. Order = order in the menu.</summary>
    private static readonly (string Name, string Code)[] Palette =
    {
        ("White", ChatColors.White.ToString()), ("Grey", ChatColors.Grey.ToString()),
        ("Red", ChatColors.Red.ToString()), ("LightRed", ChatColors.LightRed.ToString()),
        ("Orange", ChatColors.Orange.ToString()), ("Yellow", ChatColors.Yellow.ToString()),
        ("Gold", ChatColors.Gold.ToString()), ("Lime", ChatColors.Lime.ToString()),
        ("Green", ChatColors.Green.ToString()), ("LightBlue", ChatColors.LightBlue.ToString()),
        ("Blue", ChatColors.Blue.ToString()), ("Purple", ChatColors.Purple.ToString()),
        ("Magenta", ChatColors.Magenta.ToString())
    };

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Which messages of the player go to the VIP chat.</summary>
    private enum SendMode { Prefix, Always }

    /// <summary>Which VIPs receive the player's messages.</summary>
    private enum Scope { All, Team }

    /// <summary>Per-player settings, stored as one JSON cookie. A null value means "use the default".</summary>
    private class Prefs
    {
        /// <summary>Master switch: off = no sending and no receiving.</summary>
        [JsonPropertyName("e")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("p")] public string? Prefix { get; set; }
        [JsonPropertyName("m")] public SendMode Mode { get; set; } = SendMode.Prefix;
        [JsonPropertyName("s")] public Scope Scope { get; set; } = Scope.All;
        [JsonPropertyName("t")] public bool ShowTag { get; set; } = true;
        [JsonPropertyName("ct")] public string? TagColor { get; set; }
        [JsonPropertyName("cn")] public string? NameColor { get; set; }
        [JsonPropertyName("cm")] public string? MessageColor { get; set; }
    }

    public override string Feature => FeatureName;

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, Prefs> _prefs = new();
    private bool _stopped;

    public Chat(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;

        // Hooking the user message (not the say command) keeps gag/mute plugins in charge: a muted
        // player's message never reaches this hook.
        plugin.HookUserMessage(SayText2, OnChat, HookMode.Pre);
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot => _prefs.Remove(slot));
    }

    /// <summary>Called on plugin unload.</summary>
    public void Stop()
    {
        _stopped = true;
        _plugin.UnhookUserMessage(SayText2, OnChat, HookMode.Pre);
        _prefs.Clear();
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group) => _prefs.Remove(player.Slot);

    public override void OnPlayerRemoved(CCSPlayerController player, string group) => _prefs.Remove(player.Slot);

    // ---- chat -------------------------------------------------------------------------------

    private HookResult OnChat(UserMessage um)
    {
        if (_stopped) return HookResult.Continue;

        var author = Utilities.GetPlayerFromIndex(um.ReadInt("entityindex"));
        if (author is not { IsValid: true } || author.IsBot) return HookResult.Continue;

        var config = TryGetConfig(author);
        if (config == null) return HookResult.Continue;

        var prefs = Load(author);
        if (!prefs.Enabled) return HookResult.Continue;

        var message = um.ReadString("param2");
        if (string.IsNullOrWhiteSpace(message) || IsChatTrigger(message)) return HookResult.Continue;

        var text = message.Trim();
        if (prefs.Mode == SendMode.Prefix)
        {
            var prefix = CurrentPrefix(prefs, config);
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return HookResult.Continue;
            text = text[prefix.Length..].Trim();
        }

        text = Sanitize(text);
        if (text.Length == 0) return HookResult.Stop;

        var colors = Colors(config);
        var tag = prefs.ShowTag && config.Tag.Length > 0
            ? $"{ColorOf(prefs.TagColor, "Purple", colors)}{Sanitize(config.Tag)} "
            : "";
        var name = $" {tag}{ColorOf(prefs.NameColor, "Gold", colors)}{author.PlayerName}";
        var body = $"{ColorOf(prefs.MessageColor, "Green", colors)}{text}";

        um.SetString("param1", name);
        um.SetString("param2", body);
        um.SetString("messagename", $"{name}{ChatColors.Default}: {body}");

        var authorTeam = author.TeamNum;
        for (var i = um.Recipients.Count - 1; i >= 0; i--)
        {
            var recipient = um.Recipients[i];
            if (recipient == null || !recipient.IsValid)
            {
                um.Recipients.RemoveAt(i);
                continue;
            }

            if (recipient.Slot == author.Slot) continue;

            var allowed = IsClientVip(recipient) && TryGetConfig(recipient) != null && Load(recipient).Enabled
                          && (prefs.Scope == Scope.All || recipient.TeamNum == authorTeam);
            if (!allowed) um.Recipients.RemoveAt(i);
        }

        return HookResult.Changed;
    }

    /// <summary>Strips control characters (they would let a player inject chat colors) and cuts the length.</summary>
    private static string Sanitize(string text)
    {
        var clean = new string(text.Where(c => c >= ' ').ToArray()).Trim();
        return clean.Length > MaxLength ? clean[..MaxLength] : clean;
    }

    /// <summary>True when the message is a chat command (core.json PublicChatTrigger / SilentChatTrigger).</summary>
    private static bool IsChatTrigger(string message)
    {
        var trimmed = message.TrimStart();
        foreach (var trigger in CoreConfig.PublicChatTrigger.Concat(CoreConfig.SilentChatTrigger))
            if (!string.IsNullOrEmpty(trigger) && trimmed.StartsWith(trigger, StringComparison.Ordinal))
                return true;
        return false;
    }

    // ---- menu -------------------------------------------------------------------------------

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped) return;

        var config = TryGetConfig(player);
        if (config == null) return;

        var prefs = Load(player);
        var colors = Colors(config);
        var prefixes = Prefixes(config);
        var menu = CreateMenu(GetTranslatedText(Feature));

        // Every line below cycles its value on click, saves it and reopens the menu with the new value.
        menu.AddMenuOption(GetTranslatedText("vipchat.Enabled", OnOff(prefs.Enabled)), (controller, _) =>
        {
            var p = Load(controller);
            p.Enabled = !p.Enabled;
            Save(controller);
            OnSelectItem(controller, state);
        });

        menu.AddMenuOption(GetTranslatedText("vipchat.Mode", GetTranslatedText($"vipchat.mode.{prefs.Mode}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Mode = p.Mode == SendMode.Prefix ? SendMode.Always : SendMode.Prefix;
                Save(controller);
                OnSelectItem(controller, state);
            });

        // The prefix only matters in prefix mode; with a single configured prefix there is nothing to pick.
        if (prefs.Mode == SendMode.Prefix && prefixes.Length > 1)
        {
            menu.AddMenuOption(GetTranslatedText("vipchat.Prefix", CurrentPrefix(prefs, config)),
                (controller, _) =>
                {
                    var p = Load(controller);
                    var index = Array.IndexOf(prefixes, CurrentPrefix(p, config));
                    p.Prefix = prefixes[(index + 1) % prefixes.Length];
                    Save(controller);
                    OnSelectItem(controller, state);
                });
        }

        menu.AddMenuOption(
            GetTranslatedText("vipchat.Scope", GetTranslatedText($"vipchat.scope.{prefs.Scope}")),
            (controller, _) =>
            {
                var p = Load(controller);
                p.Scope = p.Scope == Scope.All ? Scope.Team : Scope.All;
                Save(controller);
                OnSelectItem(controller, state);
            });

        if (config.Tag.Length > 0)
        {
            menu.AddMenuOption(GetTranslatedText("vipchat.ShowTag", YesNo(prefs.ShowTag)), (controller, _) =>
            {
                var p = Load(controller);
                p.ShowTag = !p.ShowTag;
                Save(controller);
                OnSelectItem(controller, state);
            });

            if (prefs.ShowTag)
                AddColorOption(menu, "vipchat.TagColor", colors, prefs.TagColor, "Purple", state,
                    (p, name) => p.TagColor = name);
        }

        AddColorOption(menu, "vipchat.NameColor", colors, prefs.NameColor, "Gold", state,
            (p, name) => p.NameColor = name);
        AddColorOption(menu, "vipchat.MessageColor", colors, prefs.MessageColor, "Green", state,
            (p, name) => p.MessageColor = name);

        menu.AddMenuOption(GetTranslatedText("vipchat.Reset"), (controller, _) =>
        {
            _prefs[controller.Slot] = new Prefs();
            Save(controller);
            OnSelectItem(controller, state);
        });

        menu.Open(player);
    }

    /// <summary>A menu line that steps to the next allowed color on click.</summary>
    private void AddColorOption(IMenu menu, string key, (string Name, string Code)[] colors, string? current,
        string fallback, FeatureState state, Action<Prefs, string> set)
    {
        var active = ResolveColor(current, fallback, colors).Name;
        menu.AddMenuOption(GetTranslatedText(key, GetTranslatedText($"vipchat.color.{active}")), (controller, _) =>
        {
            var p = Load(controller);
            var index = Array.FindIndex(colors, c => c.Name == active);
            set(p, colors[(index + 1) % colors.Length].Name);
            Save(controller);
            OnSelectItem(controller, state);
        });
    }

    private string OnOff(bool value) => GetTranslatedText(value ? "vipchat.On" : "vipchat.Off");

    private string YesNo(bool value) => GetTranslatedText(value ? "vipchat.Yes" : "vipchat.No");

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

        if (!Enum.IsDefined(loaded.Mode)) loaded.Mode = SendMode.Prefix;
        if (!Enum.IsDefined(loaded.Scope)) loaded.Scope = Scope.All;

        _prefs[player.Slot] = loaded;
        return loaded;
    }

    private void Save(CCSPlayerController player) =>
        SetPlayerCookie(player.SteamID, CookieKey, JsonSerializer.Serialize(Load(player), PrefsJson));

    private ChatConfig? TryGetConfig(CCSPlayerController player)
    {
        if (!IsClientVip(player) || !PlayerHasFeature(player)) return null;
        if (GetPlayerFeatureState(player) is not FeatureState.Enabled) return null;

        try
        {
            return GetFeatureValue<ChatConfig>(player);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string[] Prefixes(ChatConfig config)
    {
        var list = config.Prefixes?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct()
            .ToArray();
        return list is { Length: > 0 } ? list : DefaultPrefixes;
    }

    /// <summary>The prefix the player picked if the group still offers it, otherwise the first configured one.</summary>
    private static string CurrentPrefix(Prefs prefs, ChatConfig config)
    {
        var prefixes = Prefixes(config);
        return prefs.Prefix != null && prefixes.Contains(prefs.Prefix) ? prefs.Prefix : prefixes[0];
    }

    private static (string Name, string Code)[] Colors(ChatConfig config)
    {
        if (config.Colors == null || config.Colors.Count == 0) return Palette;
        var allowed = Palette.Where(c => config.Colors.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        return allowed.Length > 0 ? allowed : Palette;
    }

    /// <summary>The saved color if the group still offers it, otherwise the default, otherwise the first allowed.</summary>
    private static (string Name, string Code) ResolveColor(string? saved, string fallback,
        (string Name, string Code)[] colors)
    {
        foreach (var name in new[] { saved, fallback })
        {
            if (name == null) continue;
            var found = colors.FirstOrDefault(c => c.Name == name);
            if (found.Name != null) return found;
        }

        return colors[0];
    }

    private static string ColorOf(string? saved, string fallback, (string Name, string Code)[] colors) =>
        ResolveColor(saved, fallback, colors).Code;
}
