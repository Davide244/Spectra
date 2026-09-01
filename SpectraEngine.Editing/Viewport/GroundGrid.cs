using System;
using System.Numerics;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Editing.Viewport;

/// <summary>
/// The editor's ground: a grid on the y = 0 plane at the live snap increment,
/// with the world axes running through the origin.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the single largest thing that made the viewport read as a picture
/// rather than as a place.</b> There was no grid, no horizon, no origin and no
/// compass anywhere: an object floating in flat sky has no scale, no ground and
/// nothing to be positioned relative to, and every editor in this category draws
/// one for exactly that reason.
/// </para>
/// <para>
/// <b>The spacing IS the snap increment, not a decoration.</b> A grid drawn at
/// some fixed convenient size is a lie the moment a user changes their snap: the
/// squares stop being the squares an object will land on. Reading it from the
/// live translate snap means the grid answers "where will this go" before the
/// drag rather than after it.
/// </para>
/// <para>
/// <b>It lives in <c>SpectraEngine.Editing</c></b>, like the part and
/// subtractive overlays, so a shipped game never links it.
/// </para>
/// </remarks>
public sealed class GroundGrid
{
    /// <summary>
    /// The smallest a minor cell may project to before the grid coarsens, in
    /// screen pixels at the grid's own distance.
    /// </summary>
    /// <remarks>
    /// <b>Without this rule a fine grid is the ugliest thing in any editor.</b>
    /// At a 0.25 increment and forty metres out, the minor lines land under a
    /// pixel apart and alias into a grey wash that moves as the camera moves.
    /// The spacing therefore doubles until a cell is at least this wide, so the
    /// grid always reads as a grid; what it costs is that the visible squares
    /// are sometimes a multiple of the snap rather than the snap itself, which
    /// is why the MAJOR lines stay tied to the unscaled increment.
    /// </remarks>
    public const float MinimumCellPixels = 12f;

    /// <summary>How many minor cells make one major cell.</summary>
    public const int MajorEvery = 5;

    /// <summary>
    /// The most lines the grid may emit in one frame, across both axes.
    /// </summary>
    /// <remarks>
    /// Disclosed rather than silent, like every other cap in this assembly: a
    /// grid that quietly stopped half way across the screen would read as a
    /// rendering fault. With the minimum-cell rule above, the cap is only
    /// reached at extreme aspect ratios.
    /// </remarks>
    public int MaxLines { get; set; } = 512;

    /// <summary>Whether the grid draws at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How far the grid extends from the camera's ground point, in world units.
    /// </summary>
    /// <remarks>
    /// Scaled by the camera's height as well, below, because a grid sized for a
    /// walk-around view is a postage stamp from a hundred metres up.
    /// </remarks>
    public float Radius { get; set; } = 48f;

    // LINEAR light, not display colours. These lines are flushed inside the
    // scene pass and go through the tone curve with everything else, which is
    // correct for world content: the grid should dim when the exposure rises. A
    // value copied from the overlay's display palette would arrive noticeably
    // brighter than intended.
    // DARK rather than light, which is the right way round here and not
    // obvious: the sky is a bright linear blue and the demo's ground is a light
    // green, so a pale grid disappears into both while a dark one reads against
    // either. It is also what every editor in this category does over an
    // unlit horizon.
    private static readonly Vector3 MinorColor = new(0.030f, 0.029f, 0.028f);
    private static readonly Vector3 MajorColor = new(0.011f, 0.011f, 0.012f);

    /// <summary>Lines the last <see cref="Draw"/> emitted.</summary>
    public int DrawnLastDraw { get; private set; }

    /// <summary>
    /// Lines the last <see cref="Draw"/> could not emit because
    /// <see cref="MaxLines"/> was reached.
    /// </summary>
    public int SkippedLastDraw { get; private set; }

    /// <summary>The cell size the last <see cref="Draw"/> actually used.</summary>
    /// <remarks>
    /// Reported because it is not always the snap increment: see
    /// <see cref="MinimumCellPixels"/>. A status bar showing the snap beside a
    /// grid drawn at four times it would otherwise be quietly wrong.
    /// </remarks>
    public float CellSizeLastDraw { get; private set; }

