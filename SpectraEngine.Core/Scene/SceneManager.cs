using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Entities;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Maps;
using SpectraEngine.Core.Maps.Compiled;
using SpectraEngine.Core.Physics;
using SpectraEngine.Core.Projects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;

namespace SpectraEngine.Core.Scene;

public sealed class SceneManager
{
    private readonly ILogger<SceneManager> _logger;

    // Vertical bob of the demo's moving pillar: a rigid translation that keeps
    // the brush transform valid while continuously exercising the async
    // static-world recompile pipeline (snapshot → background CSG → swap).
    private const float PillarBobAmplitude = 0.5f;
    private const double PillarBobPeriodSeconds = 4.0;

    // Cadence for the recompile-count log line: frequent enough that a short
    // smoke run proves live recompiles happen, rare enough not to spam.
    private const double CompileLogIntervalSeconds = 5.0;

    // Cadence for the smoke probe's center-screen ray. Same reasoning as
    // the compile log: a 15-second smoke run yields two or three lines.
    private const double ScreenProbeIntervalSeconds = 5.0;

    // Scattered "parts": deterministic boxes over a PartAreaSize^2 area around
    // the hand-authored structures, placed like Roblox parts (open-world
    // pillar). They give smoke runs something real to prove: the demo camera
    // cannot see all of the resulting chunks (so the chunks-visible stat
    // exercises actual culling), and the bobbing pillar's recompiles touch a
    // small dirty-cell subset of a many-chunk world. One part per grid site,
    // jittered within the site so parts never touch each other or the center
    // structures — the initial compile stays mostly-isolated carves, i.e. fast.
    private const int PartGridSites = 14;      // 14x14 sites, 4 skipped at the center

    /// <summary>
    /// Overrides the scatter grid's side length, for measuring how cost scales
    /// with content. Null keeps the demo's own <c>14</c>.
    /// </summary>
    /// <remarks>
    /// The area grows with it so the spacing, and therefore the isolation
    /// between parts, is unchanged: a denser grid would start fusing parts into
    /// each other and would measure a different CSG problem rather than more of
    /// the same one.
    /// </remarks>
    public static int? ScatterGridOverride { get; set; }
    private const float PartAreaSize = 200f;   // world units per side

    /// <summary>
    /// How many shared-brush "props" to scatter, for measuring what
    /// <em>repeated</em> content costs. Null or zero places none, which is the
    /// ordinary demo.
    /// </summary>
    /// <remarks>
    /// <b>Every prop shares ONE <see cref="Bsp.Brush"/> instance</b>, which is
    /// the whole reason this scenario is worth having.
    /// <c>PartBrushMeshCache</c> keys on brush reference identity, so N nodes
    /// resolve to one GPU mesh and the draw list carries N items with the
    /// <em>same</em> mesh and material and N different world matrices. That is
    /// one instancing batch, already expressed in the data the engine builds
    /// today, and it is the shape of content this engine is aimed at: a level
    /// dressed with a thousand copies of one crate.
    /// <para>
    /// <b>The scattered parts deliberately do NOT do this</b> — see
    /// <see cref="ScatterGridOverride"/>, where every brush gets its own
    /// randomized extents and therefore shares nothing. That scenario measures
    /// CSG and culling against world <em>size</em>; this one measures draw
    /// throughput against <em>repetition</em>. They are different questions,
    /// and until this existed only the first one had a fixture, which is why
    /// no measurement of the engine had ever had a duplicate draw in it.
    /// </para>
    /// </remarks>
    public static int? PropCountOverride { get; set; }

    /// <summary>
    /// A <c>.smap</c> bundle to run instead of the authored demo scene, or null
    /// for the demo. The graph is replaced after the demo builds, so the
    /// camera, the asset manager and the fallback material are already in
    /// place.
    /// </summary>
    /// <remarks>
    /// <b>A bad bundle logs and falls back rather than taking the run down.</b>
    /// The authored scene is still standing at that point, and a host that
    /// exits on a content error is a host nobody can debug a content error in.
    /// </remarks>
    public static string? LoadMapPathOverride { get; set; }

    /// <summary>
    /// A baked <c>.scmap</c> to run instead of the authored demo scene, named as a
    /// CONTENT path and resolved through the mounted sources, or null.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the shipped game's map path, and it wins over
    /// <see cref="LoadMapPathOverride"/>.</b> A boot that has both has a project
    /// running out of its cooked packs and a bundle still sitting in the source
    /// tree beside them; taking the loose one would be a run that says
    /// <c>--pack</c> and renders a level nothing cooked.</para>
    /// <para><b>A content path, not a file path.</b> A compiled map is a pack entry
    /// like any other and is resolved through the same
    /// <c>ContentSourceStack</c> as a texture, which is what makes a pure-pack run
    /// possible at all: the bundle folder need not exist on the shipped machine.
    /// </para>
    /// </remarks>
    public static string? CompiledMapPathOverride { get; set; }

    /// <summary>
    /// A <c>.smap</c> bundle to write the finished scene into, or null to write
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Off unless a switch names a path, for the same reason the editing
    /// self-test is: a demo that wrote files to disk without being asked is a
    /// surprise, and this one writes a directory.
    /// </remarks>
    public static string? SaveMapPathOverride { get; set; }

    /// <summary>
    /// A folder to export the finished scene into as a standalone project, or
    /// null to export nothing.
    /// </summary>
    public static string? SaveProjectPathOverride { get; set; }

    private const float PropHalfExtent = 0.4f;   // a crate, near enough
    private const float PropSpacing = 3f;        // world units between grid sites
    private const float PropStackHeight = 6f;    // vertical jitter, so it reads as a cloud

    // The demo's content, as content-root-relative paths — never asset objects.
    // Brush faces carry interned MaterialRefs and only the render thread turns
    // one into a material (see Scene.Assets), and the model paths are handed to
    // the asset manager rather than resolved here.
    private const string GridMaterialPath = "Materials/dev_grid.spectramat";
    private const string FloorMaterialPath = "Materials/floor.spectramat";
    private const string WallMaterialPath = "Materials/wall.spectramat";
    private const string PillarMaterialPath = "Materials/checker_orange.spectramat";
    private const string AccentMaterialPath = "Materials/checker_gray.spectramat";
    private const string OrbiterTexturePath = "Textures/gradient_mask.png";

    // The PBR reference row. Ordered smooth-metal, rough-metal, smooth-
    // dielectric, rough-dielectric, emissive, so the two axes of the metallic-
    // roughness workflow read left to right and the emitter sits at the end
    // where it cannot be mistaken for a lit surface.
    private static readonly string[] PbrMaterialPaths =
    [
        "Materials/pbr_gold.spectramat",
        "Materials/pbr_copper_brushed.spectramat",
        "Materials/pbr_plastic_red.spectramat",
        "Materials/pbr_rubber.spectramat",
        "Materials/pbr_emissive.spectramat",
    ];
    private const string CrateModelPath = "Models/crate.obj";
    private const string SignpostModelPath = "Models/signpost.gltf";

    // A box brush's plane order is fixed (+X, -X, +Y, -Y, +Z, -Z — see
    // Brush.CreateBox), which is what lets the demo address one face by index
    // and have it mean the same face in every run.
    private const int BoxFacePlusZ = 4;

    // The model fixtures are authored at 32 units per world unit (crate.obj
    // documents itself as "a 32-unit crate"); the demo's brushes are authored
    // at 1. Props therefore come in scaled, which puts a one-unit crate next to
    // the 2.2-unit pillars instead of a 32-unit wall of box.
    private const float ModelUnitsPerWorldUnit = 32f;

    // glTF puts v = 0 at the TOP of the image and the engine samples bottom-up,
    // and the importer ALREADY converts between the two: Assimp's own convention
    // is bottom-up, so asking it to flip as well returns the file's own numbers
    // and renders a vertically mirrored texture. This constant asked for the flip
    // for most of its life, which nobody could see because the signpost wears a
    // brick and a grid, both near enough symmetric. It was caught by the cooked
    // path, which applies the glTF flip itself from the specification: the two
    // agree on every UV of the signpost with the flip OFF and disagree on every
    // one of them with it on (ModelRuleTests). Defaults now, and the file is
    // named rather than the option, so the next reader knows there was a
    // question here.
    private static readonly ModelImportOptions GltfImportOptions = ModelImportOptions.Default;

    // The two floor corners the pillars do not occupy. Far enough from the
    // origin that the orbiting cube's radius-2 sweep clears them.
    private static readonly Vector3 CratePosition = new(2.3f, -1f, -2.3f);
    private static readonly Vector3 SignpostPosition = new(-2.3f, -1f, 2.3f);

    private static readonly Vector3 PillarHalfExtent = new(0.2f, 1.1f, 0.2f);

    private SceneNode? _spinner;
    private DemoBobAnimation? _pillarBob;
    private double _elapsed;

    // Both periodic lines wait one full interval, for the same reason: they
    // report the render view's stats, and the view is built AFTER the frame's
    // update — logging at t=0 would report a meaningless "0 of 0" from the
    // empty view and read like broken culling in a smoke log. The load-time
    // lines already cover what happened before the first frame.
    private double _nextCompileLogTime = CompileLogIntervalSeconds;

    // The asset manager the demo loaded its content through; kept so the
    // periodic smoke line can report the cache counts. Render thread.
    private AssetManager? _assets;

    // The prop requested asynchronously at load: non-null from the request
    // until the import lands (or fails) and the demo places it. Render thread.
    private ModelAsset? _pendingModel;
    private int _modelsRequested;
    private int _modelsPlaced;

    // Waits one full interval, for the reason documented on _nextCompileLogTime.
    private double _nextScreenProbeTime = ScreenProbeIntervalSeconds;

    // The renderer whose framebuffer latch supplies the picking viewport;
    // captured at demo load (the renderer is thread-safe to read from here —
    // the latch is locked internally).
    private Renderer? _renderer;

    public SceneManager(ILogger<SceneManager> logger)
    {
        _logger = logger;
    }

    /// <summary>Smoothed frame time in milliseconds, published by the engine loop.</summary>
    /// <remarks>
    /// Reported by the periodic stats line so an unattended run records what a
    /// frame cost. The window title carries the same number for a human.
    /// </remarks>
    public double FrameTimeMs { get; set; }

