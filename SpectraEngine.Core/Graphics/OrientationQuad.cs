namespace SpectraEngine.Core.Graphics;

/// <summary>
/// The geometry the texture-orientation measurement draws: a clip-space quad
/// whose texture coordinates are <b>pinned</b>, meaning derived from the vertex
/// position by one formula with no per-backend adjustment of any kind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <see cref="FullscreenTriangle"/>.</b> That one flips V on
/// D3D, and it is right to: it samples a render target, whose row order really
/// does differ between the APIs. This quad exists to answer a different
/// question - whether the three backends agree about which way up an
/// <i>uploaded</i> texture is - and an instrument carrying a compensation
/// cannot measure whether a compensation is needed.
/// </para>
/// <para>
/// A quad rather than an oversized triangle because all four corners have to be
/// real: the measurement reads the four corners of the output and asks which
/// corner of the source arrived at each. The full standard layout is carried
/// for the same reason the triangle carries it - D3D11 builds every mesh's input
/// layout from the lit shader's vertex bytecode, so a lean layout is rejected at
/// mesh creation.
/// </para>
/// </remarks>
public static class OrientationQuad
{
    /// <summary>Which part of the target the quad covers.</summary>
    public enum Coverage
    {
        /// <summary>The whole target: what the orientation measurement draws.</summary>
        Full,

        /// <summary>
        /// The upper half in clip space (y from 0 to 1). Drawn over a cleared
        /// target, this is what proves the READBACK's picture-space convention
        /// is right on this backend before any conclusion is drawn from it.
        /// </summary>
        TopHalf,
    }

    /// <summary>
    /// Vertices for the quad, in clip space, four of them, with
    /// <c>u = (x + 1) / 2</c> and <c>v = (y + 1) / 2</c>.
    /// </summary>
    public static float[] BuildVertices(Coverage coverage)
    {
        float yMin = coverage == Coverage.TopHalf ? 0f : -1f;

        // Counter-clockwise in a y-up clip space, which is the front face on all
        // three backends.
        (float X, float Y)[] corners = [(-1f, yMin), (1f, yMin), (1f, 1f), (-1f, 1f)];

        var vertices = new float[corners.Length * 8];
        for (int i = 0; i < corners.Length; i++)
        {
            (float x, float y) = corners[i];
            int o = i * 8;
            vertices[o + 0] = x;
            vertices[o + 1] = y;
            vertices[o + 2] = 0f;
            vertices[o + 3] = 0f;
            vertices[o + 4] = 0f;
            vertices[o + 5] = 1f;
            vertices[o + 6] = (x + 1f) * 0.5f;
            vertices[o + 7] = (y + 1f) * 0.5f;
        }
        return vertices;
    }

    /// <summary>Two triangles over the four corners.</summary>
    public static uint[] Indices => [0, 1, 2, 0, 2, 3];
}
