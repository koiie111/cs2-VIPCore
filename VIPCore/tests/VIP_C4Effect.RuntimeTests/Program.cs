using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using VipCoreApi;
using VIP_C4Effect;
using static VipCoreApi.IVipCoreApi;

if (args.Contains("--benchmark"))
{
    Benchmark.Run();
    return;
}

var legacyPolicy = args.Contains("--legacy-policy");

for (var mine = 0; mine < 3; mine++)
for (var others = 0; others < 3; others++)
foreach (var seeOwn in new[] { false, true })
{
    using var qa = new Fixture();
    var owner = qa.Connect(0, 2, $$"""{"m":{{mine}},"s":{{seeOwn.ToString().ToLowerInvariant()}}}""");
    var viewers = new[] { owner, qa.Connect(1, 2, $$"""{"o":{{others}}}"""),
        qa.Connect(2, 3, $$"""{"o":{{others}}}"""), qa.Connect(63, 1, $$"""{"o":{{others}}}""") };
    if (legacyPolicy)
        foreach (var viewer in viewers) qa.OpenMenu(viewer);

    var particle = qa.Plant(owner);
    var snapshot = qa.Snapshot(viewers);
    for (var i = 0; i < viewers.Length; i++)
        Fixture.Require(snapshot[i].TransmitEntities.Contains((int)particle.EntityHandle.Index) ==
            Expected(i == 0, i == 1, mine, others, seeOwn), "Visibility matrix");
    qa.Disconnect(owner);
    snapshot = qa.Snapshot(viewers[1..]);
    foreach (var info in snapshot)
        Fixture.Require(info.TransmitEntities.Contains((int)particle.EntityHandle.Index) ==
            Expected(false, false, mine, others, seeOwn), "Orphan visibility matrix");
}

if (legacyPolicy)
{
    Console.WriteLine("VIP_C4Effect 1.0.0 visibility matrix passed.");
    return;
}

using (var qa = new Fixture())
{
    var owner = qa.Connect(0, 2);
    var viewer = qa.Connect(63, 2);
    Fixture.Require(!qa.Filtering, "Idle subscription");
    var particle = qa.Plant(owner);
    Fixture.Require(!qa.Filtering && particle.Parent is CPlantedC4, "Public plant");
    qa.Menu(viewer, "c4effect.Others");
    Fixture.Require(!qa.Filtering && qa.Visible(particle, viewer), "Friendly viewer");
    qa.Team(owner, 3);
    Fixture.Require(qa.Filtering && !qa.Visible(particle, viewer), "Owner team change");
    qa.Team(viewer, 3);
    Fixture.Require(!qa.Filtering, "Viewer team change");
    qa.Menu(viewer, "c4effect.Others");
    Fixture.Require(!qa.Visible(particle, viewer), "Others menu updates active effect");
    qa.Menu(viewer, "c4effect.Reset");
    Fixture.Require(!qa.Filtering, "Reset restores public visibility");
    qa.Menu(viewer, "c4effect.Others");
    qa.Menu(viewer, "c4effect.Others");
    qa.Menu(owner, "c4effect.Mine");
    qa.Menu(owner, "c4effect.Self");
    Fixture.Require(qa.Visible(particle, owner), "Existing SeeOwn snapshot");
    qa.Plant(owner);
    particle = qa.Particles.Single();
    Fixture.Require(!qa.Visible(particle, owner), "New SeeOwn snapshot");
    var outsider = qa.Connect(2, 2, vip: false);
    Fixture.Require(!qa.Visible(particle, outsider), "New Mine snapshot");
    var invalidSlot = new CCSPlayerController { Slot = -1, EntityHandle = new(500) };
    Fixture.Require(qa.Snapshot(null, invalidSlot).All(info => info.TransmitEntities.Count == 1),
        "Invalid receivers preserve bits");
    qa.Disconnect(viewer);
    viewer = qa.Connect(63, 3, """{"o":2}""");
    Fixture.Require(!qa.Visible(particle, viewer), "Saved filter on reconnect");
    var walks = Utilities.PlayerWalks;
    var reads = CCSPlayerController.TeamReads;
    var cookies = qa.Api.CookieReads;
    var config = qa.Api.Deserializations;
    for (var i = 0; i < 20; i++) qa.Snapshot(owner, viewer);
    Fixture.Require(walks == Utilities.PlayerWalks && reads == CCSPlayerController.TeamReads &&
        cookies == qa.Api.CookieReads && config == qa.Api.Deserializations, "Hot path work");
    var raw = particle.EntityHandle.Raw;
    particle.Remove();
    var replacement = new CParticleSystem { EntityHandle = new(raw + 0x8000) };
    Fixture.Require(!qa.Filtering && qa.Visible(replacement, viewer), "Reused index");
    Server.Advance();
    Fixture.Require(!qa.Plugin.HasListener<Listeners.OnEntityDeleted>(), "Deletion listener removed");
    qa.Plant(owner);
    qa.Plugin.Fire(new EventBombExploded());
    qa.Plugin.Fire(new EventRoundStart());
    Fixture.Require(replacement.IsValid && !qa.Filtering, "Cleanup preserves unrelated entity");
}

