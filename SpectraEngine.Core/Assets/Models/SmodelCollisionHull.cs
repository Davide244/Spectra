using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// One convex collision hull, as a range into the <c>COLL</c> section's flat
/// plane array. Eight bytes, exactly as they sit on disk.
/// </summary>
/// <remarks>
/// <para><b>Plane sets, not a triangle soup, and that is what earns this format
/// its custom status.</b> A convex solid expressed as an intersection of
/// half-spaces is precisely what <c>Brush</c>'s constructor takes, so a cooked
/// model's collision becomes <c>Brush</c> instances and rides the character
/// mover's existing plane-set path with no new collision code at all. A soup
/// would demand a query structure this engine does not have and does not want.
/// </para>
/// <para><b>Several hulls per model, and they may overlap.</b> That is not a
/// compromise: the same overlapping-cover argument that lets a doorway be walked
/// through applies here, so a concave prop is represented as a set of convex
/// pieces rather than forced into one hull that would seal the gap the artist
/// modelled.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SmodelCollisionHull
{
    /// <summary>Index of this hull's first plane in the section's plane array.</summary>
    public readonly uint PlaneStart;

    /// <summary>
    /// How many planes bound this hull. Never fewer than
    /// <see cref="SmodelFormat.MinimumHullPlanes"/>, which the reader enforces
    /// because that is the floor <c>Brush</c> itself enforces.
    /// </summary>
    public readonly uint PlaneCount;

    /// <summary>Builds one hull record. Every field is assigned.</summary>
    public SmodelCollisionHull(uint planeStart, uint planeCount)
    {
        PlaneStart = planeStart;
        PlaneCount = planeCount;
    }
}
