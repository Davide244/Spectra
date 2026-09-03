using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// A sound played through a QUEUE of small buffers, refilled as the driver
/// finishes with them. The path for anything long, and the path for anything
/// that loops at all.
/// </summary>
/// <remarks>
/// <para><b>Loops live here, and that is the point of the class.</b>
/// <c>AL_LOOPING</c> repeats a whole buffer and so can express exactly one loop
/// region, the entire sound; music with an intro and ambience with a pickup bar
/// both need a region strictly inside the asset. So the engine never sets the
/// flag: <see cref="AudioLoopCursor"/> decides, in SAMPLE FRAMES, which runs of
/// source frames go into the next buffer, and the wrap is arithmetic rather
/// than a driver feature. A loop shorter than one buffer, a loop whose length
/// is not a multiple of the buffer size and a seek into the middle of a loop are
/// then all the same case, which is exactly why they work.</para>
/// <para><b>An underrun is not a finish, and telling them apart is the one
/// thing this pump must not get wrong.</b> A source whose queue emptied because
/// the pump was late reports <see cref="AudioSourceState.Stopped"/>, precisely
/// as one that played its last buffer does. Reading the state alone would end
/// the music the first time a frame hitched, permanently, with nothing logged.
/// The queue depth is the discriminator: buffers still queued means starved,
/// and the answer is to start it again and count it.</para>
/// <para><b>Buffers are owned by the voice, not by the pool.</b> A source is
/// scarce and shared; a buffer is cheap and belongs to whatever is streaming
/// through it, and handing back a source with a stranger's buffers still queued
/// is how a footstep inherits the tail of a music track.</para>
/// <para>Render thread only.</para>
/// </remarks>
public sealed class StreamingVoice : AudioVoice
{
    /// <summary>
    /// Buffers in flight. Four is one being played and three ahead of it, which
    /// at the default size is over half a second of slack: enough to ride out a
    /// static-world recompile or a gen0 pause without starving, and small enough
    /// that a stop is not audibly late.
    /// </summary>
    public const int DefaultBufferCount = 4;

    /// <summary>
    /// Frames per buffer. About 170 ms at 48 kHz, which is the trade the whole
    /// design turns on: bigger buffers survive longer stalls and make a seek
    /// land later, smaller ones do the opposite.
    /// </summary>
    public const int DefaultBufferFrames = 8192;

    // A loop shorter than one buffer produces one run per repetition, so the
    // scratch bounds how many the planner may emit. Hitting it is not an error:
    // the fill is simply shorter, which for a very short loop is the right
    // answer anyway.
    private const int MaxRunsPerBuffer = 16;

    private readonly IAudioSampleProvider _provider;
    private readonly AudioBufferFormat _bufferFormat;
    private readonly AudioFormat _format;
    private readonly uint[] _buffers;
    private readonly Queue<uint> _idle;
    private readonly short[] _scratch;
    private readonly int _bufferFrames;

    private AudioLoopCursor _cursor;

    internal StreamingVoice(
        IAudioBackend backend,
        uint source,
        IAudioSampleProvider provider,
        AudioSourceSettings settings,
        int bufferCount = DefaultBufferCount,
        int bufferFrames = DefaultBufferFrames)
        : base(backend, source, settings)
    {
        if (bufferCount < 2)
            throw new ArgumentOutOfRangeException(nameof(bufferCount), bufferCount, "A queue needs at least one buffer ahead of the one playing.");
        if (bufferFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferFrames), bufferFrames, "A buffer must hold at least one frame.");

        _provider = provider;
        _format = provider.Format;
        _bufferFormat = _format.Channels == 1 ? AudioBufferFormat.Mono16 : AudioBufferFormat.Stereo16;
        _bufferFrames = bufferFrames;
        _cursor = new AudioLoopCursor(provider.FrameCount, provider.Loop);
        _scratch = new short[(long)bufferFrames * _format.Channels];

        _buffers = new uint[bufferCount];
        _idle = new Queue<uint>(bufferCount);
        for (int i = 0; i < bufferCount; i++)
        {
            _buffers[i] = backend.CreateBuffer();
            _idle.Enqueue(_buffers[i]);
        }

        backend.ConfigureSource(source, in settings);

        // Detach anything a previous tenant of this pooled source left bound:
        // AL refuses a queue operation on a source still holding a static
        // buffer, so without this a reused source accepts nothing and plays
        // silence with no error the engine ever sees.
        backend.SetSourceBuffer(source, 0);

