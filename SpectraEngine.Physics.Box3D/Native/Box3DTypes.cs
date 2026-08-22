using System.Numerics;
using System.Runtime.InteropServices;

namespace SpectraEngine.Physics.Box3D.Native;

/// <summary>Box3D's semantic version, as reported by the loaded library.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct B3Version
{
    public int Major;
    public int Minor;
    public int Revision;

    public override readonly string ToString() => $"{Major}.{Minor}.{Revision}";
}

/// <summary>A three-component vector in Box3D's layout.</summary>
/// <remarks>
/// Field-for-field identical to <see cref="Vector3"/> today, and deliberately
/// NOT aliased to it. The binding's types must mirror the C library's, and
/// <see cref="Vector3"/> is the engine's type: tying them together would make a
/// future change to either one silently reinterpret the other's bytes. Convert
/// explicitly at the seam, where the conversion is visible and free.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3Vec3
{
    public float X;
    public float Y;
    public float Z;

    public B3Vec3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static B3Vec3 From(Vector3 v) => new(v.X, v.Y, v.Z);

    public readonly Vector3 ToVector3() => new(X, Y, Z);
}

/// <summary>A quaternion, stored as a vector part followed by the scalar.</summary>
/// <remarks>
/// <b>Vector first, scalar last</b> — which happens to match
/// <see cref="Quaternion"/>'s <c>(X, Y, Z, W)</c> order, but the agreement is a
/// coincidence of two independent choices and is pinned by the ABI manifest
/// rather than assumed. Getting this backwards produces rotations that look
/// almost right, which is the worst possible failure mode.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3Quat
{
    public B3Vec3 V;
    public float S;

    public static B3Quat From(Quaternion q) => new() { V = new B3Vec3(q.X, q.Y, q.Z), S = q.W };

    public readonly Quaternion ToQuaternion() => new(V.X, V.Y, V.Z, S);
}

/// <summary>A rigid transform: translation plus rotation, no scale.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct B3Transform
{
    public B3Vec3 P;
    public B3Quat Q;
}

/// <summary>
/// A world position — <b>a distinct type from <see cref="B3Vec3"/>, on purpose,
/// even though they are byte-identical in this build.</b>
/// </summary>
/// <remarks>
/// In C, <c>b3Pos</c> is a <c>typedef</c> of <c>b3Vec3</c> under the float build
/// and a <em>separate struct of doubles</em> under
/// <c>BOX3D_DOUBLE_PRECISION</c>. Keeping the managed types separate from the
/// binding's first line is what makes a future double build a re-generation
/// rather than a rewrite: every signature that means "a world position" already
/// says so, and only this type's fields change. Collapsing the two now would
/// save nothing and would hide, at every call site, which values are the ones
/// that would need to widen.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3Pos
{
    public float X;
    public float Y;
    public float Z;

    public B3Pos(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static B3Pos From(Vector3 v) => new(v.X, v.Y, v.Z);

    public readonly Vector3 ToVector3() => new(X, Y, Z);
}

/// <summary>
/// A world transform: a <see cref="B3Pos"/> translation with a float rotation.
/// Distinct from <see cref="B3Transform"/> for the reason on <see cref="B3Pos"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct B3WorldTransform
{
    public B3Pos P;
    public B3Quat Q;
}

/// <summary>An opaque handle to a physics world.</summary>
/// <remarks>
/// Note the <b>16-bit</b> fields: this struct is four bytes and two-byte
/// aligned, unlike the body and shape ids beside it. Passed by value on nearly
/// every call, so a wrong layout here is wrong on every call.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3WorldId
{
    public ushort Index1;
    public ushort Generation;
}

/// <summary>An opaque handle to a body.</summary>
/// <remarks>
/// <c>index1</c> is one-based (hence the name): a zeroed struct is the null
/// handle, which is what makes <c>default</c> mean "no body" without a separate
/// flag.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct B3BodyId
{
    public int Index1;
    public ushort World0;
    public ushort Generation;
}

/// <summary>An opaque handle to a shape.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct B3ShapeId
{
    public int Index1;
    public ushort World0;
    public ushort Generation;
}

/// <summary>An axis-aligned bounding box in Box3D's layout.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct B3Aabb
{
    public B3Vec3 LowerBound;
    public B3Vec3 UpperBound;
}
