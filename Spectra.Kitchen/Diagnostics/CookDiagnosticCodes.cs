namespace Spectra.Kitchen.Diagnostics;

/// <summary>
/// The reserved <c>SC####</c> number space, and every code the cooker issues
/// today.
/// </summary>
/// <remarks>
/// <para><b>The bands are the reservation, and they are the whole point of
/// reserving a prefix at all.</b> A code says which SUBSYSTEM failed before it
/// says anything else, so a person reading a build log knows whether to look at
/// their material, their model or the pack writer without reading the message
/// first:</para>
/// <list type="table">
/// <item><term>0xxx</term><description>project and CLI</description></item>
/// <item><term>1xxx</term><description>discovery and the dependency graph</description></item>
/// <item><term>2xxx</term><description>image and texture</description></item>
/// <item><term>3xxx</term><description>model</description></item>
/// <item><term>4xxx</term><description>audio</description></item>
/// <item><term>5xxx</term><description>material</description></item>
/// <item><term>6xxx</term><description>shader, WRAPPING <c>SS####</c> rather than renumbering it</description></item>
/// <item><term>7xxx</term><description>map and geometry</description></item>
/// <item><term>8xxx</term><description>script</description></item>
/// <item><term>9xxx</term><description>pack writing and integrity</description></item>
/// </list>
/// <para><b>A band with no codes in it yet is still reserved.</b> Most of them are
/// empty here because the rules that would issue them are unbuilt; the numbers
/// exist so the first image rule does not have to negotiate for a range with the
/// first audio rule.</para>
/// <para><b>The prefix collision with <c>docs/console.md</c> is resolved in the
/// cooker's favour</b> and the console arc renumbers to <c>CV####</c>. The reason
/// is asymmetric rather than a coin toss: this prefix is already bound into the
/// stderr contract every consumer of <c>scook</c> parses, and into a band plan
/// whose 6xxx entry is a WRAP of another tool's numbers, so renumbering here
/// would break a code that is deliberately not ours to renumber.</para>
/// </remarks>
public static class CookDiagnosticCodes
{
    // --- 0xxx: project and CLI -----------------------------------------------

    /// <summary>The path named is not a Spectra project.</summary>
    public static readonly CookDiagnosticId ProjectNotOpened = CookDiagnosticId.Cook(1);

    /// <summary>A verb the tool accepts and has not built yet.</summary>
    /// <remarks>
    /// Reported as an ERROR and exits non-zero, deliberately. A verb that
    /// silently does nothing and reports success teaches within one session that
    /// this tool's verbs are decorative, which is the failure the editor shell
    /// already refuses for dead buttons.
    /// </remarks>
    public static readonly CookDiagnosticId VerbNotImplemented = CookDiagnosticId.Cook(2);

    /// <summary>An option the tool accepts and does not act on yet.</summary>
    /// <remarks>
    /// A warning rather than an error, because the request is legitimate and the
    /// cook it asked for still happens; what is missing is the behaviour the
    /// switch selects. It is reported straight by the CLI rather than through the
    /// session, so <c>--strict</c> does not turn "this build ignores your
    /// <c>-j8</c>" into a failed build.
    /// </remarks>
    public static readonly CookDiagnosticId OptionNotImplemented = CookDiagnosticId.Cook(3);

    /// <summary>The output location could not be written.</summary>
    public static readonly CookDiagnosticId OutputNotWritable = CookDiagnosticId.Cook(4);

    /// <summary>A clean was asked to delete something that is not cook output.</summary>
    public static readonly CookDiagnosticId UnsafeCleanTarget = CookDiagnosticId.Cook(5);

    // --- 1xxx: discovery and the dependency graph ----------------------------

    /// <summary>The project has no content root to walk.</summary>
    public static readonly CookDiagnosticId ContentRootMissing = CookDiagnosticId.Cook(1001);

    /// <summary>A rule read or probed a path that is not in the content root.</summary>
    /// <remarks>
    /// The miss is recorded as a dependency before this is reported. That is the
    /// point of the recording: adding the file later must invalidate the rule
    /// that looked for it, or a watch loop serves a stale cook and reports
    /// success.
    /// </remarks>
    public static readonly CookDiagnosticId InputMissing = CookDiagnosticId.Cook(1002);

    /// <summary>A path a rule named cannot be a content path at all.</summary>
    public static readonly CookDiagnosticId InputPathInvalid = CookDiagnosticId.Cook(1003);

