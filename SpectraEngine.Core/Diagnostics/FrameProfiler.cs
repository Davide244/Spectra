using System;
using System.Diagnostics;

namespace SpectraEngine.Core.Diagnostics;

/// <summary>
/// The phases of one frame, in the order the render loop runs them.
/// </summary>
/// <remarks>
/// An enum rather than strings so a scope costs an array index and no
/// allocation. Anything measured every frame has to be cheaper than what it
/// measures, or the profiler becomes the profile.
/// </remarks>
public enum FramePhase
{
    /// <summary>Editor tools, camera controllers, demo animation.</summary>
    Update,

    /// <summary>Fixed-tick physics, however many ticks this frame owed.</summary>
    Physics,

    /// <summary>Landing a finished static-world compile: GPU meshes created and destroyed.</summary>
    WorldSwap,

    /// <summary>Asset uploads and part-brush mesh building.</summary>
    Assets,

    /// <summary>Frustum culling and draw-list building for the camera.</summary>
    ViewBuild,

    /// <summary>The shadow cascades: culling and depth-only draws.</summary>
    Shadows,

    /// <summary>The G-buffer fill, or the forward pass.</summary>
    Geometry,

    /// <summary>The deferred light pass.</summary>
    Lighting,

    /// <summary>Tone mapping, the debug overlay, and anything else after the scene.</summary>
    Resolve,

    /// <summary>Present, including any block on vsync or on the GPU.</summary>
    Present,
}

/// <summary>
/// Where a frame's time went, measured on the CPU, phase by phase.
/// </summary>
/// <remarks>
/// <para>
/// <b>CPU time, and it says so.</b> A phase's number is how long the render
/// thread spent inside it, which on an immediate-mode API is mostly the cost of
/// building and submitting commands rather than of the GPU executing them. That
/// is the right thing to measure first here: the engine's frame time was
/// identical at 1280x720 and at 2560x1440, which is only possible if the
/// bottleneck is on this side.
/// </para>
/// <para>
/// <b><see cref="FramePhase.Present"/> absorbs everything the other phases
/// hide.</b> Where a backend blocks for the GPU or for vsync, it blocks there,
/// so a frame that reads 1 ms of work and 15 ms of Present is waiting rather
/// than working. Reading it as "Present is slow" is the mistake it exists to
/// prevent.
/// </para>
/// <para>
/// Values are smoothed with an exponential average because a raw frame is
/// dominated by whichever one happened to land a compile or a resize. Render
/// thread only, and lock-free for that reason.
/// </para>
/// </remarks>
public sealed class FrameProfiler
{
    private static readonly double MillisecondsPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly int PhaseCount = Enum.GetValues<FramePhase>().Length;

    private readonly long[] _current;
    private readonly long[] _open;
    private readonly double[] _smoothed;
    private readonly double _smoothing;

    /// <summary>Creates a profiler. <paramref name="smoothing"/> is the weight of each new frame.</summary>
    public FrameProfiler(double smoothing = 0.05)
    {
        _current = new long[PhaseCount];
        _open = new long[PhaseCount];
        _smoothed = new double[PhaseCount];
        _smoothing = smoothing;
    }

    /// <summary>Whether phases are being timed at all. Off costs one branch per scope.</summary>
    public bool Enabled { get; set; }

    /// <summary>Milliseconds spent in <paramref name="phase"/>, smoothed over recent frames.</summary>
    public double this[FramePhase phase] => _smoothed[(int)phase];

    /// <summary>Total of every phase, smoothed. Close to the frame time when nothing is unmeasured.</summary>
    public double TotalMs
    {
        get
        {
            double total = 0;
            for (int i = 0; i < _smoothed.Length; i++) total += _smoothed[i];
            return total;
        }
    }

    /// <summary>Opens a timing scope; dispose to close it. Use with <c>using</c>.</summary>
    public Scope Measure(FramePhase phase) => new(this, phase);

    /// <summary>Folds this frame's measurements into the smoothed averages and resets.</summary>
    public void EndFrame()
    {
        if (!Enabled) return;

        for (int i = 0; i < _current.Length; i++)
        {
            double ms = _current[i] * MillisecondsPerTick;
            _smoothed[i] += (ms - _smoothed[i]) * _smoothing;
            _current[i] = 0;
        }
    }

    /// <summary>
    /// The phases worth naming, as "name ms" pairs, largest first, for a log
    /// line. Allocates: call it on a log cadence, never per frame.
    /// </summary>
    public string Describe(int top = 4)
    {
        Span<int> order = stackalloc int[PhaseCount];
        for (int i = 0; i < PhaseCount; i++) order[i] = i;

        // Selection sort over ten items: shorter than explaining why a real
        // sort was worth allocating for a log line.
        for (int i = 0; i < PhaseCount; i++)
        {
            for (int j = i + 1; j < PhaseCount; j++)
            {
                if (_smoothed[order[j]] > _smoothed[order[i]])
                    (order[i], order[j]) = (order[j], order[i]);
            }
        }

        var text = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(top, PhaseCount); i++)
        {
            double ms = _smoothed[order[i]];
            if (ms < 0.005) break;
            if (text.Length > 0) text.Append(", ");
            text.Append((FramePhase)order[i]).Append(' ').Append(ms.ToString("0.00")).Append(" ms");
        }

        return text.Length == 0 ? "not measured" : text.ToString();
    }

    private void Open(FramePhase phase) => _open[(int)phase] = Stopwatch.GetTimestamp();

    private void Close(FramePhase phase)
    {
        int index = (int)phase;
        _current[index] += Stopwatch.GetTimestamp() - _open[index];
    }

    /// <summary>One open phase. A ref struct so it cannot outlive its frame or escape to the heap.</summary>
    public readonly ref struct Scope
    {
        private readonly FrameProfiler? _profiler;
        private readonly FramePhase _phase;

        internal Scope(FrameProfiler profiler, FramePhase phase)
        {
            // Null when disabled, so Dispose is a null check rather than a
            // second lookup of the same flag.
            _profiler = profiler.Enabled ? profiler : null;
            _phase = phase;
            _profiler?.Open(phase);
        }

        public void Dispose() => _profiler?.Close(_phase);
    }
}
