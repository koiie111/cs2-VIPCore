using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using Microsoft.Extensions.Logging;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_KillIcon;

public class VipKillIcon : BasePlugin
{
    public override string ModuleName => "[VIP] KillIcon";
    public override string ModuleAuthor => "4ill (inspired by ByDexterTR KillIcon)";
    public override string ModuleVersion => "1.0.0";
    private readonly PluginCapability<IVipCoreApi> _capability = new("vipcore:core");
    private IVipCoreApi? _api;
    private KillIcon? _feature;
    private bool _unloaded;

    public override void Load(bool hotReload) => Server.NextWorldUpdate(Initialize);
    public override void OnAllPluginsLoaded(bool hotReload) => Initialize();
    private void Initialize()
    {
        if (_unloaded || _api != null) return;
        _api = _capability.Get();
        if (_api == null) return;
        _api.OnCoreReady += Register;
        Register();
    }
    private void Register()
    {
        if (_unloaded || _api == null || _feature != null) return;
        _feature = new KillIcon(this, _api);
        _api.RegisterFeature(_feature, FeatureType.Selectable);
        foreach (var player in Utilities.GetPlayers())
            if (player is { IsValid: true, IsBot: false } && _api.IsClientVip(player))
                Server.ExecuteCommand($"css_reload_vip_player {player.SteamID}");
        Logger.LogInformation("KillIcon registered with player selection");
    }
    public override void Unload(bool hotReload)
    {
        _unloaded = true;
        if (_api == null) return;
        _api.OnCoreReady -= Register;
        if (_feature == null) return;
        _api.OnPlayerSpawn -= _feature.OnPlayerSpawn;
        _api.PlayerLoaded -= _feature.OnPlayerLoaded;
        _api.PlayerRemoved -= _feature.OnPlayerRemoved;
        _api.UnRegisterFeature(_feature);
        _feature = null;
    }
}

public class KillIcon : VipFeatureBase
{
    public override string Feature => "KillIcon";
    private const string Cookie = "4ill_kill_icon";
    // Real weapon names accepted by CS2's player_death death notice renderer.
    public static readonly Dictionary<string, string> Icons = new()
    {
        ["knife"] = "Нож", ["taser"] = "Zeus", ["hegrenade"] = "Граната HE",
        ["inferno"] = "Огонь", ["awp"] = "AWP", ["ak47"] = "AK-47",
        ["m4a1"] = "M4A4", ["m4a1_silencer"] = "M4A1-S", ["deagle"] = "Desert Eagle",
        ["ssg08"] = "SSG 08", ["p90"] = "P90", ["negev"] = "Negev"
    };
    public KillIcon(BasePlugin plugin, IVipCoreApi api) : base(api) =>
        plugin.RegisterEventHandler<EventPlayerDeath>(OnDeath, HookMode.Pre);

    private bool Allowed(CCSPlayerController player) => player.IsValid && !player.IsBot
        && IsClientVip(player) && PlayerHasFeature(player)
        && GetPlayerFeatureState(player) == FeatureState.Enabled;
    private List<string> Choices(CCSPlayerController player) =>
        GetFeatureValue<List<string>>(player) ?? new();

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (!Allowed(player)) return;
        var menu = CreateMenu("Иконка убийства");
        var current = GetPlayerCookie<string>(player.SteamID, Cookie) ?? "";
        menu.AddMenuOption((current.Length == 0 ? "✓ " : "") + "Стандартная (по оружию)",
            (controller, _) => Select(controller, ""));
        foreach (var icon in Choices(player).Distinct().Where(Icons.ContainsKey))
            menu.AddMenuOption((current == icon ? "✓ " : "") + Icons[icon],
                (controller, _) => Select(controller, icon));
        menu.Open(player);
    }
    private void Select(CCSPlayerController player, string icon)
    {
        if (!Allowed(player) || (icon.Length > 0 && (!Icons.ContainsKey(icon) || !Choices(player).Contains(icon)))) return;
        SetPlayerCookie(player.SteamID, Cookie, icon);
        PrintToChat(player, "Иконка убийства: " + (icon.Length == 0 ? "стандартная" : Icons[icon]));
    }
    private HookResult OnDeath(EventPlayerDeath ev, GameEventInfo info)
    {
        var attacker = ev.Attacker;
        var victim = ev.Userid;
        if (attacker == null || victim == null || !victim.IsValid || attacker.Slot == victim.Slot || !Allowed(attacker))
            return HookResult.Continue;
        var icon = GetPlayerCookie<string>(attacker.SteamID, Cookie);
        if (!string.IsNullOrEmpty(icon) && Icons.ContainsKey(icon) && Choices(attacker).Contains(icon))
            ev.Weapon = icon;
        return HookResult.Continue;
    }
}
