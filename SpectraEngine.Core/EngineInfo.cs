/*
 * SPECTRA ENGINE
 * 
 * The spectra engine is a high-performance, cross-platform game engine designed with ease of use and data-driven flexibility in mind.
 * Built on concepts from modern game engines and older ones, implementing both scene-graph and BSP tree structures for efficient rendering and spatial management.
 * Uses the SILK.NET library together with a custom-built shader language to allow writing one shader that can be compiled for multiple pipelines (Vulcan, DirectX, OpenGL, etc)
 * 
 * The goal of this engine is to merge the simplicity of easier to learn engines with the power and flexibility of more complex ones. It should be easy to pick up.
 * This is why the engine is designed to be as data-driven as possible, together with an editor merging HAMMER and roblox studio's ease of use and flexibility.
 * 
 * The engine uses/will use AoT compilation to achieve better performance and to comply with platform guidelines (eg consoles, android, etc)
 * When writing code for this engine, keep AoT in mind and try to think things through BEFORE using AI.
 * Review AI code before using it, and test it thoroughly!
 * 
 * Anyways, if you are a contributor in the (hopefully) future, have fun working on this!
 */

namespace SpectraEngine.Core
{
    public static class EngineInfo
    {
        // *** Engine Versioning
        public const int MajorVersion = 1;
        public const int MinorVersion = 0;
        public const int RevisionVersion = 0;
        public static readonly string VersionString = $"{MajorVersion}.{MinorVersion}.{RevisionVersion}";

        // *** Internal engine versioning

        // Asset format versions
        public const int ModelFormatVersion = 1;
        public const int TextureFormatVersion = 1;
        // 2: light kinds beyond directional and point, with the angles and
        // extents they need. A document only DECLARES it needs version 2 when it
        // actually carries one of those (see MapSceneBinder), so every map
        // written before this still says 1 and still opens in an older editor.
        // 3: the entity payload, bound rather than preserved. Same per-document
        // rule: a map with no entity in it still says 1.
        public const int MapFormatVersion = 3;

        // The oldest reader that can still make sense of a map this engine writes.
        // Source formats version asymmetrically from cooked ones: a cooked artifact
        // demands an exact match and says "recook", because it is a build output that
        // can always be regenerated, while a map is the user's own data and has to
        // survive an engine older or newer than itself. A reader refuses a document
        // whose minimumReadableVersion exceeds MapFormatVersion, and otherwise carries
        // members it does not recognise through untouched.
        // Still 1, and deliberately: this constant is the floor a NEW document
        // gets, and raising it would tell every older editor to refuse every map
        // this engine writes, including the overwhelming majority that carry
        // nothing an older editor could not read. The documents that genuinely
        // need a newer reader raise their OWN minimum, per document, where the
        // fact is actually known.
        public const int MinimumReadableMapVersion = 1;

        // What a document declares when it carries a light shape older readers
        // would silently delete. See MapSceneBinder.RequiredReaderVersion.
        public const int LightShapeMapVersion = 2;

        // What a document declares when it carries an entity payload. The
        // argument is the light-shape one exactly: an older editor carried
        // 'entity' as an opaque unknown on LOAD and then rebuilt each node from
        // the scene on SAVE, where nothing held the payload - so it would open
        // such a map, show it correctly, and delete every keyvalue and wire in it
        // on the next Ctrl+S.
        public const int EntityMapVersion = 3;

        // A game project is data too: a text manifest naming the maps, the display
        // defaults and the backends, read once at boot. Same asymmetric versioning
        // as the map, and for the same reason - it is authored by a person and has
        // to survive an engine older or newer than itself.
        public const int ProjectFormatVersion = 1;
        public const int MinimumReadableProjectVersion = 1;

        /// <summary>
        /// Versions the SHAPE of compiled geometry: what the CSG compiler emits and
        /// what a vertex layout is made of. Bump it by hand whenever either can
        /// change, because a pack cooked before the change is unreadable after it and
        /// the failure is a misinterpreted vertex buffer rather than an exception.
        /// Nothing stamps or checks it yet: its enforcing reader arrives with the
        /// compiled map format, which is the first artifact that can hold a stale
        /// geometry blob. It exists now so that no pack is ever unversioned, which is
        /// what retrofitting it would cost.
        /// </summary>
        public const uint GeometryFormatVersion = 1;

        /// <summary>
        /// Version of the .spack container a cook writes. Stamped into
        /// PackHeader.FormatVersion.
        /// </summary>
        public const ushort PackFormatVersion = 1;

        /// <summary>
        /// The oldest reader that can make sense of a pack this engine writes,
        /// stamped into PackHeader.MinReaderVersion.
        /// </summary>
        /// <remarks>
        /// A pack is a cooked artifact, so the asymmetric-versioning rule would
        /// say "exact match, else recook". The header carries the floor as a
        /// field anyway, and that is the stronger form of the same rule: a v2
        /// engine that only appends a header field can keep writing packs a v1
        /// reader opens correctly, and one that changes what a payload MEANS
        /// raises this per pack. Raising it globally instead would tell every
        /// older reader to refuse every pack, including the ones it could read.
        /// </remarks>
        public const ushort MinimumReadablePackVersion = 1;

        // Shader format version - must match CompiledShaderFile.FormatVersion to load.
        // Cooked, so it versions the strict way described above: ShaderFileReader
        // refuses any other value outright and says recook, because a compiled
        // shader is a build output that can always be regenerated and the bytes
        // past the header only mean anything under the version that wrote them.
        // 2: the pipeline blob carries its vertex input reflection and its
        // generated instanced vertex stage. A v1 file has neither, so a v1 blob
        // read as v2 would take the byte after the last stage as a table length.
        public const ushort ShaderFormatVersion = 2;
    }
}
