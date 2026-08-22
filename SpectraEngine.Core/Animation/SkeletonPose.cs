using System;
using System.Numerics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Core.Animation;

/// <summary>
/// One instance's posed skeleton: local transforms, the model-space matrices
/// they compose to, and the skinning matrices a vertex shader wants.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three arrays, one per stage, and the stages are separate on purpose.</b>
/// Local transforms are what animation writes and what blending operates on;
/// model matrices are what attachments and IK read (a weapon socket, a camera
/// bone, a hit box); skinning matrices are what the GPU consumes. Collapsing
/// them would make it impossible to ask "where is this character's hand" without
/// undoing the inverse bind pose, which is a question gameplay asks constantly.
/// </para>
/// <para>
/// <b>Everything here is allocation-free after construction.</b> The arrays are
/// sized once to the skeleton and rewritten in place, because this runs per
/// character per frame and a per-frame allocation per character is how an
/// animation system becomes the thing the profiler points at.
/// </para>
/// <para>
/// <b>Threading:</b> a pose belongs to whoever owns it. Sampling and blending
/// touch nothing shared — the skeleton and clips are immutable — so posing a
/// crowd across worker threads is safe as long as each pose object has one
/// writer. Uploading is not: that stays on the render thread like every other
/// GPU write in this engine.
/// </para>
/// </remarks>
public sealed class SkeletonPose
{
    private readonly Transform[] _local;
    private readonly Matrix4x4[] _model;
    private readonly Matrix4x4[] _skinning;

    public SkeletonPose(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);

        Skeleton = skeleton;
        _local = new Transform[skeleton.BoneCount];
        _model = new Matrix4x4[skeleton.BoneCount];
        _skinning = new Matrix4x4[skeleton.BoneCount];

