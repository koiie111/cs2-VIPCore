extern alias RealCss;

using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using VIP_BuyTeamWeapon;
using VipCoreApi;
using JsonStringLocalizer = RealCss::CounterStrikeSharp.API.Core.Translations.JsonStringLocalizer;

CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

(string Command, string Entity, CsTeam Team, int Price)[] catalog =
[
    ("glock", "weapon_glock", CsTeam.Terrorist, 200),
    ("tec9", "weapon_tec9", CsTeam.Terrorist, 500),
    ("mac10", "weapon_mac10", CsTeam.Terrorist, 1050),
    ("sawedoff", "weapon_sawedoff", CsTeam.Terrorist, 1100),
    ("galil", "weapon_galilar", CsTeam.Terrorist, 1800),
    ("ak47", "weapon_ak47", CsTeam.Terrorist, 2700),
    ("sg553", "weapon_sg556", CsTeam.Terrorist, 3000),
    ("g3sg1", "weapon_g3sg1", CsTeam.Terrorist, 5000),
    ("usp", "weapon_usp_silencer", CsTeam.CounterTerrorist, 200),
    ("p2000", "weapon_hkp2000", CsTeam.CounterTerrorist, 200),
    ("fiveseven", "weapon_fiveseven", CsTeam.CounterTerrorist, 500),
    ("mp9", "weapon_mp9", CsTeam.CounterTerrorist, 1250),
    ("mag7", "weapon_mag7", CsTeam.CounterTerrorist, 1300),
    ("famas", "weapon_famas", CsTeam.CounterTerrorist, 1950),
    ("m4a1", "weapon_m4a1_silencer", CsTeam.CounterTerrorist, 2900),
    ("m4a4", "weapon_m4a1", CsTeam.CounterTerrorist, 2900),
    ("aug", "weapon_aug", CsTeam.CounterTerrorist, 3300),
    ("scar20", "weapon_scar20", CsTeam.CounterTerrorist, 5000),
];

var checks = 0;
void Check(bool condition, string scenario)
{
    checks++;
    if (!condition) throw new Exception($"Check {checks} failed: {scenario}");
}

Utilities.FailGetPlayers = true;
var startupPlugin = new BasePlugin();
var startupFeature = new BuyTeamWeapon(startupPlugin, new FakeApi(), false);
Check(startupPlugin.Commands.Keys.ToHashSet().SetEquals(
    catalog.Select(weapon => $"css_{weapon.Command}").Append("css_buyteamweapon_status")), "command registration");
startupFeature.Stop();
Utilities.FailGetPlayers = false;

foreach (var moneyFirst in new[] { true, false })
foreach (var team in new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist })
foreach (var (command, entity, _, price) in catalog)
{
    var api = new FakeApi();
    var plugin = new BasePlugin();
    var player = new CCSPlayerController { Team = team };
    var feature = new BuyTeamWeapon(plugin, api, false);
    var money = player.InGameMoneyServices!;
    var pawn = player.PlayerPawn.Value!;
    string scenario = $"{team} !{command} MoneyFirst={moneyFirst}";

    void Buy() => plugin.Commands[$"css_{command}"](player, new());
    void NoPurchase(int balance, int given, string reason)
    {
        money.Account = balance;
        Utilities.LastSchema = "";
        Buy();
        Check(player.Given == given && money.Account == balance && Utilities.LastSchema == "",
            $"{scenario}, {reason}");
    }

    plugin.Spawn(new() { Userid = player });
    NoPurchase(10000, 0, "before VIP spawn");
    // Start at a different balance so either order exercises the Money reset.
    money.Account = 800;
    if (moneyFirst) money.Account = 10000;
    api.Spawn(player);
    if (!moneyFirst) money.Account = 10000;
    NoPurchase(10000, 0, "waiting for next frame");

    Server.Drain();
    Buy();
    Check(player.Given == 1 && player.LastWeapon == entity && money.Account == 10000 - price, scenario);
    Check(Utilities.LastSchema == "CCSPlayerController_InGameMoneyServices.m_iAccount", "HUD sync");

    NoPurchase(price - 1, 1, "insufficient funds");
    money.Account = price;
    Buy();
    Check(player.Given == 2 && money.Account == 0, $"{scenario}, exact funds");

    player.RejectWeapon = true;
    NoPurchase(price, 2, "engine rejected weapon");
    Check(player.Chat.Last() == "Оружие не выдано. Деньги не списаны.", "failure message");
    player.RejectWeapon = false;
    pawn.InBuyZone = false;
    NoPurchase(10000, 2, "outside buy zone");
    pawn.InBuyZone = true;
    player.PawnIsAlive = false;
    NoPurchase(10000, 2, "dead");
    player.PawnIsAlive = true;
    player.IsVip = false;
    NoPurchase(10000, 2, "no VIP status");
    player.IsVip = true;
    player.HasFeature = false;
    NoPurchase(10000, 2, "feature absent");
    player.HasFeature = true;
    player.FeatureEnabled = false;
    NoPurchase(10000, 2, "feature disabled");
    player.FeatureEnabled = true;

    plugin.Spawn(new() { Userid = player });
    NoPurchase(10000, 2, "repeat spawn");
    api.Spawn(player);
    feature.Stop();
    Server.Drain();
    NoPurchase(10000, 2, "module unloaded");
    Check(api.Subscribers == 0, "API subscriptions removed");
}

