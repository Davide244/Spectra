using Spectra.Kitchen.Audio;
using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Diagnostics;
using SpectraEngine.Core.Assets.Audio;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Audio;
using System;
using System.IO;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// Turns an authored WAV into a <c>.saudio</c>: PCM16 at the one project sample
/// rate, with its loop points carried in sample frames.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this buys at runtime is that nothing resamples in the frame
/// loop.</b> A mixer handed sounds at 44.1, 48 and 22.05 either converts per
/// source per frame or lets the driver do it at a quality nobody chose, and
/// either way it is a cost paid forever for a mistake made once at import. The
/// cooker pays it once, offline, cached by content; a file that still arrives at
/// the wrong rate is logged and played, never fixed.
/// </para>
/// <para>
/// <b>Loop points are converted with INTEGER arithmetic, through the same
/// <see cref="AudioResampler.ConvertFrames"/> the length goes through.</b> That
/// is the single most breakable thing in this rule: a loop expressed in seconds
/// or in bytes and converted back lands a frame or two off, and a frame off at a
/// loop boundary is a click once a bar, forever, in an asset whose waveform
/// looks perfect. One conversion, no floating point in it, shared by the length
/// and the loop so the two cannot disagree.
/// </para>
/// <para>
/// <b>A stereo sound is not positional and the rule says so out loud.</b> OpenAL
/// will not spatialise a stereo buffer: it plays flat, at full level, wherever
/// the listener stands, with no error anywhere. That is the classic "why is my
/// 3D sound not 3D" report and it is free to catch here, where the channel count
/// is in hand. A sound that is MEANT to be flat says so by ending its name
/// <c>_2d</c>, which is the whole of the convention and is deliberately in the
/// file NAME: a name travels with the asset through every content source, is
/// visible in an editor's browser, and needs no sidecar file to exist. When a
/// per-asset settings mechanism lands, this is the one place that changes.
/// </para>
/// <para>
/// <b>The emitted path is the source path with a <c>.saudio</c> extension</b>
/// (<see cref="AudioContentPath.CookedPathFor"/>), and the authored file is NOT
/// also copied into the pack: shipping both would double every sound in the
/// build for content nothing reads. Everything that names a sound goes on naming
/// the <c>.wav</c>, and <see cref="AudioContentPath.Resolve"/> is the single
/// place that redirection happens for the engine and for <c>scook verify</c>
/// alike.
/// </para>
/// <para>
/// <b>A file the decoder refuses is reported and emits nothing.</b> Falling back
/// to a raw copy would be worse than failing: the pack would carry a broken WAV
/// under a path the engine resolves, the runtime would refuse it at load, and
/// the build log would say a sound cooked.
/// </para>
/// </remarks>
public sealed class AudioRule : IRule
{
    // What WaveDecoder actually reads. Listed rather than derived, for the reason
    // ImageRule gives: the set the decoder supports and the set this rule claims
    // must be the same set, or a file of that kind becomes an SC4001 rather than
    // the raw copy it was getting perfectly well before.
    private static readonly string[] SourceExtensions = [".wav", ".wave"];

    /// <summary>
    /// The suffix on a file's stem that says a sound is meant to play flat.
    /// </summary>
    /// <remarks>
    /// Lower case here and matched case-insensitively: a convention that depended
    /// on how somebody capitalised a file name would work on one machine's
    /// content and not on another's.
    /// </remarks>
    public const string FlatSuffix = "_2d";

    /// <summary>
    /// Sounds longer than this are flagged streaming and get a seek table.
    /// </summary>
    /// <remarks>
    /// A heuristic, and the only signal available: nothing in a WAV says how it
    /// is meant to be played, and there is no per-asset settings file yet. Ten
    /// seconds is comfortably past every gunshot, footstep and UI click and
    /// comfortably short of every music cue and room tone, which is the split
    /// residency actually cares about. It is a function of the frame count alone,
    /// so it stays deterministic.
    /// </remarks>
    public const double StreamingThresholdSeconds = 10.0;

    /// <summary>Seconds of audio between two seek points in a streamed sound.</summary>
    /// <remarks>
    /// One second puts a five-minute track's table at 2.4 KB and bounds the worst
    /// case of a seek to one second of decoding, which for a forward-only codec
    /// is the number that matters. PCM can seek exactly and carries the table
    /// anyway, because the table has to be in the format and validated BEFORE the
    /// codec that needs it arrives, not at the moment somebody is trying to add
    /// music.
    /// </remarks>
    public const double SecondsPerSeekEntry = 1.0;

    /// <inheritdoc/>
    public RuleKind Kind => RuleKind.Audio;

    /// <inheritdoc/>
    /// <remarks>
    /// Raise this whenever the bytes this rule emits for one source can change: a
    /// different resampler kernel, a change to the container layout, a different
    /// streaming threshold. The project RATE is not covered by it - that rides
    /// <see cref="SettingsRead"/> - and neither is
    /// <c>EngineInfo.AudioFormatVersion</c>, which a reader enforces instead.
    /// </remarks>
    public int Version => 1;