        ResetToBind();
        BuildMatrices();
    }

    public Skeleton Skeleton { get; }

    public int BoneCount => _local.Length;

    /// <summary>
    /// Per-bone transforms in PARENT space — what animation writes and blending
    /// operates on. Mutable: a gameplay layer that wants to override one joint
    /// writes here and calls <see cref="BuildMatrices"/>.
    /// </summary>
    public Span<Transform> Local => _local;

    /// <summary>Per-bone bone-space → model-space matrices. Valid after <see cref="BuildMatrices"/>.</summary>
    /// <remarks>This is what a socket reads: the world matrix of a weapon bone is this times the character's own.</remarks>
    public ReadOnlySpan<Matrix4x4> Model => _model;

    /// <summary>Per-bone mesh-space → posed-model-space matrices — what a skinning shader consumes.</summary>
    public ReadOnlySpan<Matrix4x4> Skinning => _skinning;

    /// <summary>Puts every bone back on its rest pose.</summary>
    public void ResetToBind() => Skeleton.CopyBindPose(_local);

    /// <summary>
    /// Writes the clip's pose at <paramref name="time"/> into
    /// <see cref="Local"/>. Does not build matrices.
    /// </summary>
    /// <remarks>
    /// <b>Every bone is written, not just the animated ones.</b> Bones the clip
    /// has no channel for are reset to their bind pose rather than left alone,
    /// because leaving them would make the result depend on whatever was in the
    /// pose before — so playing clip A then clip B would differ from playing B
    /// cold, and the difference would show up as one limb carrying a previous
    /// animation's pose.
    /// </remarks>
    public void Sample(AnimationClip clip, float time)
    {
        ArgumentNullException.ThrowIfNull(clip);

        ResetToBind();

        float t = clip.NormalizeTime(time);
        ReadOnlySpan<AnimationChannel> channels = clip.Channels;
        ReadOnlySpan<SkeletonBone> bones = Skeleton.Bones;

        for (int i = 0; i < channels.Length; i++)
        {
            AnimationChannel channel = channels[i];
            int bone = channel.BoneIndex;

            // A clip authored against a different skeleton is a real situation
            // (retargeting, a mesh swapped under a rig), and it must not be an
            // index-out-of-range crash deep in a frame.
            if ((uint)bone >= (uint)_local.Length)
                continue;

            // Copied out rather than passed by reference straight from the
            // span: a property on a readonly struct is not a variable, so `in`
            // has nothing to point at.
            Transform bind = bones[bone].LocalBind;
            _local[bone] = channel.SampleAt(t, in bind);
        }
    }

    /// <summary>
    /// Blends two posed skeletons into a third: <paramref name="weight"/> 0 is
    /// all <paramref name="from"/>, 1 is all <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Blending happens on LOCAL transforms, never on matrices.</b> Lerping
    /// two model matrices shears and shrinks the mesh — the classic collapsing
    /// limb — because a matrix midpoint is not a rotation. Blending the
    /// components and recomposing keeps every intermediate a valid rigid
    /// transform, which is also why <see cref="Local"/> is the stage that
    /// gameplay overrides plug into.
    /// </para>
    /// <para>
    /// The destination may alias either source, so a crossfade can blend into
    /// the pose it is fading from without a third buffer.
    /// </para>
    /// </remarks>
    public static void Blend(SkeletonPose from, SkeletonPose to, float weight, SkeletonPose destination)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(destination);

        if (!ReferenceEquals(from.Skeleton, to.Skeleton) || !ReferenceEquals(from.Skeleton, destination.Skeleton))
            throw new ArgumentException("All three poses must share one skeleton.", nameof(destination));

        float w = Math.Clamp(weight, 0f, 1f);

        ReadOnlySpan<Transform> a = from._local;
        ReadOnlySpan<Transform> b = to._local;
        Span<Transform> result = destination._local;

        for (int i = 0; i < result.Length; i++)
        {
            Quaternion qa = a[i].Rotation;
            Quaternion qb = b[i].Rotation;

            // Same shortest-path rule as keyframe interpolation, and the same
            // measured caveat: Slerp already does it, this is insurance against
            // that being an implementation detail rather than a contract.
            if (Quaternion.Dot(qa, qb) < 0f)
                qb = -qb;

            result[i] = new Transform
            {
                Position = Vector3.Lerp(a[i].Position, b[i].Position, w),
                Rotation = Quaternion.Normalize(Quaternion.Slerp(qa, qb, w)),
                Scale = Vector3.Lerp(a[i].Scale, b[i].Scale, w),
            };
        }
    }

    /// <summary>
    /// Composes <see cref="Local"/> into <see cref="Model"/> and
    /// <see cref="Skinning"/>. One forward pass, no recursion.
    /// </summary>
    /// <remarks>
    /// <b>This is what the skeleton's topological-order invariant buys.</b> A
    /// parent is always at a lower index than its children, so by the time a
    /// bone is reached its parent's model matrix is already final and the whole
    /// hierarchy resolves in one linear sweep over three contiguous arrays.
    /// <para>
    /// The multiplication order is the engine's row-vector convention
    /// throughout: a point is <c>p · M</c>, so child-then-parent composes as
    /// <c>local · parentModel</c>, and a vertex reaches posed model space as
    /// <c>v · inverseBind · boneModel</c>. Getting either backwards produces a
    /// mesh that explodes on the first frame rather than one that is subtly
    /// wrong, which is the one mercy in this arithmetic.
    /// </para>
    /// </remarks>
    public void BuildMatrices()
    {
        ReadOnlySpan<SkeletonBone> bones = Skeleton.Bones;

        for (int i = 0; i < bones.Length; i++)
        {
            Matrix4x4 local = _local[i].Model;
            int parent = bones[i].ParentIndex;

            _model[i] = parent < 0 ? local : local * _model[parent];
            _skinning[i] = bones[i].InverseBindPose * _model[i];
        }
    }

    /// <summary>
    /// The model-space matrix of a named bone, for sockets and attachments.
    /// </summary>
    /// <remarks>
    /// A convenience over <see cref="Model"/> for code that has a name rather
    /// than an index. Anything doing this every frame should resolve the index
    /// once instead.
    /// </remarks>
    public bool TryGetBoneMatrix(string name, out Matrix4x4 matrix)
    {
        if (Skeleton.TryGetBoneIndex(name, out int index))
        {
            matrix = _model[index];
            return true;
        }

        matrix = Matrix4x4.Identity;
        return false;
    }
}
