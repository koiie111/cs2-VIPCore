namespace VIP_RegenHealth;

// Each damage event creates an independent schedule; configuration is never mutated.
internal sealed class RegenSchedule
{
    public int Health { get; }
    private readonly double _interval;
    private double _nextHeal;

    public RegenSchedule(double now, int health, double delay, double interval)
    {
        Health = health;
        _nextHeal = now + Math.Max(0, delay);
        _interval = Math.Max(0.1, interval);
    }

    public bool IsDue(double now) => now >= _nextHeal;

    // Do not catch up missed ticks with a burst of health after a server stall.
    public void Advance(double now) => _nextHeal = now + _interval;
}