    /// <inheritdoc/>
    /// <remarks>
    /// The project audio rate, and only that. Not the profile: there is one
    /// resampler kernel and a preview cook of a sound and a ship cook of it are
    /// the same bytes, so declaring the profile would re-cook a project's whole
    /// sound library on a <c>--profile fast</c> run for no change at all.
    /// </remarks>
    public CookSettingKeys SettingsRead => CookSettingKeys.AudioSampleRate;

    /// <summary>Whether <paramref name="contentPath"/> is a sound this rule cooks.</summary>
    public static bool Handles(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);

        foreach (string extension in SourceExtensions)
        {
            if (contentPath.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="contentPath"/> declares itself flat by ending its
    /// stem with <see cref="FlatSuffix"/>.
    /// </summary>
    public static bool IsDeclaredFlat(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);

        string stem = Path.GetFileNameWithoutExtension(contentPath);
        return stem.EndsWith(FlatSuffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public void Cook(IRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        byte[] source = context.Read(context.SourcePath);

        DecodedAudio decoded;
        try
        {
            decoded = WaveDecoder.Decode(source, context.SourcePath);
        }
        catch (InvalidDataException ex)
        {
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.AudioUndecodable,
                $"'{context.SourcePath}' could not be decoded: {ex.Message}",
                context.SourcePath));

            return;
        }

        if (decoded.LoopWasRefused)
        {
            context.Report(CookDiagnostic.Warning(
                CookDiagnosticCodes.AudioLoopUnusable,
                $"'{context.SourcePath}' declares a loop this engine cannot play - a region outside its own " +
                "data, an empty one, or an alternating or backward loop - so the cooked sound plays once. " +
                "Only forward loops inside the sound are carried.",
                context.SourcePath));
        }

        int targetRate = context.AudioSampleRate;
        short[] samples = decoded.Samples;
        LoopRegion loop = decoded.Loop;

        if (decoded.SampleRate != targetRate)
        {
            samples = AudioResampler.Resample(samples, decoded.Channels, decoded.SampleRate, targetRate);

            // The loop rides the SAME integer conversion the length does. Doing
            // it any other way - through seconds, through a ratio in floating
            // point, through byte offsets - lands a frame or two off, and the
            // failure is audible and permanent while every measurement of the
            // file still reads correct.
            loop = ConvertLoop(loop, decoded.SampleRate, targetRate, samples.Length / decoded.Channels);

            context.Report(CookDiagnostic.Info(
                CookDiagnosticCodes.AudioResampled,
                $"'{context.SourcePath}' was resampled from {decoded.SampleRate} Hz to the project's " +
                $"{targetRate} Hz. The runtime never resamples, so this happens once, here.",
                context.SourcePath));
        }

        bool flat = IsDeclaredFlat(context.SourcePath);
        if (decoded.Channels == 2 && !flat)
        {
            context.Report(CookDiagnostic.Warning(
                CookDiagnosticCodes.AudioStereoPositional,
                $"'{context.SourcePath}' is stereo, so OpenAL will play it unpositioned however it is placed - " +
                "at full level, wherever the listener stands, with nothing reporting it. Export it mono if it " +
                $"is meant to be a sound in the world, or end its name '{FlatSuffix}' to say it is meant to be " +
                "flat.",
                context.SourcePath));
        }

        long frames = samples.Length / decoded.Channels;
        int framesPerSeekEntry = frames > StreamingThresholdSeconds * targetRate
            ? Math.Max(1, (int)(SecondsPerSeekEntry * targetRate))
            : 0;

        byte[] cooked;
        try
        {
            cooked = SaudioWriter.Write(
                new AudioFormat(targetRate, decoded.Channels),
                samples,
                loop,
                positional: !flat,
                framesPerSeekEntry);
        }
        catch (ArgumentException ex)
        {
            // The writer measures everything it is handed against the format's own
            // limits, so this is the decoder or the resampler producing something
            // the container cannot hold. Reported rather than thrown, because a
            // cook must name the asset that broke rather than stopping at SC1004.
            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.AudioEncodeFailed,
                $"'{context.SourcePath}' produced a sound the container cannot hold: {ex.Message}",
                context.SourcePath));

            return;
        }

        context.Emit(AudioContentPath.CookedPathFor(context.SourcePath), cooked, PackEntryKind.Audio);
    }

    // Both ends through the same conversion, then bounded by the resampled
    // length. The bound is not paranoia: rounding each end independently can put
    // an end one frame past a length that rounded the other way, and the reader
    // refuses a loop that ends past the sound - so without this a legitimate
    // asset would cook into a file nothing can open.
    private static LoopRegion ConvertLoop(LoopRegion loop, int fromRate, int toRate, long frames)
    {
        if (!loop.IsLooping) return LoopRegion.None;

        long start = AudioResampler.ConvertFrames(loop.StartFrame, fromRate, toRate);
        long end = Math.Min(AudioResampler.ConvertFrames(loop.EndFrame, fromRate, toRate), frames);

        // A loop shorter than one frame after conversion is a loop that was
        // shorter than one frame at the destination rate. Dropping it is the only
        // answer: LoopRegion refuses an empty region, and a region of one
        // arbitrarily chosen frame would be a buzz at the destination rate.
        return end > start ? new LoopRegion(start, end) : LoopRegion.None;
    }
}