using (var qa = new Fixture())
{
    var owner = qa.Connect(0, 2, """{"m":1}""");
    var bot = qa.Connect(1, 3, bot: true);
    var particle = qa.Plant(owner);
    qa.Team(bot, 1);
    Fixture.Require(!qa.Filtering, "Bots alone never require a network filter");
    qa.Restart();
    particle = qa.Plant(owner);
    Fixture.Require(!qa.Filtering, "Restoring bots does not subscribe CheckTransmit");
    var relay = qa.Connect(2, 1, vip: false, bot: true, hltv: true);
    Fixture.Require(qa.Filtering && !qa.Visible(particle, relay), "HLTV remains a network recipient");
    qa.Disconnect(relay);
    var enemy = qa.Connect(2, 3, vip: false);
    Fixture.Require(qa.Filtering && !qa.Visible(particle, enemy), "Enemy joining mid-effect is filtered");
    qa.Disconnect(enemy);
    Fixture.Require(!qa.Filtering, "Last hidden recipient left");
    qa.Plugin.Fire(new EventBombPlanted(bot));
    Fixture.Require(!particle.IsValid && qa.Particles.Length == 0, "A new plant retires the previous effect");
}

foreach (var canceledBy in new[] { "defuse", "explosion", "round", "map", "unload", "second plant" })
{
    using var qa = new Fixture();
    var owner = qa.Connect(0, 2);
    var guest = qa.Connect(1, 2, vip: false);
    qa.Plugin.Fire(new EventBombPlanted(owner));
    switch (canceledBy)
    {
        case "defuse": qa.Plugin.Fire(new EventBombDefused(guest)); break;
        case "explosion": qa.Plugin.Fire(new EventBombExploded()); break;
        case "round": qa.Plugin.Fire(new EventRoundStart()); break;
        case "map": qa.Plugin.Invoke<Listeners.OnMapEnd>(call => call()); break;
        case "unload": qa.Plugin.Unload(true); break;
        case "second plant": qa.Plugin.Fire(new EventBombPlanted(guest)); break;
    }
    Server.Advance();
    Fixture.Require(qa.Particles.Length == 0, $"Pending plant canceled by {canceledBy}");
}

using (var qa = new Fixture())
{
    var owner = qa.Connect(0, 2, """{"m":2,"s":true}""");
    qa.Plugin.Fire(new EventBombPlanted(owner));
    qa.Disconnect(owner);
    var newcomer = qa.Connect(0, 2);
    Server.Advance();
    Fixture.Require(!qa.Visible(qa.Particles.Single(), newcomer), "Deferred owner disconnected");
}

