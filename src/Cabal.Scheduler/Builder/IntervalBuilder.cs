using System;

namespace Cabal.Scheduler.Builder;

public class IntervalBuilder
{
    private readonly int _value;

    internal IntervalBuilder(int value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Interval should be bigger than 0.");
        _value = value;
    }

    public JobBuilder Seconds() => new JobBuilder(TimeSpan.FromSeconds(_value));
    public JobBuilder Minutes() => new JobBuilder(TimeSpan.FromMinutes(_value));
    public JobBuilder Hours() => new JobBuilder(TimeSpan.FromHours(_value));
    public JobBuilder Days() => new JobBuilder(TimeSpan.FromDays(_value));
}