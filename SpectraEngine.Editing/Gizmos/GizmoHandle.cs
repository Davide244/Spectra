using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The stable identity of one manipulator handle. Hit-testing returns one of
/// these and the drag machine consumes it, so the two never have to agree on
/// anything more fragile than an enum value — a handle keeps its identity
/// across frames even as the gizmo's world geometry is rebuilt every frame for
/// a moving camera.
/// </summary>
/// <remarks>
/// A plane handle is named for the two world axes it <em>spans</em>
/// (<see cref="PlaneXY"/> moves in x and y), so its constraint plane's normal
/// is the third axis. That is the Hammer/Roblox convention and the one
/// <see cref="GizmoHandles.PlaneNormal"/> implements.
/// </remarks>
public enum GizmoHandle
{
    /// <summary>No handle — nothing hovered, nothing being dragged.</summary>
    None = 0,

    /// <summary>The world x arrow; drags are constrained to the x axis.</summary>
    AxisX,

    /// <summary>The world y arrow; drags are constrained to the y axis.</summary>
    AxisY,

    /// <summary>The world z arrow; drags are constrained to the z axis.</summary>
    AxisZ,

    /// <summary>The quad spanning y and z; drags are constrained to the x = pivot plane.</summary>
    PlaneYZ,

    /// <summary>The quad spanning z and x; drags are constrained to the y = pivot plane.</summary>
    PlaneZX,

    /// <summary>The quad spanning x and y; drags are constrained to the z = pivot plane.</summary>
    PlaneXY,

    /// <summary>
    /// The centre disc; drags are constrained to the camera-facing plane
    /// through the pivot, so the selection follows the cursor in screen space.
    /// </summary>
    Screen,
}

/// <summary>
/// Classification and world-space vocabulary for <see cref="GizmoHandle"/>
/// values — which handles are axes, which are planes, and what world axis or
/// plane normal each one stands for.
/// </summary>
/// <remarks>
/// Pure functions over an enum: no state, no allocation, safe from any thread
/// (though everything that calls them is render-thread-only).
/// <para>
/// These answer in WORLD axes. A gizmo laid out in a node's own frame asks
/// <see cref="GizmoGeometry"/> instead (<c>Axis</c>, <c>PlaneNormal</c>,
/// <c>PlaneAxes</c>, <c>AxisPerpendiculars</c>), which answers the same
/// questions in whatever frame the gizmo was built in — the two agree exactly
/// when that frame is the world.
/// </para>
/// </remarks>
public static class GizmoHandles
{
    /// <summary>True for the three axis arrows.</summary>
    public static bool IsAxis(GizmoHandle handle) =>
        handle is GizmoHandle.AxisX or GizmoHandle.AxisY or GizmoHandle.AxisZ;

    /// <summary>True for the three plane quads.</summary>
    public static bool IsPlane(GizmoHandle handle) =>
        handle is GizmoHandle.PlaneYZ or GizmoHandle.PlaneZX or GizmoHandle.PlaneXY;

    /// <summary>
    /// The unit world axis an axis handle constrains to;
    /// <see cref="Vector3.Zero"/> for any other handle.
    /// </summary>
    public static Vector3 AxisDirection(GizmoHandle handle) => handle switch
    {
        GizmoHandle.AxisX => Vector3.UnitX,
        GizmoHandle.AxisY => Vector3.UnitY,
        GizmoHandle.AxisZ => Vector3.UnitZ,
        _ => Vector3.Zero,
    };

    /// <summary>
    /// The unit normal of a plane handle's constraint plane — the world axis
    /// the handle does <em>not</em> span. <see cref="Vector3.Zero"/> for any
    /// other handle (the screen handle's normal is the camera's, not a world
    /// axis, so it comes from the geometry instead).
    /// </summary>
    public static Vector3 PlaneNormal(GizmoHandle handle) => handle switch
    {
        GizmoHandle.PlaneYZ => Vector3.UnitX,
        GizmoHandle.PlaneZX => Vector3.UnitY,
        GizmoHandle.PlaneXY => Vector3.UnitZ,
        _ => Vector3.Zero,
    };

    /// <summary>
    /// A componentwise 0/1 mask of the world axes a handle's drag can actually
    /// move along: one axis for an arrow, two for a plane quad, all three for
    /// the screen handle (its camera-facing constraint plane is not axis
    /// aligned, so every component is free), none for
    /// <see cref="GizmoHandle.None"/>.
    /// </summary>
    /// <remarks>
    /// This is what keeps grid snapping honest. Snapping the whole result
    /// vector would quantize the axes the drag never touched — an x-arrow drag
    /// would silently pull a brush sitting at y = 0.3 onto y = 0. Masking
    /// restricts the rounding to the components the user is actually moving.
    /// <para>
    /// The screen handle is the one case where the mask does not match a
    /// constraint plane: a snapped screen drag rounds all three components, so
    /// the result can sit up to half a grid step off the camera-facing plane
    /// the cursor was tracked in. That is the right trade — the user asked for
    /// a grid position, and the plane was only ever the input mapping.
    /// </para>
    /// </remarks>
    public static Vector3 FreeAxisMask(GizmoHandle handle) => handle switch
    {
        GizmoHandle.AxisX => Vector3.UnitX,
        GizmoHandle.AxisY => Vector3.UnitY,
        GizmoHandle.AxisZ => Vector3.UnitZ,
        GizmoHandle.PlaneYZ => new Vector3(0f, 1f, 1f),
        GizmoHandle.PlaneZX => new Vector3(1f, 0f, 1f),
        GizmoHandle.PlaneXY => new Vector3(1f, 1f, 0f),
        GizmoHandle.Screen => Vector3.One,
        _ => Vector3.Zero,
    };
}