    /// <summary>Smoothed frames per second, published by the engine loop.</summary>
    public double Fps { get; set; }

    /// <summary>The scene currently being simulated and rendered, if one is loaded.</summary>
    public Scene? ActiveScene { get; private set; }

    /// <summary>
    /// Builds the editing layer for a freshly loaded scene, or null to run
    /// without one. The host sets this before <see cref="Engine.Run"/>;
    /// <see cref="LoadDemoScene"/> invokes it once, on the render thread, after
    /// the scene is complete.
    /// </summary>
    /// <remarks>
    /// A factory rather than a plain setter because the scene does not exist
    /// until the render thread has built it, and the editor is meaningless
    /// without one — so there is no window in which <see cref="Editor"/> could
    /// hold a tool bound to a scene that has gone away. Core cannot name the
    /// editing assembly at all (see <see cref="ISceneEditor"/>), which is why
    /// the host supplies the construction.
    /// </remarks>
    public Func<Scene, ISceneEditor>? EditorFactory { get; set; }

    /// <summary>
    /// Builds the physics backend for a freshly loaded scene, or null to run
    /// without one. Same shape as <see cref="EditorFactory"/>, and invoked in
    /// the same slot.
    /// </summary>
    /// <remarks>
    /// The reason for the seam is NOT the editor's reason. Gizmo code must
    /// never ship in a game binary; physics must. What this factory buys is
    /// that Core — and therefore the compiler tests, the BSP tests and any
    /// shader-only tool build — never needs a native physics library to
    /// resolve. A host that sets nothing gets
    /// <see cref="Physics.NullScenePhysics"/>, which is a supported
    /// configuration and is exactly what Edit mode is.
    /// </remarks>
    public Func<Scene, IScenePhysics>? PhysicsFactory { get; set; }

    /// <summary>
    /// The physics backend this run installed — never null once a scene is
    /// loaded, because a host that wired nothing gets
    /// <see cref="Physics.NullScenePhysics"/> rather than a null to check for
    /// on every call site in the engine loop.
    /// </summary>
    public IScenePhysics Physics { get; private set; } = NullScenePhysics.Instance;

    /// <summary>
    /// The editing layer this run installed, or null when the host supplied no
    /// <see cref="EditorFactory"/> — a shipped game, or a headless run. The
    /// engine loop drives it; the periodic stats line reports its counters.
    /// </summary>
    public ISceneEditor? Editor { get; private set; }

    /// <summary>
    /// The brush node a host-side editing self-test is expected to manipulate,
    /// or null before the demo scene is loaded.
    /// </summary>
    /// <remarks>
    /// <b>It is a specific node for a specific reason.</b> A self-test that
    /// drags a node and then asserts the static world recompiled needs the
    /// dirty-cell evidence to be attributable: the demo's only other moving
    /// brush is the bobbing pillar, which dirties its own chunk cells every
    /// single frame. This node is deliberately one whose cells the bobbing
    /// pillar never touches, so "the compile launched after the drag covers
    /// this node's cell" is a statement about the drag and nothing else.
    /// </remarks>
    public SceneNode? SelfTestNode { get; private set; }

    /// <summary>Where a first-person character starts, once the demo scene is loaded.</summary>
    /// <remarks>
    /// Content, not engine policy: it is authored by
    /// <see cref="DemoPlayArea"/> and read by whoever installs a character. A
    /// scene format will eventually carry this; until then it lives beside the
    /// self-test node, which is here for the same reason.
    /// </remarks>
    public Vector3 PlayerSpawn { get; private set; }

    /// <summary>The yaw a spawned character faces, in radians.</summary>
    public float PlayerSpawnYaw { get; private set; }

    /// <summary>Below this height a character has left the authored world and should be respawned.</summary>
    public float PlayerFallOutHeight { get; private set; } = float.NegativeInfinity;

    /// <summary>
    /// The first-person character the engine installed, or null when nothing
    /// walks. Reported in the periodic stats line.
    /// </summary>
    /// <remarks>
    /// Set by the engine rather than built here, unlike
    /// <see cref="EditorFactory"/> and <see cref="PhysicsFactory"/>: a character
    /// needs live input, which the scene manager has no access to and no reason
    /// to acquire. What it is wanted for here is the stats line — which
    /// character state a headless run can see is the difference between "the
    /// mover is running" and "the mover was never switched on".
    /// </remarks>
    public Physics.Character.FirstPersonController? Character { get; set; }

    /// <summary>
    /// The classes <see cref="StartEntityWorld"/> can build, or null for the
    /// process-wide <see cref="EntityCatalog.Shared"/> that generated
    /// registrations feed.
    /// </summary>
    /// <remarks>
    /// A seam for the same reason <see cref="PhysicsFactory"/> is one: the
    /// shared catalogue freezes on its first read, which is exactly right for a
    /// registry fed by module initializers and exactly wrong for a test suite
    /// or for a host that wants one game's classes and no others.
    /// </remarks>
    public EntityCatalog? EntityCatalog { get; set; }

    /// <summary>
    /// What every loaded scene's <see cref="Scene.EntitySchemas"/> is stamped
    /// with, or null to export the running catalogue and read it back.
    /// </summary>
    /// <remarks>
    /// <b>The default is a round trip through <c>.sentdef</c>, not a shortcut
    /// past it, and that is the point.</b> An editor reads schemas from that
    /// file and from nothing else, so anything the writer, the reader or the
    /// two enums get wrong has to surface where somebody is looking rather than
    /// the first time a game exports its definitions. Doing it on every scene
    /// load makes the round trip a thing this engine performs constantly, at a
    /// cost of a few kilobytes written and parsed once per load.
    /// <para>
    /// A host with a file already in hand (a mounted pack, an SDK game's
    /// export) sets this instead, which is also the seam a test uses to hand in
    /// exactly the classes it means.
    /// </para>
    /// </remarks>
    public EntitySchemaCatalog? EntitySchemas { get; set; }

    /// <summary>
    /// The live entity runtime, or null when nothing is playing. Render thread
    /// only, like the scene it runs over.
    /// </summary>
    /// <remarks>
    /// <b>Entities exist only while play mode owns the scene</b>, because the
    /// instances are a projection of the authored data rather than part of it:
    /// there is nothing to capture when a session stops, and an editor showing
    /// schema-driven properties needs no runtime at all. The engine starts and
    /// stops it at the play-mode boundary; what it is held HERE for is the same
    /// thing <see cref="Character"/> is held for, the periodic stats line, plus
    /// the one event the engine cannot see for itself
    /// (<see cref="OnSceneReplaced"/>).
    /// </remarks>
    public EntityWorld? EntityWorld { get; private set; }

    // Not cached: the two callers run once per scene load, the work is a few
    // kilobytes, and a cache would go stale the moment a host assigned
    // EntityCatalog between two loads - which is a wrong answer traded for a
    // saving nobody can measure.
    //
    // Reading Schemas FREEZES the catalogue, which is exactly the contract: every
    // class must register before the first lookup, and a scene load is well past
    // that point in both hosts.
    private EntitySchemaCatalog ResolveEntitySchemas() =>
        EntitySchemas ?? EntitySchemaCatalog.LoadFromSentDef(
            SentDef.Write((EntityCatalog ?? Entities.EntityCatalog.Shared).Schemas));

    /// <summary>
    /// Builds the entity runtime over the active scene and brings it to life.
    /// Called by the engine when play mode is entered; a no-op when a world is
    /// already running or no scene is loaded.
    /// </summary>
    public void StartEntityWorld()
    {
        if (EntityWorld is not null || ActiveScene is not { } scene)
            return;

        var world = new EntityWorld(scene, _logger, EntityCatalog);
        world.Activate();
        EntityWorld = world;

        // At Debug, not Information: a scene with no entities is the ordinary
        // case today and every F8 would otherwise log a line saying nothing.
        _logger.LogDebug(
            "Entity runtime active: {Entities} entity(ies), {Names} name(s)",
            world.Entities.Count, world.Index?.NameCount ?? 0);
    }

    /// <summary>
    /// Tears the entity runtime down, running every entity's
    /// <c>OnRemove</c> and letting go of the scene. Harmless when none is
    /// running.
    /// </summary>
    public void StopEntityWorld()
    {
        EntityWorld?.Deactivate();
        EntityWorld = null;
    }

    /// <summary>
    /// The graph the entity runtime was built over is about to be replaced, so
    /// the runtime goes with it.
    /// </summary>
    /// <remarks>
    /// <b>A world that outlives its graph renders perfectly and fails later.</b>
    /// A map load preserves node ids (that is what makes commands and undo work
    /// across one), so reopening a map while play mode is running would leave
    /// every stale entity to be rebound onto the fresh nodes by the target-name
    /// index's own <c>NodeAdded</c> handler: last map's think times, fire counts
    /// and wiring, attached to this map's scene, with nothing reporting it.
    /// <para>
    /// <b>Play mode itself is left alone</b> - the character keeps walking - and
    /// re-entering play builds a world over the new graph. That is stated in the
    /// log rather than worked around, because the alternative is entities that
    /// silently stopped running.
    /// </para>
    /// </remarks>
    public void OnSceneReplaced()
    {
        if (EntityWorld is null)
            return;

        StopEntityWorld();
        _logger.LogInformation(
            "The scene was replaced while the entity runtime was live, so it was torn down. " +
            "Re-enter play mode to run the new scene's entities.");
    }

    public void Initialize()
    {
        _logger.LogInformation("Scene manager initialized");
    }

    /// <summary>
    /// Which scene <see cref="LoadStartupScene"/> builds. An instance property
    /// rather than a static override like the map switches, because a shell
    /// creates a session per project and static state would leak from one
    /// session into the next.
    /// </summary>
    public StartupSceneKind Startup { get; set; } = StartupSceneKind.Demo;

    /// <summary>
    /// Builds whichever startup scene <see cref="Startup"/> names. The engine
    /// loop calls this once, on the render thread, after the renderer and the
    /// asset manager are up.
    /// </summary>
    public void LoadStartupScene(Renderer renderer, AssetManager assets)
    {
        if (Startup == StartupSceneKind.Baseplate)
            LoadBaseplateScene(renderer, assets);
        else
            LoadDemoScene(renderer, assets);
    }

