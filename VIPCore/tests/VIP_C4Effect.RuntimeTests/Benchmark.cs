using System.Diagnostics;
using CounterStrikeSharp.API.Core;

internal static class Benchmark
{
    internal static void Run()
    {
        const int ticks = 100_000;
        Console.WriteLine("mode,repeat,calls,particles,hidden,total_ms,p95_us,p99_us,max_us,bytes_per_tick");
        foreach (var mode in new[] { "idle", "public", "restricted", "multiple" })
        {
            using var qa = new Fixture();
            var owner = qa.Connect(0, 2, mode is "restricted" or "multiple" ? """{"m":1}""" : null);
            var players = new[] { owner }.Concat(Enumerable.Range(1, 23)
                .Select(i => qa.Connect(i, i % 2 + 2))).ToArray();
            if (mode != "idle") qa.Plant(owner);
            if (mode == "multiple")
                for (var i = 0; i < 4; i++)
                {
                    var defuser = players[i];
                    qa.Api.Cookies[defuser.SteamID] = $$"""{"m":{{i % 3}}}""";
                    qa.Api.LoadPlayer(defuser, "Test");
                    qa.Plugin.Fire(new EventBombDefused(defuser));
                }

            var listener = qa.Plugin.Listener<Listeners.CheckTransmit>();
            var rows = players.Select(player => (new CCheckTransmitInfo(), (CCSPlayerController?)player)).ToArray();
            var list = new CCheckTransmitInfoList(rows);
            var indices = qa.Particles.Select(entity => (int)entity.EntityHandle.Index).ToArray();
            void RunTick()
            {
                foreach (var (info, _) in rows)
                    foreach (var index in indices) info.TransmitEntities.Add(index);
                listener?.Invoke(list);
            }

            for (var i = 0; i < 10_000; i++) RunTick();
            for (var repeat = 1; repeat <= 3; repeat++)
            {
                var duration = new long[ticks];
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < ticks; i++)
                {
                    foreach (var (info, _) in rows)
                        foreach (var index in indices) info.TransmitEntities.Add(index);
                    if (listener == null) continue;
                    var start = Stopwatch.GetTimestamp();
                    listener(list);
                    duration[i] = Stopwatch.GetTimestamp() - start;
                }
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                var hidden = rows.Sum(row => indices.Length - row.Item1.TransmitEntities.Count);
                Array.Sort(duration);
                var microseconds = 1_000_000.0 / Stopwatch.Frequency;
                Console.WriteLine(FormattableString.Invariant($"{mode},{repeat},{(listener == null ? 0 : ticks)},{indices.Length},{hidden},{duration.Sum() * microseconds / 1000:F2},{duration[(int)(ticks * .95) - 1] * microseconds:F3},{duration[(int)(ticks * .99) - 1] * microseconds:F3},{duration[^1] * microseconds:F3},{(double)allocated / ticks:F2}"));
            }
        }
    }
}
