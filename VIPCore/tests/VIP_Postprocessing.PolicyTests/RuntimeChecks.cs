using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using VIP_Postprocessing;
using VipCoreApi;

internal static class RuntimeChecks
{
    private static void Expect(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }

    internal static void Run()
    {
        var plugin = new VipPostprocessing();
        plugin.Load(true); // A hot reload between maps must also wait for the entity system.
        plugin.OnAllPluginsLoaded(false);
        Server.Advance();
        var retry = plugin.Timers.Single();
        var api = new IVipCoreApi();
        PluginCapability<IVipCoreApi>.Provider = api;
        retry.Fire();
        api.Ready();
        Expect(retry.Killed && api.Feature != null, "late core registration");
        Expect(Utilities.WorldScans == 0, "no world scan between maps");
        var config = Directory.CreateTempSubdirectory("vip-postprocessing-");
        try
        {
            api.CoreConfigDirectory = config.FullName;
            File.WriteAllText(Path.Combine(config.FullName, "vip.json"), """
                {"Groups":{"invalid":null,"vip":{"Values":{"Postprocessing":[
                    null,{"file":7},{"file":"warm.vpost"},{"file":"warm.vpost"}
                ]}}}}
                """);
            var manifest = new ResourceManifest();
            plugin.Listener<Listeners.OnServerPrecacheResources>()!(manifest);
            Expect(manifest.Files.SequenceEqual(["warm.vpost"]), "precache valid files after malformed entries");
        }
        finally
        {
            config.Delete(true);
        }
        var owner = Player(0);
        var viewer = Player(63);
        var map = Utilities.CreateEntityByName<CPostProcessingVolume>("post_processing_volume");
        plugin.Listener<Listeners.OnMapStart>()!("test");
        Server.Advance();
        plugin.Fire(new EventPlayerSpawn { Userid = owner });
        Expect(Server.Frames.Count == 0 && api.EffectReads == 0, "no deferred work without a selection");
        Expect(plugin.Listener<Listeners.CheckTransmit>() == null, "idle filter");

        api.Cookies[owner.SteamID] = """{"p":"Warm"}""";
        api.Required = "#css/vip";
        api.Loaded(owner);
        Server.Advance();
        Expect(!Personal().Any(), "a permission flag cannot substitute for group membership");
        AdminManager.GroupMember = true;
        plugin.Fire(new EventPlayerSpawn { Userid = owner });
        api.Loaded(owner);
        Expect(Server.Frames.Count == 1, "coalesced spawn and VIP load");
        Server.Advance();
        var volume = Personal().Single();
        Expect(volume.Master, "selected effect activated");

        void CheckSnapshot(CCSPlayerController? recipient, bool visible, string scenario)
        {
            var info = new TransmitInfo();
            info.TransmitEntities.UnionWith([volume.EntityHandle.Index, map.EntityHandle.Index]);
            plugin.Listener<Listeners.CheckTransmit>()!(new() { (info, recipient) });
            Expect(info.TransmitEntities.SetEquals([visible ? volume.EntityHandle.Index : map.EntityHandle.Index]), scenario);
        }

        CheckSnapshot(owner, true, "live owner sees the effect");
        CheckSnapshot(viewer, false, "other live player sees the map");
        CheckSnapshot(null, false, "unknown recipient sees the map");
        var observer = new ObserverServices { ObserverTarget = new(owner.PlayerPawn.Raw) };
        viewer.Pawn.Value!.ObserverServices = observer;
        viewer.PawnIsAlive = false;
        observer.ObserverMode = (byte)ObserverMode_t.OBS_MODE_IN_EYE;
        CheckSnapshot(viewer, true, "observer follows the owner");
        observer.ObserverMode = (byte)ObserverMode_t.OBS_MODE_ROAMING;
        CheckSnapshot(viewer, false, "roaming with a stale target");
        observer.ObserverMode = (byte)ObserverMode_t.OBS_MODE_CHASE;
        owner.PlayerPawn = new(0);
        CheckSnapshot(viewer, false, "target pawn no longer belongs to the owner");
        owner.PlayerPawn = owner.Pawn;

        plugin.Fire(new EventRoundStart());
        DeleteRetiring(plugin);
        Server.Advance();
        volume = Personal().Single();
        Expect(volume.Master, "round start after an earlier spawn");
        AdminManager.GroupMember = false;
        plugin.Timers.Last().Fire();
        Expect(!volume.Master && volume.RemovalRequested, "revoked CSS group");
        DeleteRetiring(plugin);
        AdminManager.GroupMember = true;
        plugin.Fire(new EventPlayerSpawn { Userid = owner });
        Server.Advance();
        volume = Personal().Single();
        api.Access = false;
        plugin.Timers.Last().Fire();
        Expect(!volume.Master && volume.RemovalRequested, "silent access revocation");
        Expect(plugin.Listener<Listeners.CheckTransmit>() != null, "pending engine deletion stays filtered");
        CheckSnapshot(owner, false, "retiring effect hidden even from its owner");
        DeleteRetiring(plugin);
        Expect(plugin.Listener<Listeners.CheckTransmit>() == null, "last deletion detaches filter");

        api.Access = true;
        Utilities.Spawned = _ => plugin.Fire(new EventPlayerDeath { Userid = owner });
        plugin.Fire(new EventPlayerSpawn { Userid = owner });
        Server.Advance();
        Expect(Personal().Single() is { Master: false, RemovalRequested: true }, "retired during DispatchSpawn");
        DeleteRetiring(plugin);
        Utilities.Spawned = null;
        plugin.Fire(new EventPlayerSpawn { Userid = owner });
        plugin.Listener<Listeners.OnClientDisconnectPost>()!(owner.Slot);
        Player(owner.Slot);
        Server.Advance();
        Expect(!Personal().Any(), "no deferred effect on a recycled slot");
        plugin.Unload(false);
        api.Loaded(viewer);
        Expect(api.Feature == null && Server.Frames.Count == 0 && !map.RemovalRequested, "clean unload");
    }

    private static CCSPlayerController Player(int slot)
    {
        var controller = Utilities.CreateEntityByName<CCSPlayerController>("cs_player_controller");
        var pawn = Utilities.CreateEntityByName<CCSPlayerPawn>("player");
        controller.Slot = slot;
        controller.SteamID = controller.EntityHandle.Raw;
        controller.PawnIsAlive = true;
        controller.PlayerPawn = controller.Pawn = new(pawn.EntityHandle.Raw);
        pawn.OriginalController = new(controller.EntityHandle.Raw);
        Utilities.Players[slot] = controller;
        return controller;
    }

    private static IEnumerable<CPostProcessingVolume> Personal() =>
        Utilities.Entities.Values.OfType<CPostProcessingVolume>().Where(e => e.Entity.Name == "vipcore_postprocessing");

    private static void DeleteRetiring(VipPostprocessing plugin)
    {
        foreach (var volume in Personal().Where(v => v.RemovalRequested).ToArray())
        {
            plugin.Listener<Listeners.OnEntityDeleted>()!(volume);
            Utilities.Entities.Remove(volume.EntityHandle.Raw);
            volume.IsValid = false;
        }
    }
}
