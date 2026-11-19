using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using VIP_Endurance;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

static void Require(bool condition, string test)
{
    if (!condition) throw new InvalidOperationException(test);
}

var api = new FakeApi();
PluginCapability<IVipCoreApi>.Value = null;
var plugin = new VipEndurance();
plugin.Load(false);
plugin.OnAllPluginsLoaded(false);
Server.Advance();
Require(api.Registrations == 0 && plugin.RetryTimer != null, "Wait for late VIPCore provider");
PluginCapability<IVipCoreApi>.Value = api;
plugin.RetryTimer!.Fire();
Require(plugin.RetryTimer.Killed, "Stop initialization timer after binding");
api.Ready();
Require(api.Registrations == 1 && Utilities.Visits == 0, "Register once, without enumerating before map start");

var player = new CCSPlayerController { Slot = 64 };
Utilities.Players[0] = new CCSPlayerController { Slot = 0 }; // ordinary player during restore
Utilities.Players[player.Slot] = player;
api.States[player] = FeatureState.Enabled;
api.CoreEnabled = false;
Server.MapName = "test";
plugin.Listener<Listeners.OnMapStart>()(Server.MapName);
Server.Advance();
Server.Advance(); // apply first tick subscription after restore
var tick = plugin.Listener<Listeners.OnTick>();
var pawn = player.PlayerPawn.Value!;
pawn.VelocityModifier = 0.5f;
tick();
Require(pawn.VelocityModifier == 0.5f, "Core disabled during restore");
api.CoreEnabled = true;
tick();
Require(pawn.VelocityModifier == 1, "Restore toggle even while core is disabled");
api.Checks = 0;
tick();
pawn.VelocityModifier = 1.2f;
tick();
Require(api.Checks == 0 && pawn.VelocityModifier == 1.2f, "Skip VIP checks and writes at or above one");
pawn.VelocityModifier = float.NaN;
tick();
Require(float.IsNaN(pawn.VelocityModifier), "Preserve NaN");

pawn.VelocityModifier = 0.5f;
player.PawnIsAlive = false;
tick();
Require(pawn.VelocityModifier == 0.5f, "Dead player");
player.PawnIsAlive = true;
player.PlayerPawn.Value = new Pawn { VelocityModifier = 0.4f, IsValid = false };
var newPawn = player.PlayerPawn.Value;
tick();
Require(newPawn.VelocityModifier == 0.4f, "Invalid replacement pawn");
newPawn.IsValid = true;
player.Connected = PlayerConnectedState.Disconnected;
tick();
Require(newPawn.VelocityModifier == 0.4f, "Disconnected controller");
player.Connected = PlayerConnectedState.Connected;
api.FeatureAllowed = false;
tick();
Require(newPawn.VelocityModifier == 0.4f, "Live access revoked");
api.FeatureAllowed = true;
tick();
Require(newPawn.VelocityModifier == 1 && pawn.VelocityModifier == 0.5f, "Access restored and current pawn used");

void ExpectNoCandidates(string test)
{
    Utilities.Visits = CCSPlayerController.AliveChecks = 0;
    tick(); // even a callback already in flight must see the empty selection
    Server.Advance();
    Require(Utilities.Visits == 0 && CCSPlayerController.AliveChecks == 0 && plugin.FindListener<Listeners.OnTick>() == null, test);
}

api.SetState(player, "other", FeatureState.Disabled);
newPawn.VelocityModifier = 0.5f;
tick();
Require(newPawn.VelocityModifier == 1, "Ignore unrelated setter notifications");
api.SetState(player, "endurance", FeatureState.Disabled);
ExpectNoCandidates("External setter removes the last listener");
api.SetState(player, "endurance", FeatureState.Enabled);
Server.Advance();
newPawn.VelocityModifier = 0.5f;
plugin.Listener<Listeners.OnTick>()();
Require(newPawn.VelocityModifier == 1, "External setter wakes an idle module");

var replacement = new CCSPlayerController { Slot = player.Slot };
Utilities.Players[player.Slot] = replacement;
replacement.PlayerPawn.Value!.VelocityModifier = 0.3f;
newPawn.VelocityModifier = 0.4f;
player.IsValid = false;
tick();
Require(newPawn.VelocityModifier == 0.4f && replacement.PlayerPawn.Value.VelocityModifier == 0.3f,
    "An invalid cached entity handle cannot apply a previous selection to a replacement controller");
