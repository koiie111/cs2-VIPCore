using System.Text.Json;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using static VipCoreApi.IVipCoreApi;

namespace CounterStrikeSharp.API
{
    internal static class Server
    {
        internal static readonly Queue<Action> Pending = new();
        public static void NextFrame(Action callback) => Pending.Enqueue(callback);
        public static void NextWorldUpdate(Action callback) => Pending.Enqueue(callback);

        internal static void Advance()
        {
            var count = Pending.Count;
            for (var i = 0; i < count; i++) Pending.Dequeue()();
        }
    }

    internal static class Application
    {
        public static string RootDirectory => Path.GetTempPath();
    }

    internal static class Utilities
    {
        internal static readonly CCSPlayerController?[] Players = new CCSPlayerController?[65];
        internal static int Created;
        internal static int Searches;
        internal static int PlayerWalks;
        internal static uint NextEntity = 100;
        public static CCSPlayerController? GetPlayerFromSlot(int slot) => Players[slot];
        public static List<CCSPlayerController> GetPlayers()
        {
            PlayerWalks++;
            return Players.OfType<CCSPlayerController>().ToList();
        }

        public static IEnumerable<T> FindAllEntitiesByDesignerName<T>(string name) where T : CEntityInstance
        {
            Searches++;
            return CEntityInstance.Entities.Values.OfType<T>().Where(entity => entity.DesignerName.Contains(name));
        }
        public static T? CreateEntityByName<T>(string name) where T : CEntityInstance, new()
        {
            Created++;
            return new T { EntityHandle = new(NextEntity++), DesignerName = name };
        }
    }
}

namespace CounterStrikeSharp.API.Modules.Utils
{
    public sealed class CHandle<T>(uint raw) where T : CEntityInstance
    {
        public uint Raw => raw;
        public uint Index => raw & 0x7fff;
        public T? Value => CEntityInstance.Entities.GetValueOrDefault(raw) as T;
    }

    public sealed record Vector(float X = 0, float Y = 0, float Z = 0);
    public sealed class QAngle;
}

namespace CounterStrikeSharp.API.Core.Capabilities
{
    internal sealed class PluginCapability<T>(string name) where T : class
    {
        internal static T? Value;
        public T? Get() => name == "vipcore:core" ? Value : null;
    }
}

namespace CounterStrikeSharp.API.Core
{
    public enum HookResult { Continue }
    public enum HookMode { Pre, Post }
    public enum LifeState_t : byte { LIFE_ALIVE, LIFE_DEAD }

    public class CEntityInstance
    {
        internal static readonly Dictionary<uint, CEntityInstance> Entities = new();
        private CHandle<CEntityInstance> _handle = new(0);

        public CHandle<CEntityInstance> EntityHandle
        {
            get => _handle;
            init
            {
                _handle = value;
                Entities.Add(value.Raw, this);
            }
        }
        public uint Index => EntityHandle.Index;
        public string DesignerName { get; init; } = "";
        public bool IsValid { get; private set; } = true;
        public void Remove()
        {
            BasePlugin.Instance.Invoke<Listeners.OnEntityDeleted>(callback => callback(this));
            Entities.Remove(EntityHandle.Raw);
            IsValid = false;
        }
    }

    public sealed class CCSPlayerController : CEntityInstance
    {
        public int Slot { get; init; }
        public ulong SteamID { get; init; }
        public bool IsBot { get; init; }
        public bool IsHLTV { get; init; }
        private int _team = 2;
        internal static int TeamReads;
        public int TeamNum { get { TeamReads++; return _team; } set => _team = value; }
        public CHandle<Pawn> PlayerPawn { get; set; } = new(0);
    }

    public class CBaseEntity : CEntityInstance
    {
        public Vector? AbsOrigin { get; set; }
    }

    public sealed class Pawn : CBaseEntity;
    public sealed class CPlantedC4 : CBaseEntity;

    public sealed class CParticleSystem : CBaseEntity
    {
        public string EffectName { get; set; } = "";
        public bool StartActive { get; set; }
        internal CBaseEntity? Parent { get; private set; }
        public void Teleport(Vector position, QAngle angle, Vector velocity) => AbsOrigin = position;
        public void DispatchSpawn() { }
        public void AcceptInput(string input, CBaseEntity? activator = null, CBaseEntity? caller = null, string? value = null)
        {
            if (input == "FollowEntity") Parent = activator;
        }
    }

