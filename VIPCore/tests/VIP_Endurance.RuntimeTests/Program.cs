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
PluginCapability<IVipCoreApi>.Value = api;
var plugin = new VipEndurance();
plugin.Load(false);
plugin.OnAllPluginsLoaded(false);
Server.Advance();
api.Ready();
Require(api.Registrations == 1 && Utilities.Visits == 0, "Register once, without enumerating before map start");

var player = new CCSPlayerController { Slot = 63 };
Utilities.Players[player.Slot] = player;
api.States[player] = FeatureState.Enabled;
api.CoreEnabled = false;
Server.MapName = "test";
plugin.Listener<Listeners.OnMapStart>()(Server.MapName);
Server.Advance();
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
    Utilities.Visits = 0;
    tick();
    Require(Utilities.Visits == 0, test);
}

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
ExpectNoCandidates("Map end");
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
Server.Advance();
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

plugin = new VipEndurance();
plugin.Load(true);
plugin.OnAllPluginsLoaded(true);
Server.Advance();
tick = plugin.Listener<Listeners.OnTick>();
Console.WriteLine("Runtime regression checks passed");
Console.WriteLine("players selected slowed | original visits/checks/writes/bytes | optimized visits/checks/writes/bytes");

for (var i = 0; i < 64; i++)
{
    if (Utilities.Players[i] is { } existing) api.Removed(existing);
    Utilities.Players[i] = null;
}

var selected = new bool[64];
var originalWrites = 0;
Action original = () =>
{
    foreach (var client in Utilities.GetPlayers().Where(client =>
                 client.IsValid && client.PawnIsAlive && api.IsClientVip(client) &&
                 api.PlayerHasFeature(client) && selected[client.Slot]))
    {
        if (client.PlayerPawn.Value is { IsValid: true, VelocityModifier: < 1 } activePawn)
        {
            activePawn.VelocityModifier = 1;
            originalWrites++;
        }
    }
};

const int iterations = 10000;
foreach (var (count, active, slowed) in new[] { (0, 0, false), (64, 0, false), (64, 1, false), (64, 8, false), (64, 8, true) })
{
    var activePawns = new Pawn[active];
    for (var slot = 0; slot < count; slot++)
    {
        var client = Utilities.Players[slot] ??= new CCSPlayerController { Slot = slot };
        selected[slot] = slot < active;
        api.Toggle(client, selected[slot] ? FeatureState.Enabled : FeatureState.Disabled);
        if (selected[slot]) activePawns[slot] = client.PlayerPawn.Value!;
    }

    (int visits, int checks, int writes, long bytes) Measure(Action work)
    {
        work();
        Utilities.Visits = api.Checks = 0;
        var writes = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            if (slowed)
                foreach (var target in activePawns) target.VelocityModifier = 0.8f;
            work();
            if (slowed)
                foreach (var target in activePawns)
                    if (target.VelocityModifier == 1) writes++;
        }
        return (Utilities.Visits / iterations, api.Checks / iterations, writes / iterations,
            (GC.GetAllocatedBytesForCurrentThread() - before) / iterations);
    }

    var baseline = Measure(original);
    var candidate = Measure(tick.Invoke);
    Require(candidate.visits == active && candidate.checks == (slowed ? active * 2 : 0) &&
            candidate.writes == baseline.writes && candidate.bytes == 0, "Hot path work and writes");
    Console.WriteLine($"{count,7} {active,8} {slowed,6} | {baseline} | {candidate}");
}
Require(originalWrites == iterations * 8, "Baseline writes match the slowed fixture");

sealed class FakeApi : IVipCoreApi
{
    public event Action? OnCoreReady;
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
        + (PlayerLoaded?.GetInvocationList().Length ?? 0)
        + (PlayerRemoved?.GetInvocationList().Length ?? 0);

    public void Ready() => OnCoreReady?.Invoke();
    public void Loaded(CCSPlayerController player) => PlayerLoaded?.Invoke(player, "vip");
    public void Removed(CCSPlayerController player) => PlayerRemoved?.Invoke(player, "vip");

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
