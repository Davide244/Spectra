namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Generates vertex/index data for built-in mesh shapes. The vertex layout is
/// position (xyz) followed by normal (xyz) — 6 floats per vertex — matching
/// vertex attribute locations 0 and 1.
/// </summary>
public static class Primitives
{
    /// <summary>A unit cube centred on the origin with per-face flat normals.</summary>
    public static (float[] Vertices, uint[] Indices) Cube()
    {
        float[] vertices =
        [
            -0.5f, -0.5f,  0.5f,   0f,  0f,  1f,
             0.5f, -0.5f,  0.5f,   0f,  0f,  1f,
             0.5f,  0.5f,  0.5f,   0f,  0f,  1f,
            -0.5f,  0.5f,  0.5f,   0f,  0f,  1f,

             0.5f, -0.5f, -0.5f,   0f,  0f, -1f,
            -0.5f, -0.5f, -0.5f,   0f,  0f, -1f,
            -0.5f,  0.5f, -0.5f,   0f,  0f, -1f,
             0.5f,  0.5f, -0.5f,   0f,  0f, -1f,

            -0.5f, -0.5f, -0.5f,  -1f,  0f,  0f,
            -0.5f, -0.5f,  0.5f,  -1f,  0f,  0f,
            -0.5f,  0.5f,  0.5f,  -1f,  0f,  0f,
            -0.5f,  0.5f, -0.5f,  -1f,  0f,  0f,

             0.5f, -0.5f,  0.5f,   1f,  0f,  0f,
             0.5f, -0.5f, -0.5f,   1f,  0f,  0f,
             0.5f,  0.5f, -0.5f,   1f,  0f,  0f,
             0.5f,  0.5f,  0.5f,   1f,  0f,  0f,

            -0.5f,  0.5f,  0.5f,   0f,  1f,  0f,
             0.5f,  0.5f,  0.5f,   0f,  1f,  0f,
             0.5f,  0.5f, -0.5f,   0f,  1f,  0f,
            -0.5f,  0.5f, -0.5f,   0f,  1f,  0f,

            -0.5f, -0.5f, -0.5f,   0f, -1f,  0f,
             0.5f, -0.5f, -0.5f,   0f, -1f,  0f,
             0.5f, -0.5f,  0.5f,   0f, -1f,  0f,
            -0.5f, -0.5f,  0.5f,   0f, -1f,  0f,
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
}
