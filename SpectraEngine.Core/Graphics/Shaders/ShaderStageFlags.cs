using System;

namespace SpectraEngine.Core.Graphics.Shaders;

[Flags]
public enum ShaderStageFlags : byte
{
    None     = 0,
    Vertex   = 1 << 0,
    Fragment = 1 << 1,
    Geometry = 1 << 2,
    Compute  = 1 << 3,
}
