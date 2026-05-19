using System.Numerics;

namespace SpectraEngine.Core.Bsp;

/// <summary>The result of a successful <see cref="BspTree.Raycast"/>.</summary>
/// <param name="Point">The point at which the ray first enters solid space.</param>
/// <param name="Normal">The outward surface normal at the entry point.</param>
/// <param name="Distance">The distance from the ray origin to <paramref name="Point"/>.</param>
public readonly record struct BspRaycastHit(Vector3 Point, Vector3 Normal, float Distance);
