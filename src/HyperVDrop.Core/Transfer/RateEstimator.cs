namespace HyperVDrop.Core.Transfer;

/// <summary>
/// Smooths raw byte counts into a transfer rate and a remaining-time estimate.
/// </summary>
/// <remarks>
/// Guest Service Interface progress arrives as whole percentages, so raw deltas are lumpy: a 1%
/// step on a large file is a big jump followed by silence. Exponential smoothing keeps the
/// displayed speed from flickering between zero and a spike.
/// </remarks>
public sealed class RateEstimator(TimeProvider? timeProvider = null)
{
    private const double SmoothingFactor = 0.3;

    /// <summary>Ignore samples closer together than this, so tiny deltas do not distort the rate.</summary>
    private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(400);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private long _lastTimestamp;
    private long _lastBytes;
    private bool _started;

    /// <summary>Smoothed transfer rate, or <c>null</c> until there is enough data.</summary>
    public double? BytesPerSecond { get; private set; }

    public void Reset()
    {
        _started = false;
        _lastBytes = 0;
        BytesPerSecond = null;
    }

    /// <summary>Feeds in the running total of bytes transferred.</summary>
    public void Update(long bytesTransferred)
    {
        var now = _time.GetTimestamp();

        if (!_started)
        {
            _started = true;
            _lastTimestamp = now;
            _lastBytes = bytesTransferred;
            return;
        }

        var elapsed = _time.GetElapsedTime(_lastTimestamp, now);
        if (elapsed < MinimumSampleInterval)
        {
            return;
        }

        var delta = bytesTransferred - _lastBytes;
        _lastBytes = bytesTransferred;
        _lastTimestamp = now;

        if (delta < 0)
        {
            // The counter went backwards, which means a retry restarted the file.
            BytesPerSecond = null;
            return;
        }

        var instantaneous = delta / elapsed.TotalSeconds;

        BytesPerSecond = BytesPerSecond is null
            ? instantaneous
            : (SmoothingFactor * instantaneous) + ((1 - SmoothingFactor) * BytesPerSecond.Value);
    }

    /// <summary>Estimates the time left, or <c>null</c> when no useful estimate exists yet.</summary>
    public TimeSpan? EstimateRemaining(long remainingBytes)
    {
        if (remainingBytes <= 0)
        {
            return TimeSpan.Zero;
        }

        // Below roughly a kilobyte per second any estimate is noise, and would render as "9999:59".
        if (BytesPerSecond is null || BytesPerSecond.Value < 1024)
        {
            return null;
        }

        var seconds = remainingBytes / BytesPerSecond.Value;
        return seconds > TimeSpan.FromDays(1).TotalSeconds
            ? null
            : TimeSpan.FromSeconds(seconds);
    }
}
