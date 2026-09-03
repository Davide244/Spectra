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

    // --- 2xxx: image and texture ---------------------------------------------

    /// <summary>An image file the decoder could not read.</summary>
    /// <remarks>
    /// An error rather than a fall-through to the raw copy, and the difference
    /// matters: copied, the broken file would sit in the pack under a path the
    /// engine resolves, the runtime would degrade it to the magenta placeholder
    /// with a warning, and this build log would say a texture cooked.
    /// </remarks>
    public static readonly CookDiagnosticId ImageUndecodable = CookDiagnosticId.Cook(2001);

    /// <summary>An image decoded and could not be turned into a cooked container.</summary>
    /// <remarks>
    /// Distinct from <see cref="ImageUndecodable"/> because the two point at
    /// different things: that one is the author's file, this one is the encoder
    /// or the container disagreeing about a mip chain, which is the cooker's own
    /// problem and not something re-saving the PNG would fix.
    /// </remarks>
    public static readonly CookDiagnosticId ImageEncodeFailed = CookDiagnosticId.Cook(2002);

    /// <summary>A cooked image in a pack is not a readable <c>.simage</c>.</summary>
    /// <remarks>
    /// Issued by the VERIFIER. The reader's own message travels verbatim, since
    /// it already names which rule the file broke and every one of them has the
    /// same answer, which is to recook.
    /// </remarks>
    public static readonly CookDiagnosticId ImageFileUnreadable = CookDiagnosticId.Cook(2003);

    // --- 3xxx: model ---------------------------------------------------------
    //
    // Issued by BOTH the model rule and the verifier, which is the band's rule
    // working rather than two answers to one question: the cook asks whether the
    // AUTHOR's content is consistent and the verify asks whether the ARTIFACT is,
    // and a model whose material is not there is a model problem either way.

    /// <summary>A model file the glTF reader could not read.</summary>
    /// <remarks>
    /// An error rather than a fall-through to the raw copy, for the reason
    /// <see cref="ImageUndecodable"/> gives two bands over: copied, the file
    /// would sit in the pack under a path the engine resolves, the runtime would
    /// hand it to the loose importer that is not there in a shipped build, and
    /// this build log would say a model cooked. It carries the reader's own
    /// message, which names the construct that was refused - a primitive mode, a
    /// required extension, a component type - because "re-export triangulated" is
    /// actionable and "could not read the model" is not.
    /// </remarks>
    public static readonly CookDiagnosticId ModelUndecodable = CookDiagnosticId.Cook(3001);

    /// <summary>A model names a material this project does not author.</summary>
    /// <remarks>
    /// <b>Soft, and the reason it is soft is that the file being blamed is not
    /// the one that is wrong.</b> A <c>SUBM</c> material reference is a logical
    /// asset path, so a cooked model can only name a material that EXISTS as a
    /// <c>.spectramat</c>; a glTF that describes its own surface inline - a base
    /// colour texture and a factor, which is what every exporter writes - has
    /// nothing for the cooked format to point at, and the cooked submesh falls
    /// back to the engine's default material. That is a real difference between
    /// the loose path and the packed one and it deserves saying out loud, but the
    /// author's model is valid and the limitation is <c>.smodel</c> v1's, so
    /// refusing the build over it would blame the wrong party. Authoring
    /// <c>Materials/&lt;name&gt;.spectramat</c> is what silences it, and
    /// <c>--strict</c> is how a ship gate asks for the stricter reading.
    /// </remarks>
    public static readonly CookDiagnosticId ModelMaterialUnauthored = CookDiagnosticId.Cook(3002);

    /// <summary>A model read and could not be written into a cooked container.</summary>
    /// <remarks>
    /// Distinct from <see cref="ModelUndecodable"/> because the two point at
    /// different things: that one is the author's file, this one is the reader or
    /// the writer disagreeing about a count, which is the cooker's own problem
    /// and not something re-exporting would fix.
    /// </remarks>
    public static readonly CookDiagnosticId ModelEncodeFailed = CookDiagnosticId.Cook(3003);

    /// <summary>A model carried something the cooked format does not.</summary>
    /// <remarks>
    /// Info, and it exists so that dropping is never SILENT. A vertex colour set,
    /// a tangent, a second UV, a skin, a morph target: none of them makes a model
    /// unusable and none survives into a v1 <c>.smodel</c>, so the geometry is
    /// carried and the loss is named. Without this line, "my vertex colours do
    /// nothing in the engine" is a question with no answer anywhere in a build
    /// log.
    /// </remarks>
    public static readonly CookDiagnosticId ModelDataDropped = CookDiagnosticId.Cook(3004);

    /// <summary>A cooked model in a pack is not a readable <c>.smodel</c>.</summary>
    /// <remarks>
    /// Issued by the VERIFIER, and the sibling of <see cref="ImageFileUnreadable"/>
    /// and <see cref="AudioFileUnreadable"/>. The reader's own message travels
    /// verbatim, since it already names which rule the file broke and every one
    /// of them has the same answer, which is to recook.
    /// </remarks>
    public static readonly CookDiagnosticId ModelFileUnreadable = CookDiagnosticId.Cook(3005);

    /// <summary>A cooked model names a material that is not in the pack.</summary>
    /// <remarks>
    /// <b>The failure a cook structurally cannot see.</b> The model rule checks
    /// its material reference against the project folder and the material rule
    /// cooks materials, and both can succeed while the entry one of them needed
    /// never reaches the file - a rule that failed for its own reasons, an
    /// entry-path collision, a pack somebody edited. At runtime the submesh binds
    /// the default material and carries on, so the shipped game renders a grey
    /// prop and nothing anywhere says why.
    /// </remarks>
    public static readonly CookDiagnosticId ModelMaterialMissing = CookDiagnosticId.Cook(3006);

    // --- 4xxx: audio ---------------------------------------------------------

    /// <summary>An audio file the decoder could not read.</summary>
    /// <remarks>
    /// An error rather than a fall-through to the raw copy, for the reason
    /// <see cref="ImageUndecodable"/> gives one file type over: copied, the
    /// broken file would sit in the pack under a path the engine resolves, the
    /// runtime would refuse it at load, and this build log would say a sound
    /// cooked.
    /// </remarks>
    public static readonly CookDiagnosticId AudioUndecodable = CookDiagnosticId.Cook(4001);

    /// <summary>An audio file decoded and could not be written into a cooked container.</summary>
    /// <remarks>
    /// Distinct from <see cref="AudioUndecodable"/> because the two point at
    /// different things: that one is the author's file, this one is the resampler
    /// or the container disagreeing about a length, which is the cooker's own
    /// problem and not something re-exporting the WAV would fix.
    /// </remarks>
    public static readonly CookDiagnosticId AudioEncodeFailed = CookDiagnosticId.Cook(4002);

    /// <summary>A stereo sound that nothing says is meant to be flat.</summary>
    /// <remarks>
    /// <b>Silent at runtime, which is the whole reason it has a code.</b> OpenAL
    /// will not spatialise a stereo buffer: it plays at full level wherever the
    /// listener stands, with no error and no warning, and the report that comes
    /// back is "why is my 3D sound not 3D". Free to catch here, where the channel
    /// count is in hand. Soft rather than fatal because music and ambience are
    /// legitimately stereo, and the way to say so is to name the file so it ends
    /// <c>_2d</c>, which is what silences this.
    /// </remarks>
    public static readonly CookDiagnosticId AudioStereoPositional = CookDiagnosticId.Cook(4003);

    /// <summary>A sound was resampled to the project rate.</summary>
    /// <remarks>
    /// Info rather than silence. Resampling is lossy and it moves every loop
    /// point, so a sound that measures differently after a cook than the file a
    /// person exported is a question somebody will ask; a line saying which rate
    /// it came from and which it went to is the whole answer, and its absence
    /// makes the cook look like it corrupted something.
    /// </remarks>
    public static readonly CookDiagnosticId AudioResampled = CookDiagnosticId.Cook(4004);

    /// <summary>A loop the source declared that the cooked format cannot carry, so it was dropped.</summary>
    /// <remarks>
    /// One code for a loop past the end of its own data, an empty region and an
    /// alternating or backward loop, because they share an answer: the sound
    /// plays once, and the author has to fix the loop. Said out loud rather than
    /// repaired, since a cooker that quietly moved a loop point would leave a log
    /// saying the sound was fine.
    /// </remarks>
    public static readonly CookDiagnosticId AudioLoopUnusable = CookDiagnosticId.Cook(4005);

    /// <summary>A cooked sound in a pack is not a readable <c>.saudio</c>.</summary>
    /// <remarks>
    /// Issued by the VERIFIER, and the mirror of <see cref="ImageFileUnreadable"/>
    /// one band over. The reader's own message travels verbatim, since it already
    /// names which rule the file broke and every one of them has the same answer,
    /// which is to recook.
    /// </remarks>
    public static readonly CookDiagnosticId AudioFileUnreadable = CookDiagnosticId.Cook(4006);

    // --- 5xxx: material ------------------------------------------------------
    //
    // Issued by BOTH the material rule and the verifier, which is the band's rule
    // working rather than two answers to one question: a code names the subsystem
    // that failed, and a texture nobody cooked is a material problem whether it
    // was found in the project folder or in a written pack. The severity is not
    // chosen at either site - CookGate decides it once for both.

    /// <summary>A material names a texture that is not there.</summary>
    /// <remarks>
    /// The single failure cooked-only validation exists to catch. In the running
    /// engine it is a magenta placeholder and a warning; here it is fatal,
    /// because a build step whose job is to stop broken data shipping must not
    /// share the runtime's soft landing. See <c>docs/formats-and-pipeline.md</c>
    /// 4.2 for the asymmetry, written down so nobody "fixes" one to match the
    /// other.
    /// </remarks>
    public static readonly CookDiagnosticId MaterialTextureMissing = CookDiagnosticId.Cook(5001);

    /// <summary>A material has a line the parser could not use.</summary>
    /// <remarks>
    /// A warning, matching the parser: an unknown key is deliberately tolerated
    /// so material files stay forward-compatible, and turning that into an error
    /// here would refuse every file written ahead of the engine. It is the soft
    /// half of 4.2, so <c>--strict</c> does promote it.
    /// </remarks>
    public static readonly CookDiagnosticId MaterialFileMalformed = CookDiagnosticId.Cook(5002);

    /// <summary>A material names a shader that nothing in the content provides.</summary>
    /// <remarks>
    /// <b>Fatal, and silent at runtime, which is the whole reason it has a
    /// code.</b> <c>AssetManager.ResolveShader</c> falls back to the built-in lit
    /// program and logs a warning, so a shipped game draws the surface with a
    /// program its author did not choose and renders a picture that is merely
    /// wrong. What the cooker can see is CONTENT: the built-in name, and a
    /// shader asset in the project. A name only a host's
    /// <c>AssetManager.ShaderResolver</c> claims is invisible from here and is
    /// reported, because a gate that passed everything it could not see would be
    /// no gate at all.
    /// </remarks>
    public static readonly CookDiagnosticId MaterialShaderMissing = CookDiagnosticId.Cook(5003);

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

    // --- 7xxx: map and geometry, 8xxx: script --------------------------------
    //
    // Declared ahead of the rules that issue them, which is not the same thing as
    // reserving a band. These are the codes docs/formats-and-pipeline.md 4.2
    // names on either side of its fatal line, and CookGate classifies every one
    // of them today: what a cook refuses is decided once, in one table, rather
    // than by whichever rule reports it first and however that rule's author
    // happened to feel about it. A number is cheap; a map rule that lands next
    // year and quietly makes a non-rigid brush a warning is not.

    /// <summary>A brush node's world transform is not rigid.</summary>
    /// <remarks>
    /// Report <c>Scene.DescribeNonRigidDefect</c>'s message and NAME THE NODE:
    /// the defect is a scale or a shear somewhere up an ancestor chain, so the
    /// brush that fails is rarely the node somebody edited. In the running editor
    /// this is a standing status warning and the last good world keeps rendering;
    /// in a cook it is a level that cannot be compiled.
    /// </remarks>
    public static readonly CookDiagnosticId MapBrushNonRigid = CookDiagnosticId.Cook(7001);

    /// <summary>A plane set <c>Brush</c>'s own constructor rejects.</summary>
    public static readonly CookDiagnosticId MapBrushRefused = CookDiagnosticId.Cook(7002);

    /// <summary>Two scene nodes in one map claim the same Guid.</summary>
    /// <remarks>
    /// Every editor command addresses a node by id, so a duplicate makes undo,
    /// selection and every reference into the graph ambiguous - and the ambiguity
    /// resolves by traversal order, which means it presents as an edit landing on
    /// the wrong object rather than as anything that fails.
    /// </remarks>
    public static readonly CookDiagnosticId MapNodeIdDuplicate = CookDiagnosticId.Cook(7003);

    /// <summary>A brush record's face count does not match its plane count.</summary>
    /// <remarks>
    /// One <c>FaceSurface</c> per plane is the invariant the whole per-face
    /// material path rests on. A mismatch is not a rendering problem, it is an
    /// indexing one, so it is fatal here rather than degraded to the default
    /// surface.
    /// </remarks>
    public static readonly CookDiagnosticId MapFaceCountMismatch = CookDiagnosticId.Cook(7004);

    /// <summary>An entity connection names a target no node carries.</summary>
    /// <remarks>
    /// A warning by design, and the one soft case that is soft for a POSITIVE
    /// reason rather than for forward compatibility: a mapper who renames a door
    /// must not silently lose the wiring to it, so the connection is kept and
    /// said out loud.
    /// </remarks>
    public static readonly CookDiagnosticId MapConnectionTargetMissing = CookDiagnosticId.Cook(7005);

    /// <summary>An entity names a classname this build has no schema for.</summary>
    /// <remarks>
    /// A warning, matching the entity store's own decision: an entity is strings
    /// on the node and an unknown class is lossless by construction, so refusing
    /// one would refuse every map written ahead of the game's own code.
    /// </remarks>
    public static readonly CookDiagnosticId MapEntityClassUnknown = CookDiagnosticId.Cook(7006);

    /// <summary>A script the Luau front end refuses.</summary>
    public static readonly CookDiagnosticId ScriptSyntaxError = CookDiagnosticId.Cook(8001);

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