player.IsValid = true;
Utilities.Players[player.Slot] = player;
api.Toggle(player, FeatureState.Disabled);
ExpectNoCandidates("Toggle off");
api.Toggle(player, FeatureState.Enabled);
api.Toggle(player, FeatureState.NoAccess);
ExpectNoCandidates("No access in the new group");
api.Toggle(player, FeatureState.Enabled);
api.Removed(player);
ExpectNoCandidates("VIP removed");
api.Loaded(player);
plugin.Listener<Listeners.OnClientDisconnect>()(player.Slot);
Utilities.Players[player.Slot] = new CCSPlayerController { Slot = player.Slot };
ExpectNoCandidates("Slot reuse");
Utilities.Players[player.Slot] = player;
api.Loaded(player);
plugin.Listener<Listeners.OnMapEnd>()();
api.Loaded(player);
ExpectNoCandidates("Late player event after map end");
plugin.Listener<Listeners.OnMapStart>()("next");
plugin.Unload(true);
Server.Advance();
api.Ready();
api.Loaded(player);
ExpectNoCandidates("Queued map restore, event and tick after unload");
Require(api.Subscribers == 0 && api.Feature == null, "All VIP subscriptions removed");

plugin = new VipEndurance();
plugin.Load(true);
plugin.OnAllPluginsLoaded(true);
Server.Advance(); // initialize
Server.Advance(); // restore
Server.Advance(); // bind tick
tick = plugin.Listener<Listeners.OnTick>();
newPawn.VelocityModifier = 0.7f;
tick();
Require(newPawn.VelocityModifier == 1 && api.Registrations == 2, "Hot reload restores connected VIP");

plugin.Unload(false);
var early = new VipEndurance();
early.Load(false);
early.Unload(false);
Server.Advance();
Require(api.Registrations == 2, "Unloaded before initialization");

PluginCapability<IVipCoreApi>.Value = null;
var waiting = new VipEndurance();
waiting.Load(false);
Server.Advance();
waiting.Unload(false);
PluginCapability<IVipCoreApi>.Value = api;
waiting.RetryTimer!.Fire();
Require(waiting.RetryTimer.Killed && api.Registrations == 2, "Unload cancels retries for a missing provider");

plugin = new VipEndurance();
plugin.Load(true);
plugin.OnAllPluginsLoaded(true);
Server.Advance(); // initialize
Server.Advance(); // restore
Server.Advance(); // bind tick
tick = plugin.Listener<Listeners.OnTick>();
Console.WriteLine("Runtime regression checks passed");
Console.WriteLine("players VIPs selected slowed | baseline slotLookups/aliveChecks/vipChecks/writes/bytes | optimized");
var selected = new bool[64];
Action original = () =>
{
    foreach (var client in Utilities.GetPlayers().Where(client =>
                 client.IsValid && client.PawnIsAlive && api.IsClientVip(client) &&
                 api.PlayerHasFeature(client) && selected[client.Slot]))
    {
        if (client.PlayerPawn.Value is { IsValid: true, VelocityModifier: < 1 } activePawn)
            activePawn.VelocityModifier = 1;
    }
};

