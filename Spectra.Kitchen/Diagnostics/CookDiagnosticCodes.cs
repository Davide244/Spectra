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

    // --- 2xxx image, 3xxx model, 4xxx audio, 5xxx material -------------------
    // Reserved. The rules that issue them are unbuilt; see the band table above.

    // --- 6xxx: shader --------------------------------------------------------
    // Reserved, and mostly not ours to spend: a diagnostic the shader compiler
    // produced travels under its own SS#### code via CookDiagnosticId.Wrap, so
    // this band holds only failures the COOKER owns, such as a shader with no
    // blob for a requested backend.

    // --- 7xxx map and geometry, 8xxx script ----------------------------------
    // Reserved.

    // --- 9xxx: pack writing and integrity ------------------------------------

    /// <summary>The pack could not be written.</summary>
    public static readonly CookDiagnosticId PackWriteFailed = CookDiagnosticId.Cook(9001);

    /// <summary>Two assets claim one pack entry.</summary>
    public static readonly CookDiagnosticId PackEntryCollision = CookDiagnosticId.Cook(9002);

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
