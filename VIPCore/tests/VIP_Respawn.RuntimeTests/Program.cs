using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using VIP_Respawn;
using VipCoreApi;

static void Check(bool condition, string scenario)
{
    if (!condition) throw new Exception(scenario);
}

var api = new TestApi();
var plugin = new VipRespawn();
plugin.Load(false);
Server.Pending!();
Check(plugin.Retry is { Killed: false } && plugin.Command == null, "Wait for missing provider");
PluginCapability<IVipCoreApi>.Provider = api;
plugin.Retry!.Fire();
Server.Pending();
Check(plugin.Retry.Killed && api.Registered != null && !api.HasListeners, "Single registration, no idle callbacks");

var table = Marshal.AllocHGlobal(2 * IntPtr.Size);
var first = Marshal.AllocHGlobal(IntPtr.Size);
var second = Marshal.AllocHGlobal(IntPtr.Size);
try
{
    Marshal.WriteIntPtr(first, table);
    Marshal.WriteIntPtr(second, table);
    Marshal.WriteIntPtr(table, IntPtr.Size, 100);
    var player = new CCSPlayerController { Handle = first, EntityHandle = (1, 0) };
    var other = new CCSPlayerController { Handle = second, EntityHandle = (2, 1) };
    var calls = new List<(nint Target, nint Receiver)>();
    void Record(nint target, nint receiver) => calls.Add((target, receiver));
    VirtualFunction.Called = Record;
    void Command(CCSPlayerController? caller) => plugin.Command!(caller, new object());
    void Select() => api.Registered!.OnSelectItem(player, IVipCoreApi.FeatureState.Enabled);
    void Expect(params string[] keys)
    {
        Check(api.Messages.SequenceEqual(keys), $"Expected [{string.Join(", ", keys)}], got [{string.Join(", ", api.Messages)}]");
        api.Messages.Clear();
    }

    Command(null);
    player.IsValid = false;
    Command(player);
    player.IsValid = true;
    api.Vip = false;
    Command(player);
    api.Vip = true;
    api.HasFeature = false;
    Select();
    Expect();

    api.HasFeature = true;
    api.Limit = 0;
    player.Team = CsTeam.Spectator;
    player.PawnIsAlive = true;
    Select();
    Expect("respawn.Limit");
    api.Limit = 1;
    Command(player);
    player.Team = CsTeam.None;
    Command(player);
    player.Team = CsTeam.CounterTerrorist;
    Command(player);
    Expect("respawn.InTeam", "respawn.InTeam", "respawn.IsAlive");
    player.PawnIsAlive = false;
    player.PlayerPawn.Value = null;
    Select();
    player.PlayerPawn.Value = new Pawn { IsValid = false };
    Command(player);
    Expect();
    Check(GameData.Reads == 0 && player.PawnAssignments == 0, "Rejected requests never bind or respawn");

    player.PlayerPawn.Value = new Pawn();
    Command(player);
    Select();
    Command(other);
    Expect("respawn.Success", "respawn.Limit", "respawn.Success");
    Check(calls.SequenceEqual([(100, first), (100, second)]) && VirtualFunction.Binds == 1,
        "Both players share a binding and keep independent quotas");
    api.Limit = 2;
    Select();
    api.Limit = 1;
    Select();
    Expect("respawn.Success", "respawn.Limit");

    // The entity index stays the same when a freed slot is reused, its serial changes.
    player.EntityHandle = (1u + (1u << 15), 0);
    Marshal.WriteIntPtr(table, IntPtr.Size, 200);
    Command(player);
    Expect("respawn.Success");
    Check(calls[^1] == (200, first) && VirtualFunction.Binds == 2 && GameData.Reads == 1,
        "Reconnect starts fresh and a replaced vtable entry is rebound");

    plugin.RoundStart!();
    VirtualFunction.Called = (_, _) => throw new InvalidOperationException("Native failure");
    try
    {
        Command(player);
        throw new Exception("Missing native error");
    }
    catch (InvalidOperationException ex) when (ex.Message == "Native failure") { }
    Expect();
    VirtualFunction.Called = (target, receiver) =>
    {
        Record(target, receiver);
        VirtualFunction.Called = Record;
        Select();
    };
    var before = calls.Count;
    Command(player);
    Command(player);
    Expect("respawn.Success", "respawn.Limit");
    Check(calls.Count == before + 1, "Retry succeeds and a synchronous reentrant request is ignored");

    plugin.RoundStart();
    VirtualFunction.Called = (_, _) => plugin.RoundStart();
    Command(player);
    VirtualFunction.Called = Record;
    Command(player);
    Select();
    Expect("respawn.Success", "respawn.Success", "respawn.Limit");
}
finally
{
    Marshal.FreeHGlobal(second);
    Marshal.FreeHGlobal(first);
    Marshal.FreeHGlobal(table);
}

plugin.Unload(true);
Check(api.Registered == null && !api.HasListeners, "Clean unload");
var reload = new VipRespawn();
reload.Load(true);
Server.Pending!();
Check(api.Registered != null && reload.Retry == null, "Late load after core-ready");
reload.Unload(true);
var cancelled = new VipRespawn();
cancelled.Load(false);
cancelled.Unload(false);
Server.Pending!();
Check(api.Registered == null, "Unload before queued initialization");

Console.WriteLine("VIP_Respawn runtime checks passed.");
