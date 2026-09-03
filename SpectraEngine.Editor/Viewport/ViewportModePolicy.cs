using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editor.Viewport;

/// <summary>Which viewport a session asks for.</summary>
/// <remarks>
/// <b><see cref="Auto"/> is the default and it resolves to <see cref="Native"/>
/// until the history says otherwise.</b> The composited viewport measures
/// pixel-identical to the native one and works on the machine it was built on,
/// which is evidence about one driver rather than about the world; the native
/// child has a year of use behind it. So the flip is earned per machine, by
/// consecutive green sessions, and never taken on optimism.
/// </remarks>
public enum ViewportMode
{
    /// <summary>The Win32 child window. Composites above everything Avalonia draws.</summary>
    Native,

    /// <summary>The compositor-imported shared texture. Ends airspace.</summary>
    Composition,

    /// <summary>Let <see cref="ViewportModePolicy"/> decide from the recorded history.</summary>
    Auto,
}

/// <summary>
/// Why a session got the viewport it got - in both directions, because "we chose
/// composition" needs saying as loudly as "we fell back".
/// </summary>
/// <remarks>
/// <para>
/// <b>Every value here has a sentence in <see cref="ViewportModePolicy.Describe"/>
/// and a test that fails if it does not.</b> The failure this vocabulary exists
/// to prevent is a viewport that silently is not the one that was asked for: a
/// composited pane that fell back to a native child renders exactly the same
/// picture, and the difference only shows up as an overlay that mysteriously
/// does not draw, weeks later, with nothing anywhere saying why.
/// </para>
/// <para>
/// <b><see cref="FirstUpdateFaulted"/> is the one value
/// <see cref="ViewportModePolicy.Decide"/> never returns</b>, because it is not
/// a decision: it is what a live composited session reports when the hand-over
/// it already started stops working. It is in the same enum deliberately - the
/// reason a session ends up on the native child next time and the reason it must
/// be relaunched now are one vocabulary, and splitting them would let one half
/// grow a value the other never learned to describe.
/// </para>
/// </remarks>
public enum ViewportChoiceReason
{
    /// <summary>Composition, because the command line asked for it and the machine can.</summary>
    ExplicitComposition,

    /// <summary>Composition, because this machine has earned it. See <see cref="ViewportModePolicy.RequiredGreenSessions"/>.</summary>
    ProvenByHistory,

    /// <summary>Native, because the command line said so. This beats any history.</summary>
    ExplicitNative,

    /// <summary>Native, because the run of green sessions on this machine is not long enough yet.</summary>
    NotYetProven,

    /// <summary>Native, because the history was recorded against a different GPU.</summary>
    AdapterChanged,

    /// <summary>Native, because the history was recorded against a different driver build.</summary>
    DriverChanged,

    /// <summary>Native, because an embedded GL surface needs a WGL context that does not exist.</summary>
    BackendIsOpenGl,

    /// <summary>Native, because this window has no compositor to hand a frame to.</summary>
    NoCompositor,

    /// <summary>Native, because the compositor exposes no GPU interop.</summary>
    NoGpuInterop,

    /// <summary>Native, because the compositor imports no handle kind the engine produces.</summary>
    HandleKindUnsupported,

    /// <summary>Native, because the compositor cannot synchronise that handle with a keyed mutex.</summary>
    NoKeyedMutexSync,

    /// <summary>Native, because the one-texel rehearsal import was refused.</summary>
    DryRunImportFailed,

    /// <summary>
    /// A live composited session's hand-over stopped. Reported, never acted on
    /// by swapping the viewport underneath the user.
    /// </summary>
    FirstUpdateFaulted,
}

