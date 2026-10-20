using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using VipCoreApi;
using Microsoft.Extensions.Logging;

namespace VIP_RegenHealth;

public class VipRegenHealth : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Health Regeneration";
    public override string ModuleVersion => "v1.0.2";

    private RegenHealth? _regenHealth;
    private IVipCoreApi? _api;
    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload)
    {
        // Defer startup initialization until other plugins have loaded.
        Server.NextWorldUpdate(Initialize);
    }

    public override void OnAllPluginsLoaded(bool hotReload) => Initialize();

    private void Initialize()
    {
        if (_regenHealth != null) return;
        _api = PluginCapability.Get();
        if (_api == null) return;
        _api.OnCoreReady += RegisterFeature;
        // The core-ready event has already fired when loading this module on a live server.
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (_api == null || _regenHealth != null) return;
        _regenHealth = new RegenHealth(this, _api);
        _api.RegisterFeature(_regenHealth);
        Logger.LogInformation("HealthRegen registered; configured values: {Values}",
            string.Join(" | ", _api.GetAllRegisteredFeatures()
                .Where(entry => entry.feature == "HealthRegen")
                .Select(entry => entry.value?.ToString())));
    }

    public override void Unload(bool hotReload)
    {
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
        if (_regenHealth == null) return;
        _regenHealth.Stop();
        _api.UnRegisterFeature(_regenHealth);
        _regenHealth = null;
    }
}

public class RegenHealth : VipFeatureBase
{
    public override string Feature => "HealthRegen";
    private readonly RegenSchedule?[] _regen = new RegenSchedule?[65];
    private readonly CounterStrikeSharp.API.Modules.Timers.Timer _timer;

    public RegenHealth(VipRegenHealth plugin, IVipCoreApi api) : base(api)
    {
        // Keep one timer through map changes; reset all per-player deadlines on map start.
        // A sub-second poll avoids rounding the damage delay to a global one-second tick.
        _timer = plugin.AddTimer(0.1f, Timer_HealthRegen, TimerFlags.REPEAT);
        plugin.RegisterListener<Listeners.OnMapStart>(_ => Array.Clear(_regen));
        plugin.RegisterListener<Listeners.OnClientConnected>(slot => _regen[slot] = null);
        plugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot => _regen[slot] = null);
        plugin.RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            if (ev.Userid is { } player) _regen[player.Slot] = null;
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerDeath>((ev, _) =>
        {
            if (ev.Userid is { } player) _regen[player.Slot] = null;
            return HookResult.Continue;
        });
        plugin.RegisterEventHandler<EventPlayerHurt>((ev, _) =>
        {
            var player = ev.Userid;
            if (player == null || ev.DmgHealth <= 0) return HookResult.Continue;
            _regen[player.Slot] = null;
            if (!CanRegenerate(player)) return HookResult.Continue;
            var config = GetFeatureValue<Regen>(player);
            if (config.Health > 0 && float.IsFinite(config.Delay) && float.IsFinite(config.Interval))
                _regen[player.Slot] = new RegenSchedule(Server.CurrentTime, config.Health, config.Delay, config.Interval);
            return HookResult.Continue;
        });
    }

    private bool CanRegenerate(CCSPlayerController player) =>
        player.IsValid && player.PawnIsAlive && player.PlayerPawn.Value is { IsValid: true } &&
        IsClientVip(player) && PlayerHasFeature(player) &&
        GetPlayerFeatureState(player) == IVipCoreApi.FeatureState.Enabled;

    private void Timer_HealthRegen()
    {
        foreach (var player in Utilities.GetPlayers())
        {
            var schedule = _regen[player.Slot];
            if (schedule == null) continue;
            if (!CanRegenerate(player))
            {
                _regen[player.Slot] = null;
                continue;
            }
            var pawn = player.PlayerPawn.Value!;
            if (pawn.Health >= pawn.MaxHealth)
            {
                _regen[player.Slot] = null;
                continue;
            }
            if (!schedule.IsDue(Server.CurrentTime)) continue;
            pawn.Health = (int)Math.Min((long)pawn.Health + schedule.Health, pawn.MaxHealth);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
            schedule.Advance(Server.CurrentTime);
            if (pawn.Health >= pawn.MaxHealth) _regen[player.Slot] = null;
        }
    }

    public void Stop()
    {
        _timer.Kill();
        Array.Clear(_regen);
        Api.OnPlayerSpawn -= OnPlayerSpawn;
        Api.PlayerLoaded -= OnPlayerLoaded;
        Api.PlayerRemoved -= OnPlayerRemoved;
    }
}

public class Regen
{
    public int Health { get; set; }
    public float Delay { get; set; }
    public float Interval { get; set; }
}
