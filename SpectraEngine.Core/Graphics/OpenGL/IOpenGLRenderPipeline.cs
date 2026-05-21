using System;

namespace SpectraEngine.Core.Graphics.OpenGL;

/// <summary>
/// A swappable rendering strategy for the OpenGL backend. Pipelines own the
/// per-frame work (clear, scene walk, post-passes); the renderer owns the GPU
/// resources and the lifecycle of the pipelines themselves.
/// </summary>
/// <remarks>
/// Pipelines may freely change GL state during <see cref="Execute"/>, but should
/// leave the cull-face / blend / depth-test state in the same configuration the
/// renderer set up at <see cref="OpenGLRenderer.Initialize"/> time so the next
/// pipeline starts from a known baseline.
/// </remarks>
public interface IOpenGLRenderPipeline : IDisposable
{
    /// <summary>Human-readable name surfaced in UI and logs.</summary>
    string Name { get; }

    /// <summary>One-time setup; called when the pipeline is registered.</summary>
    void Initialize(OpenGLRenderer renderer);

    /// <summary>Runs one frame's worth of work for this pipeline.</summary>
    void Execute(in OpenGLRenderContext context);
}
