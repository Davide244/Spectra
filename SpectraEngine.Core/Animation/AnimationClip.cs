using System;
using System.Collections.Generic;
using System.Numerics;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Core.Animation;

/// <summary>A vector keyframe: a time in seconds and the value at it.</summary>
public readonly record struct Vector3Key(float Time, Vector3 Value);

/// <summary>A rotation keyframe: a time in seconds and the value at it.</summary>
public readonly record struct QuaternionKey(float Time, Quaternion Value);

/// <summary>
/// One bone's animation: independent position, rotation and scale tracks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three tracks are separate and any of them may be empty</b>, which is
/// what every authoring tool exports and what makes additive and partial clips
/// possible at all. An empty track means "keep the bind value", never "use
/// zero" — the difference between a clip that animates only a character's arms
/// and a clip that collapses its legs to the origin.
/// </para>
/// <para>
/// Interpolation is linear for position and scale and spherical for rotation.
/// Nothing here resamples to a fixed rate: an exporter's own key times are kept,
/// so a clip authored at 24 fps and played at 144 fps is interpolated rather
/// than stepped.
/// </para>
/// </remarks>
public sealed class AnimationChannel
{
    private static readonly Vector3Key[] NoVectors = [];
    private static readonly QuaternionKey[] NoRotations = [];

    public AnimationChannel(
        int boneIndex,
        Vector3Key[]? positions = null,
        QuaternionKey[]? rotations = null,
        Vector3Key[]? scales = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(boneIndex);

        BoneIndex = boneIndex;
        Positions = positions ?? NoVectors;
        Rotations = rotations ?? NoRotations;
        Scales = scales ?? NoVectors;
    }

    /// <summary>The bone this channel drives, resolved against the skeleton at import time.</summary>
    /// <remarks>
    /// An index rather than a name, deliberately: sampling runs every frame for
    /// every bone, and a dictionary lookup per bone per frame is a cost paid
    /// forever to avoid one paid once.
    /// </remarks>
    public int BoneIndex { get; }

    public Vector3Key[] Positions { get; }

    public QuaternionKey[] Rotations { get; }

    public Vector3Key[] Scales { get; }

    /// <summary>
    /// The transform this channel produces at <paramref name="time"/>, falling
    /// back to <paramref name="bind"/> for any track it does not carry.
    /// </summary>
    public Transform SampleAt(float time, in Transform bind) => new()
    {
        Position = SampleVector(Positions, time, bind.Position),
        Rotation = SampleRotation(Rotations, time, bind.Rotation),
        Scale = SampleVector(Scales, time, bind.Scale),
    };

    private static Vector3 SampleVector(Vector3Key[] keys, float time, Vector3 fallback)
    {
        if (keys.Length == 0)
            return fallback;
        if (keys.Length == 1)
            return keys[0].Value;

        int i = FindSegment(keys, time);
        if (i < 0)
            return keys[0].Value;
        if (i >= keys.Length - 1)
            return keys[^1].Value;

        float t = SegmentFraction(keys[i].Time, keys[i + 1].Time, time);
        return Vector3.Lerp(keys[i].Value, keys[i + 1].Value, t);
    }

    private static Quaternion SampleRotation(QuaternionKey[] keys, float time, Quaternion fallback)
    {
        if (keys.Length == 0)
            return fallback;
        if (keys.Length == 1)
            return keys[0].Value;

        int i = FindSegment(keys, time);
        if (i < 0)
            return keys[0].Value;
        if (i >= keys.Length - 1)
            return keys[^1].Value;

        float t = SegmentFraction(keys[i].Time, keys[i + 1].Time, time);

        // Shortest path, explicitly. A quaternion and its negation are the same
        // orientation, so an exporter is free to emit either — and interpolating
        // between two that happen to be on opposite hemispheres takes the long
        // way round, which is the classic "the character's forearm spins a full
        // turn between two frames that look identical" bug.
        //
        // MEASURED: System.Numerics.Quaternion.Slerp already does this itself,
        // so today this line changes nothing — a mutation test that deletes it
        // passes. It stays because shortest-path is not a DOCUMENTED guarantee
        // of that method, only its current implementation, and the failure mode
        // if it ever changed is a limb whipping through a full turn. The tests
        // pin the behaviour; they cannot pin this line, and say so.
        Quaternion a = keys[i].Value;
        Quaternion b = keys[i + 1].Value;
        if (Quaternion.Dot(a, b) < 0f)
            b = -b;

        return Quaternion.Normalize(Quaternion.Slerp(a, b, t));
    }

