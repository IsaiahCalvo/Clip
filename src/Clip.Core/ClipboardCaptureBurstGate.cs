namespace Clip.Core;

public sealed class ClipboardCaptureBurstGate
{
    private readonly TimeSpan _window;
    private string? _lastFingerprint;
    private DateTimeOffset _lastSeenAt;

    public ClipboardCaptureBurstGate(TimeSpan window)
    {
        _window = window;
    }

    public bool ShouldSkip(string? fingerprint, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return false;
        }

        if (string.Equals(_lastFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase) &&
            now - _lastSeenAt <= _window)
        {
            _lastSeenAt = now;
            return true;
        }

        _lastFingerprint = fingerprint;
        _lastSeenAt = now;
        return false;
    }
}
