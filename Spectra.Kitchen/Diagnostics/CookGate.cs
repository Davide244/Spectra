using System;

namespace Spectra.Kitchen.Diagnostics;

/// <summary>
/// What the gate decided one diagnostic code is worth.
/// </summary>
/// <remarks>
/// <b>Four points on one scale and one deliberate escape.</b> The scale is how
/// loud a code is allowed to be and whether <c>--strict</c> may move it;
/// <see cref="AsReported"/> is not a point on it at all, and says so in its own
/// remarks.
/// </remarks>
public enum CookGateVerdict
{
    /// <summary>
    /// Info, always. Something worth saying that changes nothing about the
    /// outcome, and that <c>--strict</c> must not turn into a failed build.
    /// </summary>
    Note,

    /// <summary>
    /// Warning, always. A complaint about the RUN rather than about the data:
    /// the artifact this cook produced is correct and complete, and what went
    /// wrong was the machinery around it.
    /// </summary>
    /// <remarks>
    /// <b>Exempt from <c>--strict</c> on purpose.</b> A build tool that failed
    /// because its own cache would not save, or because a switch it accepts is
    /// not wired up yet, is failing over something that says nothing about
    /// whether the content is shippable. Strict means "this run is the gate on
    /// the DATA".
    /// </remarks>
    Warning,

    /// <summary>
    /// Warning by default, error under <c>--strict</c>. The soft half of
    /// <c>docs/formats-and-pipeline.md</c> 4.2.
    /// </summary>
    WarningUnlessStrict,

    /// <summary>
    /// Error, always, whatever the flags say. The loud half of
    /// <c>docs/formats-and-pipeline.md</c> 4.2: a pack carrying this is refused.
    /// </summary>
    Fatal,

    /// <summary>
    /// The severity belongs to the tool whose diagnostic this carries, and the
    /// gate passes it through. <c>--strict</c> still promotes a warning.
    /// </summary>
    /// <remarks>
    /// <b>The one verdict that is not a decision, and it is a decision to make
    /// it so.</b> A wrapped <c>SS####</c> shader diagnostic - and <c>SC6001</c>,
    /// which is the placeholder standing in for one until the compiler numbers
    /// its own - carries a severity the compiler chose about a specific source
    /// line. Flattening those to one verdict is wrong in both directions: fatal
    /// makes the cooker refuse a build over a shader warning that <c>ssc</c>
    /// merely printed, and soft demotes a genuine compile error to a warning and
    /// ships a shader that does not exist. So this code class delegates, once,
    /// here, rather than every reporting site deciding for itself.
    /// </remarks>
    AsReported,
}

/// <summary>
/// The one place that decides what a cook diagnostic COSTS: which codes refuse
/// the pack, which merely warn, and which <c>--strict</c> is allowed to move.
/// </summary>
/// <remarks>
/// <para><b>This table is <c>docs/formats-and-pipeline.md</c> 4.2, and the whole
/// reason it is a table is the asymmetry that section exists to record.</b>
/// <c>CLAUDE.md</c> pins that content errors never reach the draw loop: a
/// missing material degrades to <c>AssetManager.DefaultMaterial</c>, a missing
/// texture to the magenta placeholder, each with a warning, because a frame must
/// keep rendering. That is exactly right for a running frame and exactly wrong
/// for a build step whose job is to stop broken data shipping. The two
/// behaviours are the same lookup with different consequences, neither is a bug
/// in the other, and the failure this guards against is somebody "fixing" one to
/// match the other - so both halves are pinned together in
/// <c>CookGateTests</c>.</para>
/// <para><b>One table, read by the cook AND by the verifier.</b> They ask the
/// same question about the same content at different moments - the cook about a
/// project folder, the verifier about a written pack - and before this existed
/// they answered it separately, each choosing a severity at every reporting
/// site. Two opinions about what a valid pack is drift the first time one of
/// them is corrected, and the drift is silent: the cook passes what the verify
/// refuses, or worse, the verify passes what the cook would have refused and the
/// pack ships.</para>
/// <para><b>A severity a rule chose is normalised, never merely accepted.</b>
/// Reporting sites still read naturally - <c>CookDiagnostic.Error</c> where the
/// author meant an error - and the gate then decides, so a site that reported a
/// fatal code as a warning cannot quietly weaken the build. The exception is
/// <see cref="CookGateVerdict.AsReported"/>, which is the deliberate
/// delegation, not an oversight.</para>
/// <para><b>An unclassified code is FATAL, and the two directions of that
/// mistake are not symmetric.</b> Defaulted soft, a code somebody forgot to
/// classify lets exactly the data it was written to stop reach a shipped build,
/// silently. Defaulted fatal, it is a failed build with a code in it, fixed by
/// one line in this table. <see cref="IsClassified"/> exists so a convention
/// test can fail the build's test run instead, which is where this should
/// actually be caught.</para>
/// </remarks>
public static class CookGate
{
    /// <summary>What the gate decided <paramref name="id"/> is worth.</summary>
    public static CookGateVerdict Verdict(CookDiagnosticId id)
    {
        // A foreign tool's code is that tool's to judge. Reclassifying it here
        // would make the cooker louder or quieter than ssc and the language
        // server about the same source line, which is the precise failure
        // CookDiagnosticId.Wrap exists to avoid.
        if (!id.IsCookCode) return CookGateVerdict.AsReported;

        return Classify(id.Number) ?? CookGateVerdict.Fatal;
    }

