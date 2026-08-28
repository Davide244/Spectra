using System.Numerics;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The stable identity of one manipulator handle. Hit-testing returns one of
/// these and the drag machine consumes it, so the two never have to agree on
/// anything more fragile than an enum value: a handle keeps its identity
/// across frames even as the gizmo's world geometry is rebuilt every frame for
/// a moving camera.
/// </summary>
/// <remarks>
/// <b>There are six axis handles, not three, and that is what lets a resize act
/// on either face of an axis.</b> A face-anchored resize moves the dragged face
/// and plants the opposite one, so a roster with only the positive ends can only
/// ever move the positive face: the negative face of every object is
/// unreachable, and "grow it leftwards" has no gesture at all. The negative
/// values are laid out contiguously after the positive ones so
/// <see cref="GizmoHandles.IsAxis"/> stays a range test and a roster can be
/// expressed as "up to <see cref="AxisZ"/>" or "up to <see cref="AxisNegZ"/>".
/// <para>
/// Which of the six a given <see cref="GizmoStyle"/> actually offers is the
/// style's business (<see cref="GizmoStyle.NegativeAxisHandles"/>); this enum
/// only names what a handle can be.
/// </para>
/// <para>
/// A plane handle is named for the two world axes it <em>spans</em>
/// (<see cref="PlaneXY"/> moves in x and y), so its constraint plane's normal
/// is the third axis. That is the Hammer/Roblox convention and the one
/// <see cref="GizmoHandles.PlaneNormal"/> implements.
/// </para>
/// </remarks>
public enum GizmoHandle
{
    /// <summary>No handle: nothing hovered, nothing being dragged.</summary>
    None = 0,

    /// <summary>The +x arrow or face handle; drags are constrained to the x axis.</summary>
    AxisX,

    /// <summary>The +y arrow or face handle; drags are constrained to the y axis.</summary>
    AxisY,

    /// <summary>The +z arrow or face handle; drags are constrained to the z axis.</summary>
    AxisZ,

    /// <summary>
    /// The −x arrow or face handle. Same axis as <see cref="AxisX"/>, opposite
    /// direction: a resize grabbed here moves the −x face and plants the +x one.
    /// </summary>
    AxisNegX,

    /// <summary>The −y arrow or face handle. See <see cref="AxisNegX"/>.</summary>
    AxisNegY,

    /// <summary>The −z arrow or face handle. See <see cref="AxisNegX"/>.</summary>
    AxisNegZ,

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
/// values: which handles are axes, which way each one points, and what world
/// axis or plane normal each one stands for.
/// </summary>
/// <remarks>
/// Pure functions over an enum: no state, no allocation, safe from any thread
/// (though everything that calls them is render-thread-only).
/// <para>
/// These answer in WORLD axes. A gizmo laid out in a node's own frame asks
/// <see cref="GizmoGeometry"/> instead (<c>Axis</c>, <c>PlaneNormal</c>,
/// <c>PlaneAxes</c>, <c>AxisPerpendiculars</c>), which answers the same
/// questions in whatever frame the gizmo was built in: the two agree exactly
/// when that frame is the world.
/// </para>
/// </remarks>
public static class GizmoHandles
{
    /// <summary>True for all six axis handles, positive and negative alike.</summary>
    public static bool IsAxis(GizmoHandle handle) =>
        handle >= GizmoHandle.AxisX && handle <= GizmoHandle.AxisNegZ;

    /// <summary>True for the three handles pointing along a positive frame axis.</summary>
    public static bool IsPositiveAxis(GizmoHandle handle) =>
        handle >= GizmoHandle.AxisX && handle <= GizmoHandle.AxisZ;

    /// <summary>True for the three handles pointing along a negative frame axis.</summary>
    public static bool IsNegativeAxis(GizmoHandle handle) =>
        handle >= GizmoHandle.AxisNegX && handle <= GizmoHandle.AxisNegZ;

    /// <summary>True for the three plane quads.</summary>
    public static bool IsPlane(GizmoHandle handle) =>
        handle is GizmoHandle.PlaneYZ or GizmoHandle.PlaneZX or GizmoHandle.PlaneXY;

