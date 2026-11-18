using System.Numerics;
using VIP_PlayerTrail;

var anchor = Vector3.Zero;
Require(!TrailMovement.Advance(ref anchor, new Vector3(6, 0, 0), out _), "Short step");
Require(!TrailMovement.Advance(ref anchor, new Vector3(11, 0, 0), out _), "Short steps accumulate");
Require(TrailMovement.Advance(ref anchor, new Vector3(12, 0, 0), out var from) && from == Vector3.Zero,
    "12-unit boundary draws from original anchor");
Require(TrailMovement.Advance(ref anchor, new Vector3(261, 0, 0), out _), "249-unit step");
Require(!TrailMovement.Advance(ref anchor, new Vector3(511, 0, 0), out _), "250-unit jump");
Require(TrailMovement.Advance(ref anchor, new Vector3(523, 0, 0), out from) && from.X == 511, "Jump resets anchor");

const int owner = 64;
const int viewer = 3;
var visibility = new TrailVisibility();
Require(!visibility.IsRestricted, "Default preferences");
foreach (var mine in Enum.GetValues<MineMode>())
foreach (var others in Enum.GetValues<OthersMode>())
foreach (var (ownerTeam, viewerTeam, teammates) in new[] { (2, 2, true), (2, 3, false), (1, 1, false) })
{
    visibility.SetPreferences(owner, mine, OthersMode.All);
    visibility.SetPreferences(viewer, MineMode.All, others);
    visibility.SetTeam(owner, ownerTeam);
    visibility.SetTeam(viewer, viewerTeam);

    var visible = (mine == MineMode.All || mine == MineMode.Team && teammates) &&
                  (others == OthersMode.All || others == OthersMode.Team && teammates);
    Require((visibility.HiddenOwners(viewer) & TrailVisibility.Bit(owner)) == (visible ? 0 : TrailVisibility.Bit(owner)),
        $"Visibility for {mine}, {others}, teams {ownerTeam}/{viewerTeam}");
    Require((visibility.HiddenOwners(owner) & TrailVisibility.Bit(owner)) == 0, "Owner always sees own trail");
}
visibility.SetPreferences(owner, MineMode.Team, OthersMode.None);
visibility.SetPreferences(viewer, MineMode.All, OthersMode.All);
visibility.SetTeam(owner, 2);
visibility.SetTeam(viewer, 2);
Require(visibility.HiddenOwners(viewer) == 0, "Ordinary teammate can see trail");
visibility.SetTeam(owner, 3);
Require(visibility.HiddenOwners(viewer) == TrailVisibility.Bit(owner), "Owner team change updates viewer");
visibility.SetTeam(viewer, 3);
Require(visibility.HiddenOwners(viewer) == 0, "Viewer team change updates its own permissions");
visibility.ResetTeams();
Require(visibility.HiddenOwners(viewer) == TrailVisibility.Bit(owner), "Map transition clears cached teams");
visibility.ResetSlot(owner);
Require(visibility.HiddenOwners(viewer) == 0 && !visibility.IsRestricted, "Reused slot starts with default preferences");

List<(string Name, string Value)> options = [("Off", "off"), ("Blue", "#00aaff"), ("Smoke", "@Smoke")];
Require(TrailSelection.Resolve(null, options) == "off", "Server5 default off");
Require(TrailSelection.Resolve("#00AAFF", options) == "#00AAFF", "Saved fixed color");
Require(TrailSelection.Resolve("@smoke", options) == "@smoke", "Saved particle name");
Require(TrailSelection.Resolve("@removed", options) == "off", "Removed selection falls back");
options.RemoveAt(0);
Require(TrailSelection.Resolve(null, options) == "#00aaff", "First color default");
Require(TrailSelection.Resolve("off", options) == "off", "Saved off always wins");
Require(TrailSelection.Resolve(null, []) == "off", "Empty group");

var entities = new TrailEntities();
entities.Add(owner, 9, 0x8009, 2);
entities.Add(owner, 10, 0x800a, null);
entities.Add(viewer, 11, 0x800b, 1);
entities.Add(viewer, 12, 0x800c, 1);
Require(entities.Owners == (TrailVisibility.Bit(owner) | TrailVisibility.Bit(viewer)), "Owners of live effects");
Require(!entities.TryTakeExpired(0.99, out _), "No early expiration");
var due = new HashSet<uint>();
while (entities.TryTakeExpired(1, out var handle)) due.Add(handle);
Require(due.SetEquals([0x800bu, 0x800cu]), "Equal deadlines expire in the same tick");
entities.Remove(11, 0x800b);
entities.Remove(12, 0x800c);
Require(entities.Owners == TrailVisibility.Bit(owner), "Deletion removes an empty owner");
entities.Remove(9, 0x8009);
Require(!entities.HasExpirations, "Last temporary deletion drops stale deadlines");
entities.Add(viewer, 9, 0x10009, 4);
entities.Add(viewer, 13, 0x800d, 5);
entities.Add(owner, 9, 0x18009, 6);
entities.Remove(9, 0x10009);
Require(!entities.TryTakeExpired(4, out _), "Old deadline and deletion cannot touch reused index");
Require(entities.TryTakeExpired(5, out var regular) && regular == 0x800d, "Independent lifetime");
entities.Remove(13, regular);
for (uint serial = 4; serial < 90; serial++)
{
    var handle = (serial << 15) | 20;
    entities.Add(viewer, 20, handle, 1000 + serial);
    entities.Remove(20, handle);
}
Require(!entities.TryTakeExpired(5, out _), "Compaction does not expire live entries early");
Require(entities.TryTakeExpired(6, out var reused) && reused == 0x18009, "Reused index expires by new deadline");
Require(entities.OwnedBy(owner).Any(e => e is (0x800a, true)), "Follow survives unrelated expiration");
entities.Clear();
Require(entities.Owners == 0 && !entities.HasExpirations && !entities.OwnedBy(owner).Any(), "Map/unload state reset");
FeatureReloadTests.Run();
Console.WriteLine("PlayerTrail runtime tests passed.");

if (args.Contains("--benchmark")) ManagedBenchmark.Run();

static void Require(bool condition, string scenario)
{
    if (!condition) throw new InvalidOperationException(scenario);
}
