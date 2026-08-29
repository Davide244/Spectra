using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SpectraEngine.Core.Maps;

/// <summary>
/// The authored map format's constants: the canonical encoding, every wire
/// member name, and the closed vocabularies.
/// </summary>
/// <remarks>
/// <para>
/// <b>One file owns the spelling of the format.</b> The reader and the writer
/// are two expressions of one layout, and the way that goes wrong is a member
/// name written in one and typed slightly differently in the other, which
/// produces a document that saves fine and loses a member on the next load with
/// nothing reporting it.
/// </para>
/// </remarks>
public static class MapFormat
{
    /// <summary>The bundle's scene document, inside a <c>.smap</c> folder.</summary>
    public const string DocumentFileName = "map.json";

    /// <summary>The extension a map bundle directory carries.</summary>
    public const string BundleExtension = ".smap";

    /// <summary>Per-user editor state, gitignored and never load-bearing.</summary>
    public const string UserStateFileName = "editor.user.json";

    /// <summary>Bundle-relative folder holding script payloads as real files.</summary>
    public const string ScriptsFolderName = "scripts";

    // --- top level ----------------------------------------------------------

    public const string FormatVersionMember = "spectramap";
    public const string MinimumReadableMember = "minimumReadableVersion";
    public const string EngineMember = "engine";
    public const string SceneMember = "scene";
    public const string NodesMember = "nodes";

    // --- node ---------------------------------------------------------------

    public const string IdMember = "id";
    public const string NameMember = "name";
    public const string RealmMember = "realm";
    public const string StateMember = "state";
    public const string KindMember = "kind";
    public const string TransformMember = "transform";
    public const string BrushMember = "brush";
    public const string LightMember = "light";
    public const string ChildrenMember = "children";

    /// <summary>
    /// Reserved at top level and per node.
    /// </summary>
    /// <remarks>
    /// Reserved because the cook must be free to ignore it wholesale, by name,
    /// without inspecting what is inside. That is a structural rule and it
    /// beats maintaining a list of editor-only member names.
    /// </remarks>
    public const string EditorMember = "editor";

    // --- transform ----------------------------------------------------------

    public const string PositionMember = "p";
    public const string RotationMember = "r";
    public const string ScaleMember = "s";

    // --- brush --------------------------------------------------------------

    public const string OperationMember = "operation";
    public const string PlanesMember = "planes";
    public const string FacesMember = "faces";
    public const string KeepSourceMember = "keepSource";
    public const string BrushTransformMember = "transform";

    // --- face ---------------------------------------------------------------

    public const string MaterialMember = "material";
    public const string UAxisMember = "u";
    public const string VAxisMember = "v";
    public const string UOffsetMember = "uo";
    public const string VOffsetMember = "vo";
    public const string UScaleMember = "us";
    public const string VScaleMember = "vs";

    // --- light --------------------------------------------------------------

    public const string ColorMember = "color";
    public const string IntensityMember = "intensity";
    public const string RangeMember = "range";
    public const string EnabledMember = "enabled";

    // --- closed vocabularies ------------------------------------------------

    public const string WorldKind = "world";
    public const string PartKind = "part";

    public const string AdditiveOperation = "additive";
    public const string SubtractiveOperation = "subtractive";

    public const string DirectionalLight = "directional";
    public const string PointLight = "point";

    /// <summary>
    /// Realms, in the vocabulary the format fixes. No enum exists in Core yet;
    /// these are validated as strings so a typo is still an error rather than a
    /// silent widening to <c>shared</c>.
    /// </summary>
    public static readonly string[] Realms = ["shared", "server", "client"];

    /// <summary>Node states, validated for the same reason as <see cref="Realms"/>.</summary>
    public static readonly string[] States = ["active", "dormant"];

    /// <summary>
    /// The canonical writer settings. Every one of these is load-bearing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>Encoder</c> is not a preference.</b> The default
    /// <see cref="JavaScriptEncoder"/> escapes <c>+ &lt; &gt; &amp;</c> and every
    /// non-ASCII character to <c>\uXXXX</c>, which would turn every inline
    /// script and every non-ASCII node name into unmergeable noise in a file
    /// whose entire purpose is being read and merged by people.
    /// </para>
    /// <para>
    /// <b><c>NewLine</c> is set explicitly because its default is
    /// <see cref="Environment.NewLine"/></b>, which is CRLF on Windows — so a
    /// writer that left it alone would emit a different file on Windows than on
    /// Linux, and byte identity would hold only within one operating system.
    /// That is a silent, platform-shaped defect in a format designed to be
    /// diffed across a team.
    /// </para>
    /// </remarks>
    public static JsonWriterOptions WriterOptions => new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = false,
    };

    /// <summary>
    /// Reader settings. Comments are disallowed and trailing commas refused,
    /// because both would be dropped on the next save and a round trip that
    /// silently deletes a reviewer's comment is worse than one that refuses it.
    /// </summary>
    public static JsonReaderOptions ReaderOptions => new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    internal static string ToWire(BrushKind kind) => kind == BrushKind.Part ? PartKind : WorldKind;

    internal static string ToWire(BrushOperation operation) =>
        operation == BrushOperation.Subtractive ? SubtractiveOperation : AdditiveOperation;

    internal static string ToWire(LightKind kind) => kind == LightKind.Point ? PointLight : DirectionalLight;

    internal static int IndexOf(string[] order, string member) => Array.IndexOf(order, member);
}