/// <summary>
/// The persisted half of the viewport decision: what was asked for, and what
/// this machine has earned.
/// </summary>
/// <remarks>
/// <b>A value rather than the settings object, so the policy is pure.</b>
/// <see cref="Shell.EditorSettings"/> stores it and does the file I/O;
/// everything that decides anything takes one of these and returns another, so
/// the whole flip policy is provable with no disk, no compositor and no GPU.
/// </remarks>
/// <param name="Mode">What the user or the command line asked for.</param>
/// <param name="GreenSessions">Consecutive green composited sessions on this machine.</param>
/// <param name="AdapterLuid">The adapter the count was earned on, or empty before the first one.</param>
/// <param name="DriverVersion">The driver build the count was earned on, or empty.</param>
public readonly record struct ViewportPreference(
    ViewportMode Mode,
    int GreenSessions,
    string AdapterLuid,
    string DriverVersion)
{
    /// <summary>A machine that has never run a composited session, on the default mode.</summary>
    public static ViewportPreference Default { get; } =
        new(ViewportMode.Auto, GreenSessions: 0, AdapterLuid: string.Empty, DriverVersion: string.Empty);
}

/// <summary>
/// What this machine turned out to be able to do, measured rather than assumed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field is the answer to a question that has already been got wrong
/// somewhere.</b> A compositor can advertise a handle kind it cannot synchronise
/// (which is why the keyed mutex is separate from the handle kind), and it can
/// advertise both and still refuse the actual import (which is why
/// <see cref="DryRunImported"/> exists and is measured with a real texture).
/// </para>
/// <para>
/// <see cref="CompareGreen"/> is not about the compositor at all: it is whether
/// the last <c>--viewport-compare</c> run on this adapter and backend agreed
/// that the shared route's colours are identical to an ordinary target's. A
/// double sRGB encode raises no exception, no HRESULT and nothing on the debug
/// layer, so a session cannot be called green without it.
/// </para>
/// </remarks>
public readonly record struct ViewportCapabilities(
    bool HasCompositor,
    bool HasGpuInterop,
    bool SupportsD3D11NtHandle,
    bool SupportsKeyedMutex,
    bool DryRunImported,
    bool CompareGreen,
    string AdapterLuid,
    string AdapterName,
    string DriverVersion)
{
    /// <summary>
    /// Nothing was measured, because nothing needed to be. Handed to
    /// <see cref="ViewportModePolicy.Decide"/> on the paths that are answered
    /// from the preference alone.
    /// </summary>
    public static ViewportCapabilities NotMeasured { get; } = new(
        HasCompositor: false,
        HasGpuInterop: false,
        SupportsD3D11NtHandle: false,
        SupportsKeyedMutex: false,
        DryRunImported: false,
        CompareGreen: false,
        AdapterLuid: string.Empty,
        AdapterName: string.Empty,
        DriverVersion: string.Empty);

    /// <summary>Everything the composited path needs, for a test that is about the preference.</summary>
    public static ViewportCapabilities Ideal { get; } = new(
        HasCompositor: true,
        HasGpuInterop: true,
        SupportsD3D11NtHandle: true,
        SupportsKeyedMutex: true,
        DryRunImported: true,
        CompareGreen: true,
        AdapterLuid: "9a91010000000000",
        AdapterName: "Test Adapter",
        DriverVersion: "31.0.101.5085");
}

/// <summary>
/// The chosen viewport, the reason, and the sentence that reason is worth
/// saying out loud.
/// </summary>
/// <remarks>
/// <b><see cref="Explanation"/> is never empty, and that is enforced by a
/// test.</b> A fallback with no reason is the whole failure mode this stage
/// exists to prevent.
/// </remarks>
public readonly record struct ViewportDecision(
    bool UseComposition,
    ViewportChoiceReason Reason,
    string Explanation);

/// <summary>
/// Decides which viewport a session gets, and never lets that decision be
/// silent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure by construction: no I/O, no Avalonia type, no GPU.</b> Measuring the
/// machine is <see cref="ViewportProbe"/>'s job and persisting the history is
/// <see cref="Shell.EditorSettings"/>'s; what is left here is the arithmetic,
/// which is the part that has to be right on a machine nobody has run it on.
/// </para>
/// <para>
/// <b>The flip is earned, never assumed.</b> The composited viewport is
/// measurably pixel-identical to the native one on the machine it was written
/// on. That is evidence about one driver. So <see cref="ViewportMode.Auto"/>
/// stays on the native child until this machine has produced
/// <see cref="RequiredGreenSessions"/> consecutive green composited sessions on
/// the same adapter and driver, and any failure or any change of either puts the
/// count back to zero.
/// </para>
/// </remarks>
public static class ViewportModePolicy
{
    /// <summary>
    /// How many consecutive green composited sessions a machine owes before
    /// <see cref="ViewportMode.Auto"/> will choose composition on it.
    /// </summary>
    /// <remarks>
    /// Five, because the failures this is guarding against are intermittent
    /// ones - a driver that imports on a cold boot and not after a sleep, a
    /// hybrid laptop that moves the compositor to the other GPU - and one clean
    /// run says nothing about those. Small enough that a machine which really
    /// works crosses it in a day of ordinary use.
    /// </remarks>
    public const int RequiredGreenSessions = 5;

