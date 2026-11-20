using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using VIP_PlayerParticle;
using VipCoreApi;
using static VipCoreApi.IVipCoreApi;

if (args.Contains("--benchmark"))
{
    ManagedBenchmark.Run();
    return;
}

VerifyPolicy();
VerifyPolicyTransitions();
VerifyLifecycle(notifications: false);
VerifyLifecycle(notifications: true);
Console.WriteLine("PlayerParticle policy and lifecycle checks passed.");

static void VerifyPolicy()
{
    var policy = new ParticleVisibility();
    foreach (var mine in Enum.GetValues<MineMode>())
    foreach (var others in Enum.GetValues<OthersMode>())
    foreach (var seeOwn in new[] { false, true })
    foreach (var ownerTeam in new[] { 0, 1, 2, 3 })
    foreach (var viewerTeam in new[] { 0, 1, 2, 3 })
    {
        policy.SetViewer(64, true, ownerTeam, mine, others, seeOwn);
        policy.SetViewer(1, true, viewerTeam, MineMode.All, others, true);
        policy.SetParticle(64, 300);

        var sameTeam = ownerTeam > 1 && ownerTeam == viewerTeam;
        var ownerAllows = mine switch { MineMode.Team => sameTeam, MineMode.Self => false, _ => true };
        var viewerAllows = others switch { OthersMode.Team => sameTeam, OthersMode.None => false, _ => true };
        Check((policy.HiddenIndices(64).Length != 0) == !seeOwn, "Own particle visibility");
        Check((policy.HiddenIndices(1).Length != 0) == !(ownerAllows && viewerAllows), "Other particle visibility");
        Check(policy.NeedsFilter == (!seeOwn || !(ownerAllows && viewerAllows)), "Filter eligibility");
        policy.SetParticle(64, 700);
        Check(!policy.HiddenIndices(1).Contains(300) && !policy.HiddenIndices(64).Contains(300),
            "Replacing an index invalidates already materialized lists");
        policy.SetParticle(64, 0);
        Check(!policy.NeedsFilter, "Last particle clears all masks");
    }
}

static void VerifyPolicyTransitions()
{
    var rng = new Random(32);
    var policy = new ParticleVisibility();
    var states = new (bool Connected, int Team, MineMode Mine, OthersMode Others, bool SeeOwn)[ParticleVisibility.Slots];
    var active = new bool[states.Length];
    for (var change = 0; change < 1000; change++)
    {
        var slot = rng.Next(states.Length);
        if (rng.Next(3) == 0)
        {
            active[slot] = !active[slot];
            policy.SetParticle(slot, active[slot] ? 100 + slot : 0);
        }
        else
        {
            var next = (Connected: rng.Next(5) != 0, Team: rng.Next(4), Mine: (MineMode)rng.Next(3),
                Others: (OthersMode)rng.Next(3), SeeOwn: rng.Next(4) != 0);
            states[slot] = next;
            policy.SetViewer(slot, next.Connected, next.Team, next.Mine, next.Others, next.SeeOwn);
        }

        var needsFilter = false;
        for (var viewer = 0; viewer < states.Length; viewer++)
        {
            UInt128 expected = 0;
            var target = states[viewer];
            if (target.Connected)
            {
                for (var owner = 0; owner < states.Length; owner++)
                {
                    if (!active[owner]) continue;
                    var source = states[owner];
                    var sameTeam = source.Team > 1 && source.Connected && source.Team == target.Team;
                    var hidden = owner == viewer ? !source.SeeOwn :
                        source.Mine == MineMode.Self || target.Others == OthersMode.None ||
                        (!sameTeam && (source.Mine == MineMode.Team || target.Others == OthersMode.Team));
                    if (hidden) expected |= UInt128.One << owner;
                }
            }
            UInt128 observed = 0;
            foreach (var index in policy.HiddenIndices(viewer))
            {
                var owner = index - 100;
                Check((uint)owner < states.Length && (observed & (UInt128.One << owner)) == 0,
                    "Snapshot contains only unique current entity indices");
                observed |= UInt128.One << owner;
            }
            Check(observed == expected, $"Visibility after change {change}, viewer {viewer}");
            needsFilter |= expected != 0;
        }
        Check(policy.NeedsFilter == needsFilter, "Subscription follows every visibility transition");
    }
}

