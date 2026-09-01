namespace SpectraEngine.Core.Graphics;

/// <summary>
/// What a draw does with the depth buffer, chosen per draw rather than per
/// shader program.
/// </summary>
/// <remarks>
/// <para>
/// It is a per-draw value because the same program is legitimately used both
/// ways. The engine already relies on that: the debug-line shader draws
/// selection highlights, gizmo handles and the marquee on top of everything,
/// which is the only way what you can see stays what you can pick.
/// </para>
/// <para>
/// It used to be a mutable flag on the D3D12 program object, set once at
/// renderer start-up and read inside pipeline-state creation without being part
/// of the pipeline cache key. That worked only because nothing ever changed it
/// after the first draw. Anything that did would have got back a cached
/// pipeline built for the other setting, silently.
/// </para>
/// </remarks>
public enum DepthMode
{
    /// <summary>Test against the depth buffer and write to it. Ordinary opaque geometry.</summary>
    TestWrite,

    /// <summary>
    /// Test and write, but accept an exact depth tie. What a line lying ON a
    /// surface needs.
    /// </summary>
    /// <remarks>
    /// <b>Two halves, and both are load-bearing for the editor's ground grid.</b>
    /// The comparison must accept equality because a grid at y = 0 drawn over a
    /// floor whose top is also at y = 0 is coplanar by construction, and a
    /// strict Less rejects exactly the case the grid exists for - visibly, and
    /// on some backends only, since GL defaults to Less while a hand-written
    /// D3D state might not.
    /// <para>
    /// And it must WRITE, which is the non-obvious half: in a deferred frame the
    /// depth buffer is the coverage mask, so the light pass returns the SKY at
    /// every pixel whose depth is still 1. A grid line over empty space that
    /// wrote no depth would write its colour into the G-buffer and then be
    /// discarded, with no error anywhere - measured, and the reason the grid was
    /// visible on the floor and absent over the horizon.
    /// </para>
    /// <para>
    /// Safe only because such lines are submitted LAST in their pass: writing
    /// depth from a plane of one-pixel lines would otherwise let the grid reject
    /// geometry drawn after it, so anything submitted later would be sliced by
    /// an invisible lattice.
    /// </para>
    /// </remarks>
    TestWriteEqual,

    /// <summary>
    /// Test but do not write. What sorted transparency needs, so that two
    /// translucent surfaces do not occlude each other by depth.
    /// </summary>
    TestNoWrite,

    /// <summary>
    /// Neither test nor write: always on top, in submission order. What editor
    /// overlays use.
    /// </summary>
    None,
}
