using System.Diagnostics;
using VIP_PlayerParticle;

internal static class ManagedBenchmark
{
    private const int Iterations = 50_000;
    private static int _sink;

    internal static void Run()
    {
        Console.WriteLine("Viewers, Owners, Restrictions, Team burst, Before µs, After µs, Before B, After B, Before particle checks, After removes");
        foreach (var viewers in new[] { 24, 64 })
        {
            Compare(viewers, 0, mixed: false);
            Compare(viewers, 1, mixed: false);
            Compare(viewers, 1, mixed: true);
            Compare(viewers, viewers, mixed: false);
            Compare(viewers, viewers, mixed: true);
            Compare(viewers, viewers, mixed: true, teamBurst: true);
        }
        GC.KeepAlive(_sink);
    }

    private static void Compare(int viewerCount, int ownerCount, bool mixed, bool teamBurst = false)
    {
        var players = Enumerable.Range(0, viewerCount).Select(slot => new Viewer(slot, mixed)).ToArray();
        var active = new int[64];
        var teams = new int[64];
        var prefs = players.ToDictionary(player => player.Slot);
        var policy = new ParticleVisibility();
        var bits = new bool[viewerCount, ownerCount + 1];
        foreach (var player in players)
            policy.SetViewer(player.Slot, true, player.Team, player.Mine, player.Others, player.SeeOwn);
        for (var slot = 0; slot < ownerCount; slot++)
        {
            active[slot] = slot + 1;
            policy.SetParticle(slot, slot + 1);
        }

        int Baseline()
        {
            var any = false;
            foreach (var entity in active)
                if (entity != 0) { any = true; break; }
            if (!any) return 0;

            Array.Clear(teams);
            foreach (var player in players.ToList())
                teams[player.Slot] = player.Team;

            var removes = 0;
            foreach (var viewer in players)
            {
                var target = prefs[viewer.Slot];
                for (var owner = 0; owner < active.Length; owner++)
                {
                    var entity = active[owner];
                    if (entity == 0) continue;
                    var source = prefs[owner];
                    bool hidden;
                    if (owner == viewer.Slot)
                        hidden = !source.SeeOwn;
                    else
                    {
                        var sameTeam = teams[owner] > 1 && teams[owner] == teams[viewer.Slot];
                        var ownerAllows = source.Mine switch
                        {
                            MineMode.Team => sameTeam, MineMode.Self => false, _ => true
                        };
                        var viewerAllows = target.Others switch
                        {
                            OthersMode.Team => sameTeam, OthersMode.None => false, _ => true
                        };
                        hidden = !ownerAllows || !viewerAllows;
                    }
                    if (!hidden) continue;
                    bits[viewer.Slot, entity] = false;
                    removes++;
                }
            }
            return removes;
        }

        int Candidate()
        {
            if (!policy.NeedsFilter) return 0;
            var removes = 0;
            foreach (var viewer in players)
            {
                foreach (var index in policy.HiddenIndices(viewer.Slot))
                {
                    bits[viewer.Slot, index] = false;
                    removes++;
                }
            }
            return removes;
        }

        void SwitchTeams(bool updatePolicy)
        {
            foreach (var player in players)
            {
                player.Team = 5 - player.Team;
                if (updatePolicy)
                    policy.SetViewer(player.Slot, true, player.Team, player.Mine, player.Others, player.SeeOwn);
            }
        }

        var removes = Candidate();
        if (removes != Baseline()) throw new InvalidOperationException("Visibility differs");
        var before = Measure(() =>
        {
            if (teamBurst) SwitchTeams(updatePolicy: false);
            return Baseline();
        });
        var after = Measure(() =>
        {
            if (teamBurst) SwitchTeams(updatePolicy: true);
            return Candidate();
        });
        Console.WriteLine(FormattableString.Invariant(
            $"{viewerCount}, {ownerCount}, {(mixed ? "mixed" : "all")}, {teamBurst}, {before.Time:F3}, {after.Time:F3}, {before.Bytes:F0}, {after.Bytes:F0}, {viewerCount * ownerCount}, {removes}"));
    }

    private static (double Time, double Bytes) Measure(Func<int> action)
    {
        for (var i = 0; i < 2000; i++) _sink += action();
        var elapsed = new double[5];
        var allocated = 0L;
        for (var sample = 0; sample < elapsed.Length; sample++)
        {
            var startBytes = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < Iterations; i++) _sink += action();
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / Iterations;
            allocated += GC.GetAllocatedBytesForCurrentThread() - startBytes;
        }
        Array.Sort(elapsed);
        return (elapsed[2], allocated / (double)(elapsed.Length * Iterations));
    }

    private sealed class Viewer(int slot, bool mixed)
    {
        internal int Slot { get; } = slot;
        internal int Team { get; set; } = slot % 2 + 2;
        internal MineMode Mine { get; } = mixed ? (MineMode)(slot % 3) : MineMode.All;
        internal OthersMode Others { get; } = mixed ? (OthersMode)((slot + 1) % 3) : OthersMode.All;
        internal bool SeeOwn { get; } = !mixed || slot % 7 != 0;
    }
}
