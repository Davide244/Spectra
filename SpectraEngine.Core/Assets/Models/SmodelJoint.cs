using System.Numerics;
using System.Runtime.InteropServices;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// One joint of a cooked skeleton, exactly as its fifty-six bytes sit in a
/// <c>SKEL</c> section.
/// </summary>
/// <remarks>
/// <para><b><see cref="ParentIndex"/> is strictly less than the joint's own
/// index, and the reader refuses a file where it is not.</b> That single
/// invariant is what lets a hierarchy be built, and every world matrix composed,
/// by one forward loop with no sort, no recursion and no visited set. The cost of
/// not enforcing it is not a wrong picture: a forward reference reads a parent
/// whose own matrix has not been computed yet, so the child is posed against
/// whatever the array happened to hold, which for a fresh array is identity and
/// therefore looks almost right.</para>
/// <para><b>The inverse bind matrix is stored as four rows of three, and the
/// omitted fourth column is the constant <c>(0, 0, 0, 1)</c>.</b> That is the
/// packing an affine transform has in <see cref="Matrix4x4"/>'s own row-vector
/// convention, where translation lives in <c>M41</c> to <c>M43</c>: dropping the
/// last ROW instead, which is the packing a column-vector engine would use, would
/// discard exactly the translation and leave every joint rotating correctly about
/// the model origin.</para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct SmodelJoint
{
    /// <summary>
    /// Offset into the <c>NAME</c> blob of this joint's name, or
    /// <see cref="SmodelFormat.NameOffsetAbsent"/> when it has none.
    /// </summary>
    public readonly uint NameOffset;

    /// <summary>
    /// Index of this joint's parent, always less than the joint's own index, or
    /// <see cref="NoParent"/> for a root.
    /// </summary>
    public readonly int ParentIndex;

    /// <summary>First row of the inverse bind matrix.</summary>
    public readonly Vector3 InverseBindRow0;

    /// <summary>Second row of the inverse bind matrix.</summary>
    public readonly Vector3 InverseBindRow1;

    /// <summary>Third row of the inverse bind matrix.</summary>
    public readonly Vector3 InverseBindRow2;

    /// <summary>Fourth row of the inverse bind matrix, which is the translation.</summary>
    public readonly Vector3 InverseBindRow3;

    /// <summary>What <see cref="ParentIndex"/> holds for a root joint.</summary>
    /// <remarks>
    /// Minus one rather than a self-reference or a sentinel index, so the
    /// "strictly less than my own index" rule is one comparison that a root
    /// satisfies for free.
    /// </remarks>
    public const int NoParent = -1;

    /// <summary>Builds one joint record. Every field is assigned.</summary>
    public SmodelJoint(
        uint nameOffset,
        int parentIndex,
        Vector3 inverseBindRow0,
        Vector3 inverseBindRow1,
        Vector3 inverseBindRow2,
        Vector3 inverseBindRow3)
    {
        NameOffset = nameOffset;
        ParentIndex = parentIndex;
        InverseBindRow0 = inverseBindRow0;
        InverseBindRow1 = inverseBindRow1;
        InverseBindRow2 = inverseBindRow2;
        InverseBindRow3 = inverseBindRow3;
    }

    /// <summary>Whether this joint is a root.</summary>
    public bool IsRoot => ParentIndex == NoParent;

    /// <summary>Whether this joint carries a name record.</summary>
    public bool HasName => NameOffset != SmodelFormat.NameOffsetAbsent;

    /// <summary>
    /// The stored rows widened back into the matrix the engine actually
    /// multiplies with.
    /// </summary>
    public Matrix4x4 InverseBind => new(
        InverseBindRow0.X, InverseBindRow0.Y, InverseBindRow0.Z, 0f,
        InverseBindRow1.X, InverseBindRow1.Y, InverseBindRow1.Z, 0f,
        InverseBindRow2.X, InverseBindRow2.Y, InverseBindRow2.Z, 0f,
        InverseBindRow3.X, InverseBindRow3.Y, InverseBindRow3.Z, 1f);
}
