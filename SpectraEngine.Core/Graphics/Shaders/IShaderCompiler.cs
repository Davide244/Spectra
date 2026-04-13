namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// Compiles SpectraShade (.spectrashade) source into a multi-pipeline compiled shader.
/// The compiler produces data for all requested backends in one pass.
/// </summary>
public interface IShaderCompiler
{
    /// <summary>
    /// Compiles SpectraShade source code to a <see cref="CompiledShaderFile"/>
    /// containing output for the specified backends.
    /// </summary>
    CompiledShaderFile Compile(string source, ReadOnlySpan<GraphicsBackend> targets);
}