    /// <summary>A rule failed for a reason it did not report itself.</summary>
    public static readonly CookDiagnosticId RuleFailed = CookDiagnosticId.Cook(1004);

    /// <summary>Content the cook found and has no rule for, so it is not in the artifact.</summary>
    /// <remarks>
    /// Said out loud rather than left implicit: a pack that mounts cleanly and has
    /// no level in it looks exactly like a working cook.
    /// </remarks>
    public static readonly CookDiagnosticId ContentNotCooked = CookDiagnosticId.Cook(1005);

    /// <summary>The cook cache could not be written.</summary>
    /// <remarks>
    /// A warning rather than an error: the artifact this run produced is correct
    /// and complete, and what was lost is the next run's ability to skip work. A
    /// cook that failed because a cache file would not save would be a build tool
    /// failing over its own optimisation.
    /// </remarks>
    public static readonly CookDiagnosticId CacheNotWritable = CookDiagnosticId.Cook(1006);

    /// <summary>A cook cache on disk could not be read and was discarded.</summary>
    /// <remarks>
    /// Info rather than silence. Discarding is the right answer for derived data,
    /// but a cook that rebuilds everything because its cache would not parse looks
    /// exactly like a slow cook, and "why is this not incremental" is
    /// unanswerable without a line saying so.
    /// </remarks>
    public static readonly CookDiagnosticId CacheDiscarded = CookDiagnosticId.Cook(1007);

    // --- 2xxx image, 3xxx model, 4xxx audio ----------------------------------
    // Reserved. The rules that issue them are unbuilt; see the band table above.

    // --- 5xxx: material ------------------------------------------------------
    //
    // Issued by the VERIFIER rather than by a material rule, which does not
    // exist yet, and that is the band's rule working rather than an exception to
    // it: a code names the subsystem that failed, and a texture nobody cooked is
    // a material problem however it was found. When the material rule lands it
    // reports the same failure under the same code.

    /// <summary>A cooked material names a texture the pack does not hold.</summary>
    /// <remarks>
    /// The single failure cooked-only validation exists to catch. In the running
    /// engine it is a magenta placeholder and a warning; here it is fatal,
    /// because a build step whose job is to stop broken data shipping must not
    /// share the runtime's soft landing. See <c>docs/formats-and-pipeline.md</c>
    /// 4.2 for the asymmetry, written down so nobody "fixes" one to match the
    /// other.
    /// </remarks>
    public static readonly CookDiagnosticId MaterialTextureMissing = CookDiagnosticId.Cook(5001);

    /// <summary>A cooked material has a line the parser could not use.</summary>
    /// <remarks>
    /// A warning, matching the parser: an unknown key is deliberately tolerated
    /// so material files stay forward-compatible, and turning that into an error
    /// here would refuse every file written ahead of the engine.
    /// </remarks>
    public static readonly CookDiagnosticId MaterialFileMalformed = CookDiagnosticId.Cook(5002);

    // --- 6xxx: shader --------------------------------------------------------
    //
    // Mostly not ours to spend: a diagnostic the shader compiler produced is
    // meant to travel under its own SS#### code via CookDiagnosticId.Wrap, so
    // this band holds failures the COOKER owns.
    //
    // SC6001 is the one deliberate exception, and it is a placeholder for a
    // wrap rather than a decision against one: SpectraShade's Diagnostic carries
    // a severity, a message and a span and NO number, anywhere in the compiler.
    // Minting numbers here would put codes on the compiler's behalf that ssc and
    // the language server do not agree with, which is the precise failure
    // wrapping exists to avoid. When the compiler numbers its diagnostics, the
    // shader rule wraps them and SC6001 is retired rather than reused.

    /// <summary>The shader compiler refused a source file. Carries its message verbatim.</summary>
    public static readonly CookDiagnosticId ShaderCompileFailed = CookDiagnosticId.Cook(6001);

    /// <summary>
    /// A cooked shader has no blob for a backend it was cooked for.
    /// </summary>
    /// <remarks>
    /// <b>Fatal in the cooker, silent at runtime, which is why it needs a
    /// code.</b> The engine falls back to compiling that shader from source and
    /// renders the right picture, so a pack one blob short mounts cleanly, runs
    /// correctly and pays for a compiler front end on every launch of the build
    /// that was meant to have left one behind. Nothing on screen and nothing in
    /// a build log says so unless this is reported.
    /// </remarks>
    public static readonly CookDiagnosticId ShaderBackendMissing = CookDiagnosticId.Cook(6002);

