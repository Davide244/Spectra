using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Core.Animation;

/// <summary>One joint of a <see cref="Skeleton"/>.</summary>
public readonly struct SkeletonBone
{
    /// <summary>The authored joint name — how an importer and a clip address this bone.</summary>
    public required string Name { get; init; }

    /// <summary>Index of the parent bone, or −1 for a root. Always LESS than this bone's own index.</summary>
    public required int ParentIndex { get; init; }

    /// <summary>The rest pose, in parent-bone space. What an unanimated bone wears.</summary>
    public required Transform LocalBind { get; init; }

    /// <summary>
    /// Mesh space → this bone's space, at the bind pose. Assimp calls it the
    /// offset matrix.
    /// </summary>
    /// <remarks>
    /// It is stored rather than derived because deriving it means inverting the
    /// bind model matrix, and a bind pose with any non-uniform scale in it makes
    /// that inversion lossy in exactly the joints where it shows.
    /// </remarks>
    public required Matrix4x4 InverseBindPose { get; init; }
}

/// <summary>
/// A joint hierarchy, flattened into one array in topological order.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the whole design, and it is enforced rather than assumed.</b>
/// A parent always sits at a lower index than its children, so composing local
/// transforms into model space is one forward pass over an array — no recursion,
/// no visited set, no pointer chasing, and every bone's parent is already
/// finished by the time it is read. The constructor refuses any other ordering,
/// because a skeleton that violates it produces a pose that is subtly wrong in
/// one limb rather than obviously wrong everywhere, and importers are perfectly
/// capable of handing over an arbitrary order.
/// </para>
/// <para>
/// <b>It is immutable and shareable.</b> One skeleton backs every instance of a
/// character; what differs per instance is the <see cref="SkeletonPose"/>.
/// </para>
/// </remarks>
public sealed class Skeleton
{
    private readonly SkeletonBone[] _bones;
    private readonly Dictionary<string, int> _byName;

    /// <summary>
    /// Builds a skeleton from bones already in topological order.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The list is empty, a parent index is out of range or not less than its
    /// child's, or two bones share a name.
    /// </exception>
    public Skeleton(IReadOnlyList<SkeletonBone> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        if (bones.Count == 0)
            throw new ArgumentException("A skeleton needs at least one bone.", nameof(bones));

        _bones = new SkeletonBone[bones.Count];
        _byName = new Dictionary<string, int>(bones.Count, StringComparer.Ordinal);

        for (int i = 0; i < bones.Count; i++)
        {
            SkeletonBone bone = bones[i];

            if (bone.ParentIndex >= i)
            {
                throw new ArgumentException(
                    $"Bone {i} ('{bone.Name}') names parent {bone.ParentIndex}, which is not before it. " +
                    "Bones must be in topological order — see the type remarks.",
                    nameof(bones));
            }

            if (bone.ParentIndex < -1)
            {
                throw new ArgumentException(
                    $"Bone {i} ('{bone.Name}') has parent index {bone.ParentIndex}; use −1 for a root.",
                    nameof(bones));
            }

            if (string.IsNullOrEmpty(bone.Name))
                throw new ArgumentException($"Bone {i} has no name; clips address bones by name.", nameof(bones));

            // Duplicate names are refused rather than resolved to the first
            // match: a clip binds to bones BY NAME, so an ambiguous name means a
            // channel silently drives whichever joint the importer happened to
            // emit first, which reads as one limb animating and its twin not.
            if (!_byName.TryAdd(bone.Name, i))
            {
                throw new ArgumentException(
                    $"Two bones are named '{bone.Name}' (indices {_byName[bone.Name]} and {i}); " +
                    "clips address bones by name, so names must be unique.",
                    nameof(bones));
            }

            _bones[i] = bone;
        }
    }

    public int BoneCount => _bones.Length;

    public ReadOnlySpan<SkeletonBone> Bones => _bones;

    /// <summary>Resolves an authored joint name to its bone index.</summary>
    public bool TryGetBoneIndex(string name, out int index) => _byName.TryGetValue(name, out index);

    /// <summary>Copies the rest pose into <paramref name="destination"/>.</summary>
    public void CopyBindPose(Span<Transform> destination)
    {
        if (destination.Length < _bones.Length)
            throw new ArgumentException($"Need room for {_bones.Length} bones.", nameof(destination));

        for (int i = 0; i < _bones.Length; i++)
            destination[i] = _bones[i].LocalBind;
    }
}
