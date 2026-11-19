// Host doubles for running the module's actual event handlers without a CS2 process.
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace CounterStrikeSharp.API
{
    public static class Server
    {
        public static string MapName = "";
        public static readonly Queue<Action> Frames = new();
        public static void NextFrame(Action action) => Frames.Enqueue(action);
        public static void NextWorldUpdate(Action action) => Frames.Enqueue(action);
        public static void Advance()
        {
            var count = Frames.Count;
            for (var i = 0; i < count; i++) Frames.Dequeue()();
        }
    }

    public static class Application { public static string RootDirectory => ""; }
    public static class Utilities
    {
        public static readonly Dictionary<int, CCSPlayerController> Players = new();
        public static readonly Dictionary<uint, CEntityInstance> Entities = new();
        public static uint NextHandle = 100;
        public static int WorldScans;
        public static Action<CPostProcessingVolume>? Spawned;
        public static CCSPlayerController? GetPlayerFromSlot(int slot) => Players.GetValueOrDefault(slot);
        public static IEnumerable<CCSPlayerController> GetPlayers() => Players.Values;
        public static IEnumerable<T> FindAllEntitiesByDesignerName<T>(string name) where T : CEntityInstance
        {
            WorldScans++;
            return Entities.Values.OfType<T>().Where(e => e.DesignerName.Contains(name));
        }
        public static T CreateEntityByName<T>(string name) where T : CEntityInstance, new()
        {
            var entity = new T { DesignerName = name, EntityHandle = new(NextHandle++) };
            Entities.Add(entity.EntityHandle.Raw, entity);
            return entity;
        }
        public static void SetStateChanged(CEntityInstance entity, string type, string field) { }
    }
}