using (var qa = new Fixture())
{
    var owner = qa.Connect(0, 2, """{"p":"missing","m":99,"o":99}""");
    var defuser = qa.Connect(1, 3, "broken");
    var guest = qa.Connect(2, 3, vip: false);
    var bot = qa.Connect(3, 3, bot: true);
    qa.Plugin.Fire(new EventBombPlanted(bot));
    Server.Advance();
    Fixture.Require(qa.Particles.Length == 0, "Bot has no effect");
    var plant = qa.Plant(owner);
    Fixture.Require(plant.EffectName == "plant.vpcf" && qa.Plugin.Timers.Count == 0, "Default plant and infinite lifetime");
    plant.AbsOrigin = new Vector(4, 5, 6);
    qa.Plugin.Fire(new EventBombDefused(defuser));
    var defuse = qa.Particles.Single();
    Fixture.Require(defuse.AbsOrigin == new Vector(4, 5, 16) && !plant.IsValid, "Defuse uses plant origin");
    var timer = qa.Plugin.Timers.Single();
    Fixture.Require(timer.Interval == 3f, "Default defuse lifetime");
    timer.Fire();
    Fixture.Require(!defuse.IsValid && qa.Plugin.Timers.Count == 0, "Expiry releases timer");
    var checks = Utilities.Searches;
    qa.Plant(owner).Remove();
    qa.Plugin.Fire(new EventBombDefused(defuser));
    Fixture.Require(Utilities.Searches == checks + 1 &&
        qa.Particles.Single().AbsOrigin == new Vector(1, 2, 13), "Defuse reuses known bomb handle");
    qa.Plugin.Fire(new EventRoundStart());
    qa.Plant(owner).Remove();
    qa.Bomb.Remove();
    var newBomb = new CPlantedC4 { EntityHandle = new(98 + 0x8000), DesignerName = "planted_c4",
        AbsOrigin = new Vector(1, 2, 3) };
    qa.Plugin.Fire(new EventBombDefused(defuser));
    Fixture.Require(Utilities.Searches == checks + 3 &&
        qa.Particles.Single().AbsOrigin == new Vector(1, 2, 13), "Defuse locates replacement for stale bomb handle");
    qa.Plugin.Fire(new EventRoundStart());
    qa.Plant(owner);
    var searches = Utilities.Searches;
    qa.Plugin.Fire(new EventBombDefused(guest));
    Fixture.Require(searches == Utilities.Searches && qa.Particles.Length == 0, "Guest defuse skips search");
    qa.Plugin.Fire(new EventBombDefused(defuser));
    Fixture.Require(qa.Particles.Single().AbsOrigin == new Vector(1, 2, 13), "Bomb lookup fallback");
    timer.FireStale();
    Fixture.Require(qa.Particles.Length == 1, "Stale timer preserves new effect");
    newBomb.Remove();
    qa.Plugin.Fire(new EventBombDefused(defuser));
    Fixture.Require(qa.Particles.Any(effect => effect.AbsOrigin == new Vector(7, 8, 19)), "Pawn fallback");
}

using (var qa = new Fixture(notifying: true))
{
    var owner = qa.Connect(0, 2);
    var viewer = qa.Connect(1, 3, """{"o":2}""");
    qa.Restart();
    Fixture.Require(!qa.Visible(qa.Plant(owner), viewer), "Hot reload restores existing player");
    qa.Api.RemovePlayer(viewer);
    Fixture.Require(!qa.Filtering, "VIP removal restores public visibility");
    var parsed = qa.Api.Deserializations;
    qa.OpenMenu(owner);
    Fixture.Require(parsed == qa.Api.Deserializations, "Group config cached");
    qa.Api.Entries["Test"] = """[null,{"name":null},{"name":"broken","particle":42},{"name":"new","particle":"new.vpcf","lifetime":5}]""";
    qa.Menu(owner, "c4effect.Plant");
    var oldMenu = qa.Api.Menu!;
    ((NotifyingApi)qa.Api).Reload();
    oldMenu.Click(owner, "P");
    Fixture.Require(!qa.Api.Cookies.ContainsKey(owner.SteamID) && qa.Api.Menu != oldMenu,
        "An obsolete menu option cannot overwrite preferences after reload");
    Fixture.Require(qa.Plant(owner).EffectName == "new.vpcf", "Reload invalidates group cache");
    Fixture.Require(qa.Plugin.Timers.Single().Interval == 5f, "Configured lifetime");
    qa.Api.Entries["Second"] = """[{"name":"Other","particle":"other.vpcf"}]""";
    qa.Api.LoadPlayer(owner, "Second");
    Fixture.Require(qa.Plant(owner).EffectName == "other.vpcf" && qa.Plugin.Timers.Count == 0,
        "Group change uses current group and cancels old timer");
    qa.Api.States[owner.SteamID] = FeatureState.Disabled;
    qa.Plugin.Fire(new EventRoundStart());
    qa.Plugin.Fire(new EventBombPlanted(owner));
    Server.Advance();
    Fixture.Require(qa.Particles.Length == 0 && qa.Plugin.Timers.Count == 0, "Access checked on cache hit");
    qa.Api.States[owner.SteamID] = FeatureState.Enabled;
    qa.Menu(owner, "c4effect.Plant");
    qa.Api.Menu!.Click(owner, "c4effect.Disable");
    parsed = qa.Api.Deserializations;
    ((NotifyingApi)qa.Api).Reload();
    qa.Plugin.Fire(new EventBombPlanted(owner));
    Fixture.Require(parsed == qa.Api.Deserializations && Server.Pending.Count == 0, "Off skips config and NextFrame");
}

