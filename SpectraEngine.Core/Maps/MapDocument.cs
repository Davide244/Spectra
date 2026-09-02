using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Scene;
using SpectraEngine.Core.Serialization;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace SpectraEngine.Core.Maps;

/// <summary>
/// An authored map, exactly as it sits on disk: the round-trip unit that
/// <see cref="MapReader"/> and <see cref="MapWriter"/> move between bytes and
/// memory, and that <see cref="MapSceneBinder"/> projects to and from a live
/// <see cref="Scene.Scene"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists so that byte identity has somewhere to live.</b> The
/// obvious design reads JSON straight into <see cref="SceneNode"/>s, and it
/// cannot work, for two independent reasons. First, unknown-member
/// preservation: a map written by a newer engine carries members this one has
/// never heard of, and they must survive a load and save unchanged - which
/// would mean parking a byte blob on <c>SceneNode</c>, where it would then have
/// to survive clone, reparent, undo and the id index, none of which have any
/// business knowing what a file format is. Second, and more decisively,
/// <b>a scene is a lossy image of a document</b>: <c>Brush</c>'s constructor
/// re-normalises every plane it is handed, so a hand-authored
/// <c>[2, 0, 0, -64]</c> comes back out of a <c>Scene</c> as
/// <c>[1, 0, 0, -32]</c>. That is correct - it is the same plane, canonicalised
/// - but it means a scene can never promise to reproduce the bytes it was built
/// from, and the document can.
/// </para>
/// <para>
/// <b>So the two round trips are separate claims, tested separately.</b>
/// <c>Write(Read(bytes)) == bytes</c> is exact and is the promise that makes a
/// hand edit in VS Code show up as a two-line diff instead of a whole-file
/// rewrite. Scene projection is the lossy half, and what it may lose is stated
/// per member below rather than discovered later.
/// </para>
/// <para>
/// <b>Members the engine does not implement yet are carried, not dropped.</b>
/// The format specification is deliberately ahead of the tree: entities,
/// scripts, realms and node state are all designed and none exist in
/// <c>SpectraEngine.Core</c>. Rather than write a reader that silently discards
/// them, this document sorts every member into one of three tiers - bound to
/// engine state, validated-and-carried (the reserved keys), or preserved
/// opaquely - so a map may travel through an older engine without losing what
/// that engine could not understand.
/// </para>
/// </remarks>
public sealed class MapDocument
{
    /// <summary>Canonical top-level member order. Indices are anchors for preserved members.</summary>
    internal static readonly string[] MemberOrder =
        [MapFormat.FormatVersionMember, MapFormat.MinimumReadableMember, MapFormat.EngineMember,
         MapFormat.SceneMember, MapFormat.EditorMember, MapFormat.NodesMember];

    /// <summary>The document's own format version, from <c>EngineInfo.MapFormatVersion</c>.</summary>
    public int FormatVersion { get; set; } = EngineInfo.MapFormatVersion;

    /// <summary>
    /// The oldest reader that can still make sense of this document. A reader
    /// refuses a document whose value here exceeds what it implements, and
    /// tolerates any <see cref="FormatVersion"/> at or above it.
    /// </summary>
    public int MinimumReadableVersion { get; set; } = EngineInfo.MinimumReadableMapVersion;

    /// <summary>Engine version that last wrote this document. Informational; never a load gate.</summary>
    public string Engine { get; set; } = EngineInfo.VersionString;

    /// <summary>Scene-level settings: the name, plus anything preserved.</summary>
    public MapSceneInfo Scene { get; set; } = new();

    /// <summary>
    /// The reserved <c>editor</c> key, carried verbatim.
    /// </summary>
    /// <remarks>
    /// Reserved rather than preserved, and the distinction is the cook's. An
    /// unknown member is opaque text a future engine might need; <c>editor</c>
    /// is a member the cook must be free to drop <i>by name</i>, wholesale,
    /// without inspecting it. It is carried as raw bytes here only because its
    /// v1 content - the shared grid size - belongs to
    /// <c>SpectraEngine.Editing</c>, an assembly Core cannot reference by
    /// design.
    /// </remarks>
    public PreservedValue? Editor { get; set; }