    /// <summary>
    /// Builds the editor's blank scene: a sun and a ground plate, nothing else.
    /// Render thread only, like <see cref="LoadDemoScene"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately tiny. A shell that wants a real level open goes through the
    /// ordinary map path afterwards, where a load failure carries a report; a
    /// shell that wants nothing gets a scene that is lit and walkable rather
    /// than a black viewport, which reads as a broken renderer to exactly the
    /// audience this editor is for.
    /// </remarks>
    public void LoadBaseplateScene(Renderer renderer, AssetManager assets)
    {
        _renderer = renderer;
        _assets = assets;

        var scene = new Scene("Untitled");
        scene.Assets = assets;
        scene.EntitySchemas = ResolveEntitySchemas();
        scene.Camera.Position = new Vector3(9f, 6f, 11f);
        scene.Camera.LookAt(Vector3.Zero);

        PopulateBaseplate(scene);
        scene.RebuildStaticWorld(renderer);

        // On the plate, a step above its top face, looking along -z like the
        // camera. Fall-out far enough below that walking off the edge reads as
        // falling before the respawn catches it.
        PlayerSpawn = new Vector3(0f, 1f, 0f);
        PlayerSpawnYaw = 0f;
        PlayerFallOutHeight = -50f;

        ActiveScene = scene;

        // Same order as the demo load: the editor and physics adopt the scene
        // only once it is complete.
        Editor = EditorFactory?.Invoke(scene);
        Physics = PhysicsFactory?.Invoke(scene) ?? NullScenePhysics.Instance;

        _logger.LogInformation(
            "Baseplate scene loaded: {Nodes} node(s), content root {Root}",
            scene.Root.Children.Count, assets.ContentRootPath);
    }

    /// <summary>
    /// Adds the baseplate starter content to a scene: a directional sun and a
    /// 64x64 ground plate whose top face sits at y = 0.
    /// </summary>
    /// <remarks>
    /// <b>Also what a fresh map starts as</b>, so "New map" in the shell and a
    /// brand-new project agree about what empty means. The plate wears
    /// <see cref="Bsp.MaterialRef.Default"/> rather than naming a material,
    /// because a new project's content root is empty and a baseplate that
    /// warned about a missing file on first boot would teach that warnings are
    /// noise. The sun's direction and tuning are the demo's, which are stated
    /// for the engine's BRDF.
    /// </remarks>
    public static void PopulateBaseplate(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        SceneNode sun = scene.Root.CreateChild("Sun");
        sun.LocalRotation = Light.RotationForDirection(new Vector3(-0.35f, -0.85f, -0.4f));
        sun.Light = new Light
        {
            Kind = LightKind.Directional,
            Color = ColorSpace.SrgbToLinear(new Vector3(1f, 0.96f, 0.88f)),
            Intensity = 11f,
        };

        // Centred extents with the node carrying the offset, the demo's own
        // pattern: a compile snapshots the NODE's world matrix as the
        // placement and ignores the brush's baked translation, so extents
        // that were not centred would leave the plate floating half a unit
        // high. Top face at y = 0.
        SceneNode plate = scene.Root.CreateChild("Baseplate");
        plate.LocalPosition = new Vector3(0f, -0.5f, 0f);
        plate.Brush = Bsp.Brush.CreateBox(new Vector3(-32f, -0.5f, -32f), new Vector3(32f, 0.5f, 32f));
    }

    /// <summary>
    /// Builds the demo scene, which is the engine's end-to-end smoke test as
    /// much as a placeholder: a spinning cube with a child cube that orbits
    /// purely by inheriting its parent's rotation; a brush-built floor, two
    /// walls and two pillars, each wearing an authored material and one of them
    /// wearing a second material on a single face; ~200 scattered part brushes;
    /// and two imported props, one loaded synchronously and one requested in the
    /// background. Render thread only — it creates GPU resources — and requires
    /// an initialized renderer and an attached asset manager.
    /// </summary>
    public void LoadDemoScene(Renderer renderer, AssetManager assets)
    {
        _renderer = renderer;
        _assets = assets;

        var loadClock = Stopwatch.StartNew();

        var scene = new Scene("Demo");
        scene.Camera.Position = new Vector3(0f, 1.5f, 5f);
        scene.Camera.LookAt(Vector3.Zero);
        // Set before the first compile: the swap resolves each chunk's face
        // materials through this on the render thread, at upload time.
        scene.Assets = assets;
        scene.EntitySchemas = ResolveEntitySchemas();

        var (vertices, indices) = Primitives.Cube();
        var cubeMesh = renderer.CreateMesh(vertices, indices, VertexAttribute.StandardLayout);
        var shader = renderer.DefaultShader
            ?? throw new InvalidOperationException("Renderer has no default shader; initialize it first.");

        // --- content ------------------------------------------------------
        // Real files from the repo's Assets folder, not procedural pixels: this
        // is the demo's proof that the on-disk content path works end to end
        // (resolve -> parse/decode/import -> upload -> hot-reload).
        //
        // Timed as its own phase because it is a startup-budget line item the
        // periodic log below cannot show: everything here is synchronous and
        // blocks the first frame, so a regression in decode or import cost has
        // to be visible as a number, not inferred from a slow-feeling launch.
        var assetClock = Stopwatch.StartNew();

        Material worldMaterial = assets.LoadMaterial(GridMaterialPath);
        Material cubeMaterial = assets.LoadMaterial(PillarMaterialPath);

        // Every material the brushes below name, pulled in before the compile
        // rather than during it: the static-world swap resolves face material
        // ids on the render thread, so an unloaded one would turn the swap
        // frame into a synchronous file read and charge the compile for it.
        assets.LoadMaterial(FloorMaterialPath);
        assets.LoadMaterial(WallMaterialPath);
        assets.LoadMaterial(AccentMaterialPath);

        // The orbiter stays hand-built around an async RequestTexture — and
        // around the one texture no material file references — so every run
        // still exercises the placeholder swap and the per-frame upload pump,
        // which a cache hit from one of the loads above would hide.
        // Asked for as data, not colour: it is a single-channel gradient, and R8
        // has no sRGB form on any backend, so requesting the default would only
        // earn a warning about a downgrade that was always going to happen.
        TextureAsset orbiterTexture = assets.RequestTexture(
            OrbiterTexturePath, colorSpace: TextureColorSpace.Linear);

        // Split here so the log can attribute the startup cost: surfaces and
        // models are very different bills (five PNG decodes versus spinning up
        // the native importer), and a single lumped number would not say which
        // one to go after when it grows.
        double surfaceMs = assetClock.Elapsed.TotalMilliseconds;

        // Two props, one per path on purpose. The crate is the load-time case
        // (synchronous, in the scene on frame one); the signpost is the
        // frames-are-running case (imported on the thread pool, GPU meshes
        // created by the asset pump, placed by Update when it lands). Between
        // them they cover both halves of the model contract every run — and
        // the async one is what keeps the first prop's importer start-up from
        // being paid twice before the window appears.
        ModelAsset? crate = LoadProp(assets, CrateModelPath, ModelImportOptions.Default);
        _pendingModel = RequestProp(assets, SignpostModelPath, GltfImportOptions);

        assetClock.Stop();
        double modelMs = assetClock.Elapsed.TotalMilliseconds - surfaceMs;

        var center = scene.Root.CreateChild("SpinningCube");
        center.MeshRenderer = new MeshRenderer(cubeMesh, cubeMaterial);
        _spinner = center;

        var orbiter = center.CreateChild("Orbiter");
        orbiter.LocalTransform = new Transform
        {
            Position = new Vector3(2f, 0f, 0f),
            Rotation = Quaternion.Identity,
            Scale = new Vector3(0.4f, 0.4f, 0.4f),
        };
        orbiter.MeshRenderer = new MeshRenderer(cubeMesh,
            new Material(shader)
                .SetVector3("uBaseColor", new Vector3(0.3f, 0.6f, 1f))
                .SetTexture("uDiffuse", 0, orbiterTexture));

        // Placed BEFORE the world compile, not after. Attaching any node bumps
        // the scene's graph-structure version, and the static-world compile
        // treats a structure change as "traversal order may have moved" — so a
        // node added after the load-time rebuild would cost the very first
        // background recompile its trusted incremental path and make it a full
        // re-validation instead (measured: 38 ms rather than 14 ms).
        if (crate is not null)
            PlaceProp(scene, crate, "Crate", CratePosition);

        // The PBR reference row, in front of the room where the startup camera
        // already looks. Mesh nodes rather than brushes, so it goes in here with
        // the other content rather than into the static-world build below.
        AddPbrSpheres(scene, renderer, assets);

        double worldMs = BuildStaticWorld(scene, renderer, worldMaterial);

        // Host overrides, both off unless a switch names a path. Load first, so
        // that naming both paths copies one bundle to another through the
        // engine's own reader and writer, which is the cheapest end-to-end check
        // the format has.
        if (CompiledMapPathOverride is { } compiledPath
            && LoadCompiledMapInto(scene, renderer, assets, compiledPath, out double compiledMs))
        {
            worldMs += compiledMs;
        }
        else if (LoadMapPathOverride is { } loadPath)
        {
            worldMs += LoadMapInto(scene, renderer, loadPath);
        }

        if (SaveMapPathOverride is { } savePath)
            SaveMapFrom(scene, savePath);

        if (SaveProjectPathOverride is { } projectPath)
            SaveProjectFrom(scene, assets, projectPath);

        ActiveScene = scene;

        // Last, and only once the scene is complete: the editing layer adopts
        // the camera and reads the selection the moment it is built, so it must
        // not see a half-authored world.
        Editor = EditorFactory?.Invoke(scene);

        // Physics comes up with the scene and goes down with it. Falling back
        // to the null backend rather than leaving this null is what keeps the
        // engine loop free of per-call null checks on a path that runs every
        // frame and every tick.
        Physics = PhysicsFactory?.Invoke(scene) ?? NullScenePhysics.Instance;
        if (Physics.IsSimulating)
            _logger.LogInformation("Physics backend installed: {Backend}", Physics.GetType().Name);

        loadClock.Stop();

        _logger.LogInformation(
            "Demo scene '{Name}' loaded in {TotalMs:0.0} ms " +
            "(assets {AssetMs:0.0} ms = {SurfaceMs:0.0} ms materials/textures + {ModelMs:0.0} ms models, " +
            "static world {WorldMs:0.0} ms); content root {Root}; " +
            "{Materials} material(s), {Textures} texture(s), {Models} model(s) requested ({Placed} placed so far)",
            scene.Name, loadClock.Elapsed.TotalMilliseconds, assetClock.Elapsed.TotalMilliseconds,
            surfaceMs, modelMs, worldMs,
            assets.ContentRootPath, assets.MaterialCount, assets.TextureCount, _modelsRequested, _modelsPlaced);
    }

