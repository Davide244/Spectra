using System;
using System.Numerics;
using SpectraEngine.Core.Assets;

namespace SpectraEngine.Core.Bsp;

/// <summary>
/// The per-face surface payload every <see cref="Polygon"/> carries alongside
/// its geometric <see cref="Polygon.Surface"/> plane: which material the face
/// wears, and the HAMMER-style texture axes that turn a world position into a
/// UV coordinate.
/// </summary>
/// <remarks>
/// <para><b>Pure value, no assets.</b> Everything here is a float or an interned
/// <see cref="MaterialRef"/> id — no GPU handles, no <c>Material</c> object, no
/// asset-manager state. That is deliberate: the payload rides through the whole
/// background CSG compile (carve, snap, weld, BSP, mesh) and only becomes a real
/// material at mesh-upload time on the render thread. A struct this small also
/// costs nothing to copy through the five polygon-construction sites that must
/// propagate it.</para>
///
/// <para><b>THE UV CONVENTION</b> — for a world-space vertex <c>p</c>:
/// <code>
///   u = dot(p, UAxis) / UScale + UOffset
///   v = dot(p, VAxis) / VScale + VOffset
/// </code>
/// <list type="bullet">
/// <item><description><c>UAxis</c>/<c>VAxis</c> are unit-length directions in
/// WORLD space (see the texture-lock note below for how they get there). They
/// need not be orthogonal to each other, and need not lie in the face plane —
/// Hammer's world-aligned projection deliberately does not.</description></item>
/// <item><description><c>UScale</c>/<c>VScale</c> are <b>world units per texture
/// repeat</b>: the projected distance the surface must span for the texture to
/// tile once. Larger scale = the texture is stretched over more world. A scale
/// of 1 therefore means "one repeat per world unit", which is exactly the
/// engine's historical projection. (Hammer calls the same number "texture
/// scale" and expresses it in units per texel; the difference is only which
/// constant the artist folds in, and this engine has no texel size at compile
/// time — the texture is not even loaded yet.)</description></item>
/// <item><description><c>UOffset</c>/<c>VOffset</c> shift the result in texture
/// repeats (not world units — they are added after the division), so an offset
/// of 0.5 slides the texture by half a tile whatever the scale.</description></item>
/// <item><description>Rotation is <b>not</b> stored separately: a rotation of
/// the texture within the face plane is expressed by rotating
/// <c>UAxis</c>/<c>VAxis</c> about the face normal. One representation, no
/// ordering ambiguity between "rotate then scale" and "scale then rotate", and
/// nothing to keep in sync when <see cref="Transformed"/> moves the
/// face.</description></item>
/// </list>
/// </para>
///
/// <para><b>WORLD-ALIGNED (default) FACES.</b> A payload whose axes are the zero
/// vector is <em>world-aligned</em>: the axes are not stored at all but derived
/// at UV-generation time from the face's own world-space normal, by the
/// dominant-axis rule in <see cref="ResolveAxes"/>. This is Hammer's "World
/// Alignment" and it is the default for every brush face, which has two
/// consequences worth being explicit about:
/// <list type="number">
/// <item><description>Default brushes keep the exact projection the engine used
/// before per-face surfaces existed — same axes, same scale of 1, same zero
/// offsets — so no existing geometry's UVs move.</description></item>
/// <item><description>A world-aligned face stays world-aligned through
/// <see cref="Transformed"/>. Rotating a brush therefore re-derives the
/// projection from the rotated normal instead of dragging the old axes along:
/// the texture stays glued to the world grid, not to the brush. That is the
/// documented semantic, and it is what Hammer does for world-aligned
/// faces.</description></item>
/// </list>
/// </para>
///
/// <para><b>TEXTURE LOCK for explicit axes.</b> When the axes ARE stored,
/// <see cref="Transformed"/> implements full texture lock for a rigid transform:
/// the axes rotate with the brush, and the translation is folded into the
/// offsets so that the UV of a point is invariant under the move. Drag or spin a
/// brush and its texture travels with the surface, pattern and alignment intact.
/// (The offset correction is exact — see the derivation in
/// <see cref="Transformed"/>.)</para>
///
/// <para><b>Equality</b> is component-wise over all seven fields, so two faces
/// compare equal exactly when they would produce the same material and the same
/// UVs. Nothing in the CSG caches keys on a polygon's payload — reuse is keyed
/// on <see cref="Brush"/> reference identity, and brushes are immutable — but
/// having the value type compare honestly keeps tests and future tooling from
/// being surprised.</para>
/// </remarks>
public readonly struct FaceSurface : IEquatable<FaceSurface>
{
    /// <summary>
    /// The default payload: engine default material, world-aligned axes, unit
    /// scale, zero offsets. Reproduces the engine's pre-per-face projection
    /// exactly.
    /// </summary>
    public static readonly FaceSurface Default = new(MaterialRef.Default);

    /// <summary>
    /// A world-aligned face wearing <paramref name="material"/>: axes derived
    /// from the face normal at UV time, scale 1, no offset.
    /// </summary>
    public FaceSurface(MaterialRef material)
        : this(material, Vector3.Zero, Vector3.Zero, 0f, 0f, 1f, 1f)
    {
    }

    /// <summary>
    /// A face with explicit texture axes (see the type remarks for the exact
    /// convention). Passing zero axes yields a world-aligned face, the same as
    /// <see cref="FaceSurface(MaterialRef)"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A scale is zero or not finite — that would make the UV of every vertex on
    /// the face infinite or NaN, which is a content bug worth catching at the
    /// assignment rather than in a vertex buffer.
    /// </exception>
    public FaceSurface(
        MaterialRef material,
        Vector3 uAxis,
        Vector3 vAxis,
        float uOffset,
        float vOffset,
        float uScale,
        float vScale)
    {
        ThrowIfUnusableScale(uScale, nameof(uScale));
        ThrowIfUnusableScale(vScale, nameof(vScale));

        Material = material;
        UAxis = uAxis;
        VAxis = vAxis;
        UOffset = uOffset;
        VOffset = vOffset;
        UScale = uScale;
        VScale = vScale;
    }

    /// <summary>The material this face wears, as an interned id (never an asset object).</summary>
    public MaterialRef Material { get; }

    /// <summary>
    /// World-space direction the U coordinate runs along. The zero vector means
    /// world-aligned — see the type remarks.
    /// </summary>
    public Vector3 UAxis { get; }

    /// <summary>
    /// World-space direction the V coordinate runs along. The zero vector means
    /// world-aligned — see the type remarks.
    /// </summary>
    public Vector3 VAxis { get; }

    /// <summary>U shift, in texture repeats (added after the scale division).</summary>
    public float UOffset { get; }

    /// <summary>V shift, in texture repeats (added after the scale division).</summary>
    public float VOffset { get; }

    /// <summary>World units per texture repeat along <see cref="UAxis"/>.</summary>
    public float UScale { get; }

    /// <summary>World units per texture repeat along <see cref="VAxis"/>.</summary>
    public float VScale { get; }

    /// <summary>
    /// True when the axes are derived from the face normal at UV time rather
    /// than stored (Hammer's world alignment). Either axis being degenerate is
    /// enough — a half-specified axis pair has no meaningful projection, and
    /// falling back beats emitting NaNs.
    /// </summary>
    public bool IsWorldAligned =>
        UAxis == Vector3.Zero || VAxis == Vector3.Zero;

    /// <summary>Returns this payload with a different material and the same texture mapping.</summary>
    public FaceSurface WithMaterial(MaterialRef material) =>
        new(material, UAxis, VAxis, UOffset, VOffset, EffectiveScale(UScale), EffectiveScale(VScale));

    /// <summary>
    /// Returns this payload with different texture axes and the same material.
    /// Pass zero axes to go back to world alignment.
    /// </summary>
    public FaceSurface WithAxes(Vector3 uAxis, Vector3 vAxis, float uOffset, float vOffset, float uScale, float vScale) =>
        new(Material, uAxis, vAxis, uOffset, vOffset, uScale, vScale);

    /// <summary>
    /// Maps the payload through a rigid <paramref name="transform"/> — the
    /// brush-local-to-world step every carved fragment takes in
    /// <see cref="Polygon.Transformed"/>.
    /// </summary>
    /// <remarks>
    /// <para>A world-aligned payload passes through untouched: its axes are
    /// re-derived from the transformed normal, which is precisely what keeps the
    /// projection locked to the world grid (and keeps default brushes
    /// bit-identical to the pre-payload engine, at any placement).</para>
    /// <para>Explicit axes are TEXTURE-LOCKED. Writing the transform as
    /// <c>p' = R·p + t</c> with <c>R</c> a rotation, the new axis is
    /// <c>U' = R·U</c> and the new offset is <c>o' = o − dot(t, U')/s</c>.
    /// Then
    /// <c>dot(p', U')/s + o' = (dot(R·p, R·U) + dot(t, R·U))/s + o − dot(t,U')/s
    /// = dot(p, U)/s + o</c> — the moved point gets exactly the UV the original
    /// point had, for any rigid placement. Scales are untouched, so the texture
    /// neither stretches nor shears.</para>
    /// <para>Non-rigid input (a scale or shear baked into the matrix) is out of
    /// contract everywhere in the brush pipeline — scene snapshots reject it
    /// before a carve — so this deliberately does not renormalize: doing so
    /// would silently paper over a transform the rest of the CSG code would
    /// still get wrong.</para>
    /// </remarks>
    public FaceSurface Transformed(in Matrix4x4 transform)
    {
        if (IsWorldAligned)
            return this;

        Vector3 u = Vector3.TransformNormal(UAxis, transform);
        Vector3 v = Vector3.TransformNormal(VAxis, transform);
        Vector3 translation = transform.Translation;

        float uScale = EffectiveScale(UScale);
        float vScale = EffectiveScale(VScale);
        return new FaceSurface(
            Material,
            u, v,
            UOffset - Vector3.Dot(translation, u) / uScale,
            VOffset - Vector3.Dot(translation, v) / vScale,
            uScale, vScale);
    }

    /// <summary>
    /// Produces the axes and scales UV generation should actually use for a face
    /// whose world-space normal is <paramref name="faceNormal"/>: the stored
    /// ones, or — for a world-aligned face — Hammer's world projection onto the
    /// two axes orthogonal to the dominant normal component.
    /// </summary>
    /// <remarks>
    /// The dominant-axis mapping (X-facing → u along +Z, v along +Y; Y-facing →
    /// u along +X, v along +Z; Z-facing → u along +X, v along +Y) with the
    /// <c>&gt;=</c> tie-breaks reproduces, component for component, the planar
    /// projection <c>CsgWorld.BuildMeshArrays</c> hard-coded before per-face
    /// surfaces existed. That is what makes the default path provably unchanged;
    /// the pinning test in <c>FaceSurfaceTests</c> exists to keep it that way.
    /// </remarks>
    public void ResolveAxes(Vector3 faceNormal, out Vector3 uAxis, out Vector3 vAxis, out float uScale, out float vScale)
    {
        uScale = EffectiveScale(UScale);
        vScale = EffectiveScale(VScale);

        if (!IsWorldAligned)
        {
            uAxis = UAxis;
            vAxis = VAxis;
            return;
        }

        float ax = MathF.Abs(faceNormal.X), ay = MathF.Abs(faceNormal.Y), az = MathF.Abs(faceNormal.Z);
        if (ax >= ay && ax >= az)
        {
            uAxis = Vector3.UnitZ;
            vAxis = Vector3.UnitY;
        }
        else if (ay >= az)
        {
            uAxis = Vector3.UnitX;
            vAxis = Vector3.UnitZ;
        }
        else
        {
            uAxis = Vector3.UnitX;
            vAxis = Vector3.UnitY;
        }
    }

    /// <summary>
    /// The UV of one world-space point on a face with the given world-space
    /// normal, by the convention documented on this type. Convenience for tools
    /// and tests — the mesh builder inlines the same arithmetic so it can hoist
    /// the axis resolution out of its per-vertex loop.
    /// </summary>
    public Vector2 ComputeUv(Vector3 worldPosition, Vector3 faceNormal)
    {
        ResolveAxes(faceNormal, out Vector3 u, out Vector3 v, out float uScale, out float vScale);
        return new Vector2(
            Vector3.Dot(worldPosition, u) / uScale + UOffset,
            Vector3.Dot(worldPosition, v) / vScale + VOffset);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Scales are compared in their effective form, so the all-zeros
    /// <c>default(FaceSurface)</c> and the explicit <see cref="Default"/> —
    /// which describe the same projection — compare equal.
    /// </remarks>
    public bool Equals(FaceSurface other) =>
        Material == other.Material &&
        UAxis == other.UAxis &&
        VAxis == other.VAxis &&
        UOffset == other.UOffset &&
        VOffset == other.VOffset &&
        EffectiveScale(UScale) == EffectiveScale(other.UScale) &&
        EffectiveScale(VScale) == EffectiveScale(other.VScale);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FaceSurface other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Material.Id);
        hash.Add(UAxis);
        hash.Add(VAxis);
        hash.Add(UOffset);
        hash.Add(VOffset);
        hash.Add(EffectiveScale(UScale));
        hash.Add(EffectiveScale(VScale));
        return hash.ToHashCode();
    }

    /// <summary>Component-wise equality (see <see cref="Equals(FaceSurface)"/>).</summary>
    public static bool operator ==(FaceSurface left, FaceSurface right) => left.Equals(right);

    /// <summary>Component-wise inequality.</summary>
    public static bool operator !=(FaceSurface left, FaceSurface right) => !left.Equals(right);

    // `default(FaceSurface)` has zero scales, which would divide every UV to
    // infinity. The constructor rejects a zero scale, so the only way to reach
    // one is that all-zeros default — which is also world-aligned and therefore
    // means "the engine default projection", whose scale is 1. Normalizing here
    // keeps that single degenerate value safe without letting a genuinely bad
    // scale in through a constructor.
    private static float EffectiveScale(float scale) => scale != 0f ? scale : 1f;

    private static void ThrowIfUnusableScale(float scale, string paramName)
    {
        if (scale == 0f || !float.IsFinite(scale))
            throw new ArgumentOutOfRangeException(
                paramName, scale, "Texture scale must be a non-zero finite number (world units per texture repeat).");
    }
}
