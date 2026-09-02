using SpectraEngine.Core.Graphics.Shaders;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The embedded base-shader lookup. It resolves by a constant resource name
/// rather than by scanning the manifest for a suffix match, so the failure this
/// pins is a build-configuration one: a shader file dropped from the
/// EmbeddedResource glob, or moved to a folder that changes its resource name,
/// is invisible to the compiler and surfaces as a shader that will not compile
/// at renderer start-up.
/// </summary>
public sealed class BaseShaderResourceTests
{
    [Fact]
    public void Every_base_shader_resolves_by_its_constant_resource_name()
    {
        BaseShaders.FileNames.Count.ShouldBeGreaterThan(0);

        foreach (string fileName in BaseShaders.FileNames)
        {
            using Stream stream = BaseShaders.OpenEmbedded(fileName);
            stream.ShouldNotBeNull($"'{fileName}' should be embedded in SpectraEngine.Core");
            stream.Length.ShouldBeGreaterThan(0, $"'{fileName}' should not be empty");
        }
    }

    [Fact]
    public void An_unknown_shader_name_throws_and_names_it()
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => BaseShaders.OpenEmbedded("NoSuchShader.spectrashade"));

        thrown.Message.ShouldContain("NoSuchShader.spectrashade");
    }

    [Fact]
    public void A_bare_suffix_of_a_real_shader_name_does_not_resolve()
    {
        // The defect the constant name closes: a suffix match answers "some
        // resource ends this way", so "Line.spectrashade" used to hand back
        // DebugLine or WorldLine depending on manifest order.
        Should.Throw<InvalidOperationException>(
            () => BaseShaders.OpenEmbedded("Line.spectrashade"));
    }

    [Fact]
    public void Every_declared_file_name_reads_as_source()
    {
        // The accessors go through the same lookup, so this catches a constant
        // that names a file the glob does not embed. The count ties the two
        // lists together: a ninth accessor added without a ninth entry in
        // FileNames would otherwise leave the enumeration test still green.
        BaseShaders.FileNames.Count.ShouldBe(8);

        foreach (string source in new[]
                 {
                     BaseShaders.Lit,
                     BaseShaders.DebugLine,
                     BaseShaders.PostResolve,
                     BaseShaders.GBufferFill,
                     BaseShaders.DeferredLight,
                     BaseShaders.ShadowDepth,
                     BaseShaders.WorldLine,
                     BaseShaders.WorldLineBlend,
                 })
        {
            source.ShouldNotBeNullOrWhiteSpace();
        }
    }
}
