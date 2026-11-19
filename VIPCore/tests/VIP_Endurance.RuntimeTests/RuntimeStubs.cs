using CounterStrikeSharp.API.Core;
using static VipCoreApi.IVipCoreApi;

namespace CounterStrikeSharp.API
{
    internal static class Server
    {
        private static readonly Queue<Action> Pending = new();
        public static string MapName = "";
        public static void NextWorldUpdate(Action action) => Pending.Enqueue(action);
        public static void Advance()
        {
            var count = Pending.Count;
            for (var i = 0; i < count; i++) Pending.Dequeue()();
        }
    }

    internal static class Utilities
    {
        public static readonly CCSPlayerController?[] Players = new CCSPlayerController?[65];
        public static int MaxPlayers = 65;
        public static int Visits;
        public static CCSPlayerController? GetPlayerFromSlot(int slot)
        {
            Visits++;
            return Players[slot];
        }

        public static List<CCSPlayerController> GetPlayers()
        {
            var players = new List<CCSPlayerController>();
            for (var slot = 0; slot < MaxPlayers; slot++)
                if (GetPlayerFromSlot(slot) is { IsValid: true, Connected: PlayerConnectedState.Connected } player)
                    players.Add(player);
            return players;
        }
    }
}

namespace CounterStrikeSharp.API.Core
{
    public enum PlayerConnectedState { Connected, Disconnected }

    public sealed class CCSPlayerController
    {
        public int Slot { get; init; }
        public bool IsValid = true;
        public static int AliveChecks;
        private bool _alive = true;
        public bool PawnIsAlive
        {
            get { AliveChecks++; return _alive; }
            set => _alive = value;
        }
        public bool IsBot;
        public bool IsHLTV;
        public PlayerConnectedState Connected;
        public readonly PawnHandle PlayerPawn = new();
    }

    public sealed class PawnHandle
    {
        public Pawn? Value = new();
    }

    public sealed class Pawn
    {
        public bool IsValid = true;
        public float VelocityModifier = 1;
    }

    public static class Listeners
    {
        public delegate void OnTick();
        public delegate void OnMapStart(string map);
        public delegate void OnMapEnd();
        public delegate void OnClientDisconnect(int slot);
    }

    public abstract class BasePlugin
    {
        private readonly Dictionary<Type, Delegate> _listeners = new();
        public abstract string ModuleAuthor { get; }
        public abstract string ModuleName { get; }
        public abstract string ModuleVersion { get; }
        public virtual void Load(bool hotReload) { }
        public virtual void OnAllPluginsLoaded(bool hotReload) { }
        public virtual void Unload(bool hotReload) { }
        public void RegisterListener<T>(T listener) where T : Delegate => _listeners.Add(typeof(T), listener);
        public void RemoveListener<T>(T listener) where T : Delegate
        {
            if (!Equals(_listeners[typeof(T)], listener)) throw new InvalidOperationException("Wrong listener");
            _listeners.Remove(typeof(T));
        }
        public T? FindListener<T>() where T : Delegate => _listeners.GetValueOrDefault(typeof(T)) as T;
        public Modules.Timers.Timer? RetryTimer;
        public Modules.Timers.Timer AddTimer(float interval, Action callback, Modules.Timers.TimerFlags flags) =>
            RetryTimer = new(callback);
        public T Listener<T>() where T : Delegate => (T)_listeners[typeof(T)];
    }
}

namespace CounterStrikeSharp.API.Core.Capabilities
{
    internal sealed class PluginCapability<T>(string name) where T : class
    {
        public static T? Value;
        public T? Get() => name == "vipcore:core" && Value != null ? Value : throw new KeyNotFoundException();
    }
}

namespace VipCoreApi
{
    public interface IVipCoreApi
    {
        enum FeatureType { Toggle }
        enum FeatureState { Enabled, Disabled, NoAccess }

        event Action? OnCoreReady;
        event Action<CCSPlayerController>? OnPlayerSpawn;
        event Action<CCSPlayerController, string>? PlayerLoaded;
        event Action<CCSPlayerController, string>? PlayerRemoved;
        void RegisterFeature(VipFeatureBase feature, FeatureType type);
        void UnRegisterFeature(VipFeatureBase feature);
        FeatureState GetPlayerFeatureState(CCSPlayerController player);
        bool IsClientVip(CCSPlayerController player);
        bool PlayerHasFeature(CCSPlayerController player);
    }

    public abstract class VipFeatureBase
    {
        public abstract string Feature { get; }
        protected IVipCoreApi Api { get; }

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
        public virtual void OnSelectItem(CCSPlayerController player, FeatureState state) { }
        protected FeatureState GetPlayerFeatureState(CCSPlayerController player) => Api.GetPlayerFeatureState(player);
        protected bool IsClientVip(CCSPlayerController player) => Api.IsClientVip(player);
        protected bool PlayerHasFeature(CCSPlayerController player) => Api.PlayerHasFeature(player);
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
