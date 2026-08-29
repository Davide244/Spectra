namespace SpectraEngine.Editing.Hosting;

/// <summary>
/// What <see cref="SceneEditorHost.Insert"/> creates: the handful of things a
/// scene is built from, at the granularity the Model strip offers them.
/// </summary>
/// <remarks>
/// <b>A separate enum rather than more <see cref="EditorHostCommand"/> verbs</b>
/// for the same reason SetSnapIncrement is a method: the host command enum is
/// the payload-free "act on the selection" vocabulary, and an insert carries
/// what to insert. It stays an enum rather than a parameter object because
/// every property of the inserted thing beyond its kind — where it lands, what
/// it measures — is the editor's decision at insert time, edited afterwards
/// through the same panel as everything else.
/// </remarks>
public enum InsertKind
{
    /// <summary>A 2x2x2 box brush fused into the static world.</summary>
    WorldBrush,

    /// <summary>The same box as a part: outside the carve, free to move cheaply.</summary>
    PartBrush,

    /// <summary>
    /// The same box subtracting instead of adding. Always world-kind, because
    /// a subtractive part is the one pairing that cancels to nothing: parts
    /// leave the placement list, which is exactly where a negative does its
    /// whole job.
    /// </summary>
    SubtractiveBrush,

    /// <summary>A point light, lifted clear of the surface it was aimed at.</summary>
    PointLight,

    /// <summary>An empty node, for organising what exists.</summary>
    Group,
}