var roundPlugin = new BasePlugin();
var roundFeature = new BuyTeamWeapon(roundPlugin, new FakeApi(), false);
var terrorist = new CCSPlayerController { Team = CsTeam.Terrorist };
var counterTerrorist = new CCSPlayerController { Team = CsTeam.CounterTerrorist };
var excluded = new CCSPlayerController[]
{
    new() { Team = CsTeam.Spectator },
    new() { PawnIsAlive = false },
    new() { FeatureEnabled = false },
    new() { IsValid = false },
};
Utilities.Players = [terrorist, counterTerrorist, ..excluded];

foreach (var (locale, heading) in new[]
{
    ("en", "Оружие противника: "),
    ("ru", "Оружие противника: "),
    ("de", "Waffen des Gegners: "),
    ("pt-BR", "Armas do time adversário: "),
    ("ro", "Armele echipei adverse: "),
    ("uk", "Зброя супротивника: "),
})
{
    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(locale);
    roundPlugin.RoundStart(new());

    foreach (var player in new[] { terrorist, counterTerrorist })
    {
        var commands = catalog.Where(weapon => weapon.Team != player.Team).Select(weapon => $"!{weapon.Command}");
        Check(player.Chat.Last() == heading + string.Join(", ", commands), $"{locale} hint for {player.Team}");
    }
    Check(excluded.All(player => player.Chat.Count == 0), $"{locale} hint eligibility");
}
roundFeature.Stop();
Console.WriteLine($"PASS: {checks} checks for command registration, purchases, spawn gating and localized round hints.");