    /// <summary>
    /// Whether this table has an opinion about <paramref name="id"/> at all.
    /// </summary>
    /// <remarks>
    /// For the convention test, and for nothing else. <see cref="Verdict"/>
    /// answers <see cref="CookGateVerdict.Fatal"/> either way, so an
    /// unclassified code is indistinguishable from a fatal one at a reporting
    /// site - which is the safe direction to be wrong in and a useless one to
    /// debug from.
    /// </remarks>
    public static bool IsClassified(CookDiagnosticId id) =>
        id.IsCookCode && Classify(id.Number) is not null;

    /// <summary>Whether a pack carrying <paramref name="id"/> is refused.</summary>
    public static bool IsFatal(CookDiagnosticId id) => Verdict(id) == CookGateVerdict.Fatal;

    /// <summary>
    /// <paramref name="diagnostic"/> at the severity the gate decided, given
    /// whether this run is <c>--strict</c>.
    /// </summary>
    /// <remarks>
    /// Returns the same instance when the severity already matches, so a run
    /// with nothing to correct allocates nothing.
    /// </remarks>
    public static CookDiagnostic Apply(CookDiagnostic diagnostic, bool strict)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        CookDiagnosticSeverity decided = Verdict(diagnostic.Id) switch
        {
            CookGateVerdict.Note => CookDiagnosticSeverity.Info,
            CookGateVerdict.Warning => CookDiagnosticSeverity.Warning,
            CookGateVerdict.WarningUnlessStrict =>
                strict ? CookDiagnosticSeverity.Error : CookDiagnosticSeverity.Warning,
            CookGateVerdict.Fatal => CookDiagnosticSeverity.Error,

            // AsReported: the reporter's severity stands, and --strict still
            // promotes a warning, because that is what --strict means for every
            // other warning in the run.
            _ => strict && diagnostic.Severity == CookDiagnosticSeverity.Warning
                ? CookDiagnosticSeverity.Error
                : diagnostic.Severity,
        };