        Prime();
    }

    /// <summary>Frames the driver has been given, which is ahead of what it has played.</summary>
    public long PositionFrames => _cursor.Position;

    /// <summary>The region being repeated, or <see cref="LoopRegion.None"/>.</summary>
    public LoopRegion Loop => _cursor.Loop;

    /// <summary>
    /// Times the queue ran dry and had to be restarted. Non-zero means the pump
    /// was late, which is a frame-time problem rather than an audio one, so it
    /// is counted here and diagnosed elsewhere.
    /// </summary>
    public int UnderrunCount { get; private set; }

    /// <summary>
    /// Jumps to <paramref name="frame"/> and refills from there. A seek into
    /// the middle of a loop keeps looping from where it landed; a seek past the
    /// loop end plays the tail and finishes.
    /// </summary>
    public void Seek(long frame)
    {
        if (IsFinished) return;

        // Stopping first is what makes every queued buffer processed, which is
        // the only state AL allows them to be unqueued in. Skipping it leaves
        // the buffers the seek is meant to discard still playing.
        Backend.Stop(Source);
        DrainQueue();

        _cursor.Seek(frame);
        Prime();
    }

    internal override bool Update()
    {
        if (IsFinished) return false;

        int processed = Backend.GetBuffersProcessed(Source);
        for (int i = 0; i < processed; i++)
        {
            uint buffer = Backend.UnqueueBuffer(Source);
            if (buffer == 0) break;
            _idle.Enqueue(buffer);
        }

        RefillQueue();

        int queued = Backend.GetBuffersQueued(Source);
        if (queued == 0)
        {
            // Nothing queued and nothing left to plan: the sound genuinely ended.
            IsFinished = true;
            return false;
        }

        // Paused is deliberately excluded rather than lumped in with stopped: a
        // paused voice is holding an offset somebody means to resume from, and
        // restarting it here would make pausing music impossible to implement
        // later without finding this line.
        AudioSourceState state = Backend.GetSourceState(Source);
        if (state is not (AudioSourceState.Playing or AudioSourceState.Paused))
        {
            // Queued buffers plus a stopped source is a starved queue, never a
            // finished one. Restarting is the whole fix; not doing it is how
            // music stops for good after one hitch.
            UnderrunCount++;
            Backend.Play(Source);
        }

        return true;
    }

    internal override void Detach()
    {
        DrainQueue();
        for (int i = 0; i < _buffers.Length; i++)
            Backend.DestroyBuffer(_buffers[i]);

        _idle.Clear();
        base.Detach();
    }

    private void Prime()
    {
        RefillQueue();
        if (Backend.GetBuffersQueued(Source) > 0)
            Backend.Play(Source);
        else
            IsFinished = true;
    }

    private void RefillQueue()
    {
        while (_idle.Count > 0)
        {
            uint buffer = _idle.Peek();
            if (Fill(buffer) <= 0) break;

            _idle.Dequeue();
            Backend.QueueBuffer(Source, buffer);
        }
    }

    // Unqueues everything the source is holding, which is legal only once the
    // source is stopped. Used by a seek and by teardown.
    private void DrainQueue()
    {
        int queued = Backend.GetBuffersQueued(Source);
        for (int i = 0; i < queued; i++)
        {
            uint buffer = Backend.UnqueueBuffer(Source);
            if (buffer == 0) break;
            _idle.Enqueue(buffer);
        }

        Backend.SetSourceBuffer(Source, 0);
    }

    /// <summary>Plans the next buffer's worth of frames, gathers them, and uploads. Returns frames written.</summary>
    private int Fill(uint buffer)
    {
        Span<AudioSegment> runs = stackalloc AudioSegment[MaxRunsPerBuffer];
        int count = _cursor.Plan(runs, _bufferFrames, out long planned);
        if (count == 0 || planned <= 0) return 0;

        int channels = _format.Channels;
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            int wanted = (int)runs[i].Count;
            int read = _provider.ReadFrames(runs[i].Offset, _scratch.AsSpan(written * channels), wanted);
            written += read;

            // A provider that returned less than its own FrameCount promised is
            // at the end of what it actually has. Uploading the short buffer is
            // right; carrying on would gather the next run at the wrong offset
            // in the scratch and splice a gap into the sound.
            if (read < wanted) break;
        }

        if (written <= 0) return 0;

        Backend.UploadBuffer(buffer, _bufferFormat, _scratch.AsSpan(0, written * channels), _format.SampleRate);
        return written;
    }
}
