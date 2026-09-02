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
    /// Test and write, but accept an exact depth tie.
    /// </summary>
    /// <remarks>
    /// The comparison accepts equality for anything lying exactly ON a
    /// surface, which a strict Less rejects — visibly, and on some backends
    /// only, since GL defaults to Less while a hand-written D3D state might
    /// not. Nothing draws with this mode today: the editor's ground grid used
    /// it while lines were opaque G-buffer overwrites whose depth write WAS
    /// the deferred coverage mask, and moved to
    /// <see cref="TestNoWriteEqual"/> when the lane became alpha-blended.
    /// Kept because "opaque decal exactly on a surface" is a real future
    /// draw, and the D3D12 state map already speaks it.
    /// </remarks>
    TestWriteEqual,

    /// <summary>
    /// Test but do not write. What sorted transparency needs, so that two
    /// translucent surfaces do not occlude each other by depth.
    /// </summary>
    TestNoWrite,

    /// <summary>
    /// Test without writing, accepting an exact depth tie. What a BLENDED line
    /// lying on a surface needs: LessEqual for the coplanar grid-on-floor case
    /// (see <see cref="TestWriteEqual"/>), and no write because a translucent
    /// pixel has no business in the depth buffer — a plane of one-pixel lines
    /// that wrote depth would slice whatever a pipeline submits after it.
    /// </summary>
    TestNoWriteEqual,

    /// <summary>
    /// Neither test nor write: always on top, in submission order. What editor
    /// overlays use.
    /// </summary>
    None,
}