namespace CounterStrikeSharp.API.Core
{
    public class CEntityInstance
    {
        public CHandle<CEntityInstance> EntityHandle = new(0);
        public bool IsValid = true;
        public string DesignerName = "";
        public Identity Entity = new();
        public T As<T>() where T : CEntityInstance => (T)this;
    }
    public sealed class Identity { public string Name = ""; }
    public sealed class CPostProcessingVolume : CEntityInstance
    {
        public bool Master;
        public bool RemovalRequested;
        public void DispatchSpawn(CEntityKeyValues keys)
        {
            Entity.Name = keys.Name;
            Utilities.Spawned?.Invoke(this);
        }
        public void AcceptInput(string input, CCSPlayerPawn pawn, object? caller, string value) { }
        public void Remove() => RemovalRequested = true; // Engine deletion can be delayed.
    }
    public sealed class CCSPlayerController : CEntityInstance
    {
        public int Slot;
        public ulong SteamID;
        public byte TeamNum;
        public bool IsBot;
        public bool PawnIsAlive;
        public CHandle<CCSPlayerPawn> PlayerPawn = new(0);
        public CHandle<CCSPlayerPawn> Pawn = new(0);
    }
    public sealed class CCSPlayerPawn : CEntityInstance
    {
        public byte LifeState;
        public object? AbsOrigin = new();
        public ObserverServices? ObserverServices;
        public CHandle<CCSPlayerController> OriginalController = new(0);
    }
    public sealed class ObserverServices
    {
        public byte ObserverMode;
        public CHandle<CEntityInstance> ObserverTarget = new(0);
    }
    public enum ObserverMode_t { OBS_MODE_IN_EYE = 2, OBS_MODE_CHASE, OBS_MODE_ROAMING }
    public enum LifeState_t { LIFE_ALIVE }
    public class CCheckTransmitInfoList : List<(TransmitInfo, CCSPlayerController?)> { }
    public sealed class TransmitInfo { public HashSet<uint> TransmitEntities = new(); }
    public sealed class ResourceManifest { public readonly List<string> Files = new(); public void AddResource(string file) => Files.Add(file); }
    public sealed class CommandInfo { public void ReplyToCommand(string message) { } }
    public class PlayerEvent { public CCSPlayerController? Userid; }
    public sealed class EventPlayerSpawn : PlayerEvent { }
    public sealed class EventPlayerDeath : PlayerEvent { }
    public sealed class EventPlayerTeam : PlayerEvent { }
    public sealed class EventRoundStart { }
    public enum HookResult { Continue }
    public static class Listeners
    {
        public delegate void OnMapStart(string name);
        public delegate void OnMapEnd();
        public delegate void OnClientDisconnectPost(int slot);
        public delegate void OnEntitySpawned(CEntityInstance entity);
        public delegate void OnEntityDeleted(CEntityInstance entity);
        public delegate void CheckTransmit(CCheckTransmitInfoList list);
        public delegate void OnServerPrecacheResources(ResourceManifest manifest);
    }
    public abstract class BasePlugin
    {
        private readonly Dictionary<Type, Delegate> _listeners = new();
        private readonly Dictionary<Type, Delegate> _events = new();
        public readonly List<Timer> Timers = new();
        public Microsoft.Extensions.Logging.TestLogger Logger { get; } = new();
        public abstract string ModuleAuthor { get; }
        public abstract string ModuleName { get; }
        public abstract string ModuleVersion { get; }
        public abstract string ModuleDescription { get; }
        public virtual void Load(bool hotReload) { }
        public virtual void OnAllPluginsLoaded(bool hotReload) { }
        public virtual void Unload(bool hotReload) { }
        public void AddCommand(string name, string help, Action<CCSPlayerController?, CommandInfo> callback) { }
        public void RegisterListener<T>(T callback) where T : Delegate =>
            _listeners[typeof(T)] = Delegate.Combine(Listener<T>(), callback);
        public T? Listener<T>() where T : Delegate => _listeners.GetValueOrDefault(typeof(T)) as T;
        public void RemoveListener<T>(T callback) where T : Delegate
        {
            var remaining = Delegate.Remove(Listener<T>(), callback);
            if (remaining == null) _listeners.Remove(typeof(T));
            else _listeners[typeof(T)] = remaining;
        }
        public void RegisterEventHandler<T>(Func<T, object?, HookResult> callback) => _events.Add(typeof(T), callback);
        public void Fire<T>(T ev) => ((Func<T, object?, HookResult>)_events[typeof(T)])(ev, null);
        public Timer AddTimer(float seconds, Action action, TimerFlags flags)
        {
            var timer = new Timer(action);
            Timers.Add(timer);
            return timer;
        }
    }
}
namespace CounterStrikeSharp.API.Core.Capabilities
{
    public sealed class PluginCapability<T>(string name) where T : class
    {
        public static T? Provider;
        public T Get() => Provider ?? throw new KeyNotFoundException(name);
    }
}
namespace CounterStrikeSharp.API.Modules.Utils
{
    public sealed class CHandle<T>(uint raw) where T : CEntityInstance
    {
        public uint Raw => raw;
        public uint Index => raw & 0x7fff;
        public T? Value => Utilities.Entities.GetValueOrDefault(raw) as T;
    }
    public sealed class CEntityKeyValues : IDisposable
    {
        public string Name = "";
        public void SetString(string key, string value) { if (key == "targetname") Name = value; }
        public void SetBool(string key, bool value) { }
        public void SetFloat(string key, float value) { }
        public void SetInt(string key, int value) { }
        public void SetVector(string key, object value) { }
        public void Dispose() { }
    }
}
namespace CounterStrikeSharp.API.Modules.Admin
{
    public static class AdminManager
    {
        public static bool GroupMember;
        public static bool PlayerHasPermissions(CCSPlayerController player, params string[] flags) => true;
        public static bool PlayerInGroup(CCSPlayerController player, params string[] groups) => GroupMember;
    }
}
namespace CounterStrikeSharp.API.Modules.Timers
{
    public enum TimerFlags { REPEAT }
    public sealed class Timer(Action action)
    {
        public bool Killed;
        public void Kill() => Killed = true;
        public void Fire() { if (!Killed) action(); }
    }
}
namespace Microsoft.Extensions.Logging
{
    public sealed class TestLogger
    {
        public void LogError(string message, params object[] args) => throw new Exception(message);
        public void LogError(Exception error, string message, params object[] args) => throw new Exception(message, error);
    }
}
namespace VipCoreApi
{
    public class IVipCoreApi
    {
        public enum FeatureType { Selectable }
        public enum FeatureState { Enabled, Disabled }
        public event Action? OnCoreReady;
        public event Action<CCSPlayerController>? OnPlayerSpawn;
        public event Action<CCSPlayerController, string>? PlayerLoaded, PlayerRemoved;
        public void Ready() => OnCoreReady?.Invoke();
        public void Spawn(CCSPlayerController p) => OnPlayerSpawn?.Invoke(p);
        public void Loaded(CCSPlayerController p) => PlayerLoaded?.Invoke(p, "vip");
        public void Removed(CCSPlayerController p) => PlayerRemoved?.Invoke(p, "vip");
        public string CoreConfigDirectory = "";
        public VipFeatureBase? Feature;
        public bool Access = true;
        public readonly Dictionary<ulong, string> Cookies = new();
        public string Required = "";
        public int EffectReads;
        public void RegisterFeature(VipFeatureBase feature, FeatureType type)
        {
            if (Feature != null) throw new InvalidOperationException("Duplicate registration");
            Feature = feature;
        }
        public void UnRegisterFeature(VipFeatureBase feature) => Feature = null;
    }
    public class TestMenu
    {
        public void AddMenuOption(string label, Action<CCSPlayerController, object?> action, bool disabled = false) { }
        public void Open(CCSPlayerController player) { }
    }
    public abstract class VipFeatureBase
    {
        public IVipCoreApi Api { get; }
        protected VipFeatureBase(IVipCoreApi api)
        {
            Api = api;
            api.OnPlayerSpawn += OnPlayerSpawn;
            api.PlayerLoaded += OnPlayerLoaded;
            api.PlayerRemoved += OnPlayerRemoved;
        }
        public abstract string Feature { get; }
        public virtual void OnSelectItem(CCSPlayerController player, IVipCoreApi.FeatureState state) { }
        public virtual void OnPlayerSpawn(CCSPlayerController player) { }
        public virtual void OnPlayerLoaded(CCSPlayerController player, string group) { }
        public virtual void OnPlayerRemoved(CCSPlayerController player, string group) { }
        protected bool IsClientVip(CCSPlayerController player) => true;
        protected bool PlayerHasFeature(CCSPlayerController player) => Api.Access;
        protected IVipCoreApi.FeatureState GetPlayerFeatureState(CCSPlayerController player) => IVipCoreApi.FeatureState.Enabled;
        protected T? GetPlayerCookie<T>(ulong steam, string key) => (T?)(object?)Api.Cookies.GetValueOrDefault(steam);
        protected void SetPlayerCookie(ulong steam, string key, string value) => Api.Cookies[steam] = value;
        protected T GetFeatureValue<T>(CCSPlayerController player)
        {
            Api.EffectReads++;
            return (T)(object)new List<VIP_Postprocessing.PostprocessingEntry>
            {
                new() { Name = "Warm", File = "warm.vpost", Required = Api.Required }
            };
        }
        protected TestMenu CreateMenu(string title) => new();
        protected string GetTranslatedText(string key, params object[] args) => key;
        protected void PrintToChat(CCSPlayerController player, string message) { }
    }
}