const int iterations = 10000;
Utilities.MaxPlayers = 64;
foreach (var (count, vips, active, slowed) in new[]
         {
             (0, 0, 0, 0), (64, 0, 0, 0), (64, 64, 0, 0), (64, 8, 1, 0),
             (64, 8, 8, 2), (64, 8, 8, 8), (64, 64, 8, 8), (64, 64, 64, 64)
         })
{
    foreach (var previous in Utilities.Players)
        if (previous != null) api.Removed(previous);
    Array.Clear(Utilities.Players);
    api.States.Clear();
    var slowedPawns = new Pawn[slowed];
    for (var slot = 0; slot < count; slot++)
    {
        var client = Utilities.Players[slot] = new CCSPlayerController { Slot = slot };
        selected[slot] = slot < active;
        if (slot < vips) api.Toggle(client, selected[slot] ? FeatureState.Enabled : FeatureState.Disabled);
        if (slot < slowed) slowedPawns[slot] = client.PlayerPawn.Value!;
    }

    Server.Advance();
    var listener = plugin.FindListener<Listeners.OnTick>();
    Require((listener != null) == (active > 0), "Dispatch only with selected slots");

    (int lookups, int alive, int checks, int writes, long bytes) Measure(Action work)
    {
        work();
        Utilities.Visits = CCSPlayerController.AliveChecks = api.Checks = 0;
        var writes = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            foreach (var target in slowedPawns) target.VelocityModifier = 0.8f;
            work();
            foreach (var target in slowedPawns)
                if (target.VelocityModifier == 1) writes++;
        }
        return (Utilities.Visits / iterations, CCSPlayerController.AliveChecks / iterations, api.Checks / iterations, writes / iterations,
            GC.GetAllocatedBytesForCurrentThread() - before);
    }

    var baseline = Measure(original);
    var candidate = Measure(() => listener?.Invoke());
    Require(baseline.lookups == 64 && baseline.alive == count && baseline.checks == count + vips && baseline.writes == slowed,
        "Baseline performs the expected work");
    Require(candidate.lookups == 0 && candidate.alive == active && candidate.checks == slowed * 2 &&
            candidate.writes == slowed && candidate.bytes == 0, "Hot path work, writes and total allocated bytes");
    Console.WriteLine($"{count,7} {vips,4} {active,8} {slowed,6} | " +
        $"{(baseline.lookups, baseline.alive, baseline.checks, baseline.writes, baseline.bytes / iterations)} | {candidate}");
}
plugin.Unload(false);

sealed class FakeApi : IVipCoreApi, IVipFeatureStateApi
{
    public event Action? OnCoreReady;
    public event Action<CCSPlayerController, string, FeatureState>? PlayerFeatureStateSet;
    public event Action<CCSPlayerController>? OnPlayerSpawn;
    public event Action<CCSPlayerController, string>? PlayerLoaded;
    public event Action<CCSPlayerController, string>? PlayerRemoved;
    public readonly Dictionary<CCSPlayerController, FeatureState> States = new();
    public bool CoreEnabled = true;
    public bool FeatureAllowed = true;
    public int Checks;
    public int Registrations;
    public VipFeatureBase? Feature;

    public int Subscribers => (OnCoreReady?.GetInvocationList().Length ?? 0)
        + (OnPlayerSpawn?.GetInvocationList().Length ?? 0)
        + (PlayerFeatureStateSet?.GetInvocationList().Length ?? 0)
        + (PlayerLoaded?.GetInvocationList().Length ?? 0)
        + (PlayerRemoved?.GetInvocationList().Length ?? 0);

    public void Ready() => OnCoreReady?.Invoke();
    public void Loaded(CCSPlayerController player) => PlayerLoaded?.Invoke(player, "vip");
    public void Removed(CCSPlayerController player) => PlayerRemoved?.Invoke(player, "vip");

    public FeatureState GetPlayerFeatureStateOrDefault(CCSPlayerController player, string feature) =>
        States.GetValueOrDefault(player, FeatureState.NoAccess);

    public void SetState(CCSPlayerController player, string feature, FeatureState state)
    {
        if (feature == "endurance") States[player] = state;
        PlayerFeatureStateSet?.Invoke(player, feature, state);
    }

    public void Toggle(CCSPlayerController player, FeatureState state)
    {
        States[player] = state;
        Feature!.OnSelectItem(player, state);
    }

    public void RegisterFeature(VipFeatureBase feature, FeatureType type)
    {
        if (Feature != null || feature.Feature != "endurance" || type != FeatureType.Toggle)
            throw new InvalidOperationException("Invalid feature registration");
        Registrations++;
        Feature = feature;
    }

    public void UnRegisterFeature(VipFeatureBase feature)
    {
        if (Feature == feature) Feature = null;
    }

    public FeatureState GetPlayerFeatureState(CCSPlayerController player) =>
        States.TryGetValue(player, out var state) ? state : throw new InvalidOperationException("player not found");

    public bool IsClientVip(CCSPlayerController player)
    {
        Checks++;
        return CoreEnabled && States.ContainsKey(player);
    }

    public bool PlayerHasFeature(CCSPlayerController player)
    {
        Checks++;
        return FeatureAllowed;
    }
}