        return diagnostic.Severity == decided ? diagnostic : diagnostic with { Severity = decided };
    }

    // Numbers rather than the CookDiagnosticCodes members, because those are
    // static readonly fields and a switch case needs a constant. That is safe
    // for the one reason it would otherwise be dangerous: a code's number is
    // append-only and never reused, so a member cannot be renumbered out from
    // under this table - and CookGateTests asserts that every declared code
    // appears here, which is the drift a comment could not catch.
    private static CookGateVerdict? Classify(int number) => number switch
    {
        // --- 0xxx: project and CLI -------------------------------------------
        1 => CookGateVerdict.Fatal,                // SC0001 ProjectNotOpened
        2 => CookGateVerdict.Fatal,                // SC0002 VerbNotImplemented
        3 => CookGateVerdict.Warning,              // SC0003 OptionNotImplemented
        4 => CookGateVerdict.Fatal,                // SC0004 OutputNotWritable
        5 => CookGateVerdict.Fatal,                // SC0005 UnsafeCleanTarget

        // --- 1xxx: discovery and the dependency graph ------------------------
        1001 => CookGateVerdict.Fatal,             // SC1001 ContentRootMissing
        1002 => CookGateVerdict.Fatal,             // SC1002 InputMissing
        1003 => CookGateVerdict.Fatal,             // SC1003 InputPathInvalid
        1004 => CookGateVerdict.Fatal,             // SC1004 RuleFailed
        1005 => CookGateVerdict.Note,              // SC1005 ContentNotCooked
        1006 => CookGateVerdict.Warning,           // SC1006 CacheNotWritable
        1007 => CookGateVerdict.Note,              // SC1007 CacheDiscarded

        // --- 2xxx: image and texture -----------------------------------------
        2001 => CookGateVerdict.Fatal,             // SC2001 ImageUndecodable
        2002 => CookGateVerdict.Fatal,             // SC2002 ImageEncodeFailed
        2003 => CookGateVerdict.Fatal,             // SC2003 ImageFileUnreadable

        // --- 3xxx: model ------------------------------------------------------
        3001 => CookGateVerdict.Fatal,             // SC3001 ModelUndecodable
        3002 => CookGateVerdict.WarningUnlessStrict, // SC3002 ModelMaterialUnauthored
        3003 => CookGateVerdict.Fatal,             // SC3003 ModelEncodeFailed
        3004 => CookGateVerdict.Note,              // SC3004 ModelDataDropped
        3005 => CookGateVerdict.Fatal,             // SC3005 ModelFileUnreadable
        3006 => CookGateVerdict.Fatal,             // SC3006 ModelMaterialMissing

        // --- 4xxx: audio ------------------------------------------------------
        4001 => CookGateVerdict.Fatal,             // SC4001 AudioUndecodable
        4002 => CookGateVerdict.Fatal,             // SC4002 AudioEncodeFailed
        4003 => CookGateVerdict.WarningUnlessStrict, // SC4003 AudioStereoPositional
        4004 => CookGateVerdict.Note,              // SC4004 AudioResampled
        4005 => CookGateVerdict.WarningUnlessStrict, // SC4005 AudioLoopUnusable
        4006 => CookGateVerdict.Fatal,             // SC4006 AudioFileUnreadable

        // --- 5xxx: material ---------------------------------------------------
        5001 => CookGateVerdict.Fatal,             // SC5001 MaterialTextureMissing
        5002 => CookGateVerdict.WarningUnlessStrict, // SC5002 MaterialFileMalformed
        5003 => CookGateVerdict.Fatal,             // SC5003 MaterialShaderMissing

        // --- 6xxx: shader -----------------------------------------------------
        6001 => CookGateVerdict.AsReported,        // SC6001 ShaderCompileFailed
        6002 => CookGateVerdict.Fatal,             // SC6002 ShaderBackendMissing
        6003 => CookGateVerdict.Fatal,             // SC6003 ShaderFileUnreadable
        6004 => CookGateVerdict.Fatal,             // SC6004 ShaderBackendUnsupported
        6005 => CookGateVerdict.WarningUnlessStrict, // SC6005 ShaderNoTargets

        // --- 7xxx: map and geometry -------------------------------------------
        // Classified before the map rule that issues them exists, which is the
        // point of a gate rather than a convention: what is fatal is decided
        // once, here, instead of by whichever rule reports it first.
        7001 => CookGateVerdict.Fatal,             // SC7001 MapBrushNonRigid
        7002 => CookGateVerdict.Fatal,             // SC7002 MapBrushRefused
        7003 => CookGateVerdict.Fatal,             // SC7003 MapNodeIdDuplicate
        7004 => CookGateVerdict.Fatal,             // SC7004 MapFaceCountMismatch
        7005 => CookGateVerdict.WarningUnlessStrict, // SC7005 MapConnectionTargetMissing
        7006 => CookGateVerdict.WarningUnlessStrict, // SC7006 MapEntityClassUnknown
        7007 => CookGateVerdict.Fatal,             // SC7007 MapDocumentMalformed

        // --- 8xxx: script ------------------------------------------------------
        8001 => CookGateVerdict.Fatal,             // SC8001 ScriptSyntaxError

        // --- 9xxx: pack writing and integrity ----------------------------------
        9001 => CookGateVerdict.Fatal,             // SC9001 PackWriteFailed
        9002 => CookGateVerdict.Fatal,             // SC9002 PackEntryCollision
        9003 => CookGateVerdict.Fatal,             // SC9003 PackNotMountable
        9004 => CookGateVerdict.Fatal,             // SC9004 PackEntryUnreadable
        9005 => CookGateVerdict.Fatal,             // SC9005 PackEntryTableUnsorted
        9006 => CookGateVerdict.WarningUnlessStrict, // SC9006 PackEntryNotVerifiable

        _ => null,
    };
}
