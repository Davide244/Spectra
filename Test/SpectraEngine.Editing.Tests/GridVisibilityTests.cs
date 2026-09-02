using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Hosting;
using SpectraEngine.Editing.Viewport;
using System.Numerics;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// The ground grid's visibility policy and its rebuilt emission: whole lines
/// with shader-side fade metadata, a mode a UI can set, and a fade envelope
/// instead of a cut.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the emission tests pin is the ABSENCE of the segment machinery.</b>
/// The first grid split every line into five flat-coloured segments and culled
/// each whole segment at a brightness threshold, which is exactly the
/// "grid visibly loads and unloads in chunks" complaint: a fifth of a line is
/// a 19-to-38-unit body appearing or vanishing in one frame. The fade lives in
/// the world-line shaders now, per pixel, as real alpha; this class emits one
/// line per grid line and four metadata values, and these tests fail if the
/// segmentation ever comes back.
/// </para>
/// </remarks>
public sealed class GridVisibilityTests
{
    private static SceneEditorHost NewHost(Scene scene)
    {
        var renderer = new CompilingRenderer();
        renderer.SetFramebufferSize(new Vector2D<int>(1280, 720));

        return new SceneEditorHost(
            NullLoggerFactory.Instance,
            scene,
            renderer,
            new InputManager(NullLogger<InputManager>.Instance));
    }

    // --- Emission ------------------------------------------------------------

    [Fact]
    public void The_grid_emits_whole_lines_and_writes_its_fade_as_metadata()
    {
        var grid = new GroundGrid();
        var output = new DebugDraw();
        var camera = new Camera { Position = new Vector3(0f, 5f, 0f) };

        grid.Draw(output, camera, increment: 1f, viewportHeight: 720f);

        // One Line call (two vertices) per reported line — no segmentation.
        output.VertexCount.ShouldBe(grid.DrawnLastDraw * 2,
            "each grid line must be ONE Line call; a vertex count above two per line means " +
            "the CPU-side segment fade is back, and with it the chunk pop it caused");
        grid.DrawnLastDraw.ShouldBeGreaterThan(4);
        grid.SkippedLastDraw.ShouldBe(0);

        // The fade window rides the buffer for the shader, from the CONTINUOUS
        // radius (48 at this height), never the step-quantised reach.
        output.FadeCenter.ShouldBe(new Vector3(0f, 0f, 0f));
        output.FadeStart.ShouldBe(48f * 0.05f, tolerance: 0.001f);
        output.FadeEnd.ShouldBe(48f * 0.62f, tolerance: 0.001f);
        output.Opacity.ShouldBe(1f);
    }

    [Fact]
    public void A_faded_out_grid_emits_nothing_at_all()
    {
        var grid = new GroundGrid { Opacity = 0f };
        var output = new DebugDraw();
        var camera = new Camera { Position = new Vector3(0f, 5f, 0f) };

        grid.Draw(output, camera, increment: 1f, viewportHeight: 720f);

        output.VertexCount.ShouldBe(0,
            "a grid at zero opacity must cost zero: every line it emitted would be " +
            "discarded per pixel anyway");
        grid.DrawnLastDraw.ShouldBe(0);
    }

    [Fact]
    public void The_grid_opacity_multiplier_reaches_the_shader_through_the_buffer()
    {
        var grid = new GroundGrid { Opacity = 0.4f };
        var output = new DebugDraw();
        var camera = new Camera { Position = new Vector3(0f, 5f, 0f) };

        grid.Draw(output, camera, increment: 1f, viewportHeight: 720f);

        output.Opacity.ShouldBe(0.4f,
            "the envelope's alpha is metadata for the shader, never a colour multiply: " +
            "dimming a dark line's COLOUR over a lit floor does not fade it");
    }

    // --- Coarsening hysteresis ----------------------------------------------

    // At fov π/3 over 720 px, a 1-unit cell projects to 12 px at height ~52
    // and the REFINE threshold (16 px on the finer level) sits at height ~39.
    // Between them is the hysteresis band these cases walk through.

