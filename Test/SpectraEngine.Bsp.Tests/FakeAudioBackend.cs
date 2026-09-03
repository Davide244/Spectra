using SpectraEngine.Core.Audio;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// An <see cref="IAudioBackend"/> with no device behind it, modelling OpenAL's
/// source states and buffer queues closely enough to test the pool's reclaim
/// policy and the streaming voice's refill loop.
/// </summary>
/// <remarks>
/// It exists because real playback is a manual gate, not an automated test: CI
/// has no sound card, and a test that needs one is a test that gets disabled.
/// The behaviours modelled are the ones the engine's own code reads back and
/// acts on, and nothing else: a queued buffer becomes processed when the test
/// says it has been consumed, a source with an empty queue goes to
/// <see cref="AudioSourceState.Stopped"/> exactly as a starved one does, and
/// nothing here makes sound.
/// </remarks>
internal sealed class FakeAudioBackend : IAudioBackend
{
    private sealed class SourceRecord
    {
        public AudioSourceState State = AudioSourceState.Initial;
        public uint StaticBuffer;
        public readonly List<uint> Queue = [];

        /// <summary>Buffers at the head of the queue the driver has finished with.</summary>
        public int Processed;
    }

    private readonly Dictionary<uint, SourceRecord> _sources = [];
    private readonly Dictionary<uint, short[]> _buffers = [];
    private uint _nextSource = 1;
    private uint _nextBuffer = 1;

    /// <param name="maxSources">The driver's source limit, so a pool can be exhausted deliberately.</param>
    public FakeAudioBackend(int maxSources = 32) => MaxSources = maxSources;

    public int MaxSources { get; }

    public string DeviceName => "fake";

    public bool IsDisposed { get; private set; }

    /// <summary>Buffers created and never destroyed. A non-zero value at the end of a test is a leak.</summary>
    public int LiveBufferCount => _buffers.Count;

    /// <summary>Sources created and never destroyed.</summary>
    public int LiveSourceCount => _sources.Count;

    /// <summary>Every buffer upload since the last reset, oldest first. The oracle for loop arithmetic.</summary>
    public List<short[]> Uploads { get; } = [];

    // --- Test-side driving ---------------------------------------------------

    /// <summary>Pretends the driver consumed <paramref name="count"/> queued buffers on the source.</summary>
    public void Consume(uint source, int count)
    {
        SourceRecord record = _sources[source];
        record.Processed = Math.Min(record.Queue.Count, record.Processed + count);
    }

    /// <summary>Pretends the source played out everything queued and stopped, which is what an underrun looks like.</summary>
    public void Starve(uint source)
    {
        SourceRecord record = _sources[source];
        record.Processed = record.Queue.Count;
        record.State = AudioSourceState.Stopped;
    }

    /// <summary>Pretends a one-shot finished on its own.</summary>
    public void Finish(uint source) => _sources[source].State = AudioSourceState.Stopped;

    /// <summary>What the source is doing, without going through the engine's own accessor.</summary>
    public AudioSourceState StateOf(uint source) => _sources[source].State;

    /// <summary>Buffers currently queued on the source.</summary>
    public int QueueDepth(uint source) => _sources[source].Queue.Count;

    /// <summary>Contents of a buffer, as uploaded.</summary>
    public short[] Contents(uint buffer) => _buffers[buffer];

    // --- IAudioBackend -------------------------------------------------------

    public uint CreateBuffer()
    {
        uint buffer = _nextBuffer++;
        _buffers[buffer] = [];
        return buffer;
    }

    public void DestroyBuffer(uint buffer) => _buffers.Remove(buffer);

    public void UploadBuffer(uint buffer, AudioBufferFormat format, ReadOnlySpan<short> pcm, int sampleRate)
    {
        short[] copy = pcm.ToArray();
        _buffers[buffer] = copy;
        Uploads.Add(copy);
    }

    public bool TryCreateSource(out uint source)
    {
        if (_sources.Count >= MaxSources)
        {
            source = 0;
            return false;
        }

        source = _nextSource++;
        _sources[source] = new SourceRecord();
        return true;
    }

    public void DestroySource(uint source) => _sources.Remove(source);

    public void ConfigureSource(uint source, in AudioSourceSettings settings)
    {
        _ = _sources[source];
        _ = settings;
    }

    public AudioSourceState GetSourceState(uint source) => _sources[source].State;

    public int GetBuffersProcessed(uint source) => _sources[source].Processed;

    public int GetBuffersQueued(uint source) => _sources[source].Queue.Count;

    public void SetSourceBuffer(uint source, uint buffer) => _sources[source].StaticBuffer = buffer;

    // Real AL leaves the source's state alone across a requeue: a playing source
    // keeps playing. Modelling that is what makes the underrun the only case
    // where a queued source reads as Stopped.
    public void QueueBuffer(uint source, uint buffer) => _sources[source].Queue.Add(buffer);

    public uint UnqueueBuffer(uint source)
    {
        SourceRecord record = _sources[source];
        if (record.Processed <= 0 || record.Queue.Count == 0)
            return 0;

        uint buffer = record.Queue[0];
        record.Queue.RemoveAt(0);
        record.Processed--;
        return buffer;
    }

    public void Play(uint source) => _sources[source].State = AudioSourceState.Playing;

    public void Stop(uint source)
    {
        SourceRecord record = _sources[source];
        record.State = AudioSourceState.Stopped;

        // AL marks every queued buffer processed on a stop, which is what makes
        // draining the queue legal. Getting this wrong in the fake would let a
        // seek pass here and hang against a real driver.
        record.Processed = record.Queue.Count;
    }

    public void Pause(uint source) => _sources[source].State = AudioSourceState.Paused;

    public void SetListener(Vector3 position, Vector3 velocity, Vector3 forward, Vector3 up)
    {
        ListenerPosition = position;
        ListenerVelocity = velocity;
        ListenerForward = forward;
        ListenerUp = up;
    }

    public void SetListenerGain(float gain) => ListenerGain = gain;

    public Vector3 ListenerPosition { get; private set; }

    public Vector3 ListenerVelocity { get; private set; }

    public Vector3 ListenerForward { get; private set; }

    public Vector3 ListenerUp { get; private set; }

    public float ListenerGain { get; private set; } = 1f;

    public void Dispose() => IsDisposed = true;
}