using (var qa = new Fixture())
{
    var owner = qa.Connect(0, 2);
    var viewer = qa.Connect(1, 3);
    qa.Api.Cookies[viewer.SteamID] = """{"o":2}""";
    qa.Plugin.Invoke<Listeners.OnMapEnd>(call => call());
    qa.Plugin.Invoke<Listeners.OnMapStart>(call => call("de_dust2"));
    Server.Advance();
    Fixture.Require(!qa.Visible(qa.Plant(owner), viewer), "Existing player preferences refreshed on map start");
    qa.Plugin.Fire(new EventBombDefused(owner));
    qa.Plugin.Fire(new EventBombDefused(owner));
    var effects = qa.Particles;
    Fixture.Require(effects.Length == 2 && effects.All(effect => !qa.Visible(effect, viewer)),
        "Multiple active effects from one owner");
    qa.Plugin.Timers[0].Fire();
    Fixture.Require(!effects[0].IsValid && effects[1].IsValid && qa.Filtering, "Independent timers");
    qa.Plugin.Timers[0].Fire();
    Fixture.Require(qa.Particles.Length == 0 && qa.Plugin.Timers.Count == 0 && !qa.Filtering,
        "Last timer expires and removes filtering");
}

using (var qa = new Fixture(late: true))
{
    Fixture.Require(qa.Api.Feature == null && qa.Plugin.Timers.Count == 1, "Missing capability schedules one retry");
    qa.Provide();
    qa.Plugin.Timers.Single().Fire();
    qa.Api.Ready();
    Server.Advance();
    Fixture.Require(qa.Api.Feature != null && qa.Plugin.Timers.Count == 0, "Late capability initialization");
    var manifest = new ResourceManifest();
    File.WriteAllText(Path.Combine(qa.Api.CoreConfigDirectory, "vip.json"), """
        {"Groups":{"broken":null,"QA":{"Values":{"C4Effect":[null,{"particle":3},{"particle":"ok.vpcf"},]}}}}
        """);
    qa.Plugin.Invoke<Listeners.OnServerPrecacheResources>(call => call(manifest));
    Fixture.Require(manifest.Resources.SequenceEqual(["ok.vpcf"]), "Precache survives invalid neighbors");
}

Console.WriteLine("VIP_C4Effect runtime checks passed.");

static bool Expected(bool self, bool allied, int mine, int others, bool own) =>
    self ? own : (mine == 0 || mine == 1 && allied) && (others == 0 || others == 1 && allied);