    /// <summary>
    /// Whether the machine has to be measured before <see cref="Decide"/> can
    /// answer.
    /// </summary>
    /// <remarks>
    /// <b>False is what keeps the default path free.</b> Measuring opens a
    /// graphics device and hands the compositor a real texture, which is not
    /// something to do on every launch of an editor that is going to use the
    /// native child anyway. It shares its whole implementation with
    /// <see cref="Decide"/>'s first phase, so the two cannot drift into
    /// measuring for a decision that was already made or skipping a measurement
    /// a decision needs.
    /// </remarks>
    public static bool RequiresMeasurement(ViewportPreference settings, GraphicsBackend backend) =>
        PreferenceVerdict(settings, backend) is null;

    /// <summary>
    /// Chooses the viewport for one session.
    /// </summary>
    /// <param name="settings">The persisted preference and history.</param>
    /// <param name="capabilities">
    /// What was measured, or <see cref="ViewportCapabilities.NotMeasured"/> when
    /// <see cref="RequiresMeasurement"/> said none was needed.
    /// </param>
    /// <param name="backend">The graphics backend this session will run on.</param>
    public static ViewportDecision Decide(
        ViewportPreference settings, ViewportCapabilities capabilities, GraphicsBackend backend)
    {
        if (PreferenceVerdict(settings, backend) is { } refused)
            return Fallback(refused);

        // The measured facts, most fundamental first, so the reported reason is
        // the deepest true one rather than the first thing that happened to be
        // checked.
        if (!capabilities.HasCompositor)
            return Fallback(ViewportChoiceReason.NoCompositor);

        if (!capabilities.HasGpuInterop)
            return Fallback(ViewportChoiceReason.NoGpuInterop);

        if (!capabilities.SupportsD3D11NtHandle)
            return Fallback(ViewportChoiceReason.HandleKindUnsupported);

        if (!capabilities.SupportsKeyedMutex)
            return Fallback(ViewportChoiceReason.NoKeyedMutexSync);

        if (!capabilities.DryRunImported)
            return Fallback(ViewportChoiceReason.DryRunImportFailed);

        if (settings.Mode is ViewportMode.Composition)
            return Chosen(ViewportChoiceReason.ExplicitComposition);

        // Auto only, and only now: the machine's identity is a MEASURED fact,
        // so a count earned on another GPU or another driver build can only be
        // caught once there is something to compare it against. An explicit
        // request does not consult the history at all and must not be refused
        // by it.
        if (!Same(settings.AdapterLuid, capabilities.AdapterLuid))
            return Fallback(ViewportChoiceReason.AdapterChanged);

        if (!Same(settings.DriverVersion, capabilities.DriverVersion))
            return Fallback(ViewportChoiceReason.DriverChanged);

        return Chosen(ViewportChoiceReason.ProvenByHistory);
    }

    /// <summary>
    /// The half of the decision that needs no measurement: null means the
    /// machine has to be asked.
    /// </summary>
    /// <remarks>
    /// <b>OpenGL is refused before anything else, whatever was asked for.</b> An
    /// embedded GL surface needs its own WGL context and proc-address loader,
    /// which is not built; letting the compositor discover that would report a
    /// missing feature as a driver failure.
    /// <para>
    /// <b>An explicit native choice comes next, and beats a green history.</b>
    /// That is the whole point of the switch: it is what somebody types when the
    /// composited path is the thing under suspicion.
    /// </para>
    /// </remarks>
    private static ViewportChoiceReason? PreferenceVerdict(
        ViewportPreference settings, GraphicsBackend backend)
    {
        if (backend is GraphicsBackend.OpenGL)
            return ViewportChoiceReason.BackendIsOpenGl;

        if (settings.Mode is ViewportMode.Native)
            return ViewportChoiceReason.ExplicitNative;

        if (settings.Mode is ViewportMode.Composition)
            return null;

        // Auto, from here down, and a short history is answered without asking
        // the machine anything: measuring opens a graphics device and hands the
        // compositor a real texture, which is not a price an editor that is
        // about to use the native child should pay on every launch. Whether the
        // count was earned HERE is a separate question and needs the
        // measurement, so it is asked in Decide.
        if (settings.GreenSessions < RequiredGreenSessions)
            return ViewportChoiceReason.NotYetProven;

        return null;
    }

