using System;

namespace SpectraEngine.Core.Audio;

/// <summary>One contiguous run of source frames: Count frames starting at Offset, both in sample frames.</summary>
public readonly record struct AudioSegment(long Offset, long Count);

/// <summary>
/// The whole of the engine's loop-point implementation: a play position in
/// SAMPLE FRAMES that knows where the loop is, and turns "give me N more
/// frames" into the runs of source frames that answer it.
/// </summary>
/// <remarks>
/// <para><b>This exists because <c>AL_LOOPING</c> cannot express a loop region
/// inside a buffer.</b> OpenAL's flag repeats the entire buffer, so a sound
/// with an intro, a pickup bar or a spin-up has nowhere to put its loop points
/// and either restarts from its intro forever or does not loop at all. The
/// engine never sets that flag. A looping sound is played through a buffer
/// QUEUE instead, and this cursor is the arithmetic that decides what goes
/// into the next buffer. The failure it prevents is silent until somebody
/// authors the first sound with an intro, at which point every looping asset
/// in the project is wrong at once.</para>
/// <para><b>A buffer is not a loop iteration.</b> The two are independent
/// lengths, and every awkward case follows from that: a loop shorter than one
/// buffer repeats several times inside one fill, a loop whose length is not a
/// multiple of the buffer size crosses the loop point part-way through a fill,
/// and a seek can land anywhere. So a fill is a LIST of runs, not one run, and
/// the caller concatenates them into the buffer it is about to upload.</para>
/// <para><b>The intro is not a special case.</b> Starting at frame 0 with a
/// loop at <c>[s, e)</c>, the first fill reads straight through <c>s</c> to
/// <c>e</c> in one run, because the intro and the first pass through the loop
/// body are contiguous in the source. Only the wrap is a boundary. Treating
/// the intro as its own phase is how an extra buffer split, and a click at
/// <c>s</c>, gets introduced for nothing.</para>
/// <para>Pure and struct-valued: no device, no allocation, no AL call, so the
/// arithmetic is testable on a machine with no sound card, which is every CI
/// machine.</para>
/// </remarks>
public struct AudioLoopCursor
{
    private readonly long _totalFrames;
    private readonly LoopRegion _loop;
    private long _position;
    private bool _exhausted;

    /// <param name="totalFrames">Decoded length of the whole sound, in sample frames.</param>
    /// <param name="loop">The repeated region, or <see cref="LoopRegion.None"/>.</param>
    public AudioLoopCursor(long totalFrames, LoopRegion loop)
    {
        if (totalFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(totalFrames), totalFrames, "A sound cannot have negative length.");
        if (loop.IsLooping && loop.EndFrame > totalFrames)
            throw new ArgumentOutOfRangeException(nameof(loop), loop, "A loop cannot end past the sound.");

        _totalFrames = totalFrames;
        _loop = loop;
        _position = 0;
        _exhausted = totalFrames == 0;
    }

    /// <summary>Frame the next fill reads from.</summary>
    public readonly long Position => _position;

    /// <summary>Total decoded frames in the sound.</summary>
    public readonly long TotalFrames => _totalFrames;

    /// <summary>The region being repeated, or <see cref="LoopRegion.None"/>.</summary>
    public readonly LoopRegion Loop => _loop;

    /// <summary>
    /// True once a non-looping sound has planned its last frame. A looping
    /// sound reaches this only after a <see cref="Seek"/> past its loop, into
    /// the tail (see <see cref="Plan"/>).
    /// </summary>
    public readonly bool IsExhausted => _exhausted;

    /// <summary>
    /// Plans the next <paramref name="requestedFrames"/> frames as runs of
    /// source frames written into <paramref name="segments"/>, and advances the
    /// position past them.
    /// </summary>
    /// <param name="segments">
    /// Scratch the caller owns. Planning stops when it is full, so a short span
    /// is a smaller fill and never a wrong one: <paramref name="plannedFrames"/>
    /// reports what was actually planned. This is the bound that stops a
    /// one-frame loop from asking for an unbounded number of runs.
    /// </param>
    /// <param name="requestedFrames">Frames the caller has room for.</param>
    /// <param name="plannedFrames">Frames actually covered by the returned runs.</param>
    /// <returns>How many entries of <paramref name="segments"/> were written.</returns>
    public int Plan(Span<AudioSegment> segments, long requestedFrames, out long plannedFrames)
    {
        if (requestedFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedFrames), requestedFrames, "Cannot plan a negative fill.");

        plannedFrames = 0;
        int count = 0;
        long remaining = requestedFrames;

        while (remaining > 0 && count < segments.Length && !_exhausted)
        {
            // The limit is the loop's end while the position is still short of
            // it, and the end of the sound otherwise. A position PAST the loop
            // end is reachable only by a seek into the tail, and reading to the
            // end of the sound is the right answer there: somebody who scrubbed
            // past the loop asked to hear the outro, not to be teleported back
            // into the body.
            long limit = _loop.IsLooping && _position < _loop.EndFrame ? _loop.EndFrame : _totalFrames;
            long available = limit - _position;
            if (available <= 0)
            {
                // Only reachable at the exact end of a non-looping sound, or if
                // a seek landed on the last frame. Nothing left to plan.
                _exhausted = true;
                break;
            }

            long take = Math.Min(remaining, available);
            segments[count++] = new AudioSegment(_position, take);
            _position += take;
            plannedFrames += take;
            remaining -= take;

            // Wrap only when the run was bounded BY the loop end. Testing the
            // position against the loop end alone would also wrap a run that
            // was bounded by the end of the sound after a seek into the tail,
            // which would make a scrub past the loop jump backwards instead of
            // playing the outro.
            if (limit == _loop.EndFrame && _loop.IsLooping && _position >= _loop.EndFrame)
                _position = _loop.StartFrame;
            else if (_position >= _totalFrames)
                _exhausted = true;
        }

        return count;
    }

    /// <summary>
    /// Moves the play position to <paramref name="frame"/>. A seek into the
    /// middle of a loop is ordinary and keeps looping from there; a seek past
    /// the loop end plays the tail and finishes.
    /// </summary>
    /// <remarks>
    /// Clamped at the end of the sound rather than refused, because scrubbing
    /// to "the end" is a real gesture; a negative frame is a caller bug and
    /// throws, since silently clamping it to zero would hide a sign error in
    /// whatever computed it.
    /// </remarks>
    public void Seek(long frame)
    {
        if (frame < 0)
            throw new ArgumentOutOfRangeException(nameof(frame), frame, "Cannot seek before the start of a sound.");

        _position = Math.Min(frame, _totalFrames);
        _exhausted = _position >= _totalFrames;
    }

    /// <summary>Returns the cursor to frame 0, ready to play the sound again from its intro.</summary>
    public void Rewind()
    {
        _position = 0;
        _exhausted = _totalFrames == 0;
    }
}
