using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace VIP_Respawn;

public class VipRespawn : BasePlugin
{
    public override string ModuleAuthor => "thesamefabius";
    public override string ModuleName => "[VIP] Respawn";
    public override string ModuleVersion => "1.0.3";

    private readonly PluginCapability<IVipCoreApi> _capability = new("vipcore:core");
    private Respawn? _respawn;
    private Timer? _retry;
    private bool _unloaded;

    public override void Load(bool hotReload) => Server.NextWorldUpdate(Initialize);

    private void Initialize()
    {
        if (_unloaded || _respawn != null) return;

        IVipCoreApi? api;
        try
        {
            api = _capability.Get();
        }
        catch (KeyNotFoundException)
        {
            // CSS 1.0.376 throws if the provider has not registered yet.
            api = null;
        }
        if (api == null)
        {
            _retry ??= AddTimer(5, Initialize, TimerFlags.REPEAT);
            return;
        }

        _retry?.Kill();
        _retry = null;
        _respawn = new Respawn(this, api);
        api.RegisterFeature(_respawn, FeatureType.Selectable);
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;
        _retry?.Kill();
        if (_respawn != null)
            _respawn.Api.UnRegisterFeature(_respawn);
    }
}

public class Respawn : VipFeatureBase
{
    public override string Feature => "Respawn";

    // Only players who actually request a respawn need state. The serial in the
    // controller handle distinguishes successive connections in the same slot.
    private sealed class RoundUsage
    {
        public int Used;
        public bool InProgress;
    }

    private readonly Dictionary<uint, RoundUsage> _usage = new();
    private int? _respawnOffset;
    private nint _lastTarget;
    private Action<nint>? _invokeRespawn;

    public Respawn(VipRespawn plugin, IVipCoreApi api) : base(api)
    {
        // VipFeatureBase subscribes these three empty handlers for every feature.
        api.OnPlayerSpawn -= OnPlayerSpawn;
        api.PlayerLoaded -= OnPlayerLoaded;
        api.PlayerRemoved -= OnPlayerRemoved;

        plugin.RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _usage.Clear();
            return HookResult.Continue;
        });
        plugin.AddCommand("css_respawn", "", (player, _) => PlayerRespawn(player));
    }

    public override void OnSelectItem(CCSPlayerController player, FeatureState state) => PlayerRespawn(player);

    private void PlayerRespawn(CCSPlayerController? player)
    {
        if (player is not { IsValid: true } || !IsClientVip(player) || !PlayerHasFeature(player)) return;

        var key = player.EntityHandle.Raw;
        _usage.TryGetValue(key, out var usage);
        if (usage?.InProgress == true) return;
        if ((usage?.Used ?? 0) >= GetFeatureValue<int>(player))
        {
            PrintToChat(player, GetTranslatedText("respawn.Limit"));
            return;
        }
        if (player.Team is CsTeam.None or CsTeam.Spectator)
        {
            PrintToChat(player, GetTranslatedText("respawn.InTeam"));
            return;
        }
        if (player.PawnIsAlive)
        {
            PrintToChat(player, GetTranslatedText("respawn.IsAlive"));
            return;
        }
        if (player.PlayerPawn.Value is not { IsValid: true } pawn) return;

        if (usage == null)
        {
            usage = new RoundUsage();
            _usage.Add(key, usage);
        }
        // SetPawn and Respawn dispatch other plugins synchronously. A nested request
        // must not spend the same use. A round reset detaches this usage record.
        usage.InProgress = true;
        try
        {
            player.SetPawn(pawn);
            var handle = player.Handle;
            if (handle == 0) return;
            InvokeRespawn(handle);
            usage.Used++;
        }
        finally
        {
            usage.InProgress = false;
        }
        PrintToChat(player, GetTranslatedText("respawn.Success"));
    }

    private void InvokeRespawn(nint controller)
    {
        var offset = _respawnOffset ??= GameData.GetOffset("CCSPlayerController_Respawn");
        // CreateVoid binds the current vtable entry, not the controller. Rebind if
        // another controller implementation or a native hook changes the target.
        var vtable = Marshal.ReadIntPtr(controller);
        if (vtable == 0) throw new InvalidOperationException("Player controller has no virtual function table.");
        var target = Marshal.ReadIntPtr(vtable, checked(offset * IntPtr.Size));
        if (_invokeRespawn == null || target != _lastTarget)
        {
            if (target == 0) throw new InvalidOperationException("Player controller has no respawn function.");
            _invokeRespawn = VirtualFunction.CreateVoid<nint>(controller, offset);
            _lastTarget = target;
        }
        _invokeRespawn(controller);
    }
}