    [Fact]
    public void The_spacing_coarsens_when_cells_get_too_small_and_does_not_flap_back()
    {
        var grid = new GroundGrid();
        var output = new DebugDraw();

        // High enough that a 1-unit cell is under 12 px: the grid coarsens.
        var high = new Camera { Position = new Vector3(0f, 60f, 0f) };
        grid.Draw(output, high, increment: 1f, viewportHeight: 720f);
        grid.CellSizeLastDraw.ShouldBe(2f);

        // Back inside the band: the finer level would be readable (≈14 px) but
        // not comfortably (≥16 px), so the grid must STAY coarse — one
        // threshold here is a camera hovering at the boundary flickering the
        // whole lattice between two spacings frame to frame.
        var band = new Camera { Position = new Vector3(0f, 45f, 0f) };
        output.Clear();
        grid.Draw(output, band, increment: 1f, viewportHeight: 720f);
        grid.CellSizeLastDraw.ShouldBe(2f,
            "refining the moment the finer level clears the coarsen threshold is a flip-flop, " +
            "not hysteresis");

        // Well below the band: the finer level is comfortably readable again.
        var low = new Camera { Position = new Vector3(0f, 30f, 0f) };
        output.Clear();
        grid.Draw(output, low, increment: 1f, viewportHeight: 720f);
        grid.CellSizeLastDraw.ShouldBe(1f);
    }

    [Fact]
    public void A_fresh_grid_in_the_hysteresis_band_starts_fine_rather_than_coarse
        ()
    {
        // The band only holds a level a camera ARRIVED at; with no history the
        // ideal answer wins. This is what pins the statefulness as hysteresis
        // rather than a ratchet.
        var grid = new GroundGrid();
        var output = new DebugDraw();
        var band = new Camera { Position = new Vector3(0f, 45f, 0f) };

        grid.Draw(output, band, increment: 1f, viewportHeight: 720f);
        grid.CellSizeLastDraw.ShouldBe(1f);
    }

    // --- The mode, its verbs, and the envelope -------------------------------

    [Fact]
    public void The_grid_mode_defaults_to_auto_and_the_set_verbs_move_it()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.GridModeName.ShouldBe("auto");

        host.Apply(EditorHostCommand.GridOn);
        host.GridModeName.ShouldBe("on");

        host.Apply(EditorHostCommand.GridOff);
        host.GridModeName.ShouldBe("off");

        host.Apply(EditorHostCommand.GridAuto);
        host.GridModeName.ShouldBe("auto");
    }

    [Fact]
    public void The_grid_verbs_stay_live_while_the_editor_is_suspended()
    {
        // A view setting, not a scene edit: play mode owns the scene, and the
        // grid mode changes what is DRAWN. The same exemption navigation has.
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        host.Suspend();
        host.Apply(EditorHostCommand.GridOn);
        host.GridModeName.ShouldBe("on");
    }

    [Fact]
    public void The_envelope_ramps_the_grid_in_and_out_rather_than_cutting()
    {
        var scene = new Scene("Editor");
        SceneEditorHost host = NewHost(scene);

        // Auto with no gesture: hidden at rest.
        host.Update(0.1);
        host.Grid.Opacity.ShouldBe(0f);

        // Switched always-on: PART WAY after one short frame — an instant 1
        // would be the cut the envelope exists to remove — and arrived shortly
        // after.
        host.Apply(EditorHostCommand.GridOn);
        host.Update(0.05);
        host.Grid.Opacity.ShouldBeGreaterThan(0.1f);
        host.Grid.Opacity.ShouldBeLessThan(1f);

        host.Update(0.05);
        host.Update(0.05);
        host.Grid.Opacity.ShouldBe(1f);

        // And out again, slower than in: afterglow, not a flash.
        host.Apply(EditorHostCommand.GridOff);
        host.Update(0.05);
        host.Grid.Opacity.ShouldBeGreaterThan(0f);
        host.Grid.Opacity.ShouldBeLessThan(0.9f);

        for (int i = 0; i < 6; i++)
            host.Update(0.05);
        host.Grid.Opacity.ShouldBe(0f);
    }
}
