namespace SpectraEngine.Core.Diagnostics;

/// <summary>
/// Accumulates frame timings and exposes a smoothed frame rate. Averaging over
/// a short interval avoids the jitter of a per-frame reading.
/// </summary>
public sealed class FpsCounter
{
    private readonly double _refreshInterval;
    private double _elapsed;
    private int _frames;

    /// <param name="refreshInterval">Seconds between refreshes of the reported values.</param>
    public FpsCounter(double refreshInterval = 0.5)
    {
        _refreshInterval = refreshInterval;
    }

    /// <summary>Smoothed frames per second from the last completed interval.</summary>
    public double Fps { get; private set; }

    /// <summary>Average milliseconds per frame from the last completed interval.</summary>
    public double FrameTimeMs { get; private set; }

    /// <summary>
    /// Records one rendered frame. Returns true on the frame where
    /// <see cref="Fps"/> and <see cref="FrameTimeMs"/> were refreshed.
    /// </summary>
    public bool Tick(double deltaTime)
    {
        _elapsed += deltaTime;
        _frames++;

        if (_elapsed < _refreshInterval || _frames == 0)
            return false;

        Fps = _frames / _elapsed;
        FrameTimeMs = _elapsed / _frames * 1000.0;
        _frames = 0;
        _elapsed = 0;
        return true;
    }
}
