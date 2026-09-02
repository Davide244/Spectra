using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using System;
using SpectraEngine.Core.Serialization;
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
    /// <summary>Canonical encoding, shared with every other authored document.</summary>
    public static JsonWriterOptions WriterOptions => CanonicalJson.WriterOptions;

    /// <summary>Reader settings, shared with every other authored document.</summary>
    public static JsonReaderOptions ReaderOptions => CanonicalJson.ReaderOptions;

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
    public const string MeshMember = "mesh";
    public const string LightMember = "light";
    public const string EntityMember = "entity";
    public const string ChildrenMember = "children";

    // --- mesh ---------------------------------------------------------------

    public const string ModelMember = "model";
    public const string SubmeshMember = "submesh";

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

    // APPENDED, in this order, and never reordered. The canonical member order
    // is what byte identity is defined against, so inserting one of these
    // earlier would rewrite every existing file that carries a light.
    public const string InnerAngleMember = "innerAngle";
    public const string OuterAngleMember = "outerAngle";
    public const string WidthMember = "width";
    public const string HeightMember = "height";
    public const string RadiusMember = "radius";

    // --- entity -------------------------------------------------------------

    public const string ClassMember = "class";
    public const string KeysMember = "keys";
    public const string OutputsMember = "outputs";

    // --- connection ---------------------------------------------------------

    public const string OutputMember = "output";
    public const string TargetMember = "target";
    public const string InputMember = "input";
    public const string ParamMember = "param";
    public const string DelayMember = "delay";
    public const string TimesMember = "times";

    /// <summary>
    /// Target names resolved at FIRE time rather than against the map, so a
    /// document naming one of these has nothing to validate.
    /// </summary>
    /// <remarks>
    /// <b>Matched ordinally, like every other name in this format.</b> A
    /// case-folding rule would need a culture to fold in, and the same file would
    /// then mean different things on different machines - which is the same
    /// reason <c>EntityData</c> matches keyvalue names ordinally.
    /// </remarks>
    public static readonly string[] RuntimeTargets = ["!self", "!activator", "!caller"];

    /// <summary>
    /// The one wildcard a target name may carry: a trailing <c>*</c>, meaning
    /// every name starting with what precedes it.
    /// </summary>
    public const char TargetWildcard = '*';

    // --- closed vocabularies ------------------------------------------------

    public const string WorldKind = "world";
    public const string PartKind = "part";

    public const string AdditiveOperation = "additive";
    public const string SubtractiveOperation = "subtractive";

    public const string DirectionalLight = "directional";
    public const string PointLight = "point";
    public const string SpotLight = "spot";
    public const string RectLight = "rect";
    public const string DiscLight = "disc";

    /// <summary>
    /// Realms, in the vocabulary the format fixes. No enum exists in Core yet;
    /// these are validated as strings so a typo is still an error rather than a
    /// silent widening to <c>shared</c>.
    /// </summary>
    public static readonly string[] Realms = ["shared", "server", "client"];

    /// <summary>Node states, validated for the same reason as <see cref="Realms"/>.</summary>
    public static readonly string[] States = ["active", "dormant"];

    internal static string ToWire(BrushKind kind) => kind == BrushKind.Part ? PartKind : WorldKind;

    internal static string ToWire(BrushOperation operation) =>
        operation == BrushOperation.Subtractive ? SubtractiveOperation : AdditiveOperation;

    /// <summary>
    /// The wire name for a light kind.
    /// </summary>
    /// <remarks>
    /// <b>A switch that THROWS, not a ternary.</b> This was
    /// <c>kind == Point ? "point" : "directional"</c>, which is correct for two
    /// kinds and silently serialises every future one as directional - a saved
    /// map that loads as the wrong shape with nothing anywhere reporting it, and
    /// the exact failure the format's own rules exist to prevent. A missing case
    /// is now a load-time error at the moment the map is written rather than a
    /// wrong picture the next time it is opened.
    /// </remarks>
    internal static string ToWire(LightKind kind) => kind switch
    {
        LightKind.Directional => DirectionalLight,
        LightKind.Point => PointLight,
        LightKind.Spot => SpotLight,
        LightKind.Rect => RectLight,
        LightKind.Disc => DiscLight,
        _ => throw new NotSupportedException($"No wire name for light kind '{kind}'."),
    };

    internal static int IndexOf(string[] order, string member) => Array.IndexOf(order, member);
}
