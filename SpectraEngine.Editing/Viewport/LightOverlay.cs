using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Gizmos;

namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// Draws every light, and the shape of whatever is selected.
/// </summary>
/// <remarks>
/// <para>
/// <b>A light was completely invisible and completely unpickable.</b> It has no
/// mesh and no brush, and <see cref="SceneNode"/> deliberately keeps lights out
/// of the spatial index - so a lamp could be found only by name in the tree, a
/// marquee dragged across one CLEARED the selection, and moving one meant
/// selecting it somewhere else and watching the numbers. That is the whole of
/// the feature this restores: an icon you can see is a thing you can click.
/// </para>
/// <para>
/// <b>Lights stay out of the BVH.</b> Admitting them would make every lamp
/// collidable and query-visible, because <c>PhysicsFlags.Default</c> carries
/// <c>CanCollide | CanQuery</c> - a lighting change that silently alters what
/// the player can walk into. The editor picks them separately instead, which is
/// what <see cref="LightPicking"/> is.
/// </para>
/// <para>
/// <b>Always on, and not behind a <c>DebugVisualization</c> flag.</b> Those are
/// off by default, and a light that is invisible at rest is unfindable - which
/// is the state this replaces, not a state worth being able to return to.
/// </para>
/// <para>
/// It lives in <c>SpectraEngine.Editing</c>, so a shipped game never links it.
/// </para>
/// </remarks>
public sealed class LightOverlay
{
    /// <summary>
    /// The icon's radius in screen pixels - and the pick radius too.
    /// </summary>
    /// <remarks>
    /// <b>ONE constant, shared with <see cref="LightPicking"/>.</b> Two would
    /// drift, and the symptom of drift is that you click something other than
    /// what you can see, which is the least reportable class of bug there is.
    /// </remarks>
    public const float IconPixels = 9f;

    /// <summary>Whether the overlay draws at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many light icons may be drawn in one frame.
    /// </summary>
    /// <remarks>
    /// Disclosed through <see cref="SkippedLastDraw"/> rather than silently
    /// truncated, like every other cap in this assembly.
    /// </remarks>
    public int MaxIcons { get; set; } = 256;

    /// <summary>Lights the last draw drew.</summary>
    public int DrawnLastDraw { get; private set; }

    /// <summary>Lights the last draw could not.</summary>
    public int SkippedLastDraw { get; private set; }

    private const int RingSegments = 32;
    private const float DisabledDim = 0.28f;

    /// <summary>
    /// Draws an icon per light, plus the selected lights' shapes.
    /// </summary>
    /// <param name="output">The depth-OFF overlay buffer: a lamp inside a wall is still a lamp.</param>
    public void Draw(DebugDraw output, Scene scene, Camera camera, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);

        DrawnLastDraw = 0;
        SkippedLastDraw = 0;

        if (!Enabled || viewportSize.Y <= 0f)
            return;

        IReadOnlyList<SceneNode> nodes = scene.LightNodes;

        for (int i = 0; i < nodes.Count; i++)
        {
            SceneNode node = nodes[i];
            if (node.Light is not { } light)
                continue;

            if (DrawnLastDraw >= MaxIcons)
            {
                SkippedLastDraw++;
                continue;
            }

            Vector3 at = node.WorldPosition;
            float radius = LightPicking.WorldRadius(camera, viewportSize, at);
            if (radius <= 0f)
                continue;

            // The lamp's OWN colour, dimmed when it is switched off. A light you
            // have disabled is still a light you need to find, and a uniform
            // icon colour would make a room full of tinted lamps say nothing
            // about which is which.
            Vector3 colour = light.Enabled
                ? Vector3.Max(light.Color, new Vector3(0.25f))
                : new Vector3(DisabledDim);

            DrawIcon(output, at, radius, colour, camera);
            DrawnLastDraw++;
        }