    /// <summary>
    /// Emits the grid into <paramref name="output"/>, sized to
    /// <paramref name="camera"/> and spaced by <paramref name="increment"/>.
    /// </summary>
    /// <param name="output">The DEPTH-TESTED line buffer, never the overlay.</param>
    /// <param name="camera">The viewport camera.</param>
    /// <param name="increment">The live translate snap, in world units.</param>
    /// <param name="viewportHeight">Viewport height in pixels, for the coarsening rule.</param>
    public void Draw(DebugDraw output, Camera camera, float increment, float viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(camera);

        DrawnLastDraw = 0;
        SkippedLastDraw = 0;
        CellSizeLastDraw = 0f;

        if (!Enabled || increment <= 0f || viewportHeight <= 0f)
            return;

        // The camera's own ground point. Everything below is measured from here
        // rather than from the origin, so the grid follows the user across an
        // unbounded world instead of being a patch they can walk off.
        Vector3 eye = camera.Position;
        float height = MathF.Max(MathF.Abs(eye.Y), 1f);

        // A grid sized for a walk-around view is a postage stamp from a hundred
        // metres up, so the extent grows with the camera's height. Capped, or a
        // camera parked at altitude asks for a grid the cap would truncate
        // anyway.
        float radius = MathF.Min(Radius * MathF.Max(1f, height / 10f), Radius * 2f);

        float cell = CoarsenedCell(increment, height, camera, viewportHeight);
        CellSizeLastDraw = cell;

        // Snapped to the MAJOR spacing, not the minor one. Snapping to the minor
        // spacing still lets the major lines slide underfoot as the camera
        // moves, which is the whole thing the snap is for: the eye tracks the
        // bright lines and reads their drift as the world sliding.
        float major = cell * MajorEvery;
        float centerX = MathF.Floor(eye.X / major) * major;
        float centerZ = MathF.Floor(eye.Z / major) * major;

        int steps = (int)MathF.Ceiling(radius / cell);

        // Two axes, and each line is one Line call: 2 * (2 * steps + 1).
        int wanted = 2 * (2 * steps + 1);
        if (wanted > MaxLines)
        {
            // Shrink the extent rather than stopping half way across the
            // screen, which would read as a rendering fault. What is lost is
            // distance, which is the least informative part of a grid.
            steps = Math.Max(1, (MaxLines / 4) - 1);
            SkippedLastDraw = wanted - (2 * (2 * steps + 1));
            radius = steps * cell;
        }

        float reach = steps * cell;

        for (int i = -steps; i <= steps; i++)
        {
            float x = centerX + (i * cell);
            float z = centerZ + (i * cell);

            // Whether a line is major is decided by its ABSOLUTE world position,
            // never by its index from the camera. Indexing from the camera makes
            // the bright lines change which world coordinates they sit on every
            // time the patch re-centres, so the grid appears to breathe.
            bool majorX = IsMultiple(x, major);
            bool majorZ = IsMultiple(z, major);

            DrawFading(output, new Vector3(x, 0f, centerZ - reach), new Vector3(x, 0f, centerZ + reach),
                majorX ? MajorColor : MinorColor, eye, reach);
            DrawFading(output, new Vector3(centerX - reach, 0f, z), new Vector3(centerX + reach, 0f, z),
                majorZ ? MajorColor : MinorColor, eye, reach);

            DrawnLastDraw += 2;
        }

        DrawAxes(output, centerX, centerZ, reach, eye);
    }

    /// <summary>
    /// Doubles the cell until it projects to at least
    /// <see cref="MinimumCellPixels"/>.
    /// </summary>
    /// <remarks>
    /// Measured at the camera's HEIGHT rather than at the grid's far edge,
    /// because the near cells are the ones a user is working in and the far ones
    /// have already faded out. Using the far edge would coarsen a grid that
    /// looks perfectly fine underfoot.
    /// </remarks>
    private static float CoarsenedCell(float increment, float height, Camera camera, float viewportHeight)
    {
        // Pixels per world unit at distance `height`: the same relation
        // GizmoGeometry uses to hold a handle at a constant screen size, and
        // reached through the same helper so the two cannot disagree about
        // whether the field of view is in degrees. (It is in radians.)
        float worldPerPixel = Gizmos.GizmoMath.WorldPerPixel(camera, viewportHeight, height);
        if (worldPerPixel <= 0f || !float.IsFinite(worldPerPixel))
            return increment;

        float cell = increment;

        // Bounded, so a degenerate camera cannot spin here. Sixteen doublings is
        // a factor of 65,536, well past any grid anyone would look at.
        for (int i = 0; i < 16 && cell / worldPerPixel < MinimumCellPixels; i++)
            cell *= 2f;

        return cell;
    }

