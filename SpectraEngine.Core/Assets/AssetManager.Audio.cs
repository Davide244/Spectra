using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Assets.Audio;
using SpectraEngine.Core.Assets.Sources;
using System;
using System.Collections.Generic;
using System.IO;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// The audio half of the asset manager: resolving a sound's path, reading its
/// cooked bytes, and holding the content reference those bytes live in.
/// </summary>
/// <remarks>
/// <para><b>An audio read FORKS on the extension, exactly as an image read
/// does.</b> A sound's bytes are the cooked <c>.saudio</c> beside the authored
/// file when a mounted source has one;
/// <see cref="AudioContentPath.Resolve"/> is the single expression of that rule,
/// shared with the cook's verifier, so the probe and the open cannot disagree
/// about which of two files a sound is - the disagreement this repo has already
/// paid for once, where a packed build resolved nothing while every log line
/// read healthy.</para>
/// <para><b>The cooked branch runs no decoder at all.</b> A <c>.saudio</c> is
/// PCM the cooker already widened, resampled and interleaved, so loading one is
/// a header parse and a span - which is the whole point of cooking it. The
/// authored branch is not a slower version of the same thing: there is no
/// source-format decoder in Core, because a WAV parser belongs beside the cooker
/// that reads authored formats rather than inside every shipped game binary. So
/// an uncooked sound is refused with a message that says to cook it.</para>
/// <para><b>The <see cref="ContentBlob"/> is held for the asset's life</b>, not
/// merely for the call. The samples are a span into a memory-mapped view, and
/// unmapping under a live span is an access violation with no managed stack -
/// so the reference travels with the span, the same rule a cooked texture
/// crossing the upload queue already follows. Every open sound is released in
/// <c>Shutdown</c>; a leaked one defers a pack's unmount for the life of the
/// process, which in a shell that opens and closes sessions is a mount leaked
/// per session.</para>
/// </remarks>
public sealed partial class AssetManager
{
    // Guards _audio only. A fourth lock rather than reusing one of the three
    // above: nothing here calls into the texture, material or model paths, and a
    // shared lock between subsystems that never touch is a lock-ordering hazard
    // waiting for the first time one of them does.
    private readonly object _audioSync = new();
    private readonly Dictionary<string, AudioAsset> _audio = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of cooked sounds currently open. Any thread.</summary>
    public int AudioCount
    {
        get { lock (_audioSync) return _audio.Count; }
    }

    /// <summary>
    /// Opens a cooked sound and returns the handle that owns its bytes, or the
    /// cached one if it is already open. Any thread: reading a <c>.saudio</c> is
    /// a header parse over content, with no device and no GPU object in it.
    /// </summary>
    /// <remarks>
    /// Throws rather than degrading, and that is deliberately the opposite of the
    /// texture path. A missing texture has a magenta placeholder to fall back to
    /// and a frame that must keep rendering; a missing sound has no equivalent
    /// stand-in, and a manager that silently handed back silence would make
    /// "nothing plays" indistinguishable from "the mixer is muted". The caller
    /// decides, which for a game is usually to log it and carry on.
    /// </remarks>
    /// <param name="relativePath">
    /// Content path of the AUTHORED sound, e.g. <c>Sounds/door_open.wav</c>. The
    /// cooked file beside it is what actually gets opened.
    /// </param>
    /// <exception cref="FileNotFoundException">No mounted source has that sound.</exception>
    /// <exception cref="Audio.SaudioFormatException">The cooked file is not one this engine can play.</exception>
    /// <exception cref="InvalidDataException">The path names an authored sound with no cooked file beside it.</exception>
    public AudioAsset LoadAudio(string relativePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string key = ContentRoot.NormalizeRelativePath(relativePath);

        lock (_audioSync)
        {
            if (_audio.TryGetValue(key, out AudioAsset? cached)) return cached;
        }

        AudioAsset asset = ReadAudioThroughContent(key);

        lock (_audioSync)
        {
            // A second caller can have opened the same sound while this one was
            // reading. Theirs wins and this one's blob is released rather than
            // being left holding a mount nothing will ever close.
            if (_audio.TryGetValue(key, out AudioAsset? raced))
            {
                asset.Dispose();
                return raced;
            }

            _audio[key] = asset;
        }

        _logger.LogInformation(
            "Loaded sound {Path} ({Frames} frames, {Format}{Loop})",
            asset.ResolvedPath,
            asset.FrameCount,
            asset.Format,
            asset.Loop.IsLooping ? $", loop {asset.Loop}" : string.Empty);

        return asset;
    }

