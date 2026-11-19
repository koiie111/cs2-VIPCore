using VIP_Postprocessing;
int checks = 0;
void Check(bool ok) { checks++; if (!ok) throw new Exception($"Check {checks} failed"); }
Check(!PostprocessingPolicy.HasExplicitEffect(null));
Check(!PostprocessingPolicy.HasExplicitEffect(""));
Check(!PostprocessingPolicy.HasExplicitEffect("off"));
Check(PostprocessingPolicy.HasExplicitEffect("warm"));
for (int owner = 0; owner < 64; owner++)
    for (int shown = -1; shown <= 64; shown++)
        Check(PostprocessingPolicy.CanTransmit(owner, shown) == (owner == shown));
Check(!PostprocessingPolicy.CanTransmit(-1, -1));
Check(!PostprocessingPolicy.CanTransmit(64, 64));
Console.WriteLine($"{checks} checks passed: explicit opt-in and exhaustive recipient isolation.");

var volumes = new VolumeVisibility();
const uint first = 100;
const uint last = 101;
const uint map = 700;
Check(!volumes.NeedsFilter);
volumes.Map.Add(map, map);
Check(!volumes.NeedsFilter);
volumes.Add(0, first, first, true, true);
volumes.Add(63, last, last, false, false);
Check(volumes.NeedsFilter);
Check(volumes.Spectatable == VolumeVisibility.OwnerBit(0));
Check(volumes.HiddenOwners(0) == VolumeVisibility.OwnerBit(63));
Check(volumes.HiddenOwners(63) == VolumeVisibility.OwnerBit(0));
Check(volumes.HiddenOwners(2) == volumes.Active);
Check(volumes.HiddenOwners(-1) == volumes.Active);
Check(volumes.HiddenOwners(64) == volumes.Active);

// Observer resolution is fresh per viewer. Moving between a permitted and denied target
// affects only that viewer. ReplaceMap follows the effect actually shown.
int Observe(int target) => (volumes.Spectatable & VolumeVisibility.OwnerBit(target)) != 0 ? target : -1;
Check(volumes.HideMap(Observe(0)));
Check(!volumes.HideMap(Observe(63)));
Check(volumes.HideMap(Observe(0)));
Check(!volumes.HideMap(Observe(-1)));
volumes.SetPreferences(63, true, true);
Check(volumes.HideMap(Observe(63)));
volumes.SetPreferences(0, false, false);
Check(!volumes.HideMap(Observe(0)));
Check(!volumes.HideMap(0));

// Failed spawn or removal: its index stays hidden while the entity still exists.
volumes.Retire(first, first);
Check(volumes.Retiring[first] == first && !volumes.HideMap(0));
Check(volumes.Handle(0) == 0 && volumes.Owns(first));
const uint replacement = first + (1 << 15);
volumes.Forget(first);
volumes.Add(0, replacement, first, true, true);
volumes.Forget(first); // An old serial must never forget its replacement.
Check(volumes.Handle(0) == replacement && volumes.NeedsFilter);
volumes.Forget(last); // External deletion.
Check(volumes.Handle(63) == 0 && !volumes.HideMap(63));
volumes.Retire(replacement, first);
Check(volumes.Active == 0 && volumes.NeedsFilter);
volumes.Forget(replacement);
Check(!volumes.NeedsFilter && volumes.Map.ContainsKey(map));
volumes.Forget(map);
Check(volumes.Map.Count == 0);
volumes.Add(63, last, last, true, true);
volumes.Map.Add(map, map);
volumes.Clear();
Check(!volumes.NeedsFilter && volumes.Spectatable == 0 && volumes.Map.Count == 0);
Check(!volumes.Owns(last) && volumes.Handle(63) == 0 && !volumes.HideMap(63));

RuntimeChecks.Run();
Console.WriteLine($"Total: {checks} policy/registry checks and runtime scenarios passed.");
if (args.Contains("--benchmark")) ManagedBenchmark.Run();
