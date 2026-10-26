using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_BuyTeamWeapon;

public class VipBuyTeamWeapon : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] BuyTeamWeapon";
    public override string ModuleVersion => "1.0.3";
    
    private BuyTeamWeapon? _buyTeamWeapon;
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _api = PluginCapability.Get();
        if (_api == null) return;

        _buyTeamWeapon = new BuyTeamWeapon(this, _api);
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

    public BuyTeamWeapon(BasePlugin basePlugin, IVipCoreApi api) : base(api)
    {
        // Players already alive during hot loading have received their spawn bonuses.
        foreach (var player in Utilities.GetPlayers().Where(p => p.IsValid && p.PawnIsAlive))
            if (player.PlayerPawn.Value is { } pawn) _readyPawns[player.Slot] = pawn.Handle;
        basePlugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            if (ev.Userid is { } player) _readyPawns.Remove(player.Slot);
            return HookResult.Continue;
        }, HookMode.Pre);
        basePlugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot => _readyPawns.Remove(slot));
        basePlugin.AddCommand("css_buyteamweapon_status", "Purchase diagnostics", (player, info) =>
        {
            if (player != null) return;
            info.ReplyToCommand($"BuyTeamWeapon 1.0.3 spawn_gate=on account_sync=on ready={_readyPawns.Count}");
        });
        basePlugin.RegisterEventHandler<EventRoundStart>(EventRoundStart);
        
        basePlugin.AddCommand("css_ak47", "buy ak47", (player, _) => BuyWeapon(player, "weapon_ak47", 2700));
        basePlugin.AddCommand("css_m4a1", "buy m4a1", (player, _) => BuyWeapon(player, "weapon_m4a1_silencer", 2900));
        basePlugin.AddCommand("css_m4a4", "buy m4a4", (player, _) => BuyWeapon(player, "weapon_m4a1", 3000));
        basePlugin.AddCommand("css_glock", "buy glock", (player, _) => BuyWeapon(player, "weapon_glock", 200));
        basePlugin.AddCommand("css_usp", "buy usp", (player, _) => BuyWeapon(player, "weapon_usp_silencer", 200));
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
        foreach (var player in Utilities.GetPlayers().Where(u => IsClientVip(u) && PlayerHasFeature(u) && GetFeatureValue<bool>(u) && u.PawnIsAlive))
        {
            PrintToChat(player, 
                GetTranslatedText("buyteamweapon.round_start", 
                    player.Team is CsTeam.Terrorist 
                ? "!m4a1, !m4a4, !glock"
                : "!ak47, !usp"));
        }

        return HookResult.Continue;
    }

    private void BuyWeapon(CCSPlayerController? player, string weaponName, int price)
    {
        if (player is null || !player.IsValid) return;
        
        if (!IsClientVip(player) || !PlayerHasFeature(player) || !GetFeatureValue<bool>(player) || !player.PawnIsAlive)
        {
            PrintToChat(player, GetTranslatedText("vip.NoAccess"));
            return;
        }

        var playerPawn = player.PlayerPawn.Value;
        if (playerPawn is null || !playerPawn.IsValid) return;
        if (!_readyPawns.TryGetValue(player.Slot, out var readyPawn) || readyPawn != playerPawn.Handle)
        {
            player.PrintToChat("[VIP] Дождитесь выдачи стартовых VIP-бонусов и повторите покупку.");
            return;
        }
        if (!playerPawn.InBuyZone)
        {
            PrintToChat(player, GetTranslatedText("buyteamweapon.in_buy_zone"));
            return;
        }
        
        var moneySerivce = player.InGameMoneyServices;
        if (moneySerivce is null) return;

        if (moneySerivce.Account < price)
        {
            PrintToChat(player, GetTranslatedText("buyteamweapon.no_money"));
            return;
        }

        moneySerivce.Account -= price;
        Utilities.SetStateChanged(player, "CCSPlayerController_InGameMoneyServices", "m_iAccount");
        
        player.GiveNamedItem(weaponName);
    }
}