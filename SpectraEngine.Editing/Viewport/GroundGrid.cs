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
/// <b>The fade lives in the SHADER, not here, and it is real alpha.</b> The
/// first version split every line into five flat-coloured segments and lerped
/// each toward black by its midpoint's distance — and that design cannot fade:
/// a dark line lerped toward black over a lit floor keeps its contrast, so
/// every segment stayed at full visual strength until a hard cull snapped its
/// whole 0.4-of-the-patch body off in one frame. The grid visibly loaded and
/// unloaded in chunks, which was the complaint verbatim. This class now emits
/// whole lines and writes the fade as <see cref="DebugDraw"/> metadata
/// (centre, start, end, opacity); the world-line shaders compute the
/// <c>(1-t)²</c> falloff per pixel and blend it as transparency toward
/// whatever is actually behind the line.
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

    /// <summary>
    /// How large a cell must project before the grid REFINES back to the finer
    /// level, in screen pixels of that finer level.
    /// </summary>
    /// <remarks>
    /// Deliberately above <see cref="MinimumCellPixels"/>: with one threshold a
    /// camera hovering at exactly the coarsening height flickers the whole
    /// lattice between two spacings frame to frame, and every crossing is a
    /// one-frame swap of half the lines. The gap is the hysteresis band —
    /// coarsen below 12 px, refine only once the finer level would come back
    /// at 16 px or more.
    /// </remarks>
    public const float RefineCellPixels = 16f;

    /// <summary>How many minor cells make one major cell.</summary>
    public const int MajorEvery = 5;

    /// <summary>
    /// The most lines the grid may emit in one frame, across both axes.
    /// </summary>
    /// <remarks>
    /// Disclosed rather than silent, like every other cap in this assembly: a
    /// grid that quietly stopped half way across the screen would read as a
    /// rendering fault. One <see cref="DebugDraw.Line"/> per grid line (the
    /// fade is per pixel now, not per segment), so the cap counts what is
    /// actually emitted; the two axes ride outside it.
    /// </remarks>
    public int MaxLines { get; set; } = 512;

    /// <summary>Whether the grid draws at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whole-grid alpha, 0..1 — the host's fade envelope writes it per frame
    /// so the grid arrives and leaves as a ramp rather than a cut.
    /// </summary>
    /// <remarks>
    /// Real transparency, carried to the shader through the line buffer's
    /// metadata: it multiplies the per-pixel falloff, never the reach, because
    /// a reach that changed with the fade would move the visible edge every
    /// frame of the ramp — which is the chunk-pop this lane was rebuilt to
    /// remove.
    /// </remarks>
    public float Opacity { get; set; } = 1f;

    // Below this the whole draw is skipped: the shader would discard every
    // pixel anyway, and skipping early keeps a faded-out grid at exactly zero
    // cost.
    private const float MinimumOpacity = 0.005f;

    /// <summary>
    /// How far the grid extends from the camera's ground point, in world units.
    /// </summary>
    /// <remarks>
    /// Scaled by the camera's height as well, below, because a grid sized for a
    /// walk-around view is a postage stamp from a hundred metres up.
    /// </remarks>
    public float Radius { get; set; } = 48f;

    // Where the per-pixel fade begins and ends, as fractions of the CONTINUOUS
    // radius — never the step-quantised reach, whose one-cell jumps used to
    // nudge every line's brightness on the frames the quantisation moved.
    private const float FadeStartFraction = 0.05f;
    private const float FadeEndFraction = 0.62f;

    // LINEAR light, not display colours. These lines are blended into the lit
    // scene before the tone curve, which is correct for world content: the
    // grid should dim when the exposure rises. A value copied from the
    // overlay's display palette would arrive noticeably brighter than
    // intended.
    // DARK rather than light, which is the right way round here and not
    // obvious: the sky is a bright linear blue and the demo's ground is a light
    // green, so a pale grid disappears into both while a dark one reads against
    // either. It is also what every editor in this category does over an
    // unlit horizon.
    private static readonly Vector3 MinorColor = new(0.030f, 0.029f, 0.028f);
    private static readonly Vector3 MajorColor = new(0.011f, 0.011f, 0.012f);

    /// <summary>Lines the last <see cref="Draw"/> emitted, axes included.</summary>
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

    // Last frame's answer, so the coarsen and refine thresholds can disagree.
    private float _lastCell;
    private float _lastIncrement;

    /// <summary>
    /// Emits the grid into <paramref name="output"/>, sized to
    /// <paramref name="camera"/> and spaced by <paramref name="increment"/>.
    /// </summary>
    /// <param name="output">The depth-tested world-line buffer, never the overlay.</param>
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

        if (!Enabled || Opacity <= MinimumOpacity || increment <= 0f || viewportHeight <= 0f)
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
        // bright lines and reads their drift as the world sliding. The jump a
        // re-centre makes is invisible because the lines it adds and removes
        // sit past the fade's end, at zero alpha.
        float major = cell * MajorEvery;
        float centerX = MathF.Floor(eye.X / major) * major;
        float centerZ = MathF.Floor(eye.Z / major) * major;

        int steps = (int)MathF.Ceiling(radius / cell);

        // Two axes of lines, one Line call each: 2 * (2 * steps + 1).
        int wanted = 2 * ((2 * steps) + 1);
        if (wanted > MaxLines)
        {
            // Shrink the extent rather than stopping half way across the
            // screen, which would read as a rendering fault. What is lost is
            // distance, which is the least informative part of a grid.
            steps = Math.Max(1, (MaxLines / 4) - 1);
            SkippedLastDraw = wanted - (2 * ((2 * steps) + 1));
        }

        float reach = steps * cell;

        // The per-pixel fade window, from the CONTINUOUS radius so it never
        // steps — clamped to the drawn reach only when the cap shrank the
        // patch, or lines would end mid-fade in a visible square edge.
        float fadeRadius = MathF.Min(radius, reach);
        output.FadeCenter = new Vector3(eye.X, 0f, eye.Z);
        output.FadeStart = fadeRadius * FadeStartFraction;
        output.FadeEnd = fadeRadius * FadeEndFraction;
        output.Opacity = Opacity;

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

            output.Line(new Vector3(x, 0f, centerZ - reach), new Vector3(x, 0f, centerZ + reach),
                majorX ? MajorColor : MinorColor);
            output.Line(new Vector3(centerX - reach, 0f, z), new Vector3(centerX + reach, 0f, z),
                majorZ ? MajorColor : MinorColor);

            DrawnLastDraw += 2;
        }

        DrawAxes(output, centerX, centerZ, reach);
        DrawnLastDraw += 2;
    }

    /// <summary>
    /// Doubles the cell until it projects to at least
    /// <see cref="MinimumCellPixels"/>, and halves it back only past
    /// <see cref="RefineCellPixels"/>.
    /// </summary>
    /// <remarks>
    /// Measured at the camera's HEIGHT rather than at the grid's far edge,
    /// because the near cells are the ones a user is working in and the far ones
    /// have already faded out. Using the far edge would coarsen a grid that
    /// looks perfectly fine underfoot. Stateful, because hysteresis needs last
    /// frame's answer: starting from it is what lets the two thresholds
    /// disagree instead of flickering at one.
    /// </remarks>
    private float CoarsenedCell(float increment, float height, Camera camera, float viewportHeight)
    {
        // Pixels per world unit at distance `height`: the same relation
        // GizmoGeometry uses to hold a handle at a constant screen size, and
        // reached through the same helper so the two cannot disagree about
        // whether the field of view is in degrees. (It is in radians.)
        float worldPerPixel = Gizmos.GizmoMath.WorldPerPixel(camera, viewportHeight, height);
        if (worldPerPixel <= 0f || !float.IsFinite(worldPerPixel))
            return increment;

        // Resume from last frame's level while it is still one of THIS
        // increment's levels; a changed increment resets the ladder.
        float cell = _lastIncrement == increment && _lastCell >= increment
            ? _lastCell
            : increment;

        // Bounded, so a degenerate camera cannot spin here. Thirty-two
        // doublings is a factor of four billion, well past any grid anyone
        // would look at.
        for (int i = 0; i < 32 && cell / worldPerPixel < MinimumCellPixels; i++)
            cell *= 2f;

        // Refine only while the FINER level would come back comfortably
        // readable. Exact halving of exact doublings, so the loop lands back
        // on the increment itself bit for bit.
        for (int i = 0; i < 32 && cell > increment && (cell * 0.5f) / worldPerPixel >= RefineCellPixels; i++)
            cell *= 0.5f;

        _lastIncrement = increment;
        _lastCell = cell;
        return cell;
    }

    // The X and Z axes, in the same hues the gizmo handles and the inspector's
    // axis letters wear, so the letter beside a field, the arrow under the
    // cursor and the line across the floor are recognisably the same axis. This
    // is what turns "a grid" into "the world has an origin".
    //
    // ALWAYS EMITTED, never gated. The old binary distance gate popped a whole
    // axis line into and out of existence the frame the snapped patch centre
    // crossed a threshold; with the per-pixel fade an axis far from the camera
    // simply renders at zero alpha, which is the same answer with no edge, for
    // the cost of two lines.
    private static void DrawAxes(DebugDraw output, float centerX, float centerZ, float reach)
    {
        var xColor = new Vector3(0.30f, 0.020f, 0.020f);
        var zColor = new Vector3(0.020f, 0.035f, 0.28f);

        output.Line(new Vector3(centerX - reach, 0f, 0f), new Vector3(centerX + reach, 0f, 0f), xColor);
        output.Line(new Vector3(0f, 0f, centerZ - reach), new Vector3(0f, 0f, centerZ + reach), zColor);
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
