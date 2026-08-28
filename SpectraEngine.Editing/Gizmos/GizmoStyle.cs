namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// Which of the two built-in manipulator styles a <see cref="GizmoStyle"/> is,
/// for a host that wants to name it, persist it, or bind a toolbar to it.
/// </summary>
public enum GizmoStyleKind
{
    /// <summary>
    /// Roblox Studio's manipulators: six per-face handles, handles standing on
    /// the selection's bounding box, resizes that hold one face. See
    /// <see cref="GizmoStyle.Studio"/>.
    /// </summary>
    Studio,

    /// <summary>
    /// The Blender/Unity/Maya layout: three positive-end handles, plane quads, a
    /// view-aligned rotate ring, and resizes about the pivot. See
    /// <see cref="GizmoStyle.Classic"/>.
    /// </summary>
    Classic,

    /// <summary>Anything a host built itself.</summary>
    Custom,
}

/// <summary>
/// Where a gizmo sits when more than one thing is selected, and for an object
/// whose geometry is not centred on its own origin.
/// </summary>
public enum GizmoPivotMode
{
    /// <summary>
    /// The average of the selected nodes' world positions: each object's own
    /// origin votes once, whatever it measures. Blender's median point, and the
    /// only mode that is meaningful for a selection with no geometry in it.
    /// </summary>
    OriginAverage,

    /// <summary>
    /// The centre of the box that encloses the whole selection, measured in the
    /// gizmo's own frame. Roblox Studio's, and the one that makes sense when the
    /// handles stand on that same box.
    /// </summary>
    BoundsCentre,
}

/// <summary>
/// Everything that differs between one editor's manipulators and another's:
/// which handles exist, where they stand, what a resize holds still, and the
/// proportions the whole gizmo is drawn and picked at.
/// </summary>
/// <remarks>
/// <b>The two styles are not skins.</b> They disagree about the roster (six
/// face handles or three axis ends), about where the handles live (on the
/// selection's box or at a fixed distance from its pivot), about what a resize
/// holds still (the opposite face or the pivot), and about where the pivot is at
/// all. A user who knows one of them and is handed the other with the wrong
/// half of these gets a manipulator that looks familiar and behaves wrong, which
/// is worse than an unfamiliar one.
/// <para>
/// <b>It is data, not a strategy object, and the hit testers and renderers stay
/// single implementations that read it.</b> Everything that varies turned out to
/// be a number, a flag or an enum, and keeping one <c>Pick</c> and one
/// <c>Draw</c> per tool is what keeps the engine's oldest gizmo rule mechanical:
/// what is drawn and what is pickable come out of the same
/// <see cref="GizmoGeometry"/>, so they cannot drift apart per style. Two
/// implementations per tool would be two chances for exactly that.
/// </para>
/// <para>
/// <b>The proportions used to be private constants on
/// <see cref="GizmoGeometry"/>.</b> <see cref="Classic"/> reproduces every one
/// of them exactly, so a gizmo built in that style is bit-for-bit the gizmo this
/// engine drew before styles existed.
/// </para>
/// <para>
/// <b>Threading:</b> immutable after construction, so the presets are safely
/// shared; everything that reads one is render-thread-only anyway. A style is
/// resolved once and held, never rebuilt per frame.
/// </para>
/// </remarks>
public sealed class GizmoStyle
{
    /// <summary>
    /// Roblox Studio's manipulators, and the engine's default: six per-direction
    /// handles for move and resize, standing on the selection's bounding box,
    /// with resizes that hold the opposite face and a pivot at the box centre.
    /// </summary>
    /// <remarks>
    /// <b>The six handles and the face anchoring are one decision, not two.</b>
    /// A resize that plants the opposite face can only ever move the face it was
    /// grabbed by, so a roster with three positive ends leaves the other three
    /// faces of every object unreachable. That pairing (Studio's semantics on a
    /// Blender roster) is precisely the defect this style exists to end.
    /// <para>
    /// <b>Handles stand on the box because that is what makes the anchoring
    /// legible.</b> When the handle you grab is sitting on the face that is
    /// about to move, "this face goes out, that one stays" needs no explaining.
    /// A handle floating at a fixed distance from the pivot, driving a face
    /// somewhere else, does.
    /// </para>
    /// <para>
    /// <b>What is deliberately missing:</b> the plane quads and the view-aligned
    /// rotate ring, neither of which Studio has, and surface dragging, which it
    /// does have and this engine does not yet (a press on an object still moves
    /// it in the camera plane here).
    /// </para>
    /// </remarks>
    public static GizmoStyle Studio { get; } = new()
    {
        Kind = GizmoStyleKind.Studio,
        Name = "Studio",
        NegativeAxisHandles = true,
        PlaneHandles = false,
        CentreDiscHandle = false,
        ViewRing = false,
        HandlesStandOffBounds = true,
        FaceAnchoredResize = true,
        PivotMode = GizmoPivotMode.BoundsCentre,
        ShaftLengthFactor = 0.34f,
        HandleBoxRadiusFactor = 0.1f,
        ScreenRadiusFactor = 0.12f,
        BoundsGapPixels = 10f,
        MinimumReachFactor = 0.42f,
    };

