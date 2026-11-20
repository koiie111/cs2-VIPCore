using System.Diagnostics;
using VIP_Postprocessing;

internal static class ManagedBenchmark
{
    private const int Snapshots = 16_384;

    internal static void Run()
    {
        Console.WriteLine("Managed snapshot simulation, median of five runs. Time in us and allocation in bytes per snapshot.");
        Console.WriteLine("Viewers Owners   Old us   New us    Old B    New B  Modeled volume accesses old/new");
        foreach (int viewers in new[] { 24, 64 })
        foreach (int owners in new[] { 0, 1, 8, viewers })
        {
            var visibility = new VolumeVisibility();
            for (var slot = 0; slot < owners; slot++)
                visibility.Add(slot, (uint)(100 + slot), (uint)slot, true, slot % 2 == 0);

            var legacySlots = new bool[VolumeVisibility.Slots];
            Array.Fill(legacySlots, true, 0, owners);
            var recipients = Enumerable.Range(0, viewers).ToArray();

            foreach (int viewer in recipients)
            {
                var oldHidden = OldHidden(legacySlots, viewer);
                var newHidden = NewHidden(visibility, viewer);
                if (oldHidden != newHidden || (viewer < owners && viewer % 2 == 0) != visibility.HideMap(viewer))
                    throw new InvalidOperationException($"Different filtering for viewer {viewer}, owners {owners}");
            }

            var old = Measure(() => LegacySnapshot(legacySlots, recipients));
            var current = Measure(() => CurrentSnapshot(visibility, recipients));
            if (old.Checksum != current.Checksum)
                throw new InvalidOperationException("Snapshot outputs differ");
            Console.WriteLine($"{viewers,7}{owners,7}{old.Time,9:F3}{current.Time,9:F3}{old.Bytes,9:F0}{current.Bytes,9:F0}  {viewers * owners}/0");
        }
    }

    private static (double Time, double Bytes, long Checksum) Measure(Func<long> snapshot)
    {
        var times = new double[5];
        var allocations = new double[5];
        long checksum = 0;
        for (var run = -2; run < times.Length; run++)
        {
            var bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            checksum = 0;
            for (var i = 0; i < Snapshots; i++)
                checksum += snapshot();
            var time = Stopwatch.GetElapsedTime(start).TotalMicroseconds / Snapshots;
            var bytes = (GC.GetAllocatedBytesForCurrentThread() - bytesBefore) / (double)Snapshots;
            if (run < 0) continue;
            times[run] = time;
            allocations[run] = bytes;
        }
        Array.Sort(times);
        Array.Sort(allocations);
        return (times[2], allocations[2], checksum);
    }

    private static IEnumerable<int> EnumerateRecipients(int[] recipients)
    {
        // CSS 1.0.378 used an iterator in the original foreach path.
        foreach (var viewer in recipients)
            yield return viewer;
    }

    private static long LegacySnapshot(bool[] slots, int[] recipients)
    {
        long checksum = 0;
        foreach (int viewer in EnumerateRecipients(recipients))
            checksum += (long)OldHidden(slots, viewer);
        return checksum;
    }

    private static long CurrentSnapshot(VolumeVisibility visibility, int[] recipients)
    {
        if (!visibility.NeedsFilter) return 0;
        long checksum = 0;
        foreach (int viewer in recipients)
            checksum += (long)NewHidden(visibility, viewer);
        return checksum;
    }

    private static ulong OldHidden(bool[] slots, int shown)
    {
        ulong removed = 0;
        for (var owner = 0; owner < slots.Length; owner++)
            if (slots[owner] && !PostprocessingPolicy.CanTransmit(owner, shown))
                removed |= 1UL << owner;
        return removed;
    }

    private static ulong NewHidden(VolumeVisibility visibility, int shown)
    {
        ulong removed = 0;
        var hidden = visibility.HiddenOwners(shown);
        while (hidden != 0)
            removed |= 1UL << (int)visibility.Index(VolumeVisibility.TakeSlot(ref hidden));
        return removed;
    }
}