        // The shapes are for the SELECTION only. A range sphere per lamp would
        // fill a lit room with overlapping circles and hide the geometry the
        // lights exist to show.
        IReadOnlyList<SceneNode> selection = scene.Selection.Items;
        for (int i = 0; i < selection.Count; i++)
        {
            SceneNode node = selection[i];
            if (node.Light is { } light)
                DrawShape(output, node, light, camera);
        }
    }

    // A small star: an octagon with spokes. Billboarded onto the camera's basis,
    // so it reads the same from every angle - a light has no orientation worth
    // showing here (its DIRECTION does, and that is the arrow below).
    private static void DrawIcon(DebugDraw output, Vector3 at, float radius, Vector3 colour, Camera camera)
    {
        Vector3 right = camera.Right * radius;
        Vector3 up = camera.Up * radius;

        const int Points = 8;
        Vector3 previous = at + right;

        for (int i = 1; i <= Points; i++)
        {
            float angle = i * (MathF.Tau / Points);
            Vector3 current = at + (right * MathF.Cos(angle)) + (up * MathF.Sin(angle));
            output.Line(previous, current, colour);
            previous = current;
        }

        // Four spokes, at the diagonals so they do not lie along the octagon's
        // own edges.
        for (int i = 0; i < 4; i++)
        {
            float angle = (i * (MathF.Tau / 4)) + (MathF.PI / 4f);
            Vector3 direction = (right * MathF.Cos(angle)) + (up * MathF.Sin(angle));
            output.Line(at + (direction * 0.9f), at + (direction * 1.7f), colour);
        }
    }

    private static void DrawShape(DebugDraw output, SceneNode node, Light light, Camera camera)
    {
        Vector3 at = node.WorldPosition;
        Vector3 colour = Vector3.Max(light.Color, new Vector3(0.35f));

        switch (light.Kind)
        {
            case LightKind.Directional:
                // Direction, not range: a sun has no position that matters and
                // no reach to draw. The arrow is the node's forward axis, which
                // IS the direction the light travels - the one fact about a
                // directional light that is easy to get backwards and silent
                // when you do.
                // The third ROW of the world matrix, which is what
                // Scene.CollectLights reads. Not a rotation applied to -Z:
                // the engine's own derivation is +Z out of the matrix, and two
                // expressions for one direction is how the arrow ends up
                // pointing the opposite way from the light that is actually
                // being cast, with nothing anywhere reporting a disagreement.
                Matrix4x4 world = node.WorldMatrix;
                var travel = Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));
                output.Arrow(at, at + (travel * 2f), colour);
                break;

            case LightKind.Point:
                // Three great circles rather than a wire sphere: the reach is a
                // scalar, and three rings say it with 96 lines where a lat-long
                // sphere costs several hundred for no more information.
                DrawRing(output, at, Vector3.UnitX, Vector3.UnitY, light.Range, colour);
                DrawRing(output, at, Vector3.UnitY, Vector3.UnitZ, light.Range, colour);
                DrawRing(output, at, Vector3.UnitZ, Vector3.UnitX, light.Range, colour);
                break;
        }
    }

    /// <summary>
    /// Emits one circle as a rolling sequence of lines.
    /// </summary>
    /// <remarks>
    /// <b>Line by line rather than through <c>DebugDraw.Polyline</c></b>, which
    /// takes an <c>IReadOnlyList</c>: building one per ring per frame is an
    /// allocation on the render thread, and these paths are held to zero by
    /// <c>EditingAllocationTests</c>.
    /// </remarks>
    private static void DrawRing(DebugDraw output, Vector3 centre, Vector3 u, Vector3 v, float radius, Vector3 colour)
    {
        if (radius <= 0f)
            return;

        Vector3 previous = centre + (u * radius);

        for (int i = 1; i <= RingSegments; i++)
        {
            float angle = i * (MathF.Tau / RingSegments);
            Vector3 current = centre + (u * radius * MathF.Cos(angle)) + (v * radius * MathF.Sin(angle));
            output.Line(previous, current, colour);
            previous = current;
        }
    }
}

/// <summary>
/// Ray-picking for light nodes, which the scene's spatial index deliberately
/// does not carry.
/// </summary>
/// <remarks>
/// Pure and allocation-free, so it can be tested without a scene graph, a
/// camera rig or a window.
/// </remarks>
public static class LightPicking
{
    /// <summary>
    /// The icon's world radius at <paramref name="at"/>, so a light is the same
    /// size to click whatever the distance.
    /// </summary>
    public static float WorldRadius(Camera camera, Vector2 viewportSize, Vector3 at)
    {
        float depth = GizmoMath.ViewDepth(camera, at);
        if (depth <= 0f)
            return 0f;

        return LightOverlay.IconPixels * GizmoMath.WorldPerPixel(camera, viewportSize.Y, depth);
    }

    /// <summary>
    /// The nearest light icon <paramref name="ray"/> passes through, if any.
    /// </summary>
    /// <remarks>
    /// A ray-versus-sphere test at the icon's own screen-constant radius, which
    /// is what makes the pick target exactly the thing that was drawn.
    /// </remarks>
    public static bool TryPick(
        Scene scene, Camera camera, in Ray3 ray, Vector2 viewportSize,
        out SceneNode? node, out float distance)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(camera);

        node = null;
        distance = float.PositiveInfinity;

        if (viewportSize.Y <= 0f)
            return false;

        IReadOnlyList<SceneNode> nodes = scene.LightNodes;

        for (int i = 0; i < nodes.Count; i++)
        {
            SceneNode candidate = nodes[i];
            Vector3 at = candidate.WorldPosition;

            float radius = WorldRadius(camera, viewportSize, at);
            if (radius <= 0f)
                continue;

            if (!TryRaySphere(in ray, at, radius, out float hit) || hit >= distance)
                continue;

            node = candidate;
            distance = hit;
        }

        return node is not null;
    }

    // The standard quadratic, with the ray direction assumed normalised (Ray3's
    // constructor guarantees it). Returns the NEAR root, clamped to zero, so a
    // ray whose origin is already inside the icon still reports a hit at the
    // origin rather than at the far side.
    private static bool TryRaySphere(in Ray3 ray, Vector3 centre, float radius, out float distance)
    {
        distance = 0f;

        Vector3 toCentre = ray.Origin - centre;
        float b = Vector3.Dot(toCentre, ray.Direction);
        float c = Vector3.Dot(toCentre, toCentre) - (radius * radius);

        // Pointing away and already outside: no root worth finding.
        if (c > 0f && b > 0f)
            return false;

        float discriminant = (b * b) - c;
        if (discriminant < 0f)
            return false;

        float near = -b - MathF.Sqrt(discriminant);
        distance = MathF.Max(near, 0f);
        return true;
    }
}
