using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Editing.Viewport;

namespace SpectraEngine.Editing.Selection;

/// <summary>
/// The pure query behind box select: which of a scene's spatial nodes a
/// screen-space rectangle covers. Stateless and side-effect free — the drag
/// state machine and the selection update live in
/// <see cref="BoxSelectController"/>, so this half is directly testable against
/// a brute-force oracle.
/// </summary>
/// <remarks>
/// <b>Two stages, and each is there for a reason.</b>
/// <list type="number">
///   <item><description>
///     <b>Broad phase:</b> the rectangle becomes a sub-frustum
///     (<c>Camera.ScreenRectToFrustum</c>) and the scene's BVH answers which
///     nodes' world AABBs meet it. This is the same machinery view culling
///     uses, so a marquee over a hundred-thousand-node world costs a tree
///     descent rather than a linear scan.
///   </description></item>
///   <item><description>
///     <b>Refinement:</b> <c>Frustum.Intersects</c> is deliberately
///     conservative — a box can straddle a corner region and be reported
///     without ever entering the volume — so each survivor is re-tested
///     against the rectangle in the space the user actually drew it: the
///     node's world AABB is projected and its screen-space bounding rectangle
///     compared with the marquee. That is the definition this type
///     implements, and the one the oracle in the tests re-derives
///     independently.
///   </description></item>
/// </list>
/// <b>Nodes that straddle the eye plane keep the broad-phase verdict.</b> A box
/// with a corner behind the camera has no finite projection (the perspective
/// divide flips sign through <c>w = 0</c>), so refining it is not merely
/// imprecise, it is undefined. Those nodes are accepted in
/// <see cref="BoxSelectMode.Intersect"/> — the volume really does reach them —
/// and rejected in <see cref="BoxSelectMode.Contain"/>, where "entirely inside
/// the rectangle" cannot be true of something that wraps the viewer.
/// <para>
/// <b>Threading:</b> render thread only, like every scene query. Allocation-free
/// once the caller's result list has grown: refinement compacts that list in
/// place rather than filtering into a second one.
/// </para>
/// </remarks>
public static class BoxSelectQuery
{
    // A corner this close to the eye plane has an unusable perspective divide.
    private const float MinClipW = 1e-4f;

    /// <summary>
    /// Fills <paramref name="results"/> with every spatial node the rectangle
    /// covers, in BVH traversal order. The list is cleared first.
    /// </summary>
    /// <param name="scene">The scene to query. Its camera defines the projection.</param>
    /// <param name="rect">The marquee, in viewport pixels (top-left origin, y down).</param>
    /// <param name="viewportSize">The viewport's extent in the same pixel units.</param>
    /// <param name="mode">Touch or fully-contain.</param>
    /// <param name="results">Reused across drags; cleared and refilled.</param>
    public static void Query(
        Scene scene,
        in ScreenRect rect,
        Vector2 viewportSize,
        BoxSelectMode mode,
        List<SceneNode> results)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(results);

        results.Clear();
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return;

        Camera camera = scene.Camera;
        Frustum frustum = camera.ScreenRectToFrustum(rect.Min, rect.Max, viewportSize);
        scene.QueryFrustum(in frustum, results);

        // Compact in place: everything at `write` and below has passed
        // refinement, everything above it has yet to be judged.
        int write = 0;
        for (int read = 0; read < results.Count; read++)
        {
            SceneNode node = results[read];
            if (!Covers(scene, camera, node, in rect, viewportSize, mode))
                continue;

            results[write++] = node;
        }

        results.RemoveRange(write, results.Count - write);

