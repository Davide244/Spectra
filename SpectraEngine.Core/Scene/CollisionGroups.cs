using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Scene;

/// <summary>
/// The named collision groups of one world, and the 64×64 matrix saying which
/// pairs of them interact.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sixty-four, not thirty-two.</b> Roblox caps at 32; the filter this maps
/// onto is a pair of <c>uint64</c> category and mask words, so 64 is what the
/// representation actually affords and there is no reason to ship a smaller
/// number than the hardware gives.
/// </para>
/// <para>
/// <b>The 65th group is a named error, never a silent drop.</b> Same rule the
/// node-identity seam applies to duplicate ids: running out of a fixed
/// resource is a thing the author must be told about at the moment it happens,
/// because the alternative is geometry that silently collides with everything.
/// </para>
/// <para>
/// <b>Group 0 is <c>Default</c> and cannot be removed or renamed.</b> Every
/// node starts in it, so a world that never mentions collision groups behaves
/// exactly as one without the feature — and the matrix starts all-true, so
/// adding a group changes nothing until a pair is explicitly disabled.
/// </para>
/// </remarks>
public sealed class CollisionGroups
{
    /// <summary>The maximum number of groups one world may name.</summary>
    public const int MaxGroups = 64;

    /// <summary>The id every node starts in.</summary>
    public const int DefaultGroup = 0;

    /// <summary>The reserved name of <see cref="DefaultGroup"/>.</summary>
    public const string DefaultGroupName = "Default";

    private readonly List<string> _names = [DefaultGroupName];
    private readonly Dictionary<string, int> _ids =
        new(StringComparer.Ordinal) { [DefaultGroupName] = DefaultGroup };

    // One mask word per group: bit j of _masks[i] means "group i collides with
    // group j". Kept symmetric by construction — every write touches both
    // halves — because a matrix that can disagree with itself is a bug with no
    // symptom until two objects pass through each other from one side only.
    private readonly ulong[] _masks = new ulong[MaxGroups];

    /// <summary>Creates a registry holding only <see cref="DefaultGroupName"/>, with everything colliding.</summary>
    public CollisionGroups()
    {
        for (int i = 0; i < MaxGroups; i++)
            _masks[i] = ulong.MaxValue;
    }

    /// <summary>How many groups are currently named, <see cref="DefaultGroupName"/> included.</summary>
    public int Count => _names.Count;

    /// <summary>The names in id order.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>
    /// Returns the id of <paramref name="name"/>, registering it if new.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// All <see cref="MaxGroups"/> ids are already taken.
    /// </exception>
    public int Register(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (_ids.TryGetValue(name, out int existing))
            return existing;

        if (_names.Count >= MaxGroups)
        {
            throw new InvalidOperationException(
                $"Cannot register collision group '{name}': all {MaxGroups} groups are taken " +
                $"({string.Join(", ", _names)}). Collision groups are a fixed 64-bit filter — " +
                "reuse an existing group or remove one that is no longer needed.");
        }

        int id = _names.Count;
        _names.Add(name);
        _ids[name] = id;
        return id;
    }

    /// <summary>Resolves a group name to its id, or false when it was never registered.</summary>
    public bool TryGetId(string name, out int id) => _ids.TryGetValue(name, out id);

    /// <summary>The name of <paramref name="id"/>.</summary>
    public string GetName(int id)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, _names.Count);
        return _names[id];
    }

    /// <summary>
    /// Sets whether two groups interact. Symmetric: setting (a, b) sets (b, a),
    /// so the matrix cannot disagree with itself.
    /// </summary>
    public void SetCollidable(int groupA, int groupB, bool collidable)
    {
        ValidateId(groupA, nameof(groupA));
        ValidateId(groupB, nameof(groupB));

        if (collidable)
        {
            _masks[groupA] |= 1UL << groupB;
            _masks[groupB] |= 1UL << groupA;
        }
        else
        {
            _masks[groupA] &= ~(1UL << groupB);
            _masks[groupB] &= ~(1UL << groupA);
        }
    }

    /// <summary>Whether two groups interact. Unregistered ids are out of range, not "false".</summary>
    public bool AreCollidable(int groupA, int groupB)
    {
        ValidateId(groupA, nameof(groupA));
        ValidateId(groupB, nameof(groupB));
        return (_masks[groupA] & (1UL << groupB)) != 0;
    }

    /// <summary>
    /// The raw mask word for <paramref name="group"/> — bit <c>j</c> set means
    /// it interacts with group <c>j</c>. This is the value a physics backend's
    /// filter consumes directly.
    /// </summary>
    public ulong GetMask(int group)
    {
        ValidateId(group, nameof(group));
        return _masks[group];
    }

    private void ValidateId(int id, string paramName)
    {
        if (id < 0 || id >= _names.Count)
        {
            throw new ArgumentOutOfRangeException(
                paramName, id,
                $"Collision group id must name a registered group (0..{_names.Count - 1}).");
        }
    }
}