    /// <summary>
    /// Replaces the scene's graph with a bundle from disk and recompiles.
    /// </summary>
    /// <returns>Milliseconds spent, so the load line still accounts for it.</returns>
    /// <summary>
    /// Replaces the scene's graph and static world with a baked map from the
    /// mounted content, running no CSG.
    /// </summary>
    /// <returns>
    /// False when there is no compiled map at that path, so the caller can fall
    /// back to the authored bundle. True when one was found, whether or not every
    /// node in it survived.
    /// </returns>
    /// <remarks>
    /// <para><b>The carve counter brackets the load, and the line prints the
    /// delta on every run.</b> The claim a compiled map makes is not that the
    /// picture is right - a loader that re-carved would draw a plausible frame,
    /// and every wall twice - it is that the carve never ran, which nothing about
    /// the resulting scene can be asked. A number on a startup line is how that
    /// claim stays true in a shipped build rather than only in a test.</para>
    /// <para><b>A miss is an ERROR and then a fallback, deliberately in that
    /// order.</b> Falling back silently would make a project cooked without its
    /// maps look exactly like a passing cooked run, which is the one thing
    /// <c>--pack</c> exists to tell apart; refusing outright would leave a level
    /// nobody can look at while they fix the cook.</para>
    /// <para><b>The demo's two live node references are DROPPED rather than
    /// rebound.</b> The bob animates a brush to force a recompile per frame and
    /// the editing self-test drags one and asserts the compile dirtied its cell -
    /// and an adopted world never compiles, by construction. Rebinding them would
    /// leave both quietly doing nothing; dropping them says so once.</para>
    /// </remarks>
    private bool LoadCompiledMapInto(
        Scene scene, Renderer renderer, AssetManager assets, string contentPath, out double milliseconds)
    {
        var clock = Stopwatch.StartNew();
        milliseconds = 0;

        if (!assets.Content.TryOpen(contentPath, out Assets.Sources.ContentBlob? file))
        {
            _logger.LogError(
                "No compiled map at '{Path}' in the mounted content. Run 'scook cook <project>' to bake the " +
                "project's maps; falling back to the authored bundle, which is NOT what a shipped build would " +
                "load", contentPath);

            return false;
        }

        long carvesBefore = Csg.CarveInvocationsOnThisThread;

        try
        {
            // Ownership of the blob passes here, and it is not disposed on the way
            // out: the adopted world's BSP nodes are a window into these bytes, and
            // on a mounted pack that window is a memory-mapped view whose unmapping
            // under a live span is an access violation with no managed stack.
            CompiledMapLoadReport report = CompiledMapLoader.Load(scene, renderer, file, contentPath);

            _pillarBob = null;
            SelfTestNode = null;

            clock.Stop();
            milliseconds = clock.Elapsed.TotalMilliseconds;

            _logger.LogInformation(
                "Compiled map '{Path}' loaded in {Ms:0.0} ms: scene '{Name}', {Nodes} node(s), {Chunks} " +
                "chunk(s) as {Submeshes} GPU mesh(es) and {Triangles} triangle(s), {Trees} BSP tree(s), " +
                "{Materials} material(s) interned, {Skipped} unknown section(s) skipped; "
                + "{Carves} carve(s) run",
                contentPath, milliseconds, scene.Name, report.NodesLoaded, report.ChunksLoaded,
                report.SubmeshesUploaded, report.TriangleCount, report.BspChunksLoaded,
                report.MaterialsInterned, report.SkippedSections,
                Csg.CarveInvocationsOnThisThread - carvesBefore);

            if (report.BakedBrushSourcesSkipped > 0)
            {
                _logger.LogInformation(
                    "Static world guard: {Count} baked brush(es) offered authored planes and were not " +
                    "re-carved. Carving them would draw those walls twice", report.BakedBrushSourcesSkipped);
            }

            if (report.Describe() is { } lost)
                _logger.LogWarning("Compiled map load is incomplete. {What}", lost);

            // Printed every run, like the entity catalogue's class count: what a
            // build cannot carry is a fact about the BUILD, and a shipped binary
            // that silently does less than the last one is exactly what a standing
            // line is for.
            _logger.LogWarning("Compiled map limits. {Gaps}", CompiledMapLoadReport.DescribeFormatGaps());

            return true;
        }
        catch (ScmapFormatException ex)
        {
            clock.Stop();
            _logger.LogError(ex,
                "Could not load compiled map '{Path}'; falling back to the authored bundle", contentPath);

            return false;
        }
    }

    private double LoadMapInto(Scene scene, Renderer renderer, string bundlePath)
    {
        var clock = Stopwatch.StartNew();

        // Captured BEFORE the graph is replaced. Both of these are live node
        // references held by the authored demo, and a replaced graph leaves
        // them pointing at detached nodes: the bob would then write a pose
        // every frame to a node nothing renders, and the editing self-test
        // would pick against one, neither of which fails or reports anything.
        //
        // Rebound by NAME rather than by id, which looks backwards and is not.
        // Ids are identity and they round-trip through the map perfectly - but
        // the demo AUTHORS its scene procedurally, so every run mints fresh
        // Guids, and the ids in a saved bundle belong to the run that wrote it.
        // The names are literals in this file, which makes them the only stable
        // key the demo actually has for its own content.
        string? bobName = _pillarBob?.Node.Name;
        string? selfTestName = SelfTestNode?.Name;

        try
        {
            var report = new MapLoadReport();
            MapSceneBinder.ApplyTo(MapBundle.Load(bundlePath), scene, report);

            // The synchronous cache-free path, which is exactly what a load is
            // for: the incremental compiler carries caches from a previous
            // world, and a world that was just replaced wholesale has none worth
            // carrying.
            scene.RebuildStaticWorld(renderer);

            // A foreign map carries neither name, both bindings drop, and that
            // is correct rather than a degradation: the authored scene they
            // belonged to is not loaded any more.
            SceneNode? bobNode = FindDemoNode(scene, bobName);
            _pillarBob = bobNode is null
                ? null
                : new DemoBobAnimation(bobNode, PillarBobAmplitude, PillarBobPeriodSeconds);
            SelfTestNode = FindDemoNode(scene, selfTestName);

            clock.Stop();

            _logger.LogInformation(
                "Loaded map bundle '{Path}' in {Ms:0.0} ms: scene '{Name}', {Nodes} root node(s); "
                + "demo animation {Bob}, self-test node {Test}",
                bundlePath, clock.Elapsed.TotalMilliseconds, scene.Name, scene.Root.Children.Count,
                _pillarBob is null ? "dropped" : "rebound", SelfTestNode is null ? "dropped" : "rebound");

            // A map naming a model this project does not have still loads, with
            // that node standing where it belongs and drawing nothing. The
            // level designer needs to see the level in order to fix the prop.
            if (report.Describe() is { } missing)
                _logger.LogWarning("Map load is incomplete. {What}", missing);
        }
        catch (Exception ex) when (ex is MapFormatException or IOException or UnauthorizedAccessException)
        {
            clock.Stop();
            _logger.LogError(ex,
                "Could not load map bundle '{Path}'; running the authored demo scene instead", bundlePath);
        }

        return clock.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Finds one of the demo's own root-level nodes by name after a map load.
    /// </summary>
    /// <remarks>
    /// Root children only, and deliberately: the two nodes this exists for are
    /// authored at the root, and a name is not unique anywhere else in a scene
    /// graph. A general find-by-name would be a worse API pretending to be a
    /// better one.
    /// </remarks>
    private static SceneNode? FindDemoNode(Scene scene, string? name)
    {
        if (name is null) return null;

        foreach (SceneNode child in scene.Root.Children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
                return child;
        }
        return null;
    }

    /// <summary>
    /// Exports the scene as a standalone project folder: the layout, the
    /// content it references, one map, and a manifest naming it.
    /// </summary>
    /// <remarks>
    /// <b>The whole content root is copied, and that is deliberately blunt.</b>
    /// Working out which files a map actually needs means parsing every
    /// material for its textures and every model for its material library,
    /// which is the cook's dependency walk and is a real piece of work with its
    /// own correctness rules. A worse guess at it here would ship projects
    /// missing one texture nobody noticed; copying everything is obviously
    /// wrong in a way that costs disk rather than correctness.
    /// </remarks>
    private void SaveProjectFrom(Scene scene, AssetManager assets, string projectPath)
    {
        try
        {
            string name = SanitiseProjectName(scene.Name);
            ProjectLayout project = ProjectLayout.Create(projectPath, name);

            int copied = CopyTree(assets.ContentRootPath, project.AssetsPath);

            string mapRelative = $"{ProjectFormat.MapsFolder}/{name}{MapFormat.BundleExtension}";
            var report = new MapSaveReport();
            MapBundle.Save(project.Resolve(mapRelative), MapSceneBinder.FromScene(scene, report));

            if (!project.Project.Maps.Contains(mapRelative))
                project.Project.Maps.Add(mapRelative);
            project.Project.StartupMap = mapRelative;
            project.Save();

            _logger.LogInformation(
                "Exported project '{Name}' to {Path}: {Map}, {Files} content file(s) copied",
                name, project.Root, mapRelative, copied);

            if (report.Describe() is { } lost)
                _logger.LogWarning("Project export is incomplete. {What}", lost);
        }
        catch (Exception ex) when (ex is MapFormatException or ProjectFormatException
                                      or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not export a project to '{Path}'", projectPath);
        }
    }

    // A scene name reaches a file name here, so anything a path cannot carry
    // has to go. Falls back rather than throwing: an awkward name is not a
    // reason to refuse an export.
    private static string SanitiseProjectName(string sceneName)
    {
        Span<char> buffer = stackalloc char[sceneName.Length];
        int length = 0;
        foreach (char c in sceneName)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-')
                buffer[length++] = c;
        }
        return length == 0 ? "Project" : new string(buffer[..length]);
    }

