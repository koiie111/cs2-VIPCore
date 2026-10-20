using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using Microsoft.Extensions.Logging;

namespace VIPCore;

public class VIP_DefuseKit : BasePlugin
{
    public override string ModuleAuthor => "T3Marius";
    public override string ModuleName => "[VIP] DefuseKit";
    public override string ModuleVersion => "1.0.1";
    private IVipCoreApi? VIP_API;
    private DefuseKit? defuseKit;
    private PluginCapability<IVipCoreApi> pluginCapabilty { get; } = new("vipcore:core");
    public override void Load(bool hotReload) => Server.NextWorldUpdate(Initialize);
    public override void OnAllPluginsLoaded(bool hotReload) => Initialize();

    private void Initialize()
    {
        if (VIP_API != null) return;
        VIP_API = pluginCapabilty.Get();
        if (VIP_API == null) return;
        VIP_API.OnCoreReady += RegisterFeature;
        RegisterFeature();
    }

    private void RegisterFeature()
    {
        if (VIP_API == null || defuseKit != null) return;
        defuseKit = new DefuseKit(this, VIP_API);
        VIP_API.RegisterFeature(defuseKit);
        Logger.LogInformation("DefuseKit registered; configured values: {Values}",
            string.Join(" | ", VIP_API.GetAllRegisteredFeatures()
                .Where(entry => entry.feature == "DefuseKit")
                .Select(entry => entry.value?.ToString())));
    }

    public override void Unload(bool hotReload)
    {
        if (VIP_API == null) return;
        VIP_API.OnCoreReady -= RegisterFeature;
        if (defuseKit == null) return;
        VIP_API.UnRegisterFeature(defuseKit);
        defuseKit = null;
    }
    public class DefuseKit : VipFeatureBase
    {
        public override string Feature => "DefuseKit";

        public DefuseKit(VIP_DefuseKit defuseKit, IVipCoreApi api) : base(api)
        {

        }
        public override void OnPlayerSpawn(CCSPlayerController player)
        {
            if (!IsClientVip(player) ||
                !PlayerHasFeature(player) ||
                GetPlayerFeatureState(player) is not IVipCoreApi.FeatureState.Enabled) return;

            if (player.Team != CsTeam.CounterTerrorist)
                return;

            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
            if (gameRules == null || gameRules.WarmupPeriod)
                return;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || !player.PawnIsAlive || pawn.ItemServices == null)
                return;

            var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
            itemServices.HasDefuser = true;
        }
    }
}