    // The index of the key at or before `time`, or −1 when time precedes the
    // first key. Binary search rather than a scan: a long clip has thousands of
    // keys and this runs per bone per frame.
    //
    // Written twice rather than once over a key-time selector: a delegate here
    // is an indirect call per search step on the hottest path the animation
    // system has, to save nine lines.
    private static int FindSegment(Vector3Key[] keys, float time)
    {
        if (time < keys[0].Time)
            return -1;

        int low = 0;
        int high = keys.Length - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (keys[mid].Time <= time) low = mid;
            else high = mid - 1;
        }

        return low;
    }

    private static int FindSegment(QuaternionKey[] keys, float time)
    {
        if (time < keys[0].Time)
            return -1;

        int low = 0;
        int high = keys.Length - 1;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (keys[mid].Time <= time) low = mid;
            else high = mid - 1;
        }

        return low;
    }

    // Guarded against coincident key times, which authoring tools do emit and
    // which would otherwise be a divide by zero producing a NaN pose — a
    // character that vanishes rather than one that stutters.
    private static float SegmentFraction(float start, float end, float time)
    {
        float span = end - start;
        return span <= 1e-9f ? 0f : Math.Clamp((time - start) / span, 0f, 1f);
    }
}

/// <summary>
/// One animation: a duration, a loop flag, and the channels that drive bones.
/// </summary>
/// <remarks>
/// Immutable and shareable, like <see cref="Skeleton"/> — one clip serves every
/// character playing it, and what differs per instance is the play head.
/// </remarks>
public sealed class AnimationClip
{
    private readonly AnimationChannel[] _channels;

    public AnimationClip(string name, float duration, IReadOnlyList<AnimationChannel> channels, bool looping = true)
    {
        ArgumentNullException.ThrowIfNull(channels);

        if (!float.IsFinite(duration) || duration < 0f)
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be finite and non-negative.");

        Name = name ?? string.Empty;
        Duration = duration;
        Looping = looping;

        _channels = new AnimationChannel[channels.Count];
        for (int i = 0; i < channels.Count; i++)
            _channels[i] = channels[i] ?? throw new ArgumentException($"Channel {i} is null.", nameof(channels));
    }

    public string Name { get; }

    /// <summary>Length in SECONDS — never in an exporter's ticks.</summary>
    /// <remarks>
    /// The conversion belongs at import, once, because a tick rate that survives
    /// into the runtime is a unit that every consumer has to remember to divide
    /// by and one of them eventually will not.
    /// </remarks>
    public float Duration { get; }

    public bool Looping { get; }

    public ReadOnlySpan<AnimationChannel> Channels => _channels;

    /// <summary>
    /// Maps a play head onto the clip: wrapped when looping, clamped when not.
    /// </summary>
    /// <remarks>
    /// Negative times wrap correctly too (C#'s <c>%</c> keeps the sign of the
    /// dividend, so the naive form plays a looping clip backwards off its own
    /// start), which is what makes a rewind or a negative playback rate work.
    /// </remarks>
    public float NormalizeTime(float time)
    {
        if (Duration <= 0f)
            return 0f;

        if (!Looping)
            return Math.Clamp(time, 0f, Duration);

        float wrapped = time % Duration;
        return wrapped < 0f ? wrapped + Duration : wrapped;
    }
}