    /// <summary>
    /// The root's children, in order. Depth is expressed by nesting.
    /// </summary>
    /// <remarks>
    /// <b>Not a flat list with parent ids, and not a record for the root
    /// itself.</b> Sibling order is traversal order is static-world placement
    /// order, which breaks ties in the carve, so a format that could lose it
    /// would rebuild a level that is valid, different and bit-unequal. A JSON
    /// array expresses that order exactly and a moved subtree stays one diff
    /// hunk. The root is omitted because <c>Scene.Root</c> is get-only and
    /// mints its own id, so a record for it could not be restored anyway.
    /// </remarks>
    public List<MapNode> Nodes { get; } = [];

    /// <summary>Top-level members this engine version does not recognise.</summary>
    public List<PreservedMember> Unknown { get; } = [];
}

/// <summary>Scene-level settings.</summary>
/// <remarks>
/// <c>scene.spawn</c> is specified and deliberately absent here: nothing on
/// <c>Scene</c> carries a gameplay spawn (what exists is
/// <c>SceneManager.PlayerSpawn</c>, a demo-side <c>Vector3</c> plus a yaw
/// scalar, not the position-and-quaternion pair the format names). It therefore
/// arrives as a preserved member and round-trips untouched rather than being
/// invented here, because a format member with no engine concept behind it is a
/// value that silently means nothing.
/// </remarks>
public sealed class MapSceneInfo
{
    internal static readonly string[] MemberOrder = [MapFormat.NameMember];

    public string Name { get; set; } = "Scene";

    public List<PreservedMember> Unknown { get; } = [];
}

/// <summary>One authored node.</summary>
public sealed class MapNode
{
    // 'entity' sits after 'light' and before 'editor', which is where the format
    // specification puts a second payload. The indices here are ANCHORS for
    // preserved members and are recomputed on every read, so inserting one
    // renumbers nothing that lives in a file.
    internal static readonly string[] MemberOrder =
        [MapFormat.IdMember, MapFormat.NameMember, MapFormat.RealmMember, MapFormat.StateMember,
         MapFormat.KindMember, MapFormat.TransformMember, MapFormat.BrushMember, MapFormat.MeshMember,
         MapFormat.LightMember, MapFormat.EntityMember, MapFormat.EditorMember, MapFormat.ChildrenMember];

    public Guid Id { get; set; }

    public string Name { get; set; } = "Node";

    /// <summary>
    /// Byte offset this node started at in the source document, or 0 when it
    /// was built in code. Not persisted; not part of the document's content.
    /// </summary>
    /// <remarks>
    /// <b>Carried so that a failure raised while BUILDING the scene can still
    /// point into the file.</b> A hand-edited plane set that is duplicated or
    /// unbounded is perfectly well-formed JSON, so the reader accepts it and
    /// <c>Brush</c>'s constructor is what rejects it - one whole stage later,
    /// by which time the byte offset would otherwise be gone and the complaint
    /// would name plane indices in a map with hundreds of brushes.
    /// </remarks>
    public long SourceOffset { get; set; }

    /// <summary>
    /// The node's own declared realm, or null when omitted (meaning inherit).
    /// </summary>
    /// <remarks>
    /// <b>Validated against the closed vocabulary on read even though no realm
    /// enum exists in Core yet.</b> That is the whole reason <c>realm</c> is a
    /// reserved key: if a mistyped <c>"sever"</c> survived as an opaque
    /// preserved member, the node would load as having declared nothing and
    /// fall through to <c>shared</c>, which is a data leak on load rather than
    /// a lost setting. Validating the string without binding it to an enum
    /// keeps that guarantee today and costs one lookup.
    /// </remarks>
    public string? Realm { get; set; }

    /// <summary>The node's own declared state, or null when omitted. Validated like <see cref="Realm"/>.</summary>
    public string? State { get; set; }

    /// <summary>
    /// Declared brush kind, or null when omitted (meaning <c>World</c>).
    /// </summary>
    /// <remarks>
    /// Reserved for the same mechanism as <see cref="Realm"/> and with a worse
    /// consequence: a preserved-and-ignored <c>kind</c> falls through to
    /// <c>World</c>, which re-admits a part brush to the carve and changes
    /// world topology on load.
    /// </remarks>
    public BrushKind? Kind { get; set; }

