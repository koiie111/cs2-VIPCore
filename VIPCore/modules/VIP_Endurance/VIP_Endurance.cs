using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Endurance;

public class VipEndurance : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Endurance";
    public override string ModuleVersion => "v1.0.3";

    private readonly PluginCapability<IVipCoreApi> _capability = new("vipcore:core");
    private IVipCoreApi? _api;
    private Endurance? _endurance;
    internal bool MapRunning { get; private set; }
    private Timer? _retry;
    private bool _unloaded;
    private int _mapGeneration;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            MapRunning = true;
            _endurance?.Clear();
            QueueRestore();
        });
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            MapRunning = false;
            _mapGeneration++;
            _endurance?.Clear();
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot => _endurance?.ClearSlot(slot));
        Server.NextWorldUpdate(Initialize);
    }

    private void Initialize()
    {
        if (_unloaded || _api != null) return;
        // CounterStrikeSharp 1.0.376 throws when the provider hasn't registered yet.
        try
        {
            _api = _capability.Get();
        }
        catch (KeyNotFoundException)
        {
            _api = null;
        }
        if (_api == null)
        {
            _retry ??= AddTimer(5.0f, Initialize, TimerFlags.REPEAT);
            return;
        }
        _retry?.Kill();
        _retry = null;
        MapRunning |= !string.IsNullOrEmpty(Server.MapName);
        _endurance = new Endurance(this, _api);
        _api.RegisterFeature(_endurance, FeatureType.Toggle);
        QueueRestore();
    }

    private void QueueRestore()
    {
        if (!MapRunning || _endurance == null) return;
        var generation = ++_mapGeneration;
        Server.NextWorldUpdate(() =>
        {
            if (!_unloaded && MapRunning && generation == _mapGeneration)
                _endurance?.RestorePlayers();
        });
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;
        _retry?.Kill();
        _retry = null;
        if (_api == null) return;
        if (_endurance != null)
        {
            _endurance.Stop();
            _api.UnRegisterFeature(_endurance);
            _endurance = null;
        }
        _api = null;
    }
}

public class Endurance : VipFeatureBase
{
    public override string Feature => "endurance";

    private readonly VipEndurance _plugin;
    private readonly IVipFeatureStateApi? _stateApi;
    // CSS controller wrappers resolve their entity handle (including its serial) on access.
    // A pawn can change on respawn, so it is still resolved from the live controller each tick.
    private readonly Dictionary<int, CCSPlayerController> _selectedPlayers = new();
    private bool _tickRegistered;
    private bool _tickUpdatePending;
    private bool _stopped;

    public Endurance(VipEndurance plugin, IVipCoreApi api) : base(api)
    {
        _plugin = plugin;
        _stateApi = api as IVipFeatureStateApi;
        if (_stateApi != null) _stateApi.PlayerFeatureStateSet += OnFeatureStateSet;
    }

    private void OnTick()
    {
        foreach (var player in _selectedPlayers.Values)
        {
            if (player is not { IsValid: true, Connected: PlayerConnectedState.Connected, PawnIsAlive: true })
                continue;

            if (player.PlayerPawn.Value is not { IsValid: true } pawn)
                continue;

            ref var modifier = ref pawn.VelocityModifier;
            if (modifier < 1.0f && IsClientVip(player) && PlayerHasFeature(player))
                modifier = 1.0f;
        }
    }

    internal void RestorePlayers()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsBot || player.IsHLTV) continue;
            if (_stateApi != null)
            {
                OnSelectItem(player, _stateApi.GetPlayerFeatureStateOrDefault(player, Feature));
                continue;
            }
            try
            {
                // VIPCore keeps the toggle even when the core or all features are temporarily disabled.
                OnPlayerLoaded(player, string.Empty);
            }
            catch (InvalidOperationException) when (!IsClientVip(player))
            {
                // GetPlayerFeatureState throws for connected players with no VIP record.
            }
        }
    }

    public override void OnPlayerLoaded(CCSPlayerController player, string group)
    {
        if (_stopped || !player.IsValid) return;
        OnSelectItem(player, GetPlayerFeatureState(player));
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        if (player.IsValid) ClearSlot(player.Slot);
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped || !_plugin.MapRunning || !player.IsValid) return;
        if (state == FeatureState.Enabled)
            _selectedPlayers[player.Slot] = player;
        else
            _selectedPlayers.Remove(player.Slot);
        UpdateTickListener();
    }

    internal void ClearSlot(int slot)
    {
        if (_selectedPlayers.Remove(slot)) UpdateTickListener();
    }

    internal void Clear()
    {
        _selectedPlayers.Clear();
        UpdateTickListener();
    }

    private void OnFeatureStateSet(CCSPlayerController player, string feature, FeatureState state)
    {
        if (feature == Feature) OnSelectItem(player, state);
    }

    internal void Stop()
    {
        _stopped = true;
        _selectedPlayers.Clear();
        if (_tickRegistered) _plugin.RemoveListener<Listeners.OnTick>(OnTick);
        _tickRegistered = false;
        if (_stateApi != null) _stateApi.PlayerFeatureStateSet -= OnFeatureStateSet;
        Api.OnPlayerSpawn -= OnPlayerSpawn;
        Api.PlayerLoaded -= OnPlayerLoaded;
        Api.PlayerRemoved -= OnPlayerRemoved;
    }

    private void UpdateTickListener()
    {
        if (_tickUpdatePending || _tickRegistered == (_selectedPlayers.Count != 0)) return;
        _tickUpdatePending = true;
        // A VIP API caller may itself be inside OnTick. Change the native listener list between dispatches.
        Server.NextWorldUpdate(() =>
        {
            _tickUpdatePending = false;
            if (_stopped || _tickRegistered == (_selectedPlayers.Count != 0)) return;
            if (_selectedPlayers.Count != 0)
                _plugin.RegisterListener<Listeners.OnTick>(OnTick);
            else
                _plugin.RemoveListener<Listeners.OnTick>(OnTick);
            _tickRegistered = _selectedPlayers.Count != 0;
        });
    }
}