    // The X and Z axes, in the same hues the gizmo handles and the inspector's
    // axis letters wear, so the letter beside a field, the arrow under the
    // cursor and the line across the floor are recognisably the same axis. This
    // is what turns "a grid" into "the world has an origin".
    //
    // DRAWN SHORTER THAN THE GRID, and that is the one thing about them that is
    // not obvious. The grid plane is at y = 0 and a level's geometry straddles
    // it - a wall standing on a floor whose top is at zero has half its height
    // below the plane - so a grid line legitimately passes IN FRONT of the wall
    // it appears to cross. That is correct, it is what every editor in this
    // category does, and it is not a depth failure (WorldLineGlTests pins the
    // depth lane in both directions). What it IS is loud: an axis at full
    // strength running a hundred units across a room reads as a rendering
    // fault, so the axes reach a third of the grid and the grid itself fades
    // hard well before its own edge.
    private static void DrawAxes(DebugDraw output, float centerX, float centerZ, float reach, Vector3 eye)
    {
        var xColor = new Vector3(0.30f, 0.020f, 0.020f);
        var zColor = new Vector3(0.020f, 0.035f, 0.28f);

        // Only where the axis actually crosses the drawn patch. An axis line
        // pinned to the patch's own edge would be a bright line that is not the
        // axis, which is worse than no axis at all.
        float axisReach = reach * 0.34f;

        if (MathF.Abs(centerZ) <= axisReach)
        {
            DrawFading(output, new Vector3(centerX - axisReach, 0f, 0f), new Vector3(centerX + axisReach, 0f, 0f),
                xColor, eye, reach);
        }

        if (MathF.Abs(centerX) <= axisReach)
        {
            DrawFading(output, new Vector3(0f, 0f, centerZ - axisReach), new Vector3(0f, 0f, centerZ + axisReach),
                zColor, eye, reach);
        }
    }

    /// <summary>
    /// Emits one line, split into segments that dim with distance from the
    /// camera's ground point.
    /// </summary>
    /// <remarks>
    /// <b>DebugDraw has colour but no alpha</b>, so the fade is a colour lerp
    /// toward black rather than toward transparency - which is the same thing
    /// against a dark ground and nothing like it against a bright sky, so the
    /// grid deliberately stops at the fade's end rather than continuing at a
    /// colour that would be visible against the horizon. Segments rather than
    /// per-vertex, because a line's two ends are its only colour samples and one
    /// long line would fade linearly across the whole patch instead of following
    /// the actual distance. Four is enough to read as a fade and costs four
    /// lines where one would do; the cap accounts for it.
    /// </remarks>
    private static void DrawFading(DebugDraw output, Vector3 a, Vector3 b, Vector3 color, Vector3 eye, float reach)
    {
        const int Segments = 5;
        var ground = new Vector3(eye.X, 0f, eye.Z);
        float fadeStart = reach * 0.05f;
        float fadeEnd = reach * 0.62f;

        Vector3 previous = a;
        for (int i = 1; i <= Segments; i++)
        {
            Vector3 next = Vector3.Lerp(a, b, i / (float)Segments);
            Vector3 mid = (previous + next) * 0.5f;

            float distance = Vector3.Distance(mid, ground);
            float t = Math.Clamp((distance - fadeStart) / MathF.Max(fadeEnd - fadeStart, 1e-4f), 0f, 1f);

            // (1 - t) SQUARED, not 1 - t squared, and the difference is the
            // whole quality of the far half. The gentler curve keeps a third of
            // its strength at 85% of the reach, which is exactly where the lines
            // have converged to a few pixels apart and turn into a moire that
            // crawls as the camera moves. This one is down to two per cent there.
            float falloff = 1f - t;
            float k = falloff * falloff;
            if (k > 0.02f)
                output.Line(previous, next, color * k);

            previous = next;
        }
    }

    // Whole-multiple test with a tolerance proportional to the spacing, because
    // the coordinates are accumulated floats and an exact equality here would
    // make the major lines flicker on and off as the camera moves.
    private static bool IsMultiple(float value, float spacing)
    {
        float ratio = value / spacing;
        return MathF.Abs(ratio - MathF.Round(ratio)) < 1e-3f;
    }
}