    /// <summary>
    /// The layout every other 3D application uses: three arrows on the positive
    /// ends, three plane quads, a centre disc, four rotate rings, and resizes
    /// about the pivot rather than about a face.
    /// </summary>
    /// <remarks>
    /// <b>Three handles is the right roster here precisely because the resize is
    /// symmetric.</b> Scaling about the pivot moves both faces at once, so the
    /// negative end of an axis is not a separate gesture: it is the same drag,
    /// pushed the other way. Blender, Unity and Maya all make this pairing, and
    /// making only half of it is what produces a manipulator that cannot reach
    /// half of its object.
    /// <para>
    /// Every proportion here is the value the engine used before styles existed.
    /// </para>
    /// </remarks>
    public static GizmoStyle Classic { get; } = new()
    {
        Kind = GizmoStyleKind.Classic,
        Name = "Classic",
        NegativeAxisHandles = false,
        PlaneHandles = true,
        CentreDiscHandle = true,
        ViewRing = true,
        HandlesStandOffBounds = false,
        FaceAnchoredResize = false,
        PivotMode = GizmoPivotMode.OriginAverage,
        ShaftLengthFactor = 1f,
        HandleBoxRadiusFactor = 0.075f,
        ScreenRadiusFactor = 0.14f,
        BoundsGapPixels = 0f,
        MinimumReachFactor = 1f,
    };

    /// <summary>Which of the built-in styles this is, or <see cref="GizmoStyleKind.Custom"/>.</summary>
    public GizmoStyleKind Kind { get; init; } = GizmoStyleKind.Custom;

    /// <summary>A short display name, for a toolbar or the periodic stats line.</summary>
    public string Name { get; init; } = "Custom";

    /// <summary>
    /// Whether the three negative-direction axis handles exist. See the remarks
    /// on <see cref="Studio"/> for why this travels with
    /// <see cref="FaceAnchoredResize"/>.
    /// </summary>
    public bool NegativeAxisHandles { get; init; }

    /// <summary>Whether the move tool offers the three two-axis plane quads.</summary>
    public bool PlaneHandles { get; init; }

    /// <summary>
    /// Whether the move tool draws a grabbable centre disc. The free-move
    /// constraint behind it (<see cref="GizmoTool.FreeMoveHandle"/>) exists
    /// either way: it is what a press on the object itself routes into, and that
    /// gesture needs no visible handle.
    /// </summary>
    public bool CentreDiscHandle { get; init; }

    /// <summary>Whether the rotate tool offers the larger view-aligned ring outside the three axis rings.</summary>
    public bool ViewRing { get; init; }

    /// <summary>
    /// Whether handles stand at the faces of the selection's bounding box rather
    /// than at a fixed distance from the pivot. The handles keep their constant
    /// screen SIZE either way; this decides only how far out they sit.
    /// </summary>
    public bool HandlesStandOffBounds { get; init; }

    /// <summary>
    /// Whether a resize holds the face opposite the grabbed handle still
    /// (Studio's) or scales about the pivot so both faces move (Blender's).
    /// <see cref="ScaleGizmo.SymmetricModifier"/> asks for the other one for the
    /// duration of a gesture, whichever way round this is.
    /// </summary>
    public bool FaceAnchoredResize { get; init; }

    /// <summary>Where the gizmo sits for a multi-selection, and for off-centre geometry.</summary>
    public GizmoPivotMode PivotMode { get; init; } = GizmoPivotMode.OriginAverage;

    /// <summary>
    /// How much of an axis handle is shaft, in units of
    /// <see cref="GizmoGeometry.AxisLength"/>, measured back from where the
    /// handle stands.
    /// </summary>
    /// <remarks>
    /// One at <see cref="Classic"/>, where the shaft runs the whole way from the
    /// pivot to the arrowhead. A fraction wherever handles stand on the bounds,
    /// where a full-length shaft would be a line drawn from inside the object
    /// out to its own surface: depth-off, so it would be visible through the
    /// object it is decorating, and long enough to be picked far from the handle
    /// it belongs to.
    /// </remarks>
    public float ShaftLengthFactor { get; init; } = 1f;

