using Spectra.Kitchen.Cache;
using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;

namespace Spectra.Kitchen.Cooking;

/// <summary>
/// Everything one cook run was asked for.
/// </summary>
/// <remarks>
/// <para><b>The library owns these, not the CLI.</b> The editor hosts the cooker
/// in process for cooked-accurate preview, so the settings a cook runs under have
/// to be expressible without a command line; <c>scook</c>'s parser produces one of
/// these and does nothing else with the values.</para>
/// <para><b>Settings that no rule reads yet are still carried.</b> They are part of
/// the cache key by design, and a switch that vanished between the parser and the
/// session would be a switch whose effect nobody could find.</para>
/// </remarks>
public sealed class CookSettings
{
    /// <summary>Where output goes. Defaults to the project's <c>cooked/</c> folder.</summary>
    public string? OutputPath { get; init; }

    /// <summary>What the cook is for.</summary>
    public CookProfile Profile { get; init; } = CookProfile.Ship;

    /// <summary>
    /// The backends a cook targets when nobody names any: the three that have a
    /// working code generator. Vulkan is opt-in until SPIR-V exists, exactly as
    /// it is in <c>ssc</c>.
    /// </summary>
    /// <remarks>
    /// Named once so the CLI's default, this class's default and a rule context
    /// built without one cannot drift into three answers to one question.
    /// </remarks>
    public static IReadOnlyList<GraphicsBackend> DefaultTargets { get; } =
        [GraphicsBackend.OpenGL, GraphicsBackend.D3D11, GraphicsBackend.D3D12];

    /// <summary>
    /// Backends shaders are cooked for, in the same grammar and with the same
    /// default as <c>ssc</c>.
    /// </summary>
    public IReadOnlyList<GraphicsBackend> Targets { get; init; } = DefaultTargets;

    /// <summary>
    /// Worker count. <c>1</c> is the determinism-oracle mode: a cook at <c>-j1</c>
    /// and a cook at <c>-jN</c> must be byte-identical.
    /// </summary>
    /// <remarks>
    /// A request rather than a fact: the scheduler clamps it to how much there is
    /// to cook and reports what it ran at on <see cref="CookResult.Workers"/>.
    /// </remarks>
    public int Jobs { get; init; } = 1;

    /// <summary>Whether the content-addressed cache may be read and written.</summary>
    public bool UseCache { get; init; } = true;

    /// <summary>
    /// Emit a cooked directory tree instead of a pack: the overlay input for the
    /// editor's cooked-accurate preview.
    /// </summary>
    public bool Loose { get; init; }

    /// <summary>Keep authored brush geometry in a cooked map, so a verify can recompile it.</summary>
    public bool KeepBrushSource { get; init; }

    /// <summary>Whether cooked scripts keep their source text.</summary>
    public ScriptSourceMode ScriptSource { get; init; } = ScriptSourceMode.Embed;

    /// <summary>Which block-compression encoder to use.</summary>
    public CookEncoder Encoder { get; init; } = CookEncoder.Managed;

    /// <summary>
    /// The one rate every cooked sound is resampled to. 48 kHz.
    /// </summary>
    /// <remarks>
    /// <b>What a project's audio rate is cannot be changed once a library
    /// exists.</b> Changing this invalidates every cooked sound in the project -
    /// which is what <see cref="CookSettingKeys.AudioSampleRate"/> makes happen
    /// correctly rather than quietly - and, worse than the rebuild, it resamples
    /// a library that has already been resampled once, so every asset takes a
    /// second generation of loss and every loop point moves again. 48 kHz
    /// because it is what every game console, every modern driver and every DAW
    /// export defaults to, so the overwhelmingly common case is a cook that
    /// resamples nothing at all.
    /// </remarks>
    /// <remarks>
    /// <b>It belongs in the project manifest and is here instead.</b> It is a
    /// property of the CONTENT, not of one invocation, so it should be recorded
    /// beside the project's name and its startup map rather than being passed per
    /// cook - and deliberately NOT on the command line, since a per-invocation
    /// override is exactly how half a library ends up at one rate and half at
    /// another. It sits on the settings until the manifest grows a place for it,
    /// and the editor, which hosts the cooker in process, sets it from there.
    /// </remarks>
    public int AudioSampleRate { get; init; } = DefaultAudioSampleRate;

    /// <summary>The project audio rate a cook uses when nobody names one.</summary>
    public const int DefaultAudioSampleRate = 48_000;

    /// <summary>The highest rate a cook will resample to; the reader's own bound.</summary>
    public const int MaxAudioSampleRate = 768_000;

    /// <summary>
    /// Promote every warning to an error.
    /// </summary>
    /// <remarks>
    /// <b>The cooker is the loud gate and the runtime is the soft landing</b>, and
    /// the asymmetry is deliberate: a missing texture at runtime degrades to a
    /// magenta placeholder with a warning because a frame must keep rendering,
    /// while a build step whose job is to stop broken data shipping should not.
    /// Strict is the setting that says "this run is the gate".
    /// </remarks>
    public bool Strict { get; init; }

    /// <summary>
    /// Where to write the cook manifest, or null for none. This is the artifact
    /// CI diffs: every asset, its id, its inputs and its output hash.
    /// </summary>
    public string? ManifestPath { get; init; }

    /// <summary>Validates what can be validated without touching a project.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Jobs"/> is below one, or <see cref="AudioSampleRate"/> is
    /// outside what a cooked sound can declare.
    /// </exception>
    public void Validate()
    {
        if (Jobs < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Jobs), Jobs, "A cook runs at least one job.");
        }

        // Refused here rather than at the first sound, because a rate the format
        // cannot record is a whole run's worth of audio the reader will refuse,
        // and the run should stop before it writes any of it.
        if (AudioSampleRate is < 1 or > MaxAudioSampleRate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AudioSampleRate),
                AudioSampleRate,
                $"A project audio rate is between 1 and {MaxAudioSampleRate} frames a second.");
        }
    }
}
