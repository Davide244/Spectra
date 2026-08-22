using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Bsp;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Physics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const float PartAreaSize = 200f;   // world units per side

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

    // glTF puts v = 0 at the TOP of the image; the engine samples bottom-up
    // (ImageDecoder flips decoded rows on upload), so the .gltf signpost needs
    // its v flipped where the .obj crate does not. Getting this wrong renders a
    // vertically mirrored texture rather than a missing one — too quiet a
    // failure to leave to a shared default.
    private static readonly ModelImportOptions GltfImportOptions =
        ModelImportOptions.Default with { FlipTextureV = true };

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

    public void Initialize()
    {
        _logger.LogInformation("Scene manager initialized");
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
        TextureAsset orbiterTexture = assets.RequestTexture(OrbiterTexturePath);

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

        double worldMs = BuildStaticWorld(scene, renderer, worldMaterial);

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

        _logger.LogInformation(
            "Static world: {Brushes} brush nodes ({Parts} scattered parts) -> {Surfaces} carved surfaces " +
            "in {Chunks} chunks wearing {Materials} distinct face material(s), compiled in {Ms:0.0} ms; " +
            "floor-solid={Floor}, pillar-solid={Pillar}, air-empty={Air}, ray-hit={Hit} at y={Y:0.000}, " +
            "doorway-open={Doorway}, lintel-solid={Lintel}",
            world.Brushes.Count, partCount, world.Surfaces.Count, world.Chunks.Count,
            CountFaceMaterials(scene), stopwatch.Elapsed.TotalMilliseconds,
            floorSolid, pillarSolid, airEmpty, rayHitsFloor, hit.Point.Y,
            doorwayOpen, lintelSolid);

        return stopwatch.Elapsed.TotalMilliseconds;
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
                seen.Add(submeshes[s].Source.Material.Id);
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
        const float spacing = PartAreaSize / PartGridSites;
        const float halfArea = PartAreaSize * 0.5f;
        ulong state = 0x5CA77E12EDB0B5EDUL;

        int count = 0;
        for (int gx = 0; gx < PartGridSites; gx++)
        {
            for (int gz = 0; gz < PartGridSites; gz++)
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
                    "{PartsVisible} of {PartsTotal} part brush(es); " +
                    "recompiled {Count} times, last touched {DirtyCells} dirty cell(s); " +
                    "physics: {PhysicsBackend}, {PhysicsBodies} body(ies) / {PhysicsShapes} shape(s); " +
                    "editing: {Selected} selected, {GizmoMode} gizmo, {Navigation} navigation, " +
                    "undo {UndoDepth} / redo {RedoDepth}",
                    assets?.TextureCount ?? 0, assets?.MaterialCount ?? 0,
                    _modelsRequested, _modelsPlaced,
                    renderView.WorldChunksVisible, renderView.WorldChunksTotal,
                    renderView.WorldMaterialBatchesVisible, renderView.WorldMaterialBatchesTotal,
                    renderView.VisibleCount, renderView.TotalCount,
                    renderView.PartBrushesVisible, renderView.PartBrushesTotal,
                    scene.StaticWorldCompileCount, scene.LastCompileDirtyCells.Count,
                    Physics.IsSimulating ? Physics.GetType().Name : "none",
                    Physics.BodyCount, Physics.StaticShapeCount,
                    editor?.SelectionCount ?? 0, editor?.GizmoModeName ?? "none",
                    editor?.NavigationModeName ?? "none",
                    editor?.UndoDepth ?? 0, editor?.RedoDepth ?? 0);
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
