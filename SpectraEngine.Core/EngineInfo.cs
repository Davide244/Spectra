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
        public const int MapFormatVersion = 1;

        // The oldest reader that can still make sense of a map this engine writes.
        // Source formats version asymmetrically from cooked ones: a cooked artifact
        // demands an exact match and says "recook", because it is a build output that
        // can always be regenerated, while a map is the user's own data and has to
        // survive an engine older or newer than itself. A reader refuses a document
        // whose minimumReadableVersion exceeds MapFormatVersion, and otherwise carries
        // members it does not recognise through untouched.
        public const int MinimumReadableMapVersion = 1;

        // A game project is data too: a text manifest naming the maps, the display
        // defaults and the backends, read once at boot. Same asymmetric versioning
        // as the map, and for the same reason - it is authored by a person and has
        // to survive an engine older or newer than itself.
        public const int ProjectFormatVersion = 1;
        public const int MinimumReadableProjectVersion = 1;

        // Shader format version — must match CompiledShaderFile.FormatVersion to load
        public const ushort ShaderFormatVersion = 1;
    }
}