    /// <summary>
    /// Re-anchors the history on the machine that is actually here, zeroing the
    /// count if either half of the machine's identity moved.
    /// </summary>
    /// <remarks>
    /// <b>This is where an adapter or driver change resets the count, and it is
    /// deliberately separate from <see cref="Record"/>.</b> A change of GPU or
    /// of driver build invalidates every session behind the count before this
    /// session has done anything at all, so it is answered at launch, against
    /// the measured machine, rather than folded into the outcome of a run that
    /// has not happened yet. Called only when the machine WAS measured: an
    /// unmeasured launch knows nothing about the adapter and must not overwrite
    /// a real history with empty strings.
    /// </remarks>
    public static ViewportPreference Rebase(
        ViewportPreference settings, string adapterLuid, string driverVersion)
    {
        ArgumentNullException.ThrowIfNull(adapterLuid);
        ArgumentNullException.ThrowIfNull(driverVersion);

        bool sameMachine = Same(settings.AdapterLuid, adapterLuid)
            && Same(settings.DriverVersion, driverVersion);

        return settings with
        {
            GreenSessions = sameMachine ? settings.GreenSessions : 0,
            AdapterLuid = adapterLuid,
            DriverVersion = driverVersion,
        };
    }

    /// <summary>
    /// Folds one finished composited session into the history: one longer if it
    /// was green, back to zero if it was not.
    /// </summary>
    /// <remarks>
    /// <b>Only a session that actually ran on the composited viewport may be
    /// recorded.</b> A native session says nothing either way about the
    /// composited path, and counting one as green would let a machine earn the
    /// flip without ever having composited a frame.
    /// </remarks>
    public static ViewportPreference Record(ViewportPreference settings, bool sessionGreen) =>
        settings with
        {
            // Capped so a machine that has been green for a year does not carry
            // an ever-growing number through a settings file for no reason: the
            // question is only ever whether it is at least the threshold.
            GreenSessions = sessionGreen
                ? Math.Min(settings.GreenSessions + 1, RequiredGreenSessions)
                : 0,
        };

    /// <summary>
    /// Whether a finished composited session counts toward the flip.
    /// </summary>
    /// <remarks>
    /// <b>Three conditions, and the third is the one that would be forgotten.</b>
    /// Debug-layer errors catch a missing barrier or a mismatched pipeline
    /// state; a faulted import or hand-over catches the synchronisation; and
    /// neither of those can see a double sRGB encode, which raises no exception,
    /// no HRESULT and nothing on the debug layer and simply washes the picture
    /// out. That is what <c>--viewport-compare</c> measures, and a machine with
    /// no such measurement on the adapter it is running on has not been shown
    /// anything about its colours.
    /// <para>
    /// The debug-layer count is the COUNTED one, which on a composited D3D12
    /// surface already excludes the one forgiven <c>ReflectSharedProperties</c>
    /// message per bridge wrap. Nothing here may re-count it.
    /// </para>
    /// </remarks>
    public static bool IsSessionGreen(int debugLayerErrors, bool faulted, bool compareGreen) =>
        debugLayerErrors == 0 && !faulted && compareGreen;