    /// <summary>A cooked shader's payload is not a readable <c>.specshadecomp</c> file.</summary>
    public static readonly CookDiagnosticId ShaderFileUnreadable = CookDiagnosticId.Cook(6003);

    /// <summary>A target backend this toolchain has no code generator for.</summary>
    /// <remarks>
    /// Vulkan today: <c>SpirVGenerator</c> throws rather than emitting. Named
    /// rather than left to arrive as SC1004 "the Shader rule failed", because a
    /// request the toolchain cannot serve yet is a different thing from a rule
    /// that broke.
    /// </remarks>
    public static readonly CookDiagnosticId ShaderBackendUnsupported = CookDiagnosticId.Cook(6004);

    /// <summary>A shader was cooked with an empty target list, so it produced nothing.</summary>
    /// <remarks>
    /// A warning rather than an error: asking for no backends is a legitimate
    /// thing a caller can express, and the cook it asked for still happened. What
    /// would not be legitimate is doing it silently, because the pack then has no
    /// shader in it and looks exactly like a working cook.
    /// </remarks>
    public static readonly CookDiagnosticId ShaderNoTargets = CookDiagnosticId.Cook(6005);

    // --- 7xxx map and geometry, 8xxx script ----------------------------------
    // Reserved.

    // --- 9xxx: pack writing and integrity ------------------------------------

    /// <summary>The pack could not be written.</summary>
    public static readonly CookDiagnosticId PackWriteFailed = CookDiagnosticId.Cook(9001);

    /// <summary>Two assets claim one pack entry.</summary>
    public static readonly CookDiagnosticId PackEntryCollision = CookDiagnosticId.Cook(9002);

    /// <summary>The pack was refused: its header, its regions or its digest.</summary>
    /// <remarks>
    /// One code for all of them because they share an answer, which is to recook.
    /// The message is the reader's own and names which check failed; splitting it
    /// into three codes would ask a build script to distinguish cases that differ
    /// only in how the file got corrupted.
    /// </remarks>
    public static readonly CookDiagnosticId PackNotMountable = CookDiagnosticId.Cook(9003);

    /// <summary>An entry is present and its payload does not decode.</summary>
    /// <remarks>
    /// <b>This is what the digest cannot catch.</b> A payload rewritten together
    /// with the digest over it hashes correctly and is still not a deflate
    /// stream, so the decode pass is a claim of its own rather than a slower
    /// restatement of the digest check.
    /// </remarks>
    public static readonly CookDiagnosticId PackEntryUnreadable = CookDiagnosticId.Cook(9004);

    /// <summary>The entry table on disk is not strictly ascending by asset id.</summary>
    /// <remarks>
    /// The writer sorts, so this can only be a file that was edited after it was
    /// written or a writer that regressed. Either way a binary search over it
    /// misses entries SILENTLY, which presents as content that is intermittently
    /// absent rather than as a corrupt file.
    /// </remarks>
    public static readonly CookDiagnosticId PackEntryTableUnsorted = CookDiagnosticId.Cook(9005);

    /// <summary>An entry could not be verified, and this says which and why.</summary>
    /// <remarks>
    /// Today the one case is an entry with no name-table record in a pack written
    /// without one: identity is a path, so with no name there is nothing to ask
    /// the reader for. Reported rather than skipped, because a verify that
    /// quietly checked fewer entries than the pack holds is a gate that weakens
    /// without failing.
    /// </remarks>
    public static readonly CookDiagnosticId PackEntryNotVerifiable = CookDiagnosticId.Cook(9006);

    /// <summary>
    /// Whether <paramref name="number"/> was retired. A retired code is never
    /// reused.
    /// </summary>
    /// <remarks>
    /// <b>Empty today, and it exists anyway.</b> This is the place a retirement is
    /// recorded, and a list that only appears once the first code is withdrawn is
    /// a list nobody remembers to create. A hand-written switch rather than a
    /// collection, so the check costs nothing and carries no static state that
    /// could initialise after the codes above.
    /// </remarks>
    public static bool IsRetired(int number) => number switch
    {
        _ => false,
    };

    /// <summary>The subsystem a band names, for help text and for log lines.</summary>
    public static string DescribeBand(int band) => band switch
    {
        0 => "project and CLI",
        1 => "discovery and dependencies",
        2 => "image",
        3 => "model",
        4 => "audio",
        5 => "material",
        6 => "shader",
        7 => "map and geometry",
        8 => "script",
        9 => "pack writing",
        _ => "unknown",
    };
}
