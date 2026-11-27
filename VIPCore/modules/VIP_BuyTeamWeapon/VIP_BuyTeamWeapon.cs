using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_BuyTeamWeapon;

public class VipBuyTeamWeapon : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] BuyTeamWeapon";
    public override string ModuleVersion => "1.0.5";

    private BuyTeamWeapon? _buyTeamWeapon;
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _buyTeamWeapon = new BuyTeamWeapon(this, _api, hotReload);
        _api.RegisterFeature(_buyTeamWeapon, FeatureType.Hide);
    }

    public override void Unload(bool hotReload)
    {
        if (_buyTeamWeapon != null)
        {
            _buyTeamWeapon.Stop();
            _api?.UnRegisterFeature(_buyTeamWeapon);
        }
    }
}

public class BuyTeamWeapon : VipFeatureBase
{
    public override string Feature => "BuyTeamWeapon";
    private sealed record Weapon(string Command, string EntityName, CsTeam NativeTeam, int Price);

    private static readonly Weapon[] Weapons =
    [
        new("glock", "weapon_glock", CsTeam.Terrorist, 200),
        new("tec9", "weapon_tec9", CsTeam.Terrorist, 500),
        new("mac10", "weapon_mac10", CsTeam.Terrorist, 1050),
        new("sawedoff", "weapon_sawedoff", CsTeam.Terrorist, 1100),
        new("galil", "weapon_galilar", CsTeam.Terrorist, 1800),
        new("ak47", "weapon_ak47", CsTeam.Terrorist, 2700),
        new("sg553", "weapon_sg556", CsTeam.Terrorist, 3000),
        new("g3sg1", "weapon_g3sg1", CsTeam.Terrorist, 5000),
        new("usp", "weapon_usp_silencer", CsTeam.CounterTerrorist, 200),
        new("p2000", "weapon_hkp2000", CsTeam.CounterTerrorist, 200),
        new("fiveseven", "weapon_fiveseven", CsTeam.CounterTerrorist, 500),
        new("mp9", "weapon_mp9", CsTeam.CounterTerrorist, 1250),
        new("mag7", "weapon_mag7", CsTeam.CounterTerrorist, 1300),
        new("famas", "weapon_famas", CsTeam.CounterTerrorist, 1950),
        new("m4a1", "weapon_m4a1_silencer", CsTeam.CounterTerrorist, 2900),
        new("m4a4", "weapon_m4a1", CsTeam.CounterTerrorist, 2900),
        new("aug", "weapon_aug", CsTeam.CounterTerrorist, 3300),
        new("scar20", "weapon_scar20", CsTeam.CounterTerrorist, 5000),
    ];

    private static readonly string TerroristCommands = ListCommands(CsTeam.Terrorist);
    private static readonly string CounterTerroristCommands = ListCommands(CsTeam.CounterTerrorist);

    private static string ListCommands(CsTeam team) =>
        string.Join(", ", Weapons.Where(weapon => weapon.NativeTeam == team).Select(weapon => $"!{weapon.Command}"));

    private readonly BasePlugin _plugin;
    private readonly Dictionary<int, IntPtr> _readyPawns = new();
    private bool _stopped;

    public void Stop()
    {
        _stopped = true;
        Api.OnPlayerSpawn -= OnPlayerSpawn;
        Api.PlayerLoaded -= OnPlayerLoaded;
        Api.PlayerRemoved -= OnPlayerRemoved;
        _readyPawns.Clear();
    }

