using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

namespace VIP_Endurance;

public class VipEndurance : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Endurance";
    public override string ModuleVersion => "v1.0.2";

    private readonly PluginCapability<IVipCoreApi> _capability = new("vipcore:core");
    private IVipCoreApi? _api;
    private Endurance? _endurance;
    private bool _mapRunning;
    private bool _unloaded;
    private int _mapGeneration;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _mapRunning = true;
            _endurance?.Clear();
            QueueRestore();
        });
        RegisterListener<Listeners.OnMapEnd>(() =>
        {
            _mapRunning = false;
            _mapGeneration++;
            _endurance?.Clear();
        });
        RegisterListener<Listeners.OnClientDisconnect>(slot => _endurance?.ClearSlot(slot));
        Server.NextWorldUpdate(Initialize);
    }

    public override void OnAllPluginsLoaded(bool hotReload) => Initialize();

    private void Initialize()
    {
        if (_unloaded || _api != null) return;
        _api = _capability.Get();
        if (_api == null) return;
        _api.OnCoreReady += RegisterFeature;
        _mapRunning |= !string.IsNullOrEmpty(Server.MapName);
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (_unloaded || _api == null || _endurance != null) return;
        _endurance = new Endurance(this, _api);
        _api.RegisterFeature(_endurance, FeatureType.Toggle);
        QueueRestore();
    }

    private void QueueRestore()
    {
        if (!_mapRunning || _endurance == null) return;
        var generation = ++_mapGeneration;
        Server.NextWorldUpdate(() =>
        {
            if (!_unloaded && _mapRunning && generation == _mapGeneration)
                _endurance?.RestorePlayers();
        });
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
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

    private ulong _selectedSlots;
    private bool _stopped;

    public Endurance(BasePlugin plugin, IVipCoreApi api) : base(api)
    {
        plugin.RegisterListener<Listeners.OnTick>(OnTick);
    }

    private void OnTick()
    {
        var slots = _selectedSlots;
        while (slots != 0)
        {
            var slot = BitOperations.TrailingZeroCount(slots);
            slots &= slots - 1;

            var player = Utilities.GetPlayerFromSlot(slot);
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
        SetSlot(player.Slot, GetPlayerFeatureState(player) == FeatureState.Enabled);
    }

    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        if (player.IsValid) ClearSlot(player.Slot);
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        if (_stopped || !player.IsValid) return;
        SetSlot(player.Slot, state == FeatureState.Enabled);
    }

    internal void ClearSlot(int slot) => SetSlot(slot, false);

    internal void Clear() => _selectedSlots = 0;

    internal void Stop()
    {
        _stopped = true;
        Clear();
        Api.OnPlayerSpawn -= OnPlayerSpawn;
        Api.PlayerLoaded -= OnPlayerLoaded;
        Api.PlayerRemoved -= OnPlayerRemoved;
    }

    private void SetSlot(int slot, bool enabled)
    {
        if ((uint)slot >= 64) return;

        var bit = 1UL << slot;
        if (enabled)
            _selectedSlots |= bit;
        else
            _selectedSlots &= ~bit;
    }
}
