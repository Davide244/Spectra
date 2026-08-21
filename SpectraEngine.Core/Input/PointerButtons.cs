using System;

namespace SpectraEngine.Core.Input;

/// <summary>
/// Backend-neutral mouse-button set. Deliberately mirrors only the three
/// buttons an editor actually binds, and deliberately names no Silk.NET type:
/// this is the vocabulary layers that must not depend on the windowing backend
/// (the editing assembly, and any future Uno/WinUI host) speak instead of
/// <c>Silk.NET.Input.MouseButton</c>.
/// </summary>
[Flags]
public enum PointerButtons
{
    /// <summary>No buttons.</summary>
    None = 0,

    /// <summary>The primary (left) button.</summary>
    Left = 1 << 0,

    /// <summary>The secondary (right) button.</summary>
    Right = 1 << 1,

    /// <summary>The middle (wheel) button.</summary>
    Middle = 1 << 2,
}
