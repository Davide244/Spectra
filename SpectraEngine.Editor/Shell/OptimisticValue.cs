using System.Collections.Generic;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// A displayed value that lights on the click and defers to the engine shortly
/// afterwards.
/// </summary>
/// <typeparam name="T">The value's type. Compared with a supplied comparer.</typeparam>
/// <remarks>
/// <para>
/// <b>Every control in the command bar was engine-authoritative and therefore
/// visibly late.</b> A click enqueued a command, the engine acted on it, the
/// next snapshot carried the new state back, and the shell's pump picked that
/// up on its next tick - a publish interval plus a pump interval plus a render
/// frame, measured at about 25ms typical and 65ms worst, and VARIABLE, which
/// the eye reads as unreliability rather than as latency. Under about 100ms a
/// response is attributed to the click automatically; past it the user has to
/// decide whether anything happened.
/// </para>
/// <para>
/// <b>The rule that makes local optimism safe is already written down</b>: the
/// shell posts SET verbs, never toggles (<c>GizmoCommand.UseTranslate</c>, not
/// <c>CycleTool</c>), so a stale echo arriving after a fast double click cannot
/// flip the value to the opposite of what was asked for. It can only be an
/// older value, which the hold-off is for.
/// </para>
/// <para>
/// <b>And the bound is the whole design.</b> An unbounded local opinion is how
/// a UI ends up permanently disagreeing with the machine it drives - the value
/// the user asked for is not always the value they get (play mode refuses on a
/// scene with no character, an edit is refused mid-gesture, a node id has left
/// the scene). After <see cref="HoldTicks"/> disagreeing snapshots the engine
/// wins visibly, which with a transition behind it reads as a refusal rather
/// than as a glitch.
/// </para>
/// <para>
/// <b>Ticks are snapshots, not milliseconds.</b> The thing being waited for is
/// an echo, which arrives in snapshots; counting wall-clock would make the same
/// hold mean six echoes at one publish rate and eighty at another, and the host
/// changes its publish rate while a gesture is in flight.
/// </para>
/// <para>UI thread only, like everything else on <see cref="ShellModel"/>.</para>
/// </remarks>
internal sealed class OptimisticValue<T>
{
    private readonly IEqualityComparer<T> _comparer;
    private T _value;
    private T _pending;
    private bool _hasPending;
    private int _ticks;

    public OptimisticValue(T initial, IEqualityComparer<T>? comparer = null)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
        _value = initial;
        _pending = initial;
    }

    /// <summary>
    /// How many disagreeing snapshots to ignore before the engine wins.
    /// </summary>
    /// <remarks>
    /// Six is about a tenth of a second at the resting publish rate and rather
    /// less while a gesture is in flight. It is deliberately short: a refusal
    /// the user cannot see is worse than a slow echo, because they will click
    /// again.
    /// </remarks>
    public int HoldTicks { get; init; } = 6;

    /// <summary>What the UI should display.</summary>
    public T Value => _value;

    /// <summary>Whether a requested value is still waiting to be confirmed.</summary>
    public bool HasPending => _hasPending;

    /// <summary>
    /// The user asked for <paramref name="wanted"/>. Shows it immediately and
    /// starts the hold-off.
    /// </summary>
    /// <returns>True when the displayed value changed.</returns>
    public bool Request(T wanted)
    {
        // A second request replaces the first rather than queueing behind it:
        // these are all last-write-wins state, and replaying an intermediate
        // click is the exact defect the engine's own request latches avoid.
        _pending = wanted;
        _hasPending = true;
        _ticks = 0;

        if (_comparer.Equals(_value, wanted))
            return false;

        _value = wanted;
        return true;
    }

    /// <summary>
    /// The engine reported <paramref name="reported"/>.
    /// </summary>
    /// <returns>True when the displayed value changed.</returns>
    public bool Apply(T reported)
    {
        if (_hasPending)
        {
            if (_comparer.Equals(reported, _pending))
            {
                // Agreement. The engine is authoritative from here, which
                // matters for the case where something else changes the value
                // next - a key press, another panel, the engine itself.
                _hasPending = false;
                _ticks = 0;
            }
            else if (++_ticks < HoldTicks)
            {
                // Still in flight. This snapshot describes a frame from before
                // the click; writing it back would undo the click on screen and
                // then redo it, which is the flicker local state exists to
                // avoid.
                return false;
            }
            else
            {
                // The engine disagreed for long enough that it is not lag. It
                // wins, visibly.
                _hasPending = false;
                _ticks = 0;
            }
        }

        if (_comparer.Equals(_value, reported))
            return false;

        _value = reported;
        return true;
    }

    /// <summary>
    /// Drops any pending request and takes <paramref name="value"/> outright.
    /// </summary>
    /// <remarks>
    /// For a session boundary: the request was aimed at an engine that is gone,
    /// and holding it against the next one would make a fresh session open
    /// showing the previous session's tool.
    /// </remarks>
    public void Reset(T value)
    {
        _value = value;
        _pending = value;
        _hasPending = false;
        _ticks = 0;
    }
}
