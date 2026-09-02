using SpectraEngine.Core.Graphics;
using System;

namespace SpectraEngine.Graphics.Tests;

/// <summary>
/// The refusals <see cref="RenderTargetDesc.Validate"/> makes once a target asks
/// to be shared.
/// </summary>
/// <remarks>
/// Pure: no device, no driver, no window. That is the point of the check living
/// on the description rather than in a backend. A shared target that cannot be
/// imported fails as an HRESULT from inside a driver, three layers below the
/// line that wrote the description and naming neither the format nor the target,
/// so the only useful place to say no is here.
/// </remarks>
public sealed class SharedRenderTargetTests
{
    [Fact]
    public void An_ordinary_target_is_unshared_unless_it_asks()
    {
        var desc = new RenderTargetDesc(256, 256);

        desc.Sharing.ShouldBe(RenderTargetSharing.None);
        Should.NotThrow(desc.Validate);
    }

    [Fact]
    public void A_shared_eight_bit_colour_target_is_accepted()
    {
        var desc = new RenderTargetDesc(
            1280, 720, TextureFormat.Rgba8, TextureColorSpace.Srgb,
            Depth: true, TextureFilter.Linear, TextureWrap.Clamp, Color: true,
            RenderTargetSharing.KeyedMutex);

        Should.NotThrow(desc.Validate);
    }

    [Fact]
    public void A_shared_half_float_target_is_refused_naming_the_format()
    {
        // Not a limitation of this engine: the external-image import that
        // consumes the handle has no half-float path at all, so this
        // description is a request nothing on the other side can satisfy.
        var desc = new RenderTargetDesc(
            1280, 720, TextureFormat.Rgba16Float, TextureColorSpace.Linear,
            Depth: true, TextureFilter.Linear, TextureWrap.Clamp, Color: true,
            RenderTargetSharing.KeyedMutex);

        Should.Throw<ArgumentOutOfRangeException>(desc.Validate)
            .Message.ShouldContain(nameof(TextureFormat.Rgba8));
    }

    [Fact]
    public void A_half_float_target_that_is_not_shared_is_still_fine()
    {
        // The refusal is about SHARING, not about the format: the HDR scene
        // target is exactly this description and must stay legal.
        var desc = new RenderTargetDesc(
            1280, 720, TextureFormat.Rgba16Float, TextureColorSpace.Linear);

        Should.NotThrow(desc.Validate);
    }

    [Fact]
    public void A_shared_depth_only_target_is_refused()
    {
        // There is no colour attachment on a shadow map, so there is nothing to
        // hand out; a backend asked for this would have to invent one.
        RenderTargetDesc desc = RenderTargetDesc.DepthOnly(2048) with
        {
            Sharing = RenderTargetSharing.KeyedMutex,
        };

        Should.Throw<ArgumentException>(desc.Validate);
    }

    [Fact]
    public void A_backend_that_cannot_share_says_so_rather_than_throwing()
    {
        // The defaults are refusals, which is what lets a caller ask one
        // question instead of consulting a capability table first. Never
        // initialized: this is the base class's own answer, and every backend
        // today inherits exactly it.
        var renderer = new Core.Graphics.OpenGL.OpenGLRenderer(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Renderer>.Instance,
            new SpectraShade.Compiler.SpectraShadeCompiler());

        renderer.TryGetSharedHandle(out Renderer.SharedTargetHandle handle).ShouldBeFalse();
        handle.ShouldBe(default);
        renderer.BeginSharedWrite().ShouldBeFalse();
        Should.NotThrow(renderer.EndSharedWrite);
    }

    [Fact]
    public void A_shared_handle_carries_its_size_and_its_generation()
    {
        // The size travels WITH the handle so the two cannot be paired a frame
        // apart, and the generation is what tells a consumer to re-import
        // rather than sample a resource that has been destroyed.
        var first = new Renderer.SharedTargetHandle(0x1234, 1280, 720, 1);
        var resized = first with { NtHandle = 0x5678, Width = 1600, Height = 900, Generation = 2 };

        resized.Generation.ShouldNotBe(first.Generation);
        resized.ShouldNotBe(first);
    }
}