    /// <summary>The sentence behind a reason. Never empty, for any value.</summary>
    public static string Describe(ViewportChoiceReason reason) => reason switch
    {
        ViewportChoiceReason.ExplicitComposition =>
            "the command line asked for the composited viewport and this machine can host it",

        ViewportChoiceReason.ProvenByHistory =>
            $"this adapter and driver have produced {RequiredGreenSessions} consecutive green composited " +
            "sessions, so auto chose composition",

        ViewportChoiceReason.ExplicitNative =>
            "the command line asked for the native child window, which beats any recorded history",

        ViewportChoiceReason.NotYetProven =>
            $"auto keeps the native child until this adapter and driver have produced " +
            $"{RequiredGreenSessions} consecutive green composited sessions",

        ViewportChoiceReason.AdapterChanged =>
            "the recorded composited sessions were earned on a different adapter, so the count starts again",

        ViewportChoiceReason.DriverChanged =>
            "the recorded composited sessions were earned on a different driver build, so the count starts " +
            "again",

        ViewportChoiceReason.BackendIsOpenGl =>
            "an embedded OpenGL surface needs its own WGL context and proc-address loader, which is not " +
            "built, so composited OpenGL is refused by name rather than attempted",

        ViewportChoiceReason.NoCompositor =>
            "this window has no compositor, so an engine frame has nowhere to go",

        ViewportChoiceReason.NoGpuInterop =>
            "this compositor exposes no GPU interop, so an engine frame cannot be imported",

        ViewportChoiceReason.HandleKindUnsupported =>
            "this compositor does not import D3D11 NT handles, which is the only kind the engine produces",

        ViewportChoiceReason.NoKeyedMutexSync =>
            "this compositor imports the handle but cannot synchronise it with a keyed mutex, which is the " +
            "only hand-over the engine implements",

        ViewportChoiceReason.DryRunImportFailed =>
            "the one-texel rehearsal import was refused, so a real frame would have been refused too - and " +
            "after an engine was already running against it",

        ViewportChoiceReason.FirstUpdateFaulted =>
            "the composited hand-over faulted while the session was running; relaunch with --viewport=native",

        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Every viewport choice reason owes a sentence."),
    };

    // --- The command line ----------------------------------------------------

    /// <summary>The switch that names the viewport for one run.</summary>
    public const string Switch = "--viewport=";

    /// <summary>How the switch is spelled, for a message that has to tell somebody what to type.</summary>
    public const string Usage = "--viewport=composition|native|auto";

    /// <summary>
    /// Reads <c>--viewport=</c> off a command line. Null means it was not
    /// given, which is not the same as <see cref="ViewportMode.Auto"/>: a mode
    /// nobody named leaves whatever is in the settings alone.
    /// </summary>
    /// <remarks>
    /// <b>Hand-written rather than <c>Enum.Parse</c>,</b> the same discipline
    /// the console's verb table and <c>GizmoShortcuts</c> follow: reflection
    /// over enum names is what trimming removes, so it would work in every debug
    /// run and fail in a published one.
    /// </remarks>
    public static ViewportMode? RequestedMode(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        ViewportMode? found = null;

        // Last one wins, so a wrapper script's default can be overridden by
        // appending rather than by editing the script.
        foreach (string arg in args)
        {
            if (!arg.StartsWith(Switch, StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryParseMode(arg[Switch.Length..], out ViewportMode mode))
                found = mode;
        }

        return found;
    }

    /// <summary>Parses one mode word.</summary>
    public static bool TryParseMode(string? text, out ViewportMode mode)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "native":
                mode = ViewportMode.Native;
                return true;

            case "composition" or "composited":
                mode = ViewportMode.Composition;
                return true;

            case "auto":
                mode = ViewportMode.Auto;
                return true;

            default:
                mode = ViewportMode.Auto;
                return false;
        }
    }

    /// <summary>The word a mode is written as, in the settings file and in a log line.</summary>
    public static string NameOf(ViewportMode mode) => mode switch
    {
        ViewportMode.Native => "native",
        ViewportMode.Composition => "composition",
        ViewportMode.Auto => "auto",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown viewport mode."),
    };

    // --- Plumbing ------------------------------------------------------------

    private static ViewportDecision Chosen(ViewportChoiceReason reason) =>
        new(UseComposition: true, reason, Describe(reason));

    private static ViewportDecision Fallback(ViewportChoiceReason reason) =>
        new(UseComposition: false, reason, Describe(reason));

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