    public sealed class CCheckTransmitInfo
    {
        public HashSet<int> TransmitEntities { get; } = new();
    }

    public sealed class CCheckTransmitInfoList(params (CCheckTransmitInfo, CCSPlayerController?)[] entries)
        : IEnumerable<(CCheckTransmitInfo, CCSPlayerController?)>
    {
        public int Count => entries.Length;
        public (CCheckTransmitInfo, CCSPlayerController?) this[int index] => entries[index];
        public IEnumerator<(CCheckTransmitInfo, CCSPlayerController?)> GetEnumerator() =>
            ((IEnumerable<(CCheckTransmitInfo, CCSPlayerController?)>)entries).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class ResourceManifest
    {
        internal readonly List<string> Resources = new();
        public void AddResource(string path) => Resources.Add(path);
    }

    public sealed record EventBombPlanted(CCSPlayerController? Userid);
    public sealed record EventBombDefused(CCSPlayerController? Userid);
    public sealed class EventBombExploded;
    public sealed record EventPlayerTeam(CCSPlayerController? Userid, int Team, bool Disconnect = false);
    public sealed class EventRoundStart;
    public sealed class GameEventInfo;

    public static class Listeners
    {
        public delegate void CheckTransmit(CCheckTransmitInfoList list);
        public delegate void OnEntityDeleted(CEntityInstance entity);
        public delegate void OnMapStart(string map);
        public delegate void OnMapEnd();
        public delegate void OnClientPutInServer(int slot);
        public delegate void OnClientDisconnectPost(int slot);
        public delegate void OnServerPrecacheResources(ResourceManifest manifest);
    }

    public abstract class BasePlugin
    {
        internal static BasePlugin Instance = null!;
        private readonly Dictionary<Type, Delegate> _listeners = new();
        private readonly Dictionary<Type, Delegate> _events = new();
        internal readonly List<Modules.Timers.Timer> Timers = new();
        public Modules.Timers.Timer AddTimer(float interval, Action callback, Modules.Timers.TimerFlags flags)
        {
            var timer = new Modules.Timers.Timer(interval, callback, flags);
            Timers.Add(timer);
            return timer;
        }
        public object Logger => this;
        public abstract string ModuleAuthor { get; }
        public abstract string ModuleName { get; }
        public abstract string ModuleVersion { get; }
        public abstract string ModuleDescription { get; }
        public virtual void Load(bool hotReload) { }
        public virtual void OnAllPluginsLoaded(bool hotReload) { }
        public virtual void Unload(bool hotReload) { }

        public void RegisterListener<T>(T listener) where T : Delegate =>
            _listeners[typeof(T)] = Delegate.Combine(_listeners.GetValueOrDefault(typeof(T)), listener);
        public void RemoveListener<T>(T listener) where T : Delegate
        {
            if (!Equals(_listeners[typeof(T)], listener)) throw new InvalidOperationException("Wrong listener");
            _listeners.Remove(typeof(T));
        }
        public void RegisterEventHandler<T>(Func<T, GameEventInfo, HookResult> handler) => _events.Add(typeof(T), handler);
        internal T? Listener<T>() where T : Delegate => (T?)_listeners.GetValueOrDefault(typeof(T));
        internal bool HasListener<T>() where T : Delegate => Listener<T>() != null;
        internal void Invoke<T>(Action<T> call) where T : Delegate
        {
            if (_listeners.TryGetValue(typeof(T), out var listener))
                foreach (var callback in listener.GetInvocationList()) call((T)callback);
        }
        internal void Fire<T>(T ev)
        {
            if (_events.TryGetValue(typeof(T), out var handler))
                ((Func<T, GameEventInfo, HookResult>)handler)(ev, new());
        }
    }
}

namespace CounterStrikeSharp.API.Modules.Timers
{
    public enum TimerFlags { REPEAT, STOP_ON_MAPCHANGE }
    public sealed class Timer(float interval, Action callback, TimerFlags flags)
    {
        public bool Active { get; private set; } = true;
        public void Kill() => Active = false;
        public float Interval => interval;
        public void Fire()
        {
            if (!Active) return;
            if (flags != TimerFlags.REPEAT) Active = false;
            callback();
        }
        public void FireStale() => callback();
    }
}

namespace Microsoft.Extensions.Logging
{
    internal static class LoggerExtensions
    {
        public static void LogError(this object logger, string message, params object[] values) =>
            throw new InvalidOperationException(message);
    }
}

namespace VipCoreApi
{
    public interface IVipCoreApi
    {
        enum FeatureType { Selectable }
        enum FeatureState { Enabled, Disabled }
        event Action? OnCoreReady;
        event Action<CCSPlayerController>? OnPlayerSpawn;
        event Action<CCSPlayerController, string>? PlayerLoaded;
        event Action<CCSPlayerController, string>? PlayerRemoved;
        string CoreConfigDirectory { get; }
        void RegisterFeature(VipFeatureBase feature, FeatureType type);
        void UnRegisterFeature(VipFeatureBase feature);
    }

