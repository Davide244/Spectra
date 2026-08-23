using System;
using System.Numerics;
using Silk.NET.Maths;
using SpectraEngine.Core.Graphics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The render-pass seam: who decides where a frame goes, and how big it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the assertions a GPU test cannot make.</b> A pass that leaked,
/// a viewport sized from the window instead of from the target, or a clear that
/// quietly stopped happening all still produce a picture on a real device, and
/// often a plausible one. What they break is the next thing that draws
/// somewhere other than the back buffer, which is the entire point of the seam.
/// </para>
/// <para>
/// <see cref="FakeRenderer"/> records passes rather than performing them, so
/// the shape of a frame is observable without a driver.
/// </para>
/// </remarks>
public sealed class RenderPassTests
{
    [Fact]
    public void A_pass_takes_its_size_from_the_target_when_it_opens()
    {
        var renderer = new FakeRenderer();
        renderer.SetFramebufferSize(new Vector2D<int>(1280, 720));

        renderer.BeginPass(PassClear.To(ClearColors.Sky));
        renderer.PassSize.ShouldBe(new Vector2D<int>(1280, 720));
        renderer.PassAspectRatio!.Value.ShouldBe(1280f / 720f, 1e-6f);
        renderer.EndPass();

        renderer.Passes.Count.ShouldBe(1);
        renderer.Passes[0].Size.ShouldBe(new Vector2D<int>(1280, 720));
    }

    [Fact]
    public void A_resize_between_frames_is_picked_up_by_the_next_pass()
    {
        // The latch is written by the main thread and read here; a pass that
        // cached the size once would render the old viewport forever.
        var renderer = new FakeRenderer();

        renderer.SetFramebufferSize(new Vector2D<int>(800, 600));
        renderer.BeginPass(PassClear.Keep);
        renderer.EndPass();

        renderer.SetFramebufferSize(new Vector2D<int>(1920, 1080));
        renderer.BeginPass(PassClear.Keep);
        renderer.EndPass();

        renderer.Passes[0].Size.ShouldBe(new Vector2D<int>(800, 600));
        renderer.Passes[1].Size.ShouldBe(new Vector2D<int>(1920, 1080));
    }

    [Fact]
    public void A_zero_height_target_has_no_aspect_ratio_rather_than_infinity()
    {
        // A minimised window, and the moment mid-resize when the latch has the
        // new size but the target does not. Dividing here would put a NaN or an
        // infinity into the projection matrix, and every subsequent frustum
        // test would silently answer nonsense.
        var renderer = new FakeRenderer();
        renderer.SetFramebufferSize(new Vector2D<int>(1280, 0));

        renderer.BeginPass(PassClear.Keep);
        renderer.PassAspectRatio.ShouldBeNull();
        renderer.EndPass();
    }

    [Fact]
    public void Passes_do_not_nest()
    {
        var renderer = new FakeRenderer();
        renderer.SetFramebufferSize(new Vector2D<int>(64, 64));

        renderer.BeginPass(PassClear.Keep);

        // Throwing beats leaving the wrong target bound for the rest of the
        // frame, which is a corrupt picture on one backend and a debug-layer
        // message on another.
        Should.Throw<InvalidOperationException>(() => renderer.BeginPass(PassClear.Keep));

        renderer.EndPass();
    }

    [Fact]
    public void Ending_a_pass_that_was_never_begun_throws()
    {
        var renderer = new FakeRenderer();
        Should.Throw<InvalidOperationException>(renderer.EndPass);
    }

    [Fact]
    public void A_clear_can_name_colour_depth_both_or_neither()
    {
        // "Do not touch this attachment" is a distinct instruction from "clear
        // it to black": an overlay pass keeps colour, a shadow pass has no
        // colour attachment to clear at all.
        PassClear.To(new Vector4(1f, 0f, 0f, 1f)).Color.ShouldBe(new Vector4(1f, 0f, 0f, 1f));
        PassClear.To(Vector4.One).Depth.ShouldBe(1f);

        PassClear.DepthOnly.Color.ShouldBeNull();
        PassClear.DepthOnly.Depth.ShouldBe(1f);

        PassClear.Keep.Color.ShouldBeNull();
        PassClear.Keep.Depth.ShouldBeNull();
    }

    [Fact]
    public void The_sky_a_pass_clears_to_is_the_one_shared_linear_constant()
    {
        // All three backends clear through PassClear now, so a divergence
        // between them can only come from the constant. Pinning it here means
        // one of them drifting is a test failure rather than a screenshot
        // somebody happens to compare.
        PassClear sky = PassClear.To(ClearColors.Sky);

        sky.Color!.Value.ShouldBe(ClearColors.Sky);
        ColorSpace.LinearToSrgb(sky.Color.Value.X).ShouldBe(0.392f, 1e-4f);
    }
}
