namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Generates vertex/index data for built-in mesh shapes. The vertex layout is
/// position (xyz) + normal (xyz) + uv (xy) — 8 floats per vertex — matching
/// vertex attribute locations 0, 1, and 2.
/// </summary>
public static class Primitives
{
    /// <summary>A unit cube centred on the origin with per-face flat normals and 0..1 UVs per face.</summary>
    public static (float[] Vertices, uint[] Indices) Cube()
    {
        // Per face, four corners ordered so the (1,2)-triangulation winds CCW
        // when viewed from outside, with UVs spanning the full 0..1 square.
        float[] vertices =
        [
            // +Z
            -0.5f, -0.5f,  0.5f,   0f,  0f,  1f,   0f, 0f,
             0.5f, -0.5f,  0.5f,   0f,  0f,  1f,   1f, 0f,
             0.5f,  0.5f,  0.5f,   0f,  0f,  1f,   1f, 1f,
            -0.5f,  0.5f,  0.5f,   0f,  0f,  1f,   0f, 1f,
            // -Z
             0.5f, -0.5f, -0.5f,   0f,  0f, -1f,   0f, 0f,
            -0.5f, -0.5f, -0.5f,   0f,  0f, -1f,   1f, 0f,
            -0.5f,  0.5f, -0.5f,   0f,  0f, -1f,   1f, 1f,
             0.5f,  0.5f, -0.5f,   0f,  0f, -1f,   0f, 1f,
            // -X
            -0.5f, -0.5f, -0.5f,  -1f,  0f,  0f,   0f, 0f,
            -0.5f, -0.5f,  0.5f,  -1f,  0f,  0f,   1f, 0f,
            -0.5f,  0.5f,  0.5f,  -1f,  0f,  0f,   1f, 1f,
            -0.5f,  0.5f, -0.5f,  -1f,  0f,  0f,   0f, 1f,
            // +X
             0.5f, -0.5f,  0.5f,   1f,  0f,  0f,   0f, 0f,
             0.5f, -0.5f, -0.5f,   1f,  0f,  0f,   1f, 0f,
             0.5f,  0.5f, -0.5f,   1f,  0f,  0f,   1f, 1f,
             0.5f,  0.5f,  0.5f,   1f,  0f,  0f,   0f, 1f,
            // +Y
            -0.5f,  0.5f,  0.5f,   0f,  1f,  0f,   0f, 0f,
             0.5f,  0.5f,  0.5f,   0f,  1f,  0f,   1f, 0f,
             0.5f,  0.5f, -0.5f,   0f,  1f,  0f,   1f, 1f,
            -0.5f,  0.5f, -0.5f,   0f,  1f,  0f,   0f, 1f,
            // -Y
            -0.5f, -0.5f, -0.5f,   0f, -1f,  0f,   0f, 0f,
             0.5f, -0.5f, -0.5f,   0f, -1f,  0f,   1f, 0f,
             0.5f, -0.5f,  0.5f,   0f, -1f,  0f,   1f, 1f,
            -0.5f, -0.5f,  0.5f,   0f, -1f,  0f,   0f, 1f,
        ];

        uint[] indices = new uint[36];
        for (uint face = 0; face < 6; face++)
        {
            uint b = face * 4;
            uint i = face * 6;
            indices[i + 0] = b + 0;
            indices[i + 1] = b + 1;
            indices[i + 2] = b + 2;
            indices[i + 3] = b + 0;
            indices[i + 4] = b + 2;
            indices[i + 5] = b + 3;
        }

        return (vertices, indices);
    }

    /// <summary>
    /// Builds a 16×16 two-colour checkerboard in RGB8 — handy as a debug
    /// diffuse texture when you don't yet have an asset pipeline. UVs above 1
    /// tile naturally with <see cref="TextureWrap.Repeat"/>.
    /// </summary>
    public static byte[] CheckerboardRgb8(int size = 16, byte light = 230, byte dark = 60)
    {
        var pixels = new byte[size * size * 3];
        int half = size / 2;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool dark2 = ((x < half) ^ (y < half));
                byte v = dark2 ? dark : light;
                int o = (y * size + x) * 3;
                pixels[o + 0] = v;
                pixels[o + 1] = v;
                pixels[o + 2] = v;
            }
        }
        return pixels;
    }
}