    /// <summary>
    /// Which way an axis handle points along its own axis: +1 for the positive
    /// three, −1 for the negative three, 0 for anything else.
    /// </summary>
    /// <remarks>
    /// This is the sign a resize needs and a move does not. A move along −x and a
    /// move along +x reach the same places (the constraint is a line, and a drag
    /// runs both ways along it), but a face-anchored resize has to know which
    /// face it is holding.
    /// </remarks>
    public static float AxisSign(GizmoHandle handle)
    {
        if (IsPositiveAxis(handle))
            return 1f;

        return IsNegativeAxis(handle) ? -1f : 0f;
    }

    /// <summary>
    /// The positive handle on the same axis: <see cref="GizmoHandle.AxisNegY"/>
    /// maps to <see cref="GizmoHandle.AxisY"/>, and a positive handle maps to
    /// itself. <see cref="GizmoHandle.None"/> for a non-axis handle.
    /// </summary>
    /// <remarks>
    /// The canonical form for anything that cares only about <em>which</em> axis
    /// is being driven and not about which end of it: colours, free-axis masks,
    /// perpendicular bases.
    /// </remarks>
    public static GizmoHandle PositiveAxis(GizmoHandle handle)
    {
        if (IsPositiveAxis(handle))
            return handle;

        if (IsNegativeAxis(handle))
            return handle - (GizmoHandle.AxisNegX - GizmoHandle.AxisX);

        return GizmoHandle.None;
    }

    /// <summary>
    /// The handle on the other end of the same axis, or
    /// <see cref="GizmoHandle.None"/> for a non-axis handle.
    /// </summary>
    public static GizmoHandle Opposite(GizmoHandle handle)
    {
        if (IsPositiveAxis(handle))
            return handle + (GizmoHandle.AxisNegX - GizmoHandle.AxisX);

        if (IsNegativeAxis(handle))
            return handle - (GizmoHandle.AxisNegX - GizmoHandle.AxisX);

        return GizmoHandle.None;
    }

    /// <summary>
    /// The unit world direction an axis handle points in, negatives included;
    /// <see cref="Vector3.Zero"/> for any other handle.
    /// </summary>
    public static Vector3 AxisDirection(GizmoHandle handle) => handle switch
    {
        GizmoHandle.AxisX => Vector3.UnitX,
        GizmoHandle.AxisY => Vector3.UnitY,
        GizmoHandle.AxisZ => Vector3.UnitZ,
        GizmoHandle.AxisNegX => -Vector3.UnitX,
        GizmoHandle.AxisNegY => -Vector3.UnitY,
        GizmoHandle.AxisNegZ => -Vector3.UnitZ,
        _ => Vector3.Zero,
    };

    /// <summary>
    /// The unit normal of a plane handle's constraint plane: the world axis
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
    /// move along: one axis for an arrow (either end of it), two for a plane
    /// quad, all three for the screen handle (its camera-facing constraint plane
    /// is not axis aligned, so every component is free), none for
    /// <see cref="GizmoHandle.None"/>.
    /// </summary>
    /// <remarks>
    /// <b>Unsigned deliberately.</b> The mask says which components a drag may
    /// write, which is a property of the axis and not of the end being held: a
    /// −x drag writes x exactly as a +x drag does. Anything that needs the
    /// direction asks <see cref="AxisSign"/> or <see cref="AxisDirection"/>.
    /// <para>
    /// This is what keeps grid snapping honest. Snapping the whole result
    /// vector would quantize the axes the drag never touched: an x-arrow drag
    /// would silently pull a brush sitting at y = 0.3 onto y = 0. Masking
    /// restricts the rounding to the components the user is actually moving.
    /// </para>
    /// <para>
    /// The screen handle is the one case where the mask does not match a
    /// constraint plane: a snapped screen drag rounds all three components, so
    /// the result can sit up to half a grid step off the camera-facing plane
    /// the cursor was tracked in. That is the right trade: the user asked for
    /// a grid position, and the plane was only ever the input mapping.
    /// </para>
    /// </remarks>
    public static Vector3 FreeAxisMask(GizmoHandle handle) => PositiveAxis(handle) switch
    {
        GizmoHandle.AxisX => Vector3.UnitX,
        GizmoHandle.AxisY => Vector3.UnitY,
        GizmoHandle.AxisZ => Vector3.UnitZ,
        _ => handle switch
        {
            GizmoHandle.PlaneYZ => new Vector3(0f, 1f, 1f),
            GizmoHandle.PlaneZX => new Vector3(1f, 0f, 1f),
            GizmoHandle.PlaneXY => new Vector3(1f, 1f, 0f),
            GizmoHandle.Screen => Vector3.One,
            _ => Vector3.Zero,
        },
    };
}
