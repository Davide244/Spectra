using System;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// The demo's vertical bob for a single brush node: a rigid per-frame
/// translation that keeps the async static-world recompile continuously
/// exercised, and that <em>yields</em> to anyone else who writes the node.
/// </summary>
/// <remarks>
/// <b>Why the yielding matters.</b> The animation is the last writer in the
/// frame — the engine runs the editor first and the demo update after it — so a
/// naive "position = rest + sin(t)" line silently discards every gizmo drag,
/// undo and redo aimed at the animated node within the same frame it was made.
/// The symptom is not a wobble but a manipulator that appears to do nothing at
/// all, which is the kind of thing that reads as a broken gizmo rather than as
/// a demo animation, so the rule here is: the edit is intent, the bob is
/// garnish, and the bob re-centres itself on whatever the edit left behind.
/// <para>
/// <b>The hand-off is invisible on purpose.</b> Adopting the raw edited
/// position as the new rest pose would snap the node by up to a full amplitude
/// on the next frame — the exact class of unexplained jump this type exists to
/// avoid. Subtracting the offset this animation last applied instead means the
/// first frame after an edit writes the node back to precisely where the editor
/// left it, and the sine simply carries on from there.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like every other scene mutation.
/// </para>
/// </remarks>
internal sealed class DemoBobAnimation
{
    private readonly SceneNode _node;
    private readonly float _amplitude;
    private readonly double _periodSeconds;

    // The pose the sine oscillates around, and the exact value this animation
    // last wrote. The node's transform setters store what they are given
    // component-exact (and early-out on an equal write), so "the node no longer
    // holds what I wrote" is a reliable, allocation-free "somebody else wrote
    // it" — no epsilon, no change token, no event subscription.
    private Vector3 _rest;
    private Vector3 _lastWritten;

    /// <summary>
    /// Starts a bob around the node's current local position.
    /// </summary>
    /// <param name="node">The node to translate; its current pose becomes the rest pose.</param>
    /// <param name="amplitude">Peak vertical displacement, in world units.</param>
    /// <param name="periodSeconds">Seconds per full cycle; must be positive.</param>
    public DemoBobAnimation(SceneNode node, float amplitude, double periodSeconds)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(periodSeconds);

        _node = node;
        _amplitude = amplitude;
        _periodSeconds = periodSeconds;
        _rest = node.LocalPosition;
        _lastWritten = _rest;
    }

    /// <summary>The pose the bob currently oscillates around.</summary>
    public Vector3 Rest => _rest;

    /// <summary>
    /// Writes this frame's pose, first adopting any external edit made since
    /// the previous call as the new rest pose.
    /// </summary>
    /// <param name="elapsedSeconds">
    /// Total elapsed demo time, in seconds — the sine's argument. Passing the
    /// running total rather than a delta keeps the phase independent of frame
    /// pacing, so a stalled frame does not shift the animation.
    /// </param>
    /// <summary>
    /// The node this drives. Exposed so a host that replaces the graph (loading
    /// a map over the authored scene) can rebind by id rather than leave the
    /// animation writing to a detached node every frame, which stops nothing
    /// and reports nothing.
    /// </summary>
    public SceneNode Node => _node;

    public void Advance(double elapsedSeconds)
    {
        Vector3 current = _node.LocalPosition;
        if (current != _lastWritten)
        {
            // A gizmo drag, an undo, a redo, or any other writer got here since
            // the last frame. Re-centre on it, cancelling out the displacement
            // this animation itself contributed, so the next write below lands
            // the node where that writer put it.
            _rest = current - (_lastWritten - _rest);
        }

        float bob = _amplitude * MathF.Sin((float)(elapsedSeconds * (2.0 * Math.PI / _periodSeconds)));

        // Rigid translation only — a scale or shear would be rejected by the
        // static-world snapshot's rigidity validation. Writing LocalPosition on
        // a brush node auto-dirties the static world, so this line alone keeps
        // the async recompile pipeline continuously exercised.
        Vector3 next = _rest + new Vector3(0f, bob, 0f);
        _node.LocalPosition = next;
        _lastWritten = next;
    }
}
