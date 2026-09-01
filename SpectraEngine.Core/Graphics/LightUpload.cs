using System;
using System.Numerics;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Pushes a frame's lights into a shader program.
/// </summary>
/// <remarks>
/// <para>
/// <b>One implementation, called from all six pipelines.</b> The single
/// directional light this replaces was six independent auto-properties, one per
/// pipeline class, each initialised to the same hardcoded vector with nothing
/// tying them together. Adding a light meant remembering six places, and
/// forgetting the wireframe ones was a bug that only appeared after somebody
/// pressed the pipeline-cycle key.
/// </para>
/// <para>
/// <b>Uploaded per draw, not per frame, and that is affordable rather than
/// sloppy.</b> Each material carries its own program, so there is no single
/// point at which "the frame's lights" could be set once; the alternative is
/// tracking which programs have already seen this frame, which costs more than
/// it saves. Both D3D backends skip an upload whose bytes did not change, and
/// OpenGL's array upload is a single call, so a static light rig costs one
/// comparison per draw.
/// </para>
/// </remarks>
public static class LightUpload
{
    /// <summary>
    /// The ambient hemisphere's upper half: what a surface facing straight up
    /// sees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ambient was a flat scalar</b>, which lights a floor and a ceiling
    /// identically and leaves everything in an unlit corner without shape. A
    /// hemisphere is the cheapest thing that fixes it - two uniforms and one
    /// mix, against an irradiance probe's cubemap and a convolution pass - and
    /// it is what makes a scene read as lit by a PLACE.
    /// </para>
    /// <para>
    /// <b>The pair's average luminance is one</b>, deliberately, so the existing
    /// ambient STRENGTH keeps meaning what it meant: turning this on must not
    /// silently rescale every scene already tuned against it.
    /// </para>
    /// </remarks>
    public static Vector3 AmbientSky { get; set; } = new(0.92f, 1.06f, 1.40f);

    /// <summary>The lower half: bounce off the ground, warmer and dimmer.</summary>
    public static Vector3 AmbientGround { get; set; } = new(1.01f, 0.93f, 0.84f);

    // Scratch buffers, reused. Sized to the hard cap so they never grow, and
    // static because the render thread is the only caller: the draw path is
    // asserted allocation-free in steady state and a per-draw array would be
    // the largest single violation of that.
    [ThreadStatic] private static Vector4[]? _positions;
    [ThreadStatic] private static Vector4[]? _colors;
    [ThreadStatic] private static Vector4[]? _axes;
    [ThreadStatic] private static Vector4[]? _tangents;

    /// <summary>
    /// Writes <paramref name="view"/>'s lights and ambient level into
    /// <paramref name="shader"/>. Safe on a shader that declares none of them:
    /// unknown uniform names are ignored on every backend.
    /// </summary>
    /// <remarks>
    /// The arrays are always filled to their full declared length, with unused
    /// slots zeroed. An array uniform must be written whole (see
    /// <see cref="ShaderProgram.SetUniform(string, ReadOnlySpan{Vector4})"/>),
    /// and a zeroed slot beyond the count is harmless because the shader's loop
    /// stops at the count anyway.
    /// </remarks>
    public static void Apply(ShaderProgram shader, RenderView view, float ambient)
        => Apply(shader, view, ambient, AmbientSky, AmbientGround);

    /// <inheritdoc cref="Apply(ShaderProgram, RenderView, float)"/>
    public static void Apply(
        ShaderProgram shader, RenderView view, float ambient, Vector3 sky, Vector3 ground)
    {
        Packed packed = Fill(view);
        shader.SetUniform("uAmbientSky", sky);
        shader.SetUniform("uAmbientGround", ground);

        shader.SetUniform("uLightPositions", packed.Positions);
        shader.SetUniform("uLightColors", packed.Colors);
        shader.SetUniform("uLightAxis", packed.Axes);
        shader.SetUniform("uLightTangent", packed.Tangents);
        shader.SetUniform("uLightCount", packed.Count);
        shader.SetUniform("uAmbient", ambient);
    }

    /// <summary>
    /// The same upload, staged into a full-screen pass rather than written to a
    /// program: the deferred light pass shades every surface in the frame at
    /// once, so it uploads the lights once instead of once per draw.
    /// </summary>
    /// <remarks>
    /// <see cref="PostPass"/> copies what it is given, which matters here: the
    /// arrays below are shared scratch that the next call overwrites.
    /// </remarks>
    public static void Apply(PostPass pass, RenderView view, float ambient)
        => Apply(pass, view, ambient, AmbientSky, AmbientGround);

    /// <inheritdoc cref="Apply(PostPass, RenderView, float)"/>
    public static void Apply(
        PostPass pass, RenderView view, float ambient, Vector3 sky, Vector3 ground)
    {
        Packed packed = Fill(view);
        pass.SetUniform("uAmbientSky", sky);
        pass.SetUniform("uAmbientGround", ground);

        pass.SetUniform("uLightPositions", packed.Positions.AsSpan());
        pass.SetUniform("uLightColors", packed.Colors.AsSpan());
        pass.SetUniform("uLightAxis", packed.Axes.AsSpan());
        pass.SetUniform("uLightTangent", packed.Tangents.AsSpan());
        pass.SetUniform("uLightCount", packed.Count);
        pass.SetUniform("uAmbient", ambient);
    }

    // Packs the view's lights into the full-length scratch arrays both entry
    // points upload. Slots past the count are zeroed rather than left stale: an
    // array uniform must be written whole, and a leftover light from a previous
    // frame would be inside the array but outside the count, which is invisible
    // until something reads past the count.
    private readonly record struct Packed(
        Vector4[] Positions, Vector4[] Colors, Vector4[] Axes, Vector4[] Tangents, int Count);

    private static Packed Fill(RenderView view)
    {
        Vector4[] positions = _positions ??= new Vector4[RenderView.MaxLights];
        Vector4[] colors = _colors ??= new Vector4[RenderView.MaxLights];
        Vector4[] axes = _axes ??= new Vector4[RenderView.MaxLights];
        Vector4[] tangents = _tangents ??= new Vector4[RenderView.MaxLights];

        ReadOnlySpan<RenderLight> lights = view.Lights;
        for (int i = 0; i < RenderView.MaxLights; i++)
        {
            positions[i] = i < lights.Length ? lights[i].PositionRange : default;
            colors[i] = i < lights.Length ? lights[i].ColorIntensity : default;
            axes[i] = i < lights.Length ? lights[i].Axis : default;
            tangents[i] = i < lights.Length ? lights[i].Tangent : default;
        }

        return new Packed(positions, colors, axes, tangents, lights.Length);
    }
}