static void VerifyLifecycle(bool notifications)
{
    const string fire = """[{"name":"Fire","particle":"fire.vpcf","offset":15}]""";
    const string aura = """[{"name":"Aura","particle":"aura.vpcf","offset":40}]""";
    var directory = Directory.CreateTempSubdirectory("particle-tests");
    try
    {
        var api = notifications ? new NotifyingApi(directory.FullName) : new FakeApi(directory.FullName);
        PluginCapability<IVipCoreApi>.Value = null;
        var plugin = new VipPlayerParticle();
        BasePlugin.Instance = plugin;
        void Reload()
        {
            if (api is NotifyingApi modern) modern.NotifyAvailability();
            else plugin.Reload!(null, null);
        }
        var owner = Join(0, 1, 2, 1000);
        var viewer = Join(64, 2, 2, 1010);
        var guest = Join(2, 3, 2, 1020);
        var bot = Join(3, 0, 3, 1100, bot: true);
        api.Entries["premium"] = fire;
        api.Entries["other"] = aura;
        api.Cookies[owner.SteamID] = """{"p":"Fire","h":25,"m":1,"o":0,"s":true}""";
        api.Cookies[viewer.SteamID] = """{"p":"off","o":2}""";
        api.LoadPlayer(owner, "premium");
        api.LoadPlayer(viewer, "premium");
        plugin.Load(hotReload: true);
        plugin.OnAllPluginsLoaded(hotReload: true);
        Server.Advance();
        Check(plugin.Timers.Single().Active && api.Feature == null, "Missing core schedules one retry");
        PluginCapability<IVipCoreApi>.Value = api;
        plugin.Timers.Single().Fire();
        Check(!plugin.Timers.Single().Active, "Retry releases after the late core registers");
        api.Feature!.OnSelectItem(owner, FeatureState.Enabled);
        Check(api.Menu != null, "Hot-loaded module offers the menu");
        plugin.Fire(new EventPlayerSpawn(owner));
        Check(Server.Pending.Count == 0 && Utilities.Created == 0, "Hot load waits for precache");

        StartMap(plugin, directory.FullName, fire);
        api.LoadPlayer(viewer, "premium");
        var deserializations = api.Deserializations;
        plugin.Fire(new EventPlayerSpawn(viewer));
        plugin.Fire(new EventPlayerSpawn(guest));
        Check(Server.Pending.Count == 0 && deserializations == api.Deserializations, "Off and guest skip spawn work");

        plugin.Fire(new EventPlayerSpawn(owner));
        plugin.Fire(new EventPlayerSpawn(owner));
        Check(Server.Pending.Count == 1, "Duplicate spawn requests coalesce");
        Server.Advance();
        var particle = Particle();
        Check(particle.EffectName == "fire.vpcf" && particle.Origin?.Z == 25 && Utilities.Created == 1,
            "Legacy selection and height are restored once");
        Check(IsHidden(plugin, viewer, particle) && !IsHidden(plugin, owner, particle) &&
              !IsHidden(plugin, guest, particle), "The opted-out viewer still filters other particles");

        if (api is NotifyingApi modern)
        {
            api.Available = false;
            modern.NotifyAvailability();
            modern.NotifyAvailability();
            Check(Server.Pending.Count == 1, "Global changes coalesce before the next frame");
            Server.Advance();
            Check(!Particles().Any() && !plugin.HasListener<Listeners.CheckTransmit>(), "Global disable clears effects");
            api.Available = true;
            modern.NotifyAvailability();
            Server.Advance();
            particle = Particle();
            Check(IsHidden(plugin, viewer, particle), "Global enable restores both owners and viewers");
        }
        api.LoadPlayer(viewer, "no_particle");
        Check(!plugin.HasListener<Listeners.CheckTransmit>(), "Lost group access resets the viewer policy");
        api.LoadPlayer(viewer, "premium");
        Check(IsHidden(plugin, viewer, particle), "Restoring access reloads the saved viewer preference");
        api.SetState(viewer, FeatureState.Disabled);
        Check(!plugin.HasListener<Listeners.CheckTransmit>(), "Disabling the feature releases the viewer filter");
        api.SetState(viewer, FeatureState.Enabled);
        Check(IsHidden(plugin, viewer, particle), "Re-enabling the feature restores the saved viewer policy");
        api.RemovePlayer(viewer);
        plugin.Fire(new EventPlayerSpawn(bot));
        plugin.Fire(new EventPlayerTeam(bot, 3));
        Check(!plugin.HasListener<Listeners.CheckTransmit>(), "Opposing bots cannot keep the callback active");

        plugin.Invoke<Listeners.OnClientPutInServer>(cb => cb(4));
        var spectator = Join(4, 0, 1, 1200, bot: true, hltv: true);
        Check(IsHidden(plugin, spectator, particle), "Entering slot is filtered before its controller is ready");
        plugin.Fire(new EventPlayerConnectFull(spectator));
        Check(IsHidden(plugin, spectator, particle), "HLTV remains a network recipient");
        plugin.Invoke<Listeners.OnClientDisconnectPost>(cb => cb(4));
        Utilities.Players[4] = null;
        Check(!plugin.HasListener<Listeners.CheckTransmit>(), "Unrestricted effect has no callback");
        guest.TeamNum = 3;
        plugin.Fire(new EventPlayerTeam(guest, 3));
        Check(IsHidden(plugin, guest, particle), "Team change updates the next snapshot");
        guest.TeamNum = 2;
        plugin.Fire(new EventPlayerTeam(guest, 2));
        Check(!plugin.HasListener<Listeners.CheckTransmit>(), "Returning to the same team removes the callback");

        api.Feature.OnSelectItem(owner, FeatureState.Enabled);
        api.Menu!.Click(owner, "playerparticle.Self");
        Check(ReferenceEquals(Particle(), particle) && IsHidden(plugin, owner, particle), "Self toggle updates visibility in place");
        particle.Remove();
        Utilities.NextEntity = particle.EntityHandle.Raw + 0x8000;
        var unrelated = Utilities.CreateEntityByName<CParticleSystem>("info_particle_system")!;
        Check(unrelated.EntityHandle.Index == particle.EntityHandle.Index &&
              !IsHidden(plugin, owner, unrelated), "A recycled entity index stays visible");
        unrelated.Remove();
        Server.Advance();
        Check(!plugin.HasListener<Listeners.OnEntityDeleted>(), "Deletion listener releases after external deletion");

        plugin.Fire(new EventPlayerSpawn(owner));
        plugin.Fire(new EventPlayerDeath(owner));
        plugin.Fire(new EventPlayerSpawn(owner));
        Server.Advance();
        Check(Particle().EffectName == "fire.vpcf", "Same-frame death and respawn apply the new request");
        var oldParticle = Particle();
        plugin.Fire(new EventPlayerSpawn(owner));
        var newPawn = new Pawn { EntityHandle = new(1301) };
        owner.PlayerPawn = new(newPawn.EntityHandle.Raw);
        Server.Advance();
        Check(!oldParticle.IsValid && Particle().Parent == newPawn, "Deferred spawn follows the current live pawn");

        plugin.Fire(new EventPlayerSpawn(owner));
        Reload();
        plugin.Fire(new EventRoundEnd());
        api.LoadPlayer(owner, "premium");
        api.Feature.OnSelectItem(owner, FeatureState.Enabled);
        api.Menu!.Click(owner, "playerparticle.Particle");
        api.Menu!.Click(owner, "Fire");
        Server.Advance();
        Check(!Particles().Any(), "Reload, selection and queued spawn respect round end");
        plugin.Fire(new EventRoundPrestart());
        plugin.Fire(new EventPlayerSpawn(owner));
        Server.Advance();
        plugin.Fire(new EventRoundStart());
        Check(Particle().Parent == newPawn, "Round start preserves an already spawned player's effect");
        var created = Utilities.Created;
        api.LoadPlayer(owner, "premium");
        Check(Utilities.Created == created, "Repeated apply reuses the same pawn, resource and height");

        api.Feature.OnSelectItem(owner, FeatureState.Enabled);
        api.Menu!.Click(owner, "playerparticle.Particle");
        var staleMenu = api.Menu!;
        api.LoadPlayer(owner, "other");
        staleMenu.Click(owner, "Fire");
        Check(!Particles().Any() && !plugin.HasListener<Listeners.CheckTransmit>(), "Group change gates new resources");
        Check(api.Cookies[owner.SteamID].Contains("Fire"), "Stale selection does not overwrite preference");

        api.LoadPlayer(owner, "premium");
        api.Entries["premium"] = """[{"name":"Renamed","particle":"fire.vpcf","offset":80}]""";
        Reload();
        Server.Advance();
        Check(Particle().EffectName == "fire.vpcf", "Missing saved name falls back to the first current entry");
        api.Feature.OnSelectItem(owner, FeatureState.Enabled);
        api.Menu!.Click(owner, "playerparticle.Reset");
        Check(Particle().Origin?.Z == 80 && !plugin.HasListener<Listeners.CheckTransmit>(),
            "Reset restores the reloaded height and the default visibility");
        api.Entries["premium"] = """[{"name":"Renamed","particle":"fire.vpcf","offset":90}]""";
        Reload();
        Server.Advance();
        Check(Particle().Origin?.Z == 90, "Reload applies a changed offset for an already precached path");
        api.Entries["premium"] = """[{"name":"Renamed","particle":"FIRE.vpcf"}]""";
        Reload();
        Server.Advance();
        Check(!Particles().Any(), "A differently cased path must appear in the manifest before use");
        api.Entries["premium"] = aura;
        Reload();
        Server.Advance();
        Check(!Particles().Any(), "Reload blocks a path absent from this map's manifest");

        plugin.Invoke<Listeners.OnMapEnd>(listener => listener());
        StartMap(plugin, directory.FullName, aura);
        plugin.Fire(new EventPlayerSpawn(owner));
        Server.Advance();
        Check(Particle().EffectName == "aura.vpcf", "New resource applies after the next precache");

        plugin.Fire(new EventPlayerSpawn(owner));
        plugin.Invoke<Listeners.OnClientDisconnectPost>(listener => listener(owner.Slot));
        owner.Remove();
        var replacement = Join(0, 4, 2, 1030);
        api.Cookies[replacement.SteamID] = "{";
        api.LoadPlayer(replacement, "premium");
        plugin.Fire(new EventPlayerSpawn(replacement));
        Server.Advance();
        Check(!Particles().Any(), "Slot reuse and damaged cookie keep the default off");

        api.Feature.OnSelectItem(replacement, FeatureState.Enabled);
        api.Menu!.Click(replacement, "playerparticle.Particle");
        api.Menu!.Click(replacement, "Aura");
        Check(Particle().EffectName == "aura.vpcf", "First manual selection creates a particle");
        api.Cookies[replacement.SteamID] = """{"p":"Aura","h":1e100}""";
        api.LoadPlayer(replacement, "premium");
        Check(Particle().Origin?.Z == 40, "An overflowing saved height falls back to the configured height");
        plugin.Fire(new EventPlayerSpawn(replacement));
        plugin.Unload(hotReload: true);
        Server.Advance();
        Check(!Particles().Any() && !api.HasCallbacks && !plugin.HasListener<Listeners.CheckTransmit>() &&
              !plugin.HasListener<Listeners.OnEntityDeleted>(), "Unload detaches callbacks and cancels stale work");
    }
    finally
    {
        directory.Delete(recursive: true);
        Array.Clear(Utilities.Players);
        CEntityInstance.Entities.Clear();
        Utilities.Created = 0;
        Utilities.NextEntity = 100;
    }
}