    /// <summary>The half-extent of a resize handle's cube, in units of <see cref="GizmoGeometry.AxisLength"/>.</summary>
    public float HandleBoxRadiusFactor { get; init; } = 0.075f;

    /// <summary>The radius of the screen-facing centre disc and the uniform resize cube.</summary>
    public float ScreenRadiusFactor { get; init; } = 0.14f;

    /// <summary>How far from the pivot a plane quad's near corner sits.</summary>
    public float PlaneOffsetFactor { get; init; } = 0.32f;

    /// <summary>The edge length of a plane quad.</summary>
    public float PlaneSizeFactor { get; init; } = 0.26f;

    /// <summary>The radius of one rotate axis ring, when rings are not sized to the bounds.</summary>
    public float RingRadiusFactor { get; init; } = 0.86f;

    /// <summary>The radius of the view-aligned rotate ring.</summary>
    public float ScreenRingRadiusFactor { get; init; } = 1.1f;

    /// <summary>The length of an arrowhead, in units of <see cref="GizmoGeometry.AxisLength"/>.</summary>
    public float HeadLengthFactor { get; init; } = 0.22f;

    /// <summary>The radius of an arrowhead's base.</summary>
    public float HeadRadiusFactor { get; init; } = 0.07f;

    /// <summary>
    /// The clearance in pixels between the selection's bounding box and the
    /// NEAR end of the handle standing off it. Ignored unless
    /// <see cref="HandlesStandOffBounds"/>.
    /// </summary>
    /// <remarks>
    /// In pixels rather than world units so the clearance looks the same at any
    /// camera distance, exactly as the handle's own size does. The handle's own
    /// body is added to it (a cube's half-extent, an arrow's whole length), so
    /// this really is the visible gap and not a distance to some point inside
    /// the handle.
    /// </remarks>
    public float BoundsGapPixels { get; init; }

    /// <summary>
    /// The floor on how far out a handle may stand, in units of
    /// <see cref="GizmoGeometry.AxisLength"/>, so a tiny or a flat object still
    /// gets handles far enough apart to aim at.
    /// </summary>
    /// <remarks>
    /// Without it, a one-centimetre part (or any object with a zero extent on
    /// one axis, which every flat mesh has) collapses its whole gizmo into a
    /// single pile of overlapping handles at the pivot, and the smallest thing
    /// in the scene becomes the hardest to manipulate.
    /// </remarks>
    public float MinimumReachFactor { get; init; } = 1f;

    /// <summary>
    /// The last axis handle in this style's roster: <see cref="GizmoHandle.AxisZ"/>
    /// for three handles, <see cref="GizmoHandle.AxisNegZ"/> for six. The axis
    /// values are contiguous, so a roster walk is <c>for (h = AxisX; h &lt;= this;
    /// h++)</c> with no allocation and no array.
    /// </summary>
    public GizmoHandle LastAxisHandle =>
        NegativeAxisHandles ? GizmoHandle.AxisNegZ : GizmoHandle.AxisZ;

    /// <summary>
    /// Whether this style offers <paramref name="handle"/> at all. The one
    /// question hit-testing and drawing both have to agree on, asked in one
    /// place so they cannot answer it differently.
    /// </summary>
    /// <param name="handle">The handle to test.</param>
    /// <param name="mode">Which tool is asking: the rosters differ per tool.</param>
    public bool Offers(GizmoHandle handle, GizmoMode mode)
    {
        if (GizmoHandles.IsNegativeAxis(handle))
        {
            // A negative ring would be the same ring: a rotation about −x and one
            // about +x sweep the same circle, so the roster stops at three
            // whatever the style says about the other two tools.
            return NegativeAxisHandles && mode != GizmoMode.Rotate;
        }

        if (GizmoHandles.IsPositiveAxis(handle))
            return true;

        if (GizmoHandles.IsPlane(handle))
            return PlaneHandles && mode == GizmoMode.Translate;

        return handle == GizmoHandle.Screen && mode switch
        {
            // The uniform resize cube is offered by both styles. Studio has no
            // such handle, but dropping it would remove the only uniform resize
            // in the editor to buy fidelity, and it sits at the pivot where
            // nothing else competes for the cursor.
            GizmoMode.Scale => true,
            GizmoMode.Rotate => ViewRing,
            _ => CentreDiscHandle,
        };
    }
}
