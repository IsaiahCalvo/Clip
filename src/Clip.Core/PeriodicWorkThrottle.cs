namespace Clip.Core;

public sealed class PeriodicWorkThrottle
{
    private readonly object _sync = new();
    private readonly TimeSpan _minimumInterval;
    private DateTimeOffset? _lastAttempt;

    public PeriodicWorkThrottle(TimeSpan minimumInterval)
    {
        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval), "Minimum interval cannot be negative.");
        }

        _minimumInterval = minimumInterval;
    }

    public bool TryBegin(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_lastAttempt is { } lastAttempt && now - lastAttempt < _minimumInterval)
            {
                return false;
            }

            _lastAttempt = now;
            return true;
        }
    }
}
