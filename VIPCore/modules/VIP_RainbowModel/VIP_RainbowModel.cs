using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using System.Drawing;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API.Core.Capabilities;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace VIP_RainbowModel;

public class VipRainbowModel : BasePlugin
{
    public override string ModuleAuthor => "WodiX";
    public override string ModuleName => "[VIP] RainbowModel";
    public override string ModuleVersion => "v1.0.5";

    private IVipCoreApi? _api;
    private RainbowModel? _rainbowModel;
    private bool _unloaded;

    private PluginCapability<IVipCoreApi> PluginCapability { get; } = new("vipcore:core");

    public override void Load(bool hotReload) => Server.NextWorldUpdate(Initialize);
    public override void OnAllPluginsLoaded(bool hotReload) => Initialize();
    private void Initialize()
    {
        if (_unloaded || _rainbowModel != null) return;
        _api = PluginCapability.Get();
        if (_api == null) return;
        _api.OnCoreReady += RegisterFeature;
        RegisterFeature();
    }
    private void RegisterFeature()
    {
        if (_unloaded || _api == null || _rainbowModel != null) return;
        _rainbowModel = new RainbowModel(this, _api);
        _api.RegisterFeature(_rainbowModel);
        Logger.LogInformation("RainbowModel registered; configured values: {Values}",
            string.Join(" | ", _api.GetAllRegisteredFeatures()
                .Where(entry => entry.feature == "RainbowModel")
                .Select(entry => entry.value?.ToString())));
    }
    public override void Unload(bool hotReload)
    {
        _unloaded = true;
        if (_api == null) return;
        _api.OnCoreReady -= RegisterFeature;
        if (_rainbowModel == null) return;
        _rainbowModel.StopAll();
        _api.UnRegisterFeature(_rainbowModel);
        _rainbowModel = null;
    }

}

public class RainbowModel : VipFeatureBase
{
    private readonly BasePlugin _basePlugin;
    public override string Feature => "RainbowModel";

    private readonly Dictionary<int, Timer> _timers = new();

    public RainbowModel(BasePlugin basePlugin, IVipCoreApi api) : base(api)
    {
        _basePlugin = basePlugin;
        basePlugin.RegisterListener<Listeners.OnClientDisconnectPost>(slot => Stop(slot));
        basePlugin.RegisterListener<Listeners.OnMapEnd>(StopAll);
        basePlugin.RegisterEventHandler<EventPlayerDeath>((ev, info) =>
        {
            if (ev.Userid != null) Stop(ev.Userid.Slot);
            return HookResult.Continue;
        });
    }
    private void Stop(int slot)
    {
        if (_timers.Remove(slot, out var timer)) timer.Kill();
    }
    public void StopAll()
    {
        foreach (var timer in _timers.Values) timer.Kill();
        _timers.Clear();
    }
    private bool Allowed(CCSPlayerController player) => player.IsValid && IsClientVip(player)
        && PlayerHasFeature(player) && GetFeatureValue<bool>(player)
        && GetPlayerFeatureState(player) == FeatureState.Enabled;

    public override void OnPlayerSpawn(CCSPlayerController player)
    {
        Stop(player.Slot);
        if (!Allowed(player)) return;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || !player.PawnIsAlive) return;
        var slot = player.Slot;
        _timers[slot] = _basePlugin.AddTimer(1.4f, () =>
        {
            if (!Allowed(player) || !pawn.IsValid || !player.PawnIsAlive
                || player.PlayerPawn.Value?.Handle != pawn.Handle)
            {
                Stop(slot);
                if (pawn.IsValid) SetRainbowModel(pawn);
                return;
            }
            SetRainbowModel(pawn, Random.Shared.Next(256), Random.Shared.Next(256), Random.Shared.Next(256));
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }
    public override void OnSelectItem(CCSPlayerController player, FeatureState state)
    {
        Stop(player.Slot);
        if (state == FeatureState.Enabled) OnPlayerSpawn(player);
        else if (player.PlayerPawn.Value is { IsValid: true } pawn) SetRainbowModel(pawn);
    }
    public override void OnPlayerRemoved(CCSPlayerController player, string group)
    {
        Stop(player.Slot);
        if (player.IsValid && player.PlayerPawn.Value is { IsValid: true } pawn) SetRainbowModel(pawn);
    }
    private static void SetRainbowModel(CCSPlayerPawn pawn, int r = 255, int g = 255, int b = 255)
    {
        pawn.Render = Color.FromArgb(255, r, g, b);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }
}
