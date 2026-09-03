using System;

namespace SpectraEngine.Editing.Hosting;

/// <summary>
/// What one <see cref="SceneEditorHost.InsertModel"/> did: the node it placed,
/// and the reason that node has no geometry when it has none.
/// </summary>
/// <remarks>
/// <para>
/// <b>A model that cannot be resolved still places a node, and this is where
/// the reason goes.</b> That is the map loader's rule
/// (<c>MapSceneBinder.AttachMesh</c>) applied to a drop: a brush that cannot be
/// built is a hole in the world, a missing prop is a missing decoration. The
/// alternative is a drag gesture that ends in silence, which is
/// indistinguishable from a drag the shell never received at all.
/// </para>
/// <para>
/// <b><see cref="Refused"/> and <see cref="Unresolved"/> are different
/// failures and a caller must not flatten them.</b> A refusal means nothing
/// happened - play mode owns the scene, or a manipulation is open - and the
/// answer is to try again; an unresolved model means a node IS in the scene and
/// in the history, and the answer is to fix the asset. Reported as one string
/// they would read as the same message and the second one would be undone by a
/// user who thought the drop had not landed.
/// </para>
/// <para>
/// Built on the render thread, read on whichever thread the caller marshalled
/// back to. Every member is a value, so it crosses that boundary the way
/// <c>FrameSnapshot</c> does.
/// </para>
/// </remarks>
/// <param name="ContentPath">The content-relative path that was asked for.</param>
/// <param name="NodeId">
/// The node that was placed, or <see cref="Guid.Empty"/> when nothing was.
/// </param>
/// <param name="NodeName">The placed node's name, or an empty string.</param>
/// <param name="Unresolved">
/// Why the placed node carries no geometry, or null when it does.
/// </param>
/// <param name="Refused">
/// Why nothing was placed at all, or null when something was.
/// </param>
public readonly record struct ModelInsertReport(
    string ContentPath,
    Guid NodeId,
    string NodeName,
    string? Unresolved,
    string? Refused)
{
    /// <summary>Whether a node reached the scene.</summary>
    public bool Placed => NodeId != Guid.Empty;

    /// <summary>Whether a node reached the scene carrying the model's geometry.</summary>
    public bool IsComplete => Placed && Unresolved is null;

    /// <summary>A report for a drop nothing acted on, naming why.</summary>
    public static ModelInsertReport RefusedBecause(string contentPath, string reason) =>
        new(contentPath, Guid.Empty, string.Empty, null, reason);

    /// <summary>One line for a status bar or an output log.</summary>
    public string Describe()
    {
        if (Refused is { } refused)
            return $"{ContentPath} was not placed: {refused}.";

        return Unresolved is { } unresolved
            ? $"{ContentPath} was placed as an empty node: {unresolved}."
            : $"{ContentPath} placed as '{NodeName}'.";
    }
}