    public MapTransform Transform { get; set; } = MapTransform.Identity;

    public MapBrush? Brush { get; set; }

    /// <summary>
    /// Where this node's mesh came from, when it came from a model file.
    /// </summary>
    /// <remarks>
    /// <b>A reference, never the geometry.</b> Vertices belong in the cooked
    /// artifact; an authored map names the source file and lets the asset
    /// system resolve it, which is the same rule that makes a face carry a
    /// material path rather than a material. A node whose mesh was built in
    /// code has none of this, permanently, because there is no file to name.
    /// </remarks>
    public MapMeshSource? Mesh { get; set; }

    /// <summary>
    /// The node's light, if any.
    /// </summary>
    /// <remarks>
    /// Not named anywhere in the format specification, which predates nothing
    /// in particular and simply omits it; the engine has had lights throughout.
    /// Placed after <c>brush</c> in the member order because that is where a
    /// second payload belongs, and recorded as an amendment rather than
    /// inferred.
    /// </remarks>
    public MapLight? Light { get; set; }

    /// <summary>
    /// The entity this node IS, if any: the class it names, its keyvalues and
    /// the wires leaving its outputs.
    /// </summary>
    /// <remarks>
    /// <b>Bound rather than preserved, and that closed a data-loss hole rather
    /// than adding a feature.</b> An <c>entity</c> member rode through the
    /// DOCUMENT path perfectly well as an unknown - but
    /// <see cref="MapSceneBinder"/> builds a FRESH <see cref="MapNode"/> from the
    /// scene on the way out, so the moment a node actually carried entity data,
    /// any save deleted it. Preservation only ever protects members that reach a
    /// document and leave it again without passing through a scene.
    /// </remarks>
    public MapEntity? Entity { get; set; }

    /// <summary>The reserved per-node <c>editor</c> key, carried verbatim.</summary>
    public PreservedValue? Editor { get; set; }

    public List<MapNode> Children { get; } = [];

    /// <summary>
    /// Members this engine version does not recognise, including the specified
    /// but unbuilt <c>script</c> payload.
    /// </summary>
    public List<PreservedMember> Unknown { get; } = [];
}

/// <summary>The authored 10-float transform, exactly as stored.</summary>
/// <remarks>
/// Never a composed world matrix - a standing invariant of the format. The
/// identity value is spelled out rather than left to <c>default</c> because
/// <c>default(Transform)</c> has a zero scale and a zero quaternion, which is
/// not the identity and would load every node collapsed to a point.
/// </remarks>
public struct MapTransform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static MapTransform Identity =>
        new() { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };
}

/// <summary>A brush: planes, per-plane surfaces, and the sign.</summary>
/// <remarks>
/// <para>
/// <b>A closed vocabulary.</b> Every member here is load-bearing geometry, so
/// an unrecognised member inside a brush record is a reader error naming the
/// node and the offset, not a preserved unknown. The reasoning is the same one
/// that makes <c>operation</c> a reserved-style closed value: a brush member
/// that is quietly ignored is a wall where a doorway was.
/// </para>
/// <para>
/// <b><see cref="Transform"/> is carried even though a node-attached brush
/// ignores it.</b> The scene places a brush from the node's world matrix and
/// never reads <c>Brush.Transform</c> - but <c>Brush.CreateBox</c> puts the
/// centering translation there, and the standalone <c>Csg.Carve</c> and
/// <c>CsgWorld.Build</c> overloads do read it. It is a public settable member
/// of the brush value; dropping it because the common path ignores it is how a
/// format loses data that nothing reports.
/// </para>
/// </remarks>
public sealed class MapBrush
{
    public BrushOperation Operation { get; set; } = BrushOperation.Additive;

    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;

    /// <summary>Planes as <c>[nx, ny, nz, d]</c>, matching <c>System.Numerics.Plane</c>'s field order. Normals point out of the solid.</summary>
    public List<Vector4> Planes { get; } = [];

    /// <summary>Per-plane surfaces, indexed by plane index. Length must equal <see cref="Planes"/>.</summary>
    public List<MapFace> Faces { get; } = [];