    private static int CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return 0;

        int copied = 0;
        foreach (string source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(from, source);
            string destination = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            copied++;
        }
        return copied;
    }

    /// <summary>Writes the finished scene out as a bundle.</summary>
    private void SaveMapFrom(Scene scene, string bundlePath)
    {
        try
        {
            var report = new MapSaveReport();
            bool wrote = MapBundle.Save(bundlePath, MapSceneBinder.FromScene(scene, report));

            _logger.LogInformation("Saved map bundle '{Path}' ({State})",
                bundlePath, wrote ? "written" : "unchanged, byte for byte");

            // Never at Information: a map that quietly forgot the props would be
            // a map somebody trusts.
            if (report.Describe() is { } lost)
                _logger.LogWarning("Map save is incomplete. {What}", lost);
        }
        catch (Exception ex) when (ex is MapFormatException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save map bundle '{Path}'", bundlePath);
        }
    }

    // Load-time prop: synchronous, so it is in the scene on the very first
    // frame. A content failure must not take the demo down with it — the rest
    // of the scene is still worth showing, and the logged reason is the
    // actionable part. (LoadModel is the one asset entry point that reports
    // failure by throwing, precisely so load-time callers can decide this.)
    private ModelAsset? LoadProp(AssetManager assets, string path, ModelImportOptions options)
    {
        _modelsRequested++;
        try
        {
            return assets.LoadModel(path, options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Demo prop {Path} failed to load; the demo runs without it", path);
            return null;
        }
    }

    // Frames-are-running prop: returns a handle immediately and reports its
    // failure through the handle, so there is nothing to catch here.
    private ModelAsset? RequestProp(AssetManager assets, string path, ModelImportOptions options)
    {
        _modelsRequested++;
        return assets.RequestModel(path, options);
    }

    // Places a loaded model: a holder node carries the placement and the
    // authored-units conversion, and the model's own subtree hangs under it
    // untouched — overwriting the instance root's transform instead would
    // silently discard whatever transform the model file put on its root node.
    private void PlaceProp(Scene scene, ModelAsset model, string name, Vector3 position)
    {
        var holder = scene.Root.CreateChild(name);
        holder.LocalTransform = new Transform
        {
            Position = position,
            Rotation = Quaternion.Identity,
            Scale = new Vector3(1f / ModelUnitsPerWorldUnit),
        };
        ModelInstantiator.InstantiateInto(holder, model);
        _modelsPlaced++;
    }

    // Authors the static, brush-based half of the demo as scene nodes — a floor
    // slab, two walls, two pillars, and ~200 scattered parts — then compiles
    // them into the scene's derived world. Returns the compile's wall time.
    private double BuildStaticWorld(Scene scene, Renderer renderer, Material worldMaterial)
    {
        // The fallback for faces that name no material of their own — which
        // here is the scattered parts, and nothing else: the hand-authored
        // structures are all explicitly surfaced below.
        scene.StaticWorldMaterial = worldMaterial;

        // Each brush node carries its size in the brush (local half-extents)
        // and its placement on the node — the brush-local frame keeps CSG
        // precision independent of world position. The walls and pillars sit
        // flush against the floor slab (its top is y = -1.0, its rim x/z = ±3);
        // CSG resolves the coincident faces there.
        //
        // Every structure wears a distinct material, and they share chunk cells
        // with each other and with the parts — so the compile has to split
        // those cells per face material, the swap has to resolve every id, and
        // one glance at the demo tells you whether it did.
        MaterialRef floorMaterial = MaterialRegistry.Intern(FloorMaterialPath);
        MaterialRef wallMaterial = MaterialRegistry.Intern(WallMaterialPath);
        MaterialRef pillarMaterial = MaterialRegistry.Intern(PillarMaterialPath);
        MaterialRef accentMaterial = MaterialRegistry.Intern(AccentMaterialPath);

        AddBrushNode(scene, "Floor", new Vector3(0f, -1.1f, 0f), new Vector3(3f, 0.1f, 3f), floorMaterial);
        AddBrushNode(scene, "WallNorth", new Vector3(0f, -0.1f, -3.1f), new Vector3(3.1f, 1f, 0.1f), wallMaterial);
        AddBrushNode(scene, "WallWest", new Vector3(-3.1f, -0.1f, 0.05f), new Vector3(0.1f, 1f, 3.05f), wallMaterial);

        // A doorway, cut with a SUBTRACTIVE brush. It is deliberately flush
        // through the north wall's full thickness — its ±z planes coincide
        // exactly with the wall's — because that is the archetypal authored cut
        // AND the coplanar case unmodified carve handling gets wrong, so a
        // regression shows up as a door that has silently sealed itself rather
        // than as a crash. The negative renders nothing of its own; what you see
        // through the opening is the cavity walls it induced in the wall, and
        // they wear this brush's own material.
        var doorway = scene.Root.CreateChild("DoorwayCut");
        doorway.LocalPosition = new Vector3(0f, -0.45f, -3.1f);
        doorway.Brush = Brush
            .CreateBox(new Vector3(-0.5f, -0.65f, -0.15f), new Vector3(0.5f, 0.65f, 0.15f), accentMaterial)
            .WithOperation(BrushOperation.Subtractive);

        AddDemoLights(scene);

        // The bobbing pillar re-compiles continuously, so its cells prove the
        // per-material split survives every incremental recompile — not just
        // the initial build. It stays editable while it does: the animation
        // adopts whatever the editor last left on the node instead of
        // overwriting it (see DemoBobAnimation).
        SceneNode bobbingPillar = AddBrushNode(
            scene, "PillarA", new Vector3(-2f, 0.1f, -2f), PillarHalfExtent, pillarMaterial);
        _pillarBob = new DemoBobAnimation(bobbingPillar, PillarBobAmplitude, PillarBobPeriodSeconds);

        // THE PER-FACE PROOF. PillarB is the bobbing pillar's twin except for
        // the single face turned towards the demo camera, which wears the gray
        // checker instead of the orange one: one brush, two materials, so the
        // face payload has to survive carve -> snap -> weld -> mesh split and
        // be resolved per submesh at upload. A regression renders as a
        // uniformly orange pillar, which is exactly the kind of thing a
        // headless smoke run cannot see and a human can.
        //
        // It doubles as the host editing self-test's subject (see
        // SelfTestNode): it is a brush node, it never moves on its own, and at
        // +x/+z it sits in chunk cells the bobbing pillar at -x/-z never
        // touches — so a dirty-cell set that covers it after a simulated drag
        // can only have come from that drag.
        var pillarB = scene.Root.CreateChild("PillarB");
        pillarB.LocalPosition = new Vector3(2f, 0.1f, 2f);
        pillarB.Brush = Brush
            .CreateBox(-PillarHalfExtent, PillarHalfExtent, pillarMaterial)
            .WithFaceMaterial(BoxFacePlusZ, accentMaterial);
        SelfTestNode = pillarB;

        // The human-scale course, authored BESIDE the room above rather than
        // replacing it. Everything up to this point is 6x6 with a 1.2-unit
        // doorway: correct as a CSG fixture, unusable by a 1.8-unit character,
        // and not to be resized for that reason. See DemoPlayArea.
        int playAreaBrushes = DemoPlayArea.Build(scene, floorMaterial, wallMaterial, accentMaterial);
        PlayerSpawn = DemoPlayArea.Spawn;
        PlayerSpawnYaw = DemoPlayArea.SpawnYaw;
        PlayerFallOutHeight = DemoPlayArea.FallOutHeight;

        // One PART brush, floating clear of everything else so the difference
        // is legible rather than a z-fight. It proves the other half of the
        // brush story renders at all: it is not in the placement list, carves
        // nothing, is carved by nothing, and draws from its own brush-local
        // mesh under its node's world matrix. A regression here is an invisible
        // box — which the periodic stats line catches ("N of N part brush(es)")
        // even when nobody is looking at the window.
        var floatingPart = scene.Root.CreateChild("FloatingPart");
        floatingPart.LocalPosition = new Vector3(0f, 2.6f, -3f);
        floatingPart.LocalRotation = Quaternion.CreateFromYawPitchRoll(0.6f, 0.3f, 0f);
        floatingPart.BrushKind = BrushKind.Part;
        floatingPart.Brush = Brush
            .CreateBox(new Vector3(-0.4f, -0.4f, -0.4f), new Vector3(0.4f, 0.4f, 0.4f), pillarMaterial)
            .WithFaceMaterial(BoxFacePlusZ, accentMaterial);

        int partCount = AddScatteredParts(scene);
        int propCount = AddSharedProps(scene, accentMaterial);
        if (propCount > 0)
        {
            // Said out loud because the number that matters is not the node
            // count but how few meshes it resolves to: one, and the gap between
            // the two is exactly what instancing would collapse.
            // One placeholder per argument, and NEVER the same name twice: a
            // repeated name with a single argument binds nothing and the whole
            // line is dropped, with no exception and no warning anywhere. That
            // is how this one spent its first run invisible.
            _logger.LogInformation(
                "Props: {Nodes} part-brush node(s) sharing 1 brush instance -> " +
                "1 GPU mesh and {Draws} draw(s) differing only in world matrix",
                propCount, propCount);
        }

        // Initial build stays synchronous: the sanity checks below need the
        // compiled world immediately, and load time may block. Frame-to-frame
        // edits afterwards go through the engine's async recompile pump. The
        // duration is logged as startup-cost evidence: ~200 mostly-isolated
        // parts must compile in well under a second.
        var stopwatch = Stopwatch.StartNew();
        scene.RebuildStaticWorld(renderer);
        stopwatch.Stop();
        var world = scene.StaticWorld!;

        // Sanity-check CSG and the routed per-cell BSP queries against the
        // geometry we built. The probes sit inside the part-free center sites,
        // so the scatter cannot perturb them.
        bool floorSolid = world.ContainsPoint(new Vector3(0f, -1.1f, 0f));
        bool pillarSolid = world.ContainsPoint(new Vector3(-2f, 0f, -2f));
        bool airEmpty = !world.ContainsPoint(new Vector3(0f, 3f, 0f));
        bool rayHitsFloor = world.Raycast(
            new Vector3(0f, 3f, 0f), -Vector3.UnitY, 10f, out var hit);

        // The subtraction, checked both ways in one breath. A doorway that
        // silently seals itself renders as a solid wall — correct-looking
        // geometry, no exception anywhere — so "open" has to be asserted, not
        // eyeballed; and the lintel above it proves the cut did not take more
        // than it was asked for, which is the failure mode of a wall/face
        // partition that has gone wrong.
        bool doorwayOpen = !world.ContainsPoint(new Vector3(0f, -0.45f, -3.1f));
        bool lintelSolid = world.ContainsPoint(new Vector3(0f, 0.75f, -3.1f));

        // The same two questions asked of the play area's door, at the size a
        // person walks through. Worth asking twice: the room's door is 1.2 units
        // tall and its cut is flush in z, this one is 2.2 and flush in x, and a
        // carve bug that respects one axis and not the other would pass the
        // first probe and strand the character behind a sealed wall.
        bool playDoorOpen = !world.ContainsPoint(new Vector3(143.5f, 3.0f, 0f));
        bool playDoorJamb = world.ContainsPoint(new Vector3(143.5f, 3.0f, 2f));
        bool playFloorSolid = world.ContainsPoint(new Vector3(133f, -0.5f, 0f));
        bool playChasmOpen = !world.ContainsPoint(new Vector3(153.5f, -2.5f, 10f));

        _logger.LogInformation(
            "Static world: {Brushes} brush nodes ({Parts} scattered parts, {Play} play area) -> " +
            "{Surfaces} carved surfaces " +
            "in {Chunks} chunks wearing {Materials} distinct face material(s), compiled in {Ms:0.0} ms; " +
            "floor-solid={Floor}, pillar-solid={Pillar}, air-empty={Air}, ray-hit={Hit} at y={Y:0.000}, " +
            "doorway-open={Doorway}, lintel-solid={Lintel}; " +
            "play area: floor-solid={PlayFloor}, door-open={PlayDoor}, jamb-solid={PlayJamb}, " +
            "chasm-open={PlayChasm}",
            world.Brushes.Count, partCount, playAreaBrushes, world.Surfaces.Count, world.Chunks.Count,
            CountFaceMaterials(scene), stopwatch.Elapsed.TotalMilliseconds,
            floorSolid, pillarSolid, airEmpty, rayHitsFloor, hit.Point.Y,
            doorwayOpen, lintelSolid,
            playFloorSolid, playDoorOpen, playDoorJamb, playChasmOpen);

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    // One allocating string per five seconds, and only while the character is
    // walking — the rest of the stats line is allocation-free and this stays out
    // of its way by returning an interned literal when nothing is running.
    //
    // It reports the two disclosed counters as well as the pose, because both
    // are silent failures otherwise: uncovered cut brushes mean the character is
    // colliding with geometry that is not drawn, and dropped planes mean a wall
    // stopped existing for a tick because the contact budget was full.
    private string DescribeCharacter()
    {
        if (Character is not { Active: true } character)
            return Character is null ? "not installed" : "idle";

        Physics.Character.CharacterState state = character.State;
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "at ({0:0.0}, {1:0.0}, {2:0.0}), {3:0.0} sunit/s, {4}, {5} respawn(s), " +
            "{6} lane rebuild(s), {7} uncovered cut brush(es), {8} dropped plane(s)",
            state.Position.X, state.Position.Y, state.Position.Z,
            character.HorizontalSpeed,
            state.Grounded ? "grounded" : "airborne",
            character.Respawns,
            character.Collision.WorldLaneRebuilds,
            character.Collision.UncoveredCutBrushes,
            character.Collision.DroppedPlanes);
    }

    // The same shape as DescribeCharacter above, and for the same reason: one
    // allocating string per five seconds, only while a world is running, and an
    // interned literal the rest of the time.
    //
    // It reports the disclosed counters as well as the population, because both
    // failures are silent otherwise: a budget trip means a cascade was dropped
    // mid-tick, and a discarded count that keeps climbing means the level has a
    // relay loop in it that is still firing.
    private string DescribeEntities()
    {
        if (EntityWorld is not { IsActive: true } world)
            return "not running";

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0} live, {1} name(s), {2} dispatch(es) last tick, {3} pending, " +
            "{4} budget trip(s), {5} discarded",
            world.Entities.Count,
            world.Index?.NameCount ?? 0,
            world.LastTickDispatchCount,
            world.PendingEventCount,
            world.DispatchBudgetTripCount,
            world.DiscardedEventCount);
    }

    // How many distinct material ids the uploaded chunks actually carry — the
    // machine-checkable half of the per-face proof. The brushes above name five
    // materials between them (four explicit plus the parts' default fallback),
    // so anything less than that in the log means a face payload was lost
    // somewhere between authoring and the GPU submesh split, whatever the
    // screen happens to look like. Load-time only; the HashSet is deliberate
    // and never touches a frame.
    private static int CountFaceMaterials(Scene scene)
    {
        var seen = new HashSet<int>();
        IReadOnlyList<StaticWorldChunkMesh> chunks = scene.StaticWorldChunkMeshes;
        for (int i = 0; i < chunks.Count; i++)
        {
            StaticWorldSubmesh[] submeshes = chunks[i].Submeshes;
            for (int s = 0; s < submeshes.Length; s++)
                seen.Add(submeshes[s].SourceMaterial.Id);
        }
        return seen.Count;
    }

    // Scatters one part-brush per grid site over the PartAreaSize^2 area (see
    // the constants above). Deterministic by construction: a fixed-seed LCG
    // instead of System.Random (whose sequence is not guaranteed stable across
    // runtimes), so every run builds the identical world and smoke-run logs
    // stay comparable. Returns the number of parts added.
    private static int AddScatteredParts(Scene scene)
    {
        int sites = ScatterGridOverride ?? PartGridSites;
        const float spacing = PartAreaSize / PartGridSites;
        float halfArea = sites * spacing * 0.5f;
        ulong state = 0x5CA77E12EDB0B5EDUL;

        int count = 0;
        for (int gx = 0; gx < sites; gx++)
        {
            for (int gz = 0; gz < sites; gz++)
            {
                // Skip sites whose square overlaps the hand-authored center
                // ([-5,5]^2 in x/z covers the floor, both walls and both
                // pillars): parts must not fuse with the structures the sanity
                // probes test.
                float siteMinX = -halfArea + gx * spacing;
                float siteMinZ = -halfArea + gz * spacing;
                if (siteMinX < 5f && siteMinX + spacing > -5f &&
                    siteMinZ < 5f && siteMinZ + spacing > -5f)
                    continue;

                // Half-extents in [0.4, 1.6]; the center jitter keeps the part
                // (max reach 1.6) strictly inside its site square, so parts
                // never touch across sites and every carve stays isolated.
                var halfExtent = new Vector3(
                    0.4f + NextFloat01(ref state) * 1.2f,
                    0.4f + NextFloat01(ref state) * 1.2f,
                    0.4f + NextFloat01(ref state) * 1.2f);
                float cx = siteMinX + 2f + NextFloat01(ref state) * (spacing - 4f);
                float cz = siteMinZ + 2f + NextFloat01(ref state) * (spacing - 4f);
                // Bottoms rest at or just above the floor plane (y = -1.0).
                float bottom = -1f + NextFloat01(ref state) * 0.5f;

                AddBrushNode(scene, $"Part{count}", new Vector3(cx, bottom + halfExtent.Y, cz), halfExtent);
                count++;
            }
        }

        return count;
    }

    // Scatters PropCountOverride part-brush nodes that ALL SHARE ONE brush
    // instance — see PropCountOverride for why that sharing is the point.
    // Deterministic like AddScatteredParts, and for the same reason: a
    // throughput measurement that moved between runs would measure the LCG.
    // Returns the number placed.
    private static int AddSharedProps(Scene scene, MaterialRef material)
    {
        int count = PropCountOverride ?? 0;
        if (count <= 0)
            return 0;

        // One brush, N nodes. PartBrushMeshCache keys on reference identity,
        // so this stays one GPU mesh however large count gets — which is what
        // makes the draw list's N entries a batch rather than N unrelated
        // draws that merely look alike.
        Brush shared = Brush.CreateBox(
            new Vector3(-PropHalfExtent), new Vector3(PropHalfExtent), material);

        // A square grid sized to the count, with the SPACING fixed rather than
        // the area: doubling the count has to measure twice the content, not
        // the same content packed tighter into the same frustum.
        int side = (int)Math.Ceiling(Math.Sqrt(count));
        float half = (side - 1) * PropSpacing * 0.5f;
        ulong state = 0x9E3779B97F4A7C15UL;

        // Under one parent, so the scene tree stays legible at a thousand of
        // them and the whole scenario is one collapsed row.
        SceneNode props = scene.Root.CreateChild("Props");

        int placed = 0;
        for (int gx = 0; gx < side && placed < count; gx++)
        {
            for (int gz = 0; gz < side && placed < count; gz++)
            {
                float x = -half + gx * PropSpacing;
                float z = -half + gz * PropSpacing;

                // Clear of the hand-authored room, whose sanity probes raycast
                // through this space and would otherwise hit a prop.
                if (MathF.Abs(x) < 8f && MathF.Abs(z) < 8f)
                    continue;

                SceneNode node = props.CreateChild($"Prop{placed}");
                node.LocalPosition = new Vector3(
                    x, 0.5f + NextFloat01(ref state) * PropStackHeight, z);
                node.LocalRotation = Quaternion.CreateFromYawPitchRoll(
                    NextFloat01(ref state) * MathF.Tau, 0f, 0f);

                // Kind BEFORE brush: the brush setter dirties the static world
                // through the node's scene, and a part must never be admitted
                // to the placement list even for the one frame in between.
                node.BrushKind = BrushKind.Part;
                node.Brush = shared;
                placed++;
            }
        }

        return placed;
    }

    // Minimal 64-bit LCG (Knuth MMIX constants), uniform float in [0, 1) from
    // the high 24 bits — the same generator the CsgBench harness uses for its
    // deterministic worlds.
    private static float NextFloat01(ref ulong state)
    {
        state = state * 6364136223846793005UL + 1442695040888963407UL;
        return (state >> 40) * (1.0f / (1 << 24));
    }

    // A brush node: placement on the node, size in the brush. CreateBox with
    // symmetric extents keeps the brush's local frame centred on its origin.
    // A non-default material makes the compile split that brush's cells into
    // per-material submeshes, so the demo exercises the multi-material render
    // path rather than only the uniform fast path.
    // The demo's light rig: one sun plus a few coloured point lights.
    //
    // Deliberately more than the engine used to have and fewer than the cap, so
    // an ordinary run exercises the nearest-N selection without ever hitting the
    // drop path. Colours are LINEAR, because everything downstream of here is:
    // a colour picked in a paint program goes through ColorSpace.SrgbToLinear
    // first, and typing display values straight in is how a light ends up
    // looking washed out and nothing says why.
    private static void AddDemoLights(Scene scene)
    {
        // The sun. A directional light takes its direction from the node's
        // forward axis, so it is placed by rotation rather than position, and
        // the rotation is DERIVED from the direction rather than typed as euler
        // angles, because this light spent its whole existence shining upward
        // from below when it was authored as a yaw and a pitch. Down and away
        // from the startup camera, which sits at +z looking at the origin.
        SceneNode sun = scene.Root.CreateChild("Sun");
        sun.LocalRotation = Light.RotationForDirection(new Vector3(-0.35f, -0.85f, -0.4f));
        sun.Light = new Light
        {
            Kind = LightKind.Directional,
            Color = ColorSpace.SrgbToLinear(new Vector3(1f, 0.96f, 0.88f)),

            // RETUNED FOR PBR, and the factor is not a matter of taste. The old
            // forward shader was albedo * N.L * radiance with no normalisation;
            // a real Lambert term divides by pi, so every one of these numbers
            // was suddenly worth a third of what it had been. The scene did not
            // read as a lighting bug, it read as a dark room, which is exactly
            // why it survived the pipeline change.
            Intensity = 11f,
        };

        AddPointLight(scene, "LampWarm", new Vector3(-3f, 2.2f, 2.5f),
            new Vector3(1f, 0.55f, 0.2f), intensity: 45f, range: 9f);
        AddPointLight(scene, "LampCool", new Vector3(3.2f, 1.8f, -2.2f),
            new Vector3(0.3f, 0.6f, 1f), intensity: 38f, range: 8f);

        // Over the play area, so walking there in play mode is lit by something
        // other than the sun and the point-light path is exercised where a
        // person actually looks at it.
        AddPointLight(scene, "PlayAreaLamp", DemoPlayArea.Center + new Vector3(-8f, 4f, 0f),
            new Vector3(0.9f, 0.9f, 1f), intensity: 110f, range: 18f);
    }

    private static void AddPointLight(
        Scene scene, string name, Vector3 position, Vector3 displayColor, float intensity, float range)
    {
        SceneNode node = scene.Root.CreateChild(name);
        node.LocalPosition = position;
        node.Light = new Light
        {
            Kind = LightKind.Point,
            Color = ColorSpace.SrgbToLinear(displayColor),
            Intensity = intensity,
            Range = range,
        };
    }

    /// <summary>
    /// The PBR reference row: one sphere per test material, floating above the
    /// demo room where the startup camera already looks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Spheres, because a flat face cannot show whether a BRDF is right.</b>
    /// One normal per face makes a highlight all-or-nothing and roughness read
    /// as a brightness change; on a curve it becomes a shape that grows and
    /// softens, which is the thing a regression would alter.
    /// </para>
    /// <para>
    /// <b>They are mesh nodes, not brushes, and that is deliberate.</b> A brush
    /// would be carved into the static world, welded to its neighbours and split
    /// per face material, none of which a lighting reference wants, and a
    /// sphere is the worst possible CSG input. These draw straight from a shared
    /// mesh under their own world matrix.
    /// </para>
    /// <para>
    /// Visible in every pipeline, and meaningful in exactly one so far: the
    /// forward shader reads neither roughness nor metallic, so the row is five
    /// diffuse balls there and five distinct surfaces under deferred. That
    /// difference is the fastest way to see which pipeline is running.
    /// </para>
    /// </remarks>
    private static void AddPbrSpheres(Scene scene, Renderer renderer, AssetManager assets)
    {
        var (vertices, indices) = Primitives.Sphere();
        Mesh mesh = renderer.CreateMesh(vertices, indices, VertexAttribute.StandardLayout);

        var row = scene.Root.CreateChild("PbrReference");

        // Above the room's walls (their tops are y = 0.9) so nothing occludes
        // the row, and centred on x so the startup camera at +z frames it.
        float spacing = 1.15f;
        float firstX = -0.5f * spacing * (PbrMaterialPaths.Length - 1);

        for (int i = 0; i < PbrMaterialPaths.Length; i++)
        {
            Material material = assets.LoadMaterial(PbrMaterialPaths[i]);
            var node = row.CreateChild($"PbrSphere{i}");
            node.LocalTransform = new Transform
            {
                Position = new Vector3(firstX + i * spacing, 1.7f, -1f),
                Rotation = Quaternion.Identity,
                Scale = new Vector3(0.9f),
            };
            node.MeshRenderer = new MeshRenderer(mesh, material);
        }
    }

    // Meshes created per second since the last report. A static scene should
    // create none: any steady rate is GPU resource churn, which on D3D12 costs
    // far more than the drawing it feeds.
    private long _lastMeshesCreated;

    private int _lastCompileCount;

    private double CompileRate(Scene scene)
    {
        int now = scene.StaticWorldCompileCount;
        double rate = (now - _lastCompileCount) / CompileLogIntervalSeconds;
        _lastCompileCount = now;
        return rate;
    }

    private long _lastAllocatedBytes;
    private long _lastRenderThreadBytes;
    private int _lastGen0;
    private int _lastGen1;
    private int _lastGen2;

    private double MeshCreationRate()
    {
        if (_renderer is null) return 0;
        long now = _renderer.MeshesCreated;
        double rate = (now - _lastMeshesCreated) / CompileLogIntervalSeconds;
        _lastMeshesCreated = now;
        return rate;
    }

    /// <summary>
    /// Managed allocation over the last log interval, and what the collector
    /// did about it.
    /// </summary>
    /// <remarks>
    /// <b>The rate is the number, not the heap size.</b> A frame loop that
    /// allocates steadily keeps a FLAT heap, because gen0 collects as fast as it
    /// fills, so "memory is not growing" says nothing at all about whether this
    /// engine allocates per frame. What it costs instead is a gen0 pause in the
    /// middle of a frame, which is exactly the stutter a game cannot have and
    /// exactly what a smoothed average frame time hides. Reported as a rate and
    /// a collection count so both are visible without a profiler attached.
    /// Sampled once per interval: two counter reads, not a per-frame cost.
    /// </remarks>
    private string DescribeMemory()
    {
        long allocated = GC.GetTotalAllocatedBytes(precise: false);
        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);

        // This method runs on the render thread, so the second counter is the
        // frame loop's OWN share. Splitting them is the whole point: garbage
        // made by a background compile costs a worker's time, garbage made here
        // costs the frame, and one total cannot tell them apart.
        long onThisThread = GC.GetAllocatedBytesForCurrentThread();

        double megabytesPerSecond =
            (allocated - _lastAllocatedBytes) / (1024.0 * 1024.0) / CompileLogIntervalSeconds;
        double renderMegabytesPerSecond =
            (onThisThread - _lastRenderThreadBytes) / (1024.0 * 1024.0) / CompileLogIntervalSeconds;
        string collections =
            $"{(gen0 - _lastGen0) / CompileLogIntervalSeconds:0.0}/s gen0, " +
            $"{gen1 - _lastGen1} gen1, {gen2 - _lastGen2} gen2";

        _lastAllocatedBytes = allocated;
        _lastRenderThreadBytes = onThisThread;
        _lastGen0 = gen0;
        _lastGen1 = gen1;
        _lastGen2 = gen2;

        return $"{megabytesPerSecond:0.0} MB/s allocated ({renderMegabytesPerSecond:0.0} on the render thread), " +
               $"{collections}, {GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024)} MB heap";
    }

    // One phrase for the shadow state, because "shadows are on" and "shadows
    // drew something" are different claims and a smoke log needs the second.
    // A caster count of zero with shadows enabled means the map was fitted and
    // nothing was in it, which is the shape of a cull bug.
    private static string DescribeShadows(Renderer? renderer)
    {
        if (renderer is null) return "no renderer";
        if (!renderer.ShadowsEnabled) return "shadows off";
        if (renderer.ShadowMap is not { } map) return "shadows on, no caster yet";

        // The saved count is reported rather than the batch count, because what
        // matters is the draws that did NOT happen; a scene that silently stops
        // batching reads as zero here instead of only as a slower frame.
        string batched = renderer.ShadowDrawsSaved > 0
            ? $", {renderer.ShadowDrawsSaved} draw(s) saved by instancing"
            : string.Empty;

        return $"shadows on ({renderer.ShadowCasterCount} caster(s){batched}, " +
               $"{map.CascadeCount} cascade(s) in a {map.Resolution}px atlas, " +
               $"texel {map.WorldTexelSize:0.000} sunit near / " +
               $"{map.CoarsestWorldTexelSize:0.000} far, {map.Distance:0} sunit range)";
    }

    // Reported separately from the shadow pass's saving, and for the same
    // reason: the two collapse different draw lists (the shadow pass batches
    // per cascade, the geometry pass once for the camera), so one number
    // covering both would hide either of them stopping.
    private static string DescribeGeometryBatching(Renderer? renderer) =>
        renderer is { GeometryDrawsSaved: > 0 }
            ? $", {renderer.GeometryDrawsSaved} geometry draw(s) saved by instancing"
            : string.Empty;

    // The tool and its handle style, composed. The editor reports them
    // separately now because a toolbar has to know which of three buttons is lit
    // and splitting a string to find out is a contract nobody wrote down; this
    // line wants them together, and says so here rather than making the editor
    // carry a second combined label for one caller.
    private static string DescribeGizmo(ISceneEditor? editor) =>
        editor is null ? "none" : $"{editor.GizmoModeName}/{editor.GizmoStyleName}";

    private static SceneNode AddBrushNode(
        Scene scene, string name, Vector3 center, Vector3 halfExtent, MaterialRef material = default)
    {
        var node = scene.Root.CreateChild(name);
        node.LocalPosition = center;
        node.Brush = Brush.CreateBox(-halfExtent, halfExtent, material);
        return node;
    }

    /// <summary>
    /// Per-frame demo update: animation, the async prop hand-off, and the two
    /// periodic smoke lines. Render thread only. <paramref name="renderView"/>
    /// is the engine's draw list as built LAST frame (this runs before this
    /// frame's build) — one frame stale, which a multi-second cadence doesn't
    /// care about.
    /// </summary>
    /// <remarks>
    /// Selection and manipulation are NOT handled here: they belong to the
    /// editing layer the host installs through <see cref="EditorFactory"/>,
    /// which the engine drives through <see cref="ISceneEditor"/> before this
    /// runs. A host with no editor gets a demo that animates and renders but
    /// does not select — which is exactly what a shipped game wants.
    /// </remarks>
    public void Update(double deltaTime, RenderView renderView)
    {
        _elapsed += deltaTime;

        if (_spinner is not null)
        {
            _spinner.LocalRotation = Quaternion.CreateFromYawPitchRoll(
                (float)_elapsed * 0.6f,
                (float)_elapsed * 0.4f,
                0f);
        }

        // Runs AFTER the editor has had the frame (see Engine.Run), so it must
        // never be the writer that wins an argument with a gizmo: the animation
        // re-centres on any edit made since the last frame rather than
        // overwriting it. See DemoBobAnimation for why that is not optional.
        _pillarBob?.Advance(_elapsed);

        if (ActiveScene is { } scene)
        {
            ProcessPendingProp(scene);

            // Bounded-spam smoke evidence for the whole frame pipeline, in one
            // line a headless run can grep:
            //
            //  * the asset counts prove the content chain actually resolved
            //    files — a missing texture or an unimportable model degrades
            //    silently on screen (placeholder checker, absent prop) but
            //    shows up here as a count that never reaches its target;
            //  * the chunk and material-batch counts come from last frame's
            //    render view (one frame stale, which a 5 s cadence does not
            //    care about) and prove real culling AND the per-material split:
            //    batches > chunks is only possible if some chunk carries more
            //    than one face material;
            //  * the dirty-cell count of the latest compile must stay small
            //    while only the pillar bobs, however many chunks the world has;
            //  * the editing state — how much is selected, which manipulator is
            //    live, WHICH NAVIGATION MODEL is driving the camera, how deep
            //    the history is — is the one part of the frame a headless run
            //    cannot see at all, and it is also the cheapest check that the
            //    host actually wired an editor in: a run whose editing fields
            //    read "none" has an unwired viewport, however healthy everything
            //    else looks. The navigation label in particular is the only way
            //    a smoke log distinguishes "the editor camera is driving" from
            //    "the engine fell back to its own fly camera".
            //
            // Fixed arity, no collections formatted — the line's length cannot
            // grow with the size of the world.
            if (_elapsed >= _nextCompileLogTime)
            {
                _nextCompileLogTime = _elapsed + CompileLogIntervalSeconds;
                AssetManager? assets = _assets;
                ISceneEditor? editor = Editor;
                _logger.LogInformation(
                    "Assets: {Textures} texture(s), {Materials} material(s), " +
                    "{Models} model(s) requested / {Placed} placed; " +
                    "world: {ChunksVisible} of {ChunksTotal} chunks visible, " +
                    "{BatchesVisible} of {BatchesTotal} material batches; " +
                    "scene: {NodesVisible} of {NodesTotal} mesh nodes, " +
                    "{PartsVisible} of {PartsTotal} part brush(es){InertParts}; " +
                    "recompiled {Count} times, last touched {DirtyCells} dirty cell(s); " +
                    "physics: {PhysicsBackend}, {PhysicsBodies} body(ies) / {PhysicsShapes} shape(s); " +
                    "editing: {Selected} selected, {GizmoMode} gizmo, {Navigation} navigation, " +
                    "undo {UndoDepth} / redo {RedoDepth}; " +
                    "rendering: {Pipeline} pipeline, {Shadows}{GeometryBatching}, {FrameMs:0.00} ms/frame ({Fps:0} fps) [{Phases}]; " +
                    "churn: {MeshRate:0} mesh(es)/s, {CompileRate:0} compile(s)/s, {Pooled} buffer(s) pooled; " +
                    "memory: {Memory}; " +
                    "character: {CharacterMode}; " +
                    "entities: {EntityRuntime}",
                    assets?.TextureCount ?? 0, assets?.MaterialCount ?? 0,
                    _modelsRequested, _modelsPlaced,
                    renderView.WorldChunksVisible, renderView.WorldChunksTotal,
                    renderView.WorldMaterialBatchesVisible, renderView.WorldMaterialBatchesTotal,
                    renderView.VisibleCount, renderView.TotalCount,
                    renderView.PartBrushesVisible, renderView.PartBrushesTotal,
                    scene.InertPartBrushCount > 0
                        ? $", {scene.InertPartBrushCount} INERT (subtractive parts carve nothing and draw nothing)"
                        : string.Empty,
                    scene.StaticWorldCompileCount, scene.LastCompileDirtyCells.Count,
                    Physics.IsSimulating ? Physics.GetType().Name : "none",
                    Physics.BodyCount, Physics.StaticShapeCount,
                    editor?.SelectionCount ?? 0, DescribeGizmo(editor),
                    editor?.NavigationModeName ?? "none",
                    editor?.UndoDepth ?? 0, editor?.RedoDepth ?? 0,
                    _renderer?.CurrentPipelineName ?? "none", DescribeShadows(_renderer),
                    DescribeGeometryBatching(_renderer), FrameTimeMs, Fps,
                    _renderer?.Profiler.Describe() ?? "not measured", MeshCreationRate(), CompileRate(scene), _renderer?.PooledBufferCount ?? 0,
                    DescribeMemory(),
                    DescribeCharacter(),
                    DescribeEntities());
            }

            if (_elapsed >= _nextScreenProbeTime)
            {
                _nextScreenProbeTime = _elapsed + ScreenProbeIntervalSeconds;
                RunScreenProbe(scene, renderView);
            }
        }
    }

    // Places the asynchronously-requested prop on the frame its import lands.
    // Polling the handle is a couple of reference reads on a frame with nothing
    // pending and nothing at all once it has been placed — cheaper than calling
    // RequestModel again (which is idempotent, but takes the model lock).
    //
    // Attaching the subtree bumps the graph-structure version, so the one
    // background recompile launched after it falls back from the trusted
    // incremental diff to a full re-validation. That is the documented cost of
    // a structural scene edit and it is paid exactly once, about a second into
    // the run — the alternative (no runtime scene edits) is not an engine.
    private void ProcessPendingProp(Scene scene)
    {
        if (_pendingModel is not { } model) return;

        if (model.IsReady)
        {
            _pendingModel = null;
            PlaceProp(scene, model, "Signpost", SignpostPosition);
            _logger.LogInformation(
                "Async prop {Path} landed {Seconds:0.00} s after load and was placed in the scene",
                model.RelativePath, _elapsed);
            return;
        }

        // Import failures are reported on the handle, not thrown — the caller
        // that asked is several frames gone by now. Stop polling: asking again
        // would retry the same broken file every frame.
        if (model.Error is { } error)
        {
            _pendingModel = null;
            _logger.LogWarning(
                "Async prop {Path} failed to import ({Error}); the demo runs without it",
                model.RelativePath, error);
        }
    }

    // Smoke probe: cast a ray through the viewport centre and report what it
    // struck, plus the render view's culling stats. With the demo camera aimed
    // at the scene the centre ray is expected to hit world geometry or a mesh
    // node — the Information line is a live end-to-end check that camera
    // unprojection, the BVH raycast, and the culling stats all agree with what
    // is on screen.
    //
    // Deliberately named "Scene probe" and not "self-test": the host's editing
    // self-test logs its own PASS/FAIL line, and two different checks answering
    // to the same grep in a smoke log is how a green run gets mistaken for a
    // proof it never made.
    private void RunScreenProbe(Scene scene, RenderView renderView)
    {
        if (!TryGetViewportSize(out Vector2 viewport))
            return;

        Ray3 ray = scene.Camera.ScreenPointToRay(viewport * 0.5f, viewport);
        if (scene.Raycast(in ray, out SceneRaycastHit hit))
        {
            _logger.LogInformation(
                "Scene probe: center ray hit '{Node}' at {Distance:0.00} m; " +
                "{Visible} of {Total} mesh nodes, {ChunksVisible} of {ChunksTotal} world chunks visible",
                hit.Node.Name, hit.Distance, renderView.VisibleCount, renderView.TotalCount,
                renderView.WorldChunksVisible, renderView.WorldChunksTotal);
        }
        else
        {
            _logger.LogInformation(
                "Scene probe: center ray hit nothing; " +
                "{Visible} of {Total} mesh nodes, {ChunksVisible} of {ChunksTotal} world chunks visible",
                renderView.VisibleCount, renderView.TotalCount,
                renderView.WorldChunksVisible, renderView.WorldChunksTotal);
        }
    }

    // The renderer's framebuffer latch as a float viewport size, matching the
    // aspect ratio the pipelines render with. False while minimized — a
    // zero-sized viewport has no rays through it (and would divide by zero in
    // the unprojection).
    private bool TryGetViewportSize(out Vector2 viewport)
    {
        Vector2D<int> framebuffer = _renderer?.FramebufferSize ?? default;
        if (framebuffer.X <= 0 || framebuffer.Y <= 0)
        {
            viewport = default;
            return false;
        }

        viewport = new Vector2(framebuffer.X, framebuffer.Y);
        return true;
    }

    public void Shutdown()
    {
        // Before the scene goes: a run closed while play mode was active still
        // owns a live runtime, and every entity is owed its OnRemove.
        StopEntityWorld();

        ActiveScene = null;
        Editor = null;

        // The null backend is shared and outlives any scene, so disposing it is
        // deliberately harmless — see NullScenePhysics.Dispose.
        Physics.Dispose();
        Physics = NullScenePhysics.Instance;
        SelfTestNode = null;
        _spinner = null;
        _pillarBob = null;
        _renderer = null;
        // Dropped, not unloaded: the asset manager owns every model and texture
        // the demo asked for and releases them itself (ReleaseGraphicsResources
        // on the render thread, which has already run by the time this does).
        _pendingModel = null;
        _assets = null;
        _logger.LogInformation("Scene manager shut down");
    }
}
