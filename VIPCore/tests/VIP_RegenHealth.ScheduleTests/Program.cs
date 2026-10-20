using VIP_RegenHealth;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var schedule = new RegenSchedule(100, 1, 8, 1);
Check(!schedule.IsDue(107.999), "Must wait the full damage delay");
Check(schedule.IsDue(108), "First heal must be due exactly at eight seconds");
Check(schedule.Health == 1, "Configured health amount must be preserved");
schedule.Advance(108);
Check(!schedule.IsDue(108.999), "No early interval tick");
Check(schedule.IsDue(109), "Interval=1 must heal every second, not every two seconds");
schedule.Advance(109);
Check(schedule.IsDue(110), "Consecutive ticks must retain the configured interval");

// A new damage event replaces the schedule, including after healing has started.
schedule = new RegenSchedule(109.5, 1, 8, 1);
Check(!schedule.IsDue(117.499), "Repeated damage must restart the full delay");
Check(schedule.IsDue(117.5), "Damage reset must expire at the new deadline");
var otherPlayer = new RegenSchedule(105, 1, 8, 1);
Check(otherPlayer.IsDue(113), "Players must have independent deadlines");
Check(!schedule.IsDue(113), "Another player's healing must not affect this player");

var fractional = new RegenSchedule(0, 3, 0.5, 0.25);
Check(!fractional.IsDue(0.499), "Fractional delay must not be truncated");
Check(fractional.IsDue(0.5), "Fractional delay must be honored");
fractional.Advance(0.5);
Check(!fractional.IsDue(0.749) && fractional.IsDue(0.75), "Fractional interval must be honored");

var stalled = new RegenSchedule(0, 1, 8, 1);
stalled.Advance(20);
Check(!stalled.IsDue(20) && stalled.IsDue(21), "Server stalls must not produce catch-up heal bursts");
var zero = new RegenSchedule(10, 1, 0, 0);
Check(zero.IsDue(10), "Zero delay may heal immediately");
zero.Advance(10);
Check(!zero.IsDue(10) && zero.IsDue(10.1), "Zero interval must remain bounded by the poll interval");
Console.WriteLine("All 18 regeneration schedule assertions passed.");