    /// <summary>Whether the cook keeps this brush's authored planes in the compiled map.</summary>
    public bool KeepSource { get; set; }
}

/// <summary>One brush face's material and texture projection.</summary>
/// <remarks>
/// An <i>open</i> record, unlike <see cref="MapBrush"/>: a face payload is
/// where per-surface authoring grows (smoothing groups, lightmap scale,
/// per-face flags), and none of those change what the solid is, so preserving
/// an unrecognised one is safe where preserving an unrecognised brush member
/// would not be.
/// </remarks>
public sealed class MapFace
{
    internal static readonly string[] MemberOrder =
        [MapFormat.MaterialMember, MapFormat.UAxisMember, MapFormat.VAxisMember,
         MapFormat.UOffsetMember, MapFormat.VOffsetMember, MapFormat.UScaleMember, MapFormat.VScaleMember];

    /// <summary>
    /// Content-root-relative path of a <c>.spectramat</c>, or null for the
    /// engine default material.
    /// </summary>
    /// <remarks>
    /// <b>The path, never <c>MaterialRef.Id</c>.</b> Ids are handed out in
    /// first-intern order within one process, so the same map loaded second
    /// instead of first gets different ids for the same files. A standing
    /// invariant of the format states it, and the failure it prevents is a
    /// world that textures itself differently depending on load order.
    /// </remarks>
    public string? Material { get; set; }

    /// <summary>Explicit U axis, or null for world-aligned (the dominant-axis rule).</summary>
    public Vector3? UAxis { get; set; }

    /// <summary>Explicit V axis, or null for world-aligned.</summary>
    public Vector3? VAxis { get; set; }

    public float UOffset { get; set; }
    public float VOffset { get; set; }

    /// <summary>World units per texture repeat. Defaults to 1; zero is refused by <c>FaceSurface</c>.</summary>
    public float UScale { get; set; } = 1f;
    public float VScale { get; set; } = 1f;

    public List<PreservedMember> Unknown { get; } = [];
}

/// <summary>A reference to one submesh of a model file.</summary>
/// <remarks>
/// Open, like a face record: this is where a model reference grows (an
/// authored material override, a LOD choice, a collision hint), and none of it
/// changes which geometry is named.
/// </remarks>
public sealed class MapMeshSource
{
    internal static readonly string[] MemberOrder = [MapFormat.ModelMember, MapFormat.SubmeshMember];

    /// <summary>Content-root-relative path of the model file.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Index into the model's submesh list. Omitted iff zero.</summary>
    public int Submesh { get; set; }

    public List<PreservedMember> Unknown { get; } = [];
}

/// <summary>A light payload.</summary>
public sealed class MapLight
{
    // APPEND ONLY. The canonical order is what byte identity is defined
    // against, so a new member goes at the END and every file written before it
    // existed still round-trips to the same bytes.
    internal static readonly string[] MemberOrder =
        [MapFormat.KindMember, MapFormat.ColorMember, MapFormat.IntensityMember,
         MapFormat.RangeMember, MapFormat.EnabledMember,
         MapFormat.InnerAngleMember, MapFormat.OuterAngleMember,
         MapFormat.WidthMember, MapFormat.HeightMember, MapFormat.RadiusMember];

    public LightKind Kind { get; set; } = LightKind.Directional;

    /// <summary>Linear RGB, not a display colour.</summary>
    public Vector3 Color { get; set; } = Vector3.One;

    public float Intensity { get; set; } = 1f;

    /// <summary>
    /// Falloff range. Defaults to 10 and is written whenever it differs.
    /// </summary>
    /// <remarks>
    /// <b>Never written as zero and never defaulted to zero on read.</b>
    /// <c>Light.Range</c> refuses any value that is not strictly positive, so
    /// the natural shortcut - omit it for a directional light, let the missing
    /// number default to <c>0f</c> - throws out of the property setter in the
    /// middle of a load. The default here is the field initialiser's 10, which
    /// is the value the setter would have accepted.
    /// </remarks>
    public float Range { get; set; } = 10f;

    public bool Enabled { get; set; } = true;

    /// <summary>A spot's fully-lit half-angle in degrees.</summary>
    public float InnerAngle { get; set; } = 25f;

