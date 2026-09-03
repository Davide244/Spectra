using System;

namespace SpectraEngine.Core.Audio;

/// <summary>
/// One sound in flight: a pooled AL source plus whatever is feeding it. Handed
/// out by <see cref="AudioManager"/>, which also takes it back; a caller holds
/// one only to stop it or to move it.
/// </summary>
/// <remarks>
/// A voice never outlives its source. Once <see cref="IsFinished"/> is true the
/// source has gone back to the pool and may already be carrying a different
/// sound, so every method here is a no-op from that moment: a handle a caller
/// kept must not be able to stop somebody else's audio.
/// </remarks>
public abstract class AudioVoice
{
    private protected readonly IAudioBackend Backend;

    private protected AudioVoice(IAudioBackend backend, uint source, AudioSourceSettings settings)
    {
        Backend = backend;
        Source = source;
        Settings = settings;
    }

    /// <summary>The pooled AL source this voice is driving.</summary>
    internal uint Source { get; private set; }

    /// <summary>Gain, pitch, placement. Reapplied by <see cref="Configure"/>.</summary>
    public AudioSourceSettings Settings { get; private set; }

    /// <summary>True once the sound is over and the source has been reclaimed.</summary>
    public bool IsFinished { get; private protected set; }

    /// <summary>Moves or re-levels a sound that is still playing. Ignored once finished.</summary>
    public void Configure(in AudioSourceSettings settings)
    {
        if (IsFinished) return;
        Settings = settings;
        Backend.ConfigureSource(Source, in settings);
    }

    /// <summary>
    /// Ends the sound now. The source goes back to the pool on the next
    /// <see cref="AudioManager.Update"/>, not here, so a caller stopping a voice
    /// from inside a loop over voices cannot mutate the pool underneath it.
    /// </summary>
    public void Stop()
    {
        if (IsFinished) return;
        Backend.Stop(Source);
        IsFinished = true;
    }

    /// <summary>
    /// Per-frame work. Returns false once the voice is done and its source can
    /// be reclaimed.
    /// </summary>
    internal abstract bool Update();

    /// <summary>Frees anything the voice owns beyond its source, and forgets the source.</summary>
    internal virtual void Detach()
    {
        IsFinished = true;
        Source = 0;
    }
}

/// <summary>
/// A whole clip bound to a source and played once. The cheap path, and the only
/// one that does not touch a buffer queue.
/// </summary>
/// <remarks>
/// Reachable only for a clip with no loop points, because a loop is buffer-queue
/// arithmetic in this engine and there is nothing to do arithmetic with here.
/// A clip that loops goes through <see cref="StreamingVoice"/> instead, even
/// when it is fully resident.
/// </remarks>
public sealed class StaticVoice : AudioVoice
{
    internal StaticVoice(IAudioBackend backend, uint source, AudioClip clip, AudioSourceSettings settings)
        : base(backend, source, settings)
    {
        Clip = clip;
        backend.ConfigureSource(source, in settings);
        backend.SetSourceBuffer(source, clip.Buffer);
        backend.Play(source);
    }

    /// <summary>
    /// The clip bound to the source. Kept so destroying a clip can stop exactly
    /// the voices holding its buffer: AL refuses to delete a buffer a source
    /// still has bound, and the refusal leaks the buffer rather than reporting
    /// anything a caller sees.
    /// </summary>
    internal AudioClip Clip { get; }

    internal override bool Update()
    {
        if (IsFinished) return false;

        // Paused counts as live: a paused source is holding an offset somebody
        // means to resume from, and reclaiming it would lose that.
        AudioSourceState state = Backend.GetSourceState(Source);
        if (state is AudioSourceState.Playing or AudioSourceState.Paused)
            return true;

        IsFinished = true;
        return false;
    }
}
