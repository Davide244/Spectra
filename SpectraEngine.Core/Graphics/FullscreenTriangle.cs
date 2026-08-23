using System;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The geometry a post-processing pass draws: one triangle large enough to
/// cover the whole target, with its texture coordinates already correct for the
/// backend that will sample through it.
/// </summary>
/// <remarks>
/// <para>
/// <b>One triangle, not two.</b> A quad has a diagonal seam along which the
/// rasteriser produces two partial quads of fragments and derivatives are
/// discontinuous; a single oversized triangle covers the same pixels with none
/// of that, and the parts outside the viewport are clipped for free.
/// </para>
/// <para>
/// <b>It carries a real vertex buffer, because there is no other option.</b>
/// The vertex-less draw everybody reaches for needs a vertex-ID input, and
/// SpectraShade has none. It also carries the full standard layout (position,
/// normal, uv) despite using only two of the three: D3D11 builds every mesh's
/// input layout from the lit shader's vertex bytecode, so a lean layout is
/// rejected at mesh creation with an error that points nowhere near the shader
/// that wanted it.
/// </para>
/// <para>
/// <b>The V coordinate is flipped on D3D, and that is the whole trap of this
/// milestone.</b> The two APIs disagree about which row of a render target is
/// row zero. In OpenGL a framebuffer's origin is bottom-left, so the fragment at
/// the bottom of the screen writes texel row 0 and sampling that texture at
/// v = 0 reads it back. In D3D the render-target origin is top-left, so the same
/// fragment writes the LAST row and v = 0 reads the top instead. Uploaded
/// textures do not show this because <c>ImageDecoder</c> flips image rows on the
/// way in, which makes v = 0 the bottom of the picture on both; a target written
/// by rasterisation never passes through that code. Baking the flip into the
/// vertex data keeps it out of the shader, where it would need a variant or a
/// branch, and out of the sampler, where nothing can express it.
/// </para>
/// </remarks>
public static class FullscreenTriangle
{
    /// <summary>Vertices, in clip space, with backend-correct texture coordinates.</summary>
    /// <remarks>
    /// Positions run to 3 rather than 1 so the triangle's edges fall outside the
    /// view and every pixel is interior. UVs are scaled to match, so the
    /// interpolated value across the visible area is still exactly 0 to 1.
    /// </remarks>
    public static float[] BuildVertices(GraphicsBackend backend)
    {
        // True when sampling a render target at v = 0 reads the row that was
        // rasterised at the TOP of the target, which is the D3D convention.
        bool topLeftOrigin = backend is not GraphicsBackend.OpenGL;

        // Clip-space corners of the oversized triangle: bottom-left, bottom-right
        // (off-screen), top-left (off-screen). Counter-clockwise in a y-up clip
        // space, which is the front face on all three backends.
        (float X, float Y)[] corners = [(-1f, -1f), (3f, -1f), (-1f, 3f)];

        var vertices = new float[corners.Length * 8];
        for (int i = 0; i < corners.Length; i++)
        {
            (float x, float y) = corners[i];
            float u = (x + 1f) * 0.5f;
            float v = (y + 1f) * 0.5f;
            if (topLeftOrigin) v = 1f - v;

            int o = i * 8;
            vertices[o + 0] = x;
            vertices[o + 1] = y;
            vertices[o + 2] = 0f;
            // Normal: unused by the resolve shader, present because the layout is.
            vertices[o + 3] = 0f;
            vertices[o + 4] = 0f;
            vertices[o + 5] = 1f;
            vertices[o + 6] = u;
            vertices[o + 7] = v;
        }
        return vertices;
    }

    /// <summary>
    /// Indices. Three real ones, because D3D11 builds an immutable index buffer
    /// and rejects a zero-length one.
    /// </summary>
    public static uint[] Indices => [0, 1, 2];
}