    public abstract class VipFeatureBase
    {
        protected IVipCoreApi Api { get; }
        private FakeApi Core => (FakeApi)Api;
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
        public virtual void OnSelectItem(CCSPlayerController player, FeatureState state) { }
        public bool IsClientVip(CCSPlayerController player) => Core.Groups.ContainsKey(player.SteamID);
        public bool PlayerHasFeature(CCSPlayerController player) => Core.Available && Core.Entries.ContainsKey(Core.Groups[player.SteamID]);
        public FeatureState GetPlayerFeatureState(CCSPlayerController player) => Core.States.GetValueOrDefault(player.SteamID, FeatureState.Enabled);
        public T GetFeatureValue<T>(CCSPlayerController player)
        {
            Core.Deserializations++;
            return JsonSerializer.Deserialize<T>(Core.Entries[Core.Groups[player.SteamID]])!;
        }
        public string GetClientVipGroup(CCSPlayerController player) => Core.Groups[player.SteamID];
        public T? GetPlayerCookie<T>(ulong steamId, string key) where T : class
        {
            Core.CookieReads++;
            return Core.Cookies.GetValueOrDefault(steamId) as T;
        }
        public void SetPlayerCookie(ulong steamId, string key, string data) => Core.Cookies[steamId] = data;
        public string GetTranslatedText(string name, params object[] args) => name;
        public TestMenu CreateMenu(string title) => new(Core);
        public void PrintToChat(CCSPlayerController player, string message) { }
    }

    internal class FakeApi(string configDirectory) : IVipCoreApi
    {
        public event Action? OnCoreReady;
        public event Action<CCSPlayerController>? OnPlayerSpawn;
        public event Action<CCSPlayerController, string>? PlayerLoaded;
        public event Action<CCSPlayerController, string>? PlayerRemoved;
        public string CoreConfigDirectory => configDirectory;
        internal VipFeatureBase? Feature;
        internal readonly Dictionary<ulong, FeatureState> States = new();
        internal int CookieReads;
        internal void Ready() => OnCoreReady?.Invoke();
        internal bool Available = true;
        internal int Deserializations;
        internal readonly Dictionary<ulong, string> Groups = new();
        internal readonly Dictionary<string, string> Entries = new();
        internal readonly Dictionary<ulong, string> Cookies = new();
        internal TestMenu? Menu;

        public void RegisterFeature(VipFeatureBase feature, FeatureType type) => Feature = feature;
        public void UnRegisterFeature(VipFeatureBase feature) => Feature = null;
        internal void LoadPlayer(CCSPlayerController player, string group)
        {
            Groups[player.SteamID] = group;
            PlayerLoaded?.Invoke(player, group);
        }
        internal void RemovePlayer(CCSPlayerController player)
        {
            PlayerRemoved?.Invoke(player, Groups[player.SteamID]);
            Groups.Remove(player.SteamID);
        }
        internal virtual bool HasCallbacks => OnCoreReady != null || OnPlayerSpawn != null || PlayerLoaded != null ||
                                       PlayerRemoved != null;
    }

    internal sealed class NotifyingApi(string directory) : FakeApi(directory)
    {
        public event Action? ConfigReloaded;
        internal void Reload() => ConfigReloaded?.Invoke();
        internal override bool HasCallbacks => base.HasCallbacks || ConfigReloaded != null;
    }

    public sealed class TestMenu
    {
        private readonly FakeApi _api;
        internal readonly Dictionary<string, Action<CCSPlayerController, object?>> Options = new();
        internal TestMenu(FakeApi api) => _api = api;
        public void AddMenuOption(string name, Action<CCSPlayerController, object?> call, bool selected = false) =>
            Options.Add(name, call);
        public void Open(CCSPlayerController player) => _api.Menu = this;
        internal void Click(CCSPlayerController player, string name) => Options[name](player, null);
    }
}