internal sealed class Fixture : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("vip-c4effect-").FullName;
    internal FakeApi Api { get; }
    internal VipC4Effect Plugin { get; private set; } = new();
    internal CPlantedC4 Bomb { get; }
    internal bool Filtering => Plugin.HasListener<Listeners.CheckTransmit>();
    internal CParticleSystem[] Particles => CEntityInstance.Entities.Values.OfType<CParticleSystem>().ToArray();

    internal Fixture(bool notifying = false, bool late = false)
    {
        Array.Clear(Utilities.Players);
        CEntityInstance.Entities.Clear();
        Server.Pending.Clear();
        Utilities.NextEntity = 100;
        Bomb = new CPlantedC4 { EntityHandle = new(98), DesignerName = "planted_c4", AbsOrigin = new(1, 2, 3) };
        Api = notifying ? new NotifyingApi(_directory) : new FakeApi(_directory);
        Api.Entries["Test"] = """[{"name":"P","particle":"plant.vpcf"},{"name":"D","particle":"defuse.vpcf","defuse":true}]""";
        PluginCapability<IVipCoreApi>.Value = late ? null : Api;
        Start();
    }

    private void Start()
    {
        BasePlugin.Instance = Plugin;
        Plugin.Load(true);
        var walks = Utilities.PlayerWalks;
        Plugin.OnAllPluginsLoaded(true);
        Require(Utilities.PlayerWalks == walks, "Plugin startup leaves native player lookup to world update");
        Server.Advance();
    }

    internal void Provide() => PluginCapability<IVipCoreApi>.Value = Api;

    internal void Restart()
    {
        Plugin.Unload(true);
        Plugin = new VipC4Effect();
        Start();
    }

    internal CCSPlayerController Connect(int slot, int team, string? cookie = null, bool vip = true, bool bot = false,
        bool hltv = false)
    {
        var identity = Utilities.NextEntity++;
        var pawn = new Pawn { EntityHandle = new(Utilities.NextEntity++), AbsOrigin = new(7, 8, 9) };
        var player = new CCSPlayerController { Slot = slot, EntityHandle = new(identity), SteamID = identity,
            TeamNum = team, IsBot = bot, IsHLTV = hltv,
            PlayerPawn = new(pawn.EntityHandle.Raw) };
        Utilities.Players[slot] = player;
        Plugin.Invoke<Listeners.OnClientPutInServer>(call => call(slot));
        if (cookie != null) Api.Cookies[player.SteamID] = cookie;
        if (vip) Api.LoadPlayer(player, "Test");
        return player;
    }

    internal void Team(CCSPlayerController player, int team)
    {
        player.TeamNum = team;
        Plugin.Fire(new EventPlayerTeam(player, team));
    }

    internal void Disconnect(CCSPlayerController player)
    {
        Utilities.Players[player.Slot] = null;
        Plugin.Invoke<Listeners.OnClientDisconnectPost>(call => call(player.Slot));
    }

    internal CParticleSystem Plant(CCSPlayerController owner)
    {
        Plugin.Fire(new EventBombPlanted(owner));
        Server.Advance();
        return Particles.Last();
    }

    internal void OpenMenu(CCSPlayerController player) => Api.Feature!.OnSelectItem(player, FeatureState.Enabled);
    internal void Menu(CCSPlayerController player, string item)
    {
        OpenMenu(player);
        Api.Menu!.Click(player, item);
    }

    internal CCheckTransmitInfo[] Snapshot(params CCSPlayerController?[] players)
    {
        var entities = Particles;
        var rows = players.Select(player =>
        {
            var info = new CCheckTransmitInfo();
            foreach (var entity in entities) info.TransmitEntities.Add((int)entity.EntityHandle.Index);
            return (info, player);
        }).ToArray();
        Plugin.Listener<Listeners.CheckTransmit>()?.Invoke(new(rows));
        return rows.Select(row => row.info).ToArray();
    }

    internal bool Visible(CParticleSystem effect, CCSPlayerController viewer) =>
        Snapshot(viewer)[0].TransmitEntities.Contains((int)effect.EntityHandle.Index);

    internal static void Require(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }

    public void Dispose()
    {
        Plugin.Unload(true);
        if (Plugin.ModuleVersion != "1.0.0") Require(!Api.HasCallbacks && Api.Feature == null && Plugin.Timers.Count == 0, "Unload cleanup");
        File.Delete(Path.Combine(_directory, "vip.json"));
        Directory.Delete(_directory, recursive: false);
    }
}
