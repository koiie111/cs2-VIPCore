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
        internal static uint NextEntity = 100;
        public static CCSPlayerController? GetPlayerFromSlot(int slot) => Players[slot];
        public static List<CCSPlayerController> GetPlayers() => Players.OfType<CCSPlayerController>().ToList();
        public static T? CreateEntityByName<T>(string name) where T : CEntityInstance, new()
        {
            Created++;
            return new T { EntityHandle = new(NextEntity++) };
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

namespace CounterStrikeSharp.API.Modules.Admin
{
    internal static class AdminManager
    {
        public static bool PlayerHasPermissions(CCSPlayerController player, params string[] permissions) => true;
    }
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
        public int TeamNum { get; set; } = 2;
        public CHandle<Pawn> PlayerPawn { get; set; } = new(0);
    }

    public sealed class Pawn : CEntityInstance
    {
        public byte LifeState;
        public Vector? AbsOrigin { get; set; } = new();
    }

    public sealed class CParticleSystem : CEntityInstance
    {
        public string EffectName { get; set; } = "";
        public bool StartActive { get; set; }
        internal Vector? Origin { get; private set; }
        public void Teleport(Vector position, QAngle angle, Vector velocity) => Origin = position;
        public void DispatchSpawn() { }
        public void AcceptInput(string input, Pawn? activator = null, Pawn? caller = null, string? value = null) { }
    }

    public sealed class CCheckTransmitInfo
    {
        public HashSet<int> TransmitEntities { get; } = new();
    }

    public sealed class CCheckTransmitInfoList(params (CCheckTransmitInfo, CCSPlayerController?)[] entries)
    {
        public int Count => entries.Length;
        public (CCheckTransmitInfo, CCSPlayerController?) this[int index] => entries[index];
    }

    public sealed class ResourceManifest
    {
        internal readonly List<string> Resources = new();
        public void AddResource(string path) => Resources.Add(path);
    }

    public sealed record EventPlayerSpawn(CCSPlayerController? Userid);
    public sealed record EventPlayerDeath(CCSPlayerController? Userid);
    public sealed record EventPlayerTeam(CCSPlayerController? Userid, int Team, bool Disconnect = false);
    public sealed class EventRoundStart;
    public sealed class EventRoundEnd;

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
        internal Func<CCSPlayerController?, object?, HookResult>? Reload;
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
        public void RegisterEventHandler<T>(Func<T, object?, HookResult> handler) => _events.Add(typeof(T), handler);
        public void AddCommandListener(string name, Func<CCSPlayerController?, object?, HookResult> handler, HookMode mode) =>
            Reload = name == "css_vip_reload" && mode == HookMode.Post ? handler : throw new InvalidOperationException();
        internal bool HasListener<T>() where T : Delegate => _listeners.ContainsKey(typeof(T));
        internal void Invoke<T>(Action<T> call) where T : Delegate
        {
            if (_listeners.TryGetValue(typeof(T), out var listener))
                foreach (var callback in listener.GetInvocationList()) call((T)callback);
        }
        internal void Fire<T>(T ev) => ((Func<T, object?, HookResult>)_events[typeof(T)])(ev, null);
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
        public bool PlayerHasFeature(CCSPlayerController player) => Core.Entries.ContainsKey(Core.Groups[player.SteamID]);
        public FeatureState GetPlayerFeatureState(CCSPlayerController player) => FeatureState.Enabled;
        public T GetFeatureValue<T>(CCSPlayerController player)
        {
            Core.Deserializations++;
            return JsonSerializer.Deserialize<T>(Core.Entries[Core.Groups[player.SteamID]])!;
        }
        public T? GetPlayerCookie<T>(ulong steamId, string key) where T : class => Core.Cookies.GetValueOrDefault(steamId) as T;
        public void SetPlayerCookie(ulong steamId, string key, string data) => Core.Cookies[steamId] = data;
        public string GetTranslatedText(string name, params object[] args) => name;
        public TestMenu CreateMenu(string title) => new(Core);
        public void PrintToChat(CCSPlayerController player, string message) { }
    }

    internal sealed class FakeApi(string configDirectory) : IVipCoreApi
    {
        public event Action? OnCoreReady;
        public event Action<CCSPlayerController>? OnPlayerSpawn;
        public event Action<CCSPlayerController, string>? PlayerLoaded;
        public event Action<CCSPlayerController, string>? PlayerRemoved;
        public string CoreConfigDirectory => configDirectory;
        internal VipFeatureBase? Feature;
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
        internal bool HasCallbacks => OnCoreReady != null || OnPlayerSpawn != null || PlayerLoaded != null ||
                                       PlayerRemoved != null;
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