namespace CounterStrikeSharp.API.Modules.Commands { public class CommandInfo {public void ReplyToCommand(string s){} } }
namespace CounterStrikeSharp.API.Modules.Utils { public enum CsTeam{Spectator,Terrorist,CounterTerrorist} }
namespace CounterStrikeSharp.API.Core.Capabilities { public class PluginCapability<T>(string name) {public T? Get(){_ = name; return default;}} }
namespace CounterStrikeSharp.API.Core {
 using CounterStrikeSharp.API.Modules.Commands;
 using CounterStrikeSharp.API.Modules.Utils;
 public enum HookResult{Continue} public enum HookMode{Pre,Post}
 public class GameEventInfo{}
 public class EventRoundStart{}
 public class EventPlayerSpawn{public CCSPlayerController? Userid{get;set;}}
 public static class Listeners {public delegate void OnClientDisconnectPost(int slot);}
 public class BasePlugin {
 public virtual string ModuleAuthor=>"";public virtual string ModuleName=>"";public virtual string ModuleVersion=>"";
 public virtual void OnAllPluginsLoaded(bool b){} public virtual void Unload(bool b){}
 public Dictionary<string,Action<CCSPlayerController?,CommandInfo>> Commands=new();
 public Action<EventPlayerSpawn> Spawn=_=>{};
 public Action<EventRoundStart> RoundStart=_=>{};
 public JsonStringLocalizer Localizer = new(Path.Combine(AppContext.BaseDirectory, "lang"));
 public void AddCommand(string name,string desc,Action<CCSPlayerController?,CommandInfo> f)=>Commands[name]=f;
 public void RegisterEventHandler<T>(Func<T,GameEventInfo,HookResult> f,HookMode mode=HookMode.Post)
 {
     if (typeof(T)==typeof(EventPlayerSpawn)) Spawn=e=>f((T)(object)e,new());
     if (typeof(T)==typeof(EventRoundStart)) RoundStart=e=>f((T)(object)e,new());
 }
 public void RegisterListener<T>(T f) where T:Delegate{}
 }
 public class Pawn {public IntPtr Handle=new(123); public bool InBuyZone=true;public bool IsValid=true;}
 public class PawnRef{public Pawn? Value=new();}
 public class Money {public int Account=10000;}
 public class CCSPlayerController {
 public bool IsValid=true,PawnIsAlive=true;public int Slot=1,Given; public CsTeam Team=CsTeam.Terrorist;
 public PawnRef PlayerPawn=new();public Money? InGameMoneyServices=new();
 public bool IsVip = true, HasFeature = true, FeatureEnabled = true, RejectWeapon;
 public string? LastWeapon;
 public List<string> Chat = new();
 public IntPtr GiveNamedItem(string name)
 {
     if (RejectWeapon) return IntPtr.Zero;
     LastWeapon = name;
     Given++;
     return new IntPtr(1);
 }
 public void PrintToChat(string s) => Chat.Add(s);
 }
}
namespace CounterStrikeSharp.API {
 using CounterStrikeSharp.API.Core;
 public static class Utilities {public static string LastSchema="";public static bool FailGetPlayers; public static List<CCSPlayerController> Players = new(); public static List<CCSPlayerController> GetPlayers()=>FailGetPlayers ? throw new Exception("Global Variables not initialized yet") : Players;public static void SetStateChanged(CCSPlayerController p,string c,string f)=>LastSchema=c+"."+f;}
 public static class Server {static Queue<Action> q=new();public static void NextFrame(Action f)=>q.Enqueue(f); public static void Drain(){while(q.TryDequeue(out var f))f();}}
}
namespace VipCoreApi {
 using CounterStrikeSharp.API.Core;
 public interface IVipCoreApi {
 public enum FeatureType{Hide}
 event Action<CCSPlayerController>? OnPlayerSpawn;
 event Action<CCSPlayerController,string>? PlayerLoaded;
 event Action<CCSPlayerController,string>? PlayerRemoved;
 void RegisterFeature(VipFeatureBase f,FeatureType t);void UnRegisterFeature(VipFeatureBase f);
 }
 public class FakeApi:IVipCoreApi {
 public event Action<CCSPlayerController>? OnPlayerSpawn;
 public event Action<CCSPlayerController,string>? PlayerLoaded;
 public event Action<CCSPlayerController,string>? PlayerRemoved;
 public int Subscribers=>(OnPlayerSpawn?.GetInvocationList().Length??0)+(PlayerLoaded?.GetInvocationList().Length??0)+(PlayerRemoved?.GetInvocationList().Length??0);
 public void Spawn(CCSPlayerController p)=>OnPlayerSpawn?.Invoke(p);
 public void RegisterFeature(VipFeatureBase f,IVipCoreApi.FeatureType t){} public void UnRegisterFeature(VipFeatureBase f){}
 }
 public abstract class VipFeatureBase {
 public IVipCoreApi Api;public abstract string Feature{get;}
 protected VipFeatureBase(IVipCoreApi api){Api=api;api.OnPlayerSpawn+=OnPlayerSpawn;api.PlayerLoaded+=OnPlayerLoaded;api.PlayerRemoved+=OnPlayerRemoved;}
 public virtual void OnPlayerSpawn(CCSPlayerController p){}public virtual void OnPlayerLoaded(CCSPlayerController p,string s){}public virtual void OnPlayerRemoved(CCSPlayerController p,string s){}
 public bool IsClientVip(CCSPlayerController p)=>p.IsVip;public bool PlayerHasFeature(CCSPlayerController p)=>p.HasFeature;
 public T GetFeatureValue<T>(CCSPlayerController p)=>(T)(object)p.FeatureEnabled;
 public string GetTranslatedText(string s,params object[] x)=>s;public void PrintToChat(CCSPlayerController p,string s)=>p.PrintToChat(s);
 }
}