        AppendCoveredLights(scene, camera, in rect, viewportSize, mode, results);
    }

    /// <summary>
    /// Adds every light whose icon the rectangle covers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second pass, because lights are not in the spatial index</b> - and
    /// they are deliberately not, since admitting them would make every lamp
    /// collidable and query-visible through <c>PhysicsFlags.Default</c>. Before
    /// this, dragging a marquee across a room full of lamps selected none of
    /// them and CLEARED whatever was selected, which reads as the marquee being
    /// broken rather than as lights being special.
    /// </para>
    /// <para>
    /// <b>It tests the ICON, at the same screen-constant radius the overlay
    /// draws and the click picks.</b> One definition of where a light is on
    /// screen, so a marquee and a click cannot disagree about what a lamp is -
    /// which is the whole reason this lives here rather than in the controller.
    /// </para>
    /// <para>
    /// Linear in the number of lights. That is the right complexity: it is
    /// proportional to what can be selected this way rather than to the world,
    /// and it wants an index of its own only once lights themselves number in
    /// the thousands.
    /// </para>
    /// </remarks>
    private static void AppendCoveredLights(
        Scene scene,
        Camera camera,
        in ScreenRect rect,
        Vector2 viewportSize,
        BoxSelectMode mode,
        List<SceneNode> results)
    {
        IReadOnlyList<SceneNode> lights = scene.LightNodes;

        for (int i = 0; i < lights.Count; i++)
        {
            SceneNode node = lights[i];

            // A light node that ALSO carries geometry is already in the index
            // and has already been judged; adding it again would duplicate it
            // in the selection.
            if (node.Brush is not null || node.MeshRenderer is not null)
                continue;

            if (CoversLightIcon(camera, node, in rect, viewportSize, mode))
                results.Add(node);
        }
    }

    /// <summary>
    /// Whether the rectangle covers this light's icon. Public so the brute-force
    /// oracle in the tests can use exactly the same rule.
    /// </summary>
    public static bool CoversLightIcon(
        Camera camera,
        SceneNode node,
        in ScreenRect rect,
        Vector2 viewportSize,
        BoxSelectMode mode)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(node);

        Vector3 at = node.WorldPosition;

        // Behind the eye plane: the divide has no meaning, and the same ruling
        // the boxes get applies - Intersect accepts, Contain does not.
        float depth = Vector3.Dot(at - camera.Position, camera.Forward);
        if (depth <= MinClipW)
            return false;

        Vector2 screen = Project(camera, at, viewportSize);
        float radius = LightOverlay.IconPixels;

        var icon = new ScreenRect(
            new Vector2(screen.X - radius, screen.Y - radius),
            new Vector2(screen.X + radius, screen.Y + radius));

        return mode == BoxSelectMode.Contain
            ? rect.Contains(in icon)
            : rect.Intersects(in icon);
    }

    private static Vector2 Project(Camera camera, Vector3 world, Vector2 viewportSize)
    {
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), camera.GetViewProjection());
        float inverseW = 1f / MathF.Max(clip.W, MinClipW);

        return new Vector2(
            (clip.X * inverseW * 0.5f + 0.5f) * viewportSize.X,
            (0.5f - clip.Y * inverseW * 0.5f) * viewportSize.Y);
    }

    /// <summary>
    /// The refinement predicate on its own: whether the rectangle covers this
    /// one node under <paramref name="mode"/>, assuming the broad phase already
    /// admitted it. Exposed so a host (or a test) can ask about a single node
    /// without running a query.
    /// </summary>
    public static bool Covers(
        Scene scene,
        Camera camera,
        SceneNode node,
        in ScreenRect rect,
        Vector2 viewportSize,
        BoxSelectMode mode)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(node);

        if (!scene.TryGetWorldBounds(node, out Aabb bounds))
            return false; // Not a spatial node: nothing to cover.

        if (!TryProjectBounds(camera, in bounds, viewportSize, out ScreenRect projected))
            return mode == BoxSelectMode.Intersect; // Straddles the eye plane — see the type remarks.

        return mode == BoxSelectMode.Contain
            ? rect.Contains(in projected)
            : rect.Intersects(in projected);
    }

    /// <summary>
    /// Projects a world AABB to its screen-space bounding rectangle. False when
    /// any corner is at or behind the eye plane, where the perspective divide
    /// has no meaning.
    /// </summary>
    /// <remarks>
    /// The rectangle bounds the <em>projected corners</em>, which is a superset
    /// of the box's true silhouette (projection is not affine, so an edge can
    /// bow outward — but only within the convex hull of the projected corners,
    /// which this rectangle contains). Erring outward is the right direction
    /// for <see cref="BoxSelectMode.Intersect"/> and the conservative direction
    /// for <see cref="BoxSelectMode.Contain"/>: neither mode ever claims
    /// coverage it does not have.
    /// </remarks>
    public static bool TryProjectBounds(
        Camera camera, in Aabb bounds, Vector2 viewportSize, out ScreenRect rect)
    {
        ArgumentNullException.ThrowIfNull(camera);

        rect = default;
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return false;

        Matrix4x4 viewProjection = camera.GetViewProjection();
        Vector3 min = bounds.Min;
        Vector3 max = bounds.Max;

        var screenMin = new Vector2(float.MaxValue);
        var screenMax = new Vector2(float.MinValue);

        // The eight corners, enumerated by the three bits of the index — no
        // array, so this stays allocation-free on the per-frame path.
        for (int corner = 0; corner < 8; corner++)
        {
            var world = new Vector3(
                (corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);

            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
            if (clip.W < MinClipW)
                return false;

            // Clip → NDC → pixels, the exact inverse of the mapping
            // Camera.ScreenPointToRay applies (including the y flip).
            float inverseW = 1f / clip.W;
            var pixel = new Vector2(
                (clip.X * inverseW + 1f) * 0.5f * viewportSize.X,
                (1f - clip.Y * inverseW) * 0.5f * viewportSize.Y);

            screenMin = Vector2.Min(screenMin, pixel);
            screenMax = Vector2.Max(screenMax, pixel);
        }

        rect = new ScreenRect(screenMin, screenMax);
        return true;
    }
}
