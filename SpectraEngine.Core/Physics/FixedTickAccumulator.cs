using System;

namespace SpectraEngine.Core.Physics;

/// <summary>
/// Turns a variable frame delta into a whole number of fixed simulation ticks,
/// plus the fraction of a tick left over for render interpolation.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists so that <see cref="IScenePhysics.Step"/> can promise a
/// fixed step and mean it.</b> Handing the backend a frame delta would make the
/// simulation a function of frame rate — the same input produces a different
/// world on a faster machine — which breaks determinism, replay, and any future
/// server reconciliation. Every one of those depends on "tick N is tick N
/// everywhere", and a variable step quietly makes that false.
/// </para>
/// <para>
/// <b>The tick cap is the anti-spiral, and dropping time is the deliberate
/// lesser evil.</b> When a frame takes longer than the ticks it owes, the debt
/// grows, which makes the next frame longer still — a slide that presents as a
/// hard freeze rather than a slowdown. Capping the ticks and discarding the
/// excess makes the failure visible instead (the world moves slower than the
/// wall clock) and recoverable. <see cref="DroppedTicks"/> counts it rather
/// than hiding it, because silently losing simulated time is exactly the kind
/// of thing that gets reported as "physics is inconsistent".
/// </para>
/// <para>
/// Render-thread only, like everything it drives.
/// </para>
/// </remarks>
public sealed class FixedTickAccumulator
{
    private readonly float _fixedDt;
    private readonly int _maxTicksPerFrame;
    // Double, because the engine's frame delta is a double and the residual is
    // carried across every frame for the process's life — accumulating that in
    // float would let rounding drift the phase over a long session.
    private double _accumulated;

    /// <summary>Creates an accumulator at the engine defaults.</summary>
    public FixedTickAccumulator()
        : this(PhysicsDefaults.FixedDeltaTime, PhysicsDefaults.MaxTicksPerFrame)
    {
    }

    /// <summary>Creates an accumulator with an explicit step and cap.</summary>
    public FixedTickAccumulator(float fixedDt, int maxTicksPerFrame)
    {
        _fixedDt = fixedDt > 0f ? fixedDt : PhysicsDefaults.FixedDeltaTime;
        _maxTicksPerFrame = maxTicksPerFrame > 0 ? maxTicksPerFrame : 1;
    }

    /// <summary>The fixed step, in seconds, every tick advances by.</summary>
    public float FixedDeltaTime => _fixedDt;

    /// <summary>Ticks run since construction.</summary>
    public long TotalTicks { get; private set; }

    /// <summary>
    /// Ticks the cap discarded since construction. Non-zero means the machine
    /// could not keep up and simulated time was lost — a real event that should
    /// be reportable, never silent.
    /// </summary>
    public long DroppedTicks { get; private set; }

    /// <summary>
    /// How far the current frame sits between the last completed tick and the
    /// next one, in <c>[0, 1)</c> — the blend factor for render interpolation.
    /// </summary>
    public float Alpha
    {
        get
        {
            // The residual is mathematically in [0, fixedDt), so the ratio is
            // in [0, 1) — but narrowing the double ratio to float can round a
            // value a hair below one UP to exactly 1f. Clamped rather than
            // documented away, because a half-open contract that occasionally
            // delivers its endpoint is the kind of lie a consumer only finds
            // through an out-of-range index: `(int)(alpha * n)` is n, not n-1.
            float alpha = (float)(_accumulated / _fixedDt);
            if (alpha >= 1f)
                return MathF.BitDecrement(1f);
            return alpha < 0f ? 0f : alpha;
        }
    }

    /// <summary>
    /// Adds a frame's elapsed time and returns how many fixed ticks to run now.
    /// </summary>
    /// <remarks>
    /// A non-finite or negative delta contributes nothing rather than throwing:
    /// this runs every frame on the render thread, and a single bad timer
    /// reading — a debugger pause, a clock step, a first frame — must not be
    /// able to take the process down or bank a colossal debt.
    /// </remarks>
    public int Advance(double deltaTime)
    {
        if (!double.IsFinite(deltaTime) || deltaTime <= 0d)
            return 0;

        _accumulated += deltaTime;

        int ticks = (int)(_accumulated / _fixedDt);
        if (ticks <= 0)
            return 0;

        if (ticks > _maxTicksPerFrame)
        {
            // Discard the excess outright rather than carrying it: carrying is
            // what makes the debt compound into the freeze this cap exists to
            // prevent.
            DroppedTicks += ticks - _maxTicksPerFrame;
            ticks = _maxTicksPerFrame;
            _accumulated = 0d;
        }
        else
        {
            _accumulated -= ticks * (double)_fixedDt;
        }

        TotalTicks += ticks;
        return ticks;
    }

    /// <summary>
    /// Discards the partial tick without running it — for a scene change or a
    /// resume, where the elapsed wall-clock time is not simulated time.
    /// </summary>
    public void Reset() => _accumulated = 0d;
}
