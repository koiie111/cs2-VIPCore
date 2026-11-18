using System.Diagnostics;
using System.Numerics;
using VIP_PlayerTrail;

internal static class ManagedBenchmark
{
    private const int Viewers = 24;
    private const int Ticks = 16_384;

    internal static void Run()
    {
        Console.WriteLine("Managed simulation, 24 viewers. Time and bytes per tick (median of 5 runs).");
        Console.WriteLine("Scenario                Baseline us    Candidate us    Baseline B    Candidate B");
        foreach (var (name, active, follow, restricted) in new[]
                 {
                     ("Off", 0, false, false),
                     ("Stationary Follow", 8, true, false),
                     ("Moving beams/tracers", 8, false, false),
                     ("Restricted visibility", 8, false, true)
                 })
        {
            var baseline = Measure(false, active, follow, restricted);
            var candidate = Measure(true, active, follow, restricted);
            if (baseline.Count != candidate.Count) throw new InvalidOperationException($"Different output for {name}");
            Console.WriteLine($"{name,-24}{baseline.Time,10:F3}{candidate.Time,16:F3}{baseline.Bytes,14:F1}{candidate.Bytes,15:F1}");
        }
    }

    private static (double Time, double Bytes, long Count) Measure(bool optimized, int active, bool follow, bool restricted)
    {
        var times = new double[5];
        var allocations = new double[5];
        long count = 0;
        for (var run = -2; run < times.Length; run++)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            count = Simulate(optimized, active, follow, restricted);
            var time = Stopwatch.GetElapsedTime(start).TotalMicroseconds / Ticks;
            var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)Ticks;
            if (run < 0) continue;
            times[run] = time;
            allocations[run] = bytes;
        }
        Array.Sort(times);
        Array.Sort(allocations);
        return (times[2], allocations[2], count);
    }

    private static void AddBaselineTimer(HashSet<uint> indices, List<(int Deadline, Action Remove)> timers,
        uint index, int deadline)
    {
        indices.Add(index);
        timers.Add((deadline, () => indices.Remove(index)));
    }

    private static long Simulate(bool optimized, int active, bool follow, bool restricted)
    {
        var visibility = new TrailVisibility();
        var tracked = new TrailEntities();
        var baselineOwners = new HashSet<uint>[TrailVisibility.Slots];
        var timers = new List<(int Deadline, Action Remove)>();
        var anchors = new Vector3[TrailVisibility.Slots];
        var teams = new int[Viewers];
        for (var slot = 0; slot < Viewers; slot++)
        {
            teams[slot] = 2 + slot % 2;
            visibility.SetTeam(slot, teams[slot]);
            if (restricted && slot < active) visibility.SetPreferences(slot, MineMode.Team, OthersMode.Team);
        }
        for (var slot = 0; slot < active; slot++)
        {
            baselineOwners[slot] = [];
            if (!follow) continue;
            var index = (uint)(slot + 1);
            if (optimized) tracked.Add(slot, index, index, null);
            else baselineOwners[slot].Add(index);
        }

        long result = 0;
        uint nextIndex = 100;
        for (var tick = 1; tick <= Ticks; tick++)
        {
            if (optimized)
            {
                if (tracked.HasExpirations)
                    while (tracked.TryTakeExpired(tick, out var handle)) tracked.Remove(handle, handle);
            }
            else
            {
                for (var i = timers.Count - 1; i >= 0; i--)
                {
                    if (timers[i].Deadline > tick) continue;
                    timers[i].Remove();
                    timers.RemoveAt(i);
                }
            }

            if (tick % 2 == 0)
            {
                var slots = optimized ? active : TrailVisibility.Slots;
                for (var slot = 0; slot < slots; slot++)
                {
                    if (slot >= active) continue;
                    var position = follow ? Vector3.One : new Vector3(tick * 5, slot, 0);
                    if (follow)
                    {
                        if (!optimized || anchors[slot] != position) anchors[slot] = position;
                        continue;
                    }

                    ref var anchor = ref anchors[slot];
                    if (optimized)
                    {
                        if (!TrailMovement.Advance(ref anchor, position, out _)) continue;
                    }
                    else
                    {
                        var distance = Vector3.Distance(anchor, position);
                        if (distance < 12) continue;
                        anchor = position;
                        if (distance >= 250) continue;
                    }

                    var index = nextIndex++;
                    // Alternate beam and tracer lifetimes, with the same expiry schedule in both paths.
                    var deadline = tick + (slot % 2 == 0 ? 39 : 64);
                    if (optimized)
                    {
                        tracked.Add(slot, index, index, deadline);
                    }
                    else
                    {
                        AddBaselineTimer(baselineOwners[slot], timers, index, deadline);
                    }
                    result++;
                }
            }

            if (optimized)
            {
                if (!visibility.IsRestricted || tracked.Owners == 0) continue;
                for (var viewer = 0; viewer < Viewers; viewer++)
                {
                    var hidden = tracked.Owners & visibility.HiddenOwners(viewer);
                    while (hidden != 0)
                    {
                        var owner = (int)UInt128.TrailingZeroCount(hidden);
                        hidden &= hidden - 1;
                        result += tracked.Indices(owner)!.Count;
                    }
                }
            }
            else if (active != 0)
            {
                var hasRestrictions = false;
                for (var slot = 0; slot < TrailVisibility.Slots && !hasRestrictions; slot++)
                    hasRestrictions = restricted && slot < active;
                if (!hasRestrictions) continue;

                var roster = teams.ToList();
                for (var viewer = 0; viewer < Viewers; viewer++)
                for (var owner = 0; owner < TrailVisibility.Slots; owner++)
                {
                    if (baselineOwners[owner] is not { Count: > 0 } indices || owner == viewer) continue;
                    if (roster[owner] != roster[viewer]) result += indices.Count;
                }
            }
        }
        return result;
    }
}
