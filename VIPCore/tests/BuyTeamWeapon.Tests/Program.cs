using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using VIP_BuyTeamWeapon;
using VipCoreApi;
var checks=0;
void Check(bool value) { checks++; if(!value) throw new Exception($"Check {checks} failed"); }
foreach(var moneyFirst in new[]{true,false})
foreach(var price in new[]{("css_ak47",2700),("css_m4a1",2900),("css_m4a4",3000),("css_glock",200),("css_usp",200)})
{
 var api=new FakeApi(); var plugin=new BasePlugin(); var p=new CCSPlayerController();
 var feature=new BuyTeamWeapon(plugin,api);
 void Buy()=>plugin.Commands[price.Item1](p,new());
 plugin.Spawn(new(){Userid=p});
 Buy(); Check(p.Given==0 && p.InGameMoneyServices!.Account==10000);
 if(moneyFirst)p.InGameMoneyServices!.Account=10000;
 api.Spawn(p);
 if(!moneyFirst)p.InGameMoneyServices!.Account=10000;
 Buy();Check(p.Given==0);
 Server.Drain();Buy();Check(p.Given==1 && p.InGameMoneyServices!.Account==10000-price.Item2);
 Check(Utilities.LastSchema=="CCSPlayerController_InGameMoneyServices.m_iAccount");
 p.InGameMoneyServices!.Account=price.Item2-1;Buy();Check(p.Given==1);
 p.InGameMoneyServices.Account=price.Item2;Buy();Check(p.Given==2 && p.InGameMoneyServices.Account==0);
 p.InGameMoneyServices.Account=10000;p.PlayerPawn.Value!.InBuyZone=false;Buy();Check(p.Given==2 && p.InGameMoneyServices.Account==10000);
 p.PlayerPawn.Value.InBuyZone=true;p.PawnIsAlive=false;Buy();Check(p.Given==2 && p.InGameMoneyServices.Account==10000);p.PawnIsAlive=true;
 plugin.Spawn(new(){Userid=p});p.InGameMoneyServices.Account=10000;Buy();Check(p.Given==2);
 api.Spawn(p);feature.Stop();Server.Drain();Buy();Check(p.Given==2);
 Check(api.Subscribers==0);
}
Console.WriteLine($"PASS: {checks} checks executing real BuyTeamWeapon source; five commands, both Money subscription orders, early purchase, repeat spawn, exact/insufficient funds, unload.");
namespace CounterStrikeSharp.API.Modules.Commands { public class CommandInfo {public void ReplyToCommand(string s){} } }
namespace CounterStrikeSharp.API.Modules.Utils { public enum CsTeam{Terrorist,CounterTerrorist} }
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
 public void AddCommand(string name,string desc,Action<CCSPlayerController?,CommandInfo> f)=>Commands[name]=f;
 public void RegisterEventHandler<T>(Func<T,GameEventInfo,HookResult> f,HookMode mode=HookMode.Post){if(typeof(T)==typeof(EventPlayerSpawn)) Spawn=e=>f((T)(object)e,new());}
 public void RegisterListener<T>(T f) where T:Delegate{}
 }
 public class Pawn {public IntPtr Handle=new(123); public bool InBuyZone=true;public bool IsValid=true;}
 public class PawnRef{public Pawn? Value=new();}
 public class Money {public int Account=10000;}
 public class CCSPlayerController {
 public bool IsValid=true,PawnIsAlive=true;public int Slot=1,Given; public CsTeam Team=CsTeam.Terrorist;
 public PawnRef PlayerPawn=new();public Money? InGameMoneyServices=new();
 public void GiveNamedItem(string name)=>Given++;public void PrintToChat(string s){}
 }
}
namespace CounterStrikeSharp.API {
 using CounterStrikeSharp.API.Core;
 public static class Utilities {public static string LastSchema="";public static List<CCSPlayerController> GetPlayers()=>new();public static void SetStateChanged(CCSPlayerController p,string c,string f)=>LastSchema=c+"."+f;}
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
 public bool IsClientVip(CCSPlayerController p)=>true;public bool PlayerHasFeature(CCSPlayerController p)=>true;
 public T GetFeatureValue<T>(CCSPlayerController p)=>(T)(object)true;
 public string GetTranslatedText(string s,params object[] x)=>s;public void PrintToChat(CCSPlayerController p,string s){}
 }
}
