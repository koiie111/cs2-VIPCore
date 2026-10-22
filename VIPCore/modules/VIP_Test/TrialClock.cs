namespace VIP_Test;

/// <summary>Pure rules of the play-time trial: no engine, no database.</summary>
public static class TrialClock
{
    public const int TeamT = 2;
    public const int TeamCt = 3;

    /// <summary>Only time spent in a team counts: spectators and unassigned players keep their budget.</summary>
    public static bool CountsAsPlaying(int teamNum) => teamNum is TeamT or TeamCt;

    public static int Remaining(int budgetSeconds, int usedSeconds, int pendingSeconds) =>
        Math.Max(0, budgetSeconds - usedSeconds - pendingSeconds);

    public static bool ShouldWarn(int remaining, int warnBeforeSeconds, bool alreadyWarned) =>
        !alreadyWarned && warnBeforeSeconds > 0 && remaining > 0 && remaining <= warnBeforeSeconds;

    public static bool IsOver(int remaining) => remaining <= 0;

    /// <summary>Russian duration for chat: «10 минут», «1 минута», «45 секунд».</summary>
    public static string Format(int seconds)
    {
        if (seconds >= 60)
        {
            var minutes = (seconds + 59) / 60;
            return minutes + " " + Plural(minutes, "минута", "минуты", "минут");
        }
        seconds = Math.Max(0, seconds);
        return seconds + " " + Plural(seconds, "секунда", "секунды", "секунд");
    }

    private static string Plural(int n, string one, string few, string many)
    {
        n = Math.Abs(n) % 100;
        var n1 = n % 10;
        if (n is > 10 and < 20) return many;
        if (n1 is > 1 and < 5) return few;
        return n1 == 1 ? one : many;
    }
}
