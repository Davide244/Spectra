using SpectraEngine.Core.Bsp;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// What a node IS, reduced to the one fact a list of names cannot show.
/// </summary>
/// <remarks>
/// <b>This exists for the boundary, not for the engine.</b> Nothing inside the
/// engine asks a node what kind it is; every system reads the payload it cares
/// about and ignores the rest. A UI cannot do that: it holds ids and names
/// across a thread boundary and never touches a node, so without a stamp
/// travelling with the change, a tree of two hundred rows cannot tell a light
/// from a wall.
/// <para>
/// <b>Derived, never stored.</b> It is a pure function of the payloads a node
/// carries, computed where the change is recorded, so it can never disagree
/// with the node the way a cached flag would.
/// </para>
/// </remarks>
public enum SceneNodeKind
{
    /// <summary>Carries no payload and no children: a transform and a name.</summary>
    Empty,

    /// <summary>
    /// Has children and no payload of its own.
    /// </summary>
    /// <remarks>
    /// <b>There is no "group" marker on a node, and this is the whole of the
    /// definition.</b> Grouping creates a plain parent (see
    /// <c>StructuralEditor.TryGroup</c>), so anything with children and nothing
    /// else is one. That means a node stops reading as a group the moment it is
    /// emptied, which is exactly what a tree should show.
    /// </remarks>
    Group,

    /// <summary>Draws a mesh.</summary>
    Mesh,

    /// <summary>An additive brush fused into the compiled static world.</summary>
    BrushWorld,

    /// <summary>
    /// An additive brush that stays out of the carve, drawing from its own mesh
    /// and costing no recompile when it moves.
    /// </summary>
    BrushPart,

    /// <summary>
    /// A brush that removes solid rather than adding it.
    /// </summary>
    /// <remarks>
    /// Called out separately from the two additive kinds because a subtractive
    /// brush renders nothing at all: in a viewport it is invisible and
    /// unpickable, so the tree is the only place it can be seen.
    /// </remarks>
    BrushSubtractive,

    /// <summary>Carries a light.</summary>
    Light,
}

/// <summary>
/// Answers <see cref="SceneNodeKind"/> for a node.
/// </summary>
public static class SceneNodeClassifier
{
    /// <summary>
    /// Classifies <paramref name="node"/> by the payloads it carries.
    /// <b>Render thread only</b>: it reads a live node.
    /// </summary>
    /// <remarks>
    /// <b>The order is the priority order, and the brush cases come first</b>
    /// because a brush node legitimately carries a mesh renderer as well and
    /// what it IS, for a level editor, is the brush.
    /// </remarks>
    public static SceneNodeKind Classify(SceneNode node)
    {
        if (node.Brush is { } brush)
        {
            if (brush.Operation == BrushOperation.Subtractive)
                return SceneNodeKind.BrushSubtractive;

            return node.BrushKind == BrushKind.Part
                ? SceneNodeKind.BrushPart
                : SceneNodeKind.BrushWorld;
        }

        if (node.Light is not null)
            return SceneNodeKind.Light;

        if (node.MeshRenderer is not null)
            return SceneNodeKind.Mesh;

        return node.Children.Count > 0 ? SceneNodeKind.Group : SceneNodeKind.Empty;
    }
}