static CCSPlayerController Join(int slot, ulong steamId, int team, uint handle, bool bot = false, bool hltv = false)
{
    var pawn = new Pawn { EntityHandle = new(handle + 1) };
    var player = new CCSPlayerController
    {
        EntityHandle = new(handle), Slot = slot, SteamID = steamId, TeamNum = team, IsBot = bot, IsHLTV = hltv,
        PlayerPawn = new(pawn.EntityHandle.Raw)
    };
    Utilities.Players[slot] = player;
    return player;
}

static void StartMap(BasePlugin plugin, string directory, string entries)
{
    File.WriteAllText(Path.Combine(directory, "vip.json"),
        """{"Groups":{"premium":{"Values":{"PlayerParticle":""" + entries + "}}}}");
    var manifest = new ResourceManifest();
    plugin.Invoke<Listeners.OnServerPrecacheResources>(listener => listener(manifest));
    plugin.Invoke<Listeners.OnMapStart>(listener => listener("test"));
    Check(manifest.Resources.Count == 1, "Resource added to the map manifest");
}

static IEnumerable<CParticleSystem> Particles() => CEntityInstance.Entities.Values.OfType<CParticleSystem>();
static CParticleSystem Particle() => Particles().Single();

static bool IsHidden(BasePlugin plugin, CCSPlayerController viewer, CParticleSystem particle)
{
    var index = (int)particle.EntityHandle.Index;
    var info = new CCheckTransmitInfo();
    info.TransmitEntities.Add(index);
    plugin.Invoke<Listeners.CheckTransmit>(listener => listener(new CCheckTransmitInfoList((info, viewer))));
    return !info.TransmitEntities.Contains(index);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
}
