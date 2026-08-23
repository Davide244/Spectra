using System;
using System.Collections.Generic;

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
    /// A unit-radius UV sphere centred on the origin, with exact analytic
    /// normals and a longitude/latitude UV map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because a cube cannot show whether a BRDF is right.</b>
    /// Every fragment of a flat face shares one normal, so a specular highlight
    /// is either entirely present or entirely absent and roughness reads as a
    /// brightness change. Curvature is what makes the highlight a shape that
    /// grows and softens, which is the thing worth looking at, and it is the
    /// reason every PBR reference image in existence is a row of spheres.
    /// </para>
    /// <para>
    /// The seam is real and deliberate: the last column of vertices duplicates
    /// the first at u = 1 instead of u = 0, because one vertex cannot carry two
    /// texture coordinates. Poles are a fan of distinct vertices for the same
    /// reason. Both cost a few vertices and avoid a visible tear.
    /// </para>
    /// </remarks>
    /// <param name="segments">Divisions around the equator; at least 3.</param>
    /// <param name="rings">Divisions from pole to pole; at least 2.</param>
    public static (float[] Vertices, uint[] Indices) Sphere(int segments = 32, int rings = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(rings, 2);

        int columns = segments + 1;
        int rows = rings + 1;
        float[] vertices = new float[columns * rows * 8];

        int v = 0;
        for (int y = 0; y < rows; y++)
        {
            // Latitude from the south pole up, so v = 0 is the bottom of the
            // image the way it is on every cube face.
            float vCoord = y / (float)rings;
            float phi = vCoord * MathF.PI;
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);

            for (int x = 0; x < columns; x++)
            {
                float uCoord = x / (float)segments;
                float theta = uCoord * MathF.Tau;

                // On a unit sphere centred on the origin the position and the
                // normal are the same vector, which is why no normal needs
                // averaging and there is no smoothing decision to get wrong.
                float nx = MathF.Cos(theta) * sinPhi;
                float ny = -cosPhi;
                float nz = MathF.Sin(theta) * sinPhi;

                vertices[v++] = nx * 0.5f;
                vertices[v++] = ny * 0.5f;
                vertices[v++] = nz * 0.5f;
                vertices[v++] = nx;
                vertices[v++] = ny;
                vertices[v++] = nz;
                vertices[v++] = uCoord;
                vertices[v++] = vCoord;
            }
        }

        var indices = new List<uint>(segments * rings * 6);
        for (int y = 0; y < rings; y++)
        {
            for (int x = 0; x < segments; x++)
            {
                uint a = (uint)(y * columns + x);
                uint b = (uint)(a + columns);

                // Degenerate triangles at the poles are skipped rather than
                // emitted: one of the two per quad collapses to a line there,
                // and a zero-area triangle is a wasted index with an undefined
                // normal in anything that later reads the mesh.
                if (y != 0)
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(a + 1);
                }
                if (y != rings - 1)
                {
                    indices.Add(a + 1);
                    indices.Add(b);
                    indices.Add(b + 1);
                }
            }
        }

        return (vertices, indices.ToArray());
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
