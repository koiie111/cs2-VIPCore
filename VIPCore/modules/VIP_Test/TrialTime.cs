namespace VIP_Test;

public static class TrialTime
{
    public static int ToCoreUnits(long seconds, int timeMode)
    {
        var unit = timeMode switch
        {
            0 => 1,
            1 => 60,
            2 => 3600,
            3 => 86400,
            _ => throw new ArgumentOutOfRangeException(nameof(timeMode))
        };
        return checked((int)Math.Max(1L, checked(seconds + unit - 1) / unit));
    }
}