    /// <summary>
    /// Whether any mounted source can answer for <paramref name="relativePath"/>
    /// as a sound. Any thread.
    /// </summary>
    /// <remarks>
    /// It asks the SAME stack the open asks, on the SAME resolved path, which is
    /// the whole reason it is expressed here rather than inline at each caller:
    /// a probe that looked only for the authored file would report every sound in
    /// a packed build as missing.
    /// </remarks>
    public bool AudioExists(string relativePath)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);
        return Content.Exists(AudioContentPath.Resolve(Content, key));
    }

    /// <summary>
    /// Releases one open sound and its content reference. Any thread. Returns
    /// false when nothing was open under that path.
    /// </summary>
    /// <remarks>
    /// Anything still PLAYING it is unaffected: <c>AudioManager.CreateClip</c>
    /// copies the samples it is handed, into an AL buffer or into the array a
    /// looping clip is queued from, so a voice never reads this span again after
    /// the clip is made. The two lifetimes are genuinely independent, which is
    /// why this needs no coordination with the audio device at all.
    /// </remarks>
    public bool UnloadAudio(string relativePath)
    {
        string key = ContentRoot.NormalizeRelativePath(relativePath);

        AudioAsset? asset;
        lock (_audioSync)
        {
            if (!_audio.Remove(key, out asset)) return false;
        }

        asset.Dispose();
        _logger.LogInformation("Unloaded sound {Path}", key);
        return true;
    }

    // The fourth content read, and the second one that forks. Any thread: opening
    // and parsing a .saudio header are pure CPU.
    private AudioAsset ReadAudioThroughContent(string key)
    {
        string resolved = AudioContentPath.Resolve(Content, key);

        if (!AudioContentPath.IsCooked(resolved))
        {
            // The authored file exists and there is no cooked one beside it. Said
            // as its own message rather than falling through to "not found",
            // because the two have completely different answers and only one of
            // them is "cook the project".
            throw new InvalidDataException(
                $"Sound '{resolved}' has no cooked '{AudioContentPath.CookedPathFor(key)}' beside it, and the " +
                "engine reads cooked audio only; run scook over the project.");
        }

        ContentBlob blob = OpenOrThrow(resolved);

        try
        {
            // No decode and no copy. The blob TRAVELS with the result, because
            // the payload it describes is a span into these very bytes.
            SaudioInfo info = SaudioReader.Read(blob.Span, resolved);
            return new AudioAsset(key, resolved, info, blob);
        }
        catch
        {
            // A refusal here leaves nobody holding the reference, so it is
            // released before the message goes up. Without this a project whose
            // .saudio files are one version stale would leak a pack reference per
            // sound.
            blob.Dispose();
            throw;
        }
    }

    // Called from Shutdown, which is the CPU-side teardown: a ContentBlob is a
    // content reference rather than a GPU object, so it does not belong in
    // ReleaseGraphicsResources and does not need the render thread.
    private void ReleaseAudioResources()
    {
        List<AudioAsset> open;
        lock (_audioSync)
        {
            if (_audio.Count == 0) return;

            open = new List<AudioAsset>(_audio.Values);
            _audio.Clear();
        }

        for (int i = 0; i < open.Count; i++) open[i].Dispose();
        _logger.LogInformation("Asset manager released {Count} open sound(s)", open.Count);
    }
}