    public BuyTeamWeapon(BasePlugin basePlugin, IVipCoreApi api, bool hotReload) : base(api)
    {
        _plugin = basePlugin;

        // Players already alive during hot loading have received their spawn bonuses.
        if (hotReload)
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (player.IsValid && player.PawnIsAlive && player.PlayerPawn.Value is { IsValid: true } pawn)
                    _readyPawns[player.Slot] = pawn.Handle;
            }
        }

        basePlugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            if (ev.Userid is { } player) _readyPawns.Remove(player.Slot);
            return HookResult.Continue;
        }, HookMode.Pre);
        basePlugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot => _readyPawns.Remove(slot));
        basePlugin.AddCommand("css_buyteamweapon_status", "Purchase diagnostics", (player, info) =>
        {
            if (player != null) return;
            info.ReplyToCommand($"BuyTeamWeapon {basePlugin.ModuleVersion} spawn_gate=on account_sync=on ready={_readyPawns.Count}");
        });
        basePlugin.RegisterEventHandler<EventRoundStart>(EventRoundStart);

        foreach (var weapon in Weapons)
            basePlugin.AddCommand($"css_{weapon.Command}", $"Buy {weapon.Command}", (player, _) => BuyWeapon(player, weapon));
    }

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        var pawnHandle = player.PlayerPawn.Value?.Handle;
        // VIPCore invokes all spawn features synchronously. Wait until Money and
        // every other feature have finished, independent of subscription order.
        Server.NextFrame(() =>
        {
            if (!_stopped && player.IsValid && player.PawnIsAlive && pawnHandle.HasValue &&
                player.PlayerPawn.Value?.Handle == pawnHandle.Value)
                _readyPawns[player.Slot] = pawnHandle.Value;
        });
    }

    private HookResult EventRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_stopped) return HookResult.Continue;

        string? terroristHint = null;
        string? counterTerroristHint = null;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || !player.PawnIsAlive) continue;

            var team = player.Team;
            if (team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist) || !CanBuyTeamWeapon(player)) continue;

            var hint = team == CsTeam.Terrorist
                ? (terroristHint ??= _plugin.Localizer["buyteamweapon.round_start", CounterTerroristCommands])
                : (counterTerroristHint ??= _plugin.Localizer["buyteamweapon.round_start", TerroristCommands]);
            PrintToChat(player, hint);
        }

        return HookResult.Continue;
    }

    private bool CanBuyTeamWeapon(CCSPlayerController player) =>
        IsClientVip(player) && PlayerHasFeature(player) && GetFeatureValue<bool>(player);

    private void BuyWeapon(CCSPlayerController? player, Weapon weapon)
    {
        if (_stopped || player is null || !player.IsValid) return;

        if (!player.PawnIsAlive || !CanBuyTeamWeapon(player))
        {
            PrintToChat(player, GetTranslatedText("vip.NoAccess"));
            return;
        }

        var playerPawn = player.PlayerPawn.Value;
        if (playerPawn is null || !playerPawn.IsValid) return;
        if (!_readyPawns.TryGetValue(player.Slot, out var readyPawn) || readyPawn != playerPawn.Handle)
        {
            PrintToChat(player, _plugin.Localizer["buyteamweapon.wait_for_spawn"]);
            return;
        }
        if (!playerPawn.InBuyZone)
        {
            PrintToChat(player, _plugin.Localizer["buyteamweapon.in_buy_zone"]);
            return;
        }

        var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault(proxy => proxy.IsValid)?.GameRules;
        if (gameRules is null)
        {
            PrintToChat(player, _plugin.Localizer["buyteamweapon.give_failed"]);
            return;
        }
        if (gameRules.BuyTimeEnded)
        {
            PrintToChat(player, _plugin.Localizer["buyteamweapon.buy_time_ended"]);
            return;
        }

        var moneyServices = player.InGameMoneyServices;
        if (moneyServices is null) return;

        if (moneyServices.Account < weapon.Price)
        {
            PrintToChat(player, _plugin.Localizer["buyteamweapon.no_money"]);
            return;
        }

        if (player.GiveNamedItem(weapon.EntityName) == IntPtr.Zero)
        {
            PrintToChat(player, _plugin.Localizer["buyteamweapon.give_failed"]);
            return;
        }

        moneyServices.Account -= weapon.Price;
        Utilities.SetStateChanged(player, "CCSPlayerController_InGameMoneyServices", "m_iAccount");
    }
}