    /// <summary>A spot's outer half-angle in degrees.</summary>
    public float OuterAngle { get; set; } = 35f;

    /// <summary>A rect light's width in world units.</summary>
    public float Width { get; set; } = 1f;

    /// <summary>A rect light's height in world units.</summary>
    public float Height { get; set; } = 1f;

    /// <summary>A disc light's radius in world units.</summary>
    public float Radius { get; set; } = 0.5f;

    public List<PreservedMember> Unknown { get; } = [];
}

/// <summary>An entity payload: a class name, its keyvalues, and its outgoing wires.</summary>
/// <remarks>
/// <para>
/// <b>An OPEN record, like <see cref="MapFace"/> and unlike <see cref="MapBrush"/>.</b>
/// An entity payload is exactly where this format grows - spawn flags, editor
/// hints, per-class members a game defines for itself - and none of it changes
/// what the solid is, so carrying an unrecognised member through is safe where
/// carrying an unrecognised brush member would be a wall where a doorway was.
/// </para>
/// <para>
/// <b>The class is TEXT and is never resolved here.</b> A map may name a class
/// this build has no schema for, because it was authored against a game that is
/// not installed; such a map must still load, still show in the tree and still
/// save unchanged. A codec that looked the class up would have to decide what to
/// do when the lookup failed, and every answer to that question loses data.
/// </para>
/// </remarks>
public sealed class MapEntity
{
    // APPEND ONLY, for the reason stated on MapLight: the canonical order is
    // what byte identity is defined against.
    internal static readonly string[] MemberOrder =
        [MapFormat.ClassMember, MapFormat.KeysMember, MapFormat.OutputsMember];

    /// <summary>The entity class this node is, as the file spells it.</summary>
    public string Class { get; set; } = string.Empty;

    /// <summary>
    /// The authored keyvalues, in AUTHORED ORDER, values as text.
    /// </summary>
    /// <remarks>
    /// <b>An ordered list, deliberately not a dictionary</b>, for the two reasons
    /// <c>EntityData.Keyvalues</c> states: member order has to round-trip
    /// byte-identically through a file a person hand-edits, and a hand-written
    /// duplicate must survive rather than have one of its two entries silently
    /// dropped. Values are strings because keyvalues are string-typed on the
    /// wire; a schema is what says what the text means.
    /// </remarks>
    public List<KeyValuePair<string, string>> Keys { get; } = [];

    /// <summary>The wires leaving this entity's outputs, in authored order.</summary>
    public List<MapConnection> Outputs { get; } = [];

    public List<PreservedMember> Unknown { get; } = [];
}

/// <summary>
/// One authored wire, the document's image of
/// <see cref="Entities.EntityConnection"/>.
/// </summary>
/// <remarks>
/// Open, like the entity record that holds it and for the same reason. The one
/// thing it cannot round-trip is <see cref="Unknown"/> through a SCENE:
/// <c>EntityConnection</c> is a fixed-width value with nowhere to park raw bytes,
/// so a preserved member inside a connection survives document-to-document and
/// not document-to-scene-to-document. That is the same lossy boundary a
/// <c>MapFace</c>'s unknowns cross, and it is why byte identity is a claim about
/// documents.
/// </remarks>
public sealed class MapConnection
{
    internal static readonly string[] MemberOrder =
        [MapFormat.OutputMember, MapFormat.TargetMember, MapFormat.InputMember,
         MapFormat.ParamMember, MapFormat.DelayMember, MapFormat.TimesMember];

    /// <summary>The output that fires this wire.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// The name of the entity or entities to send to; <c>targetname</c> IS
    /// <c>SceneNode.Name</c>.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The input to send.</summary>
    public string Input { get; set; } = string.Empty;

    /// <summary>The argument to send. Omitted when empty.</summary>
    public string Param { get; set; } = string.Empty;

    /// <summary>Seconds to wait before sending. Omitted at zero.</summary>
    public float Delay { get; set; }

    /// <summary>
    /// How many times this wire may fire, or
    /// <see cref="Entities.EntityConnection.Infinite"/>. Omitted at infinite,
    /// which is the common case.
    /// </summary>
    public int Times { get; set; } = Entities.EntityConnection.Infinite;

    public List<PreservedMember> Unknown { get; } = [];
}
