using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;

namespace CounterStrikeSharp.API
{
    static class Server
    {
        public static Action? Pending;
        public static void NextWorldUpdate(Action callback) => Pending = callback;
    }
}

namespace CounterStrikeSharp.API.Core
{
    public sealed class CCSPlayerController
    {
        public bool IsValid = true;
        public nint Handle;
        public (uint Raw, int Slot) EntityHandle;
        public CsTeam Team = CsTeam.Terrorist;
        public bool PawnIsAlive;
        public PawnHandle PlayerPawn = new();
        public int PawnAssignments;
        public void SetPawn(Pawn pawn)
        {
            if (pawn != PlayerPawn.Value || !pawn.IsValid) throw new InvalidOperationException("Wrong pawn");
            PawnAssignments++;
        }
    }
    public sealed class Pawn { public bool IsValid = true; }
    public sealed class PawnHandle { public Pawn? Value = new(); }
    public enum HookResult { Continue }
    public sealed class EventRoundStart { }
    public static class GameData
    {
        public static int Reads;
        public static int GetOffset(string key)
        {
            if (key != "CCSPlayerController_Respawn") throw new ArgumentException(key);
            Reads++;
            return 1;
        }
    }
    public abstract class BasePlugin
    {
        public abstract string ModuleAuthor { get; }
        public abstract string ModuleName { get; }
        public abstract string ModuleVersion { get; }
        public virtual void Load(bool hotReload) { }
        public virtual void Unload(bool hotReload) { }
        public Action? RoundStart;
        public Action<CCSPlayerController?, object>? Command;
        public Modules.Timers.Timer? Retry;
        public void RegisterEventHandler<T>(Func<T, object, HookResult> handler) where T : new() =>
            RoundStart = () => handler(new T(), new object());
        public void AddCommand(string name, string help, Action<CCSPlayerController?, object> callback)
        {
            if (name != "css_respawn" || Command != null) throw new InvalidOperationException(name);
            Command = callback;
        }
        public Modules.Timers.Timer AddTimer(float interval, Action callback, Modules.Timers.TimerFlags flags) =>
            Retry = new(callback);
    }
}

namespace CounterStrikeSharp.API.Core.Capabilities
{
    sealed class PluginCapability<T>(string name)
    {
        public static T? Provider;
        public T Get() => Provider ?? throw new KeyNotFoundException(name);
    }
}

namespace CounterStrikeSharp.API.Modules.Timers
{
    public enum TimerFlags { REPEAT }
    public sealed class Timer(Action callback)
    {
        public bool Killed;
        public void Kill() => Killed = true;
        public void Fire() { if (!Killed) callback(); }
    }
}

namespace CounterStrikeSharp.API.Modules.Memory
{
    static class VirtualFunction
    {
        public static int Binds;
        public static Action<nint, nint>? Called;
        public static Action<T> CreateVoid<T>(nint controller, int offset)
        {
            var vtable = System.Runtime.InteropServices.Marshal.ReadIntPtr(controller);
            var target = System.Runtime.InteropServices.Marshal.ReadIntPtr(vtable, offset * IntPtr.Size);
            Binds++;
            return argument => Called!(target, (nint)(object)argument!);
        }
    }
}

namespace CounterStrikeSharp.API.Modules.Utils
{
    public enum CsTeam { None, Spectator, Terrorist, CounterTerrorist }
}

namespace VipCoreApi
{
    public interface IVipCoreApi
    {
        enum FeatureType { Selectable }
        enum FeatureState { Enabled }
        event Action<CCSPlayerController>? OnPlayerSpawn;
        event Action<CCSPlayerController, string>? PlayerLoaded, PlayerRemoved;
        void RegisterFeature(VipFeatureBase feature, FeatureType type);
        void UnRegisterFeature(VipFeatureBase feature);
    }
    public abstract class VipFeatureBase
    {
        public IVipCoreApi Api { get; }
        public abstract string Feature { get; }
        protected VipFeatureBase(IVipCoreApi api)
        {
            Api = api;
            api.OnPlayerSpawn += OnPlayerSpawn;
            api.PlayerLoaded += OnPlayerLoaded;
            api.PlayerRemoved += OnPlayerRemoved;
        }
        public virtual void OnPlayerSpawn(CCSPlayerController player) { }
        public virtual void OnPlayerLoaded(CCSPlayerController player, string group) { }
        public virtual void OnPlayerRemoved(CCSPlayerController player, string group) { }
        public virtual void OnSelectItem(CCSPlayerController player, IVipCoreApi.FeatureState state) { }
        public bool IsClientVip(CCSPlayerController player) => ((TestApi)Api).Vip;
        public bool PlayerHasFeature(CCSPlayerController player) => ((TestApi)Api).HasFeature;
        public T GetFeatureValue<T>(CCSPlayerController player) => (T)(object)((TestApi)Api).Limit;
        public string GetTranslatedText(string key) => key;
        public void PrintToChat(CCSPlayerController player, string text) => ((TestApi)Api).Messages.Add(text);
    }

    sealed class TestApi : IVipCoreApi
    {
        public bool Vip = true, HasFeature = true;
        public int Limit = 1;
        public List<string> Messages = [];
        public VipFeatureBase? Registered;
        public event Action<CCSPlayerController>? OnPlayerSpawn;
        public event Action<CCSPlayerController, string>? PlayerLoaded, PlayerRemoved;
        public bool HasListeners => OnPlayerSpawn != null || PlayerLoaded != null || PlayerRemoved != null;
        public void RegisterFeature(VipFeatureBase feature, IVipCoreApi.FeatureType type)
        {
            if (Registered != null || feature.Feature != "Respawn") throw new InvalidOperationException();
            Registered = feature;
        }
        public void UnRegisterFeature(VipFeatureBase feature)
        {
            if (Registered != feature) throw new InvalidOperationException();
            Registered = null;
        }
    }
}
