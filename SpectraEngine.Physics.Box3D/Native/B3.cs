using System.Runtime.InteropServices;

namespace SpectraEngine.Physics.Box3D.Native;

/// <summary>
/// The raw Box3D entry points. One-to-one with the C API, no policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-curated, not generated.</b> Box3D exports 547 functions; this binds
/// the ones the engine actually calls. A generated binding would be larger,
/// would need regenerating on every pin move, and would bind surface nobody has
/// read — and every unread struct is a layout nobody checked.
/// </para>
/// <para>
/// <b>Everything here is <c>internal</c> and unwrapped.</b> Null checks,
/// refcounting, id validity and lifetime rules belong one layer up. This type's
/// only job is to be a faithful transcription, so that when behaviour surprises
/// somebody the question "does the binding match the header?" has a short
/// answer.
/// </para>
/// <para>
/// The assembly disables runtime marshalling, so every signature here is
/// blittable except where a C <c>bool</c> return or parameter forces a
/// one-byte marshal — those are annotated explicitly and generate a small
/// managed conversion rather than a runtime marshalling stub.
/// </para>
/// </remarks>
internal static partial class B3
{
    private const string Lib = "box3d";

    // --- Tier 0: process init and the ABI handshake -------------------------

    /// <summary>
    /// Whether the loaded library was built with double precision.
    /// </summary>
    /// <remarks>
    /// <b>The only runtime proof that the DLL matches the struct layouts this
    /// assembly declares.</b> Every managed type here assumes the float build;
    /// a double DLL changes the width of positions and every struct containing
    /// one, silently. Check this once at startup and refuse to continue.
    /// <para>
    /// There is a second, automatic guard: the C header renames
    /// <c>b3CreateWorld</c> under the double build, so a mismatched DLL throws
    /// <see cref="System.EntryPointNotFoundException"/> at world creation
    /// rather than corrupting memory. Both are worth having — this one names
    /// the problem, that one catches it if nobody asked.
    /// </para>
    /// </remarks>
    [LibraryImport(Lib, EntryPoint = "b3IsDoublePrecision")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool IsDoublePrecision();

    /// <summary>The library's own version, as compiled.</summary>
    /// <remarks>
    /// Trust this over the build system: at the pinned commit this reports
    /// 0.2.0 while the upstream <c>CMakeLists.txt</c> still says 0.1.0.
    /// </remarks>
    [LibraryImport(Lib, EntryPoint = "b3GetVersion")]
    internal static partial B3Version GetVersion();

    /// <summary>
    /// Sets how many metres one length unit represents, rescaling the library's
    /// own collision and constraint tolerances.
    /// </summary>
    /// <remarks>
    /// <b>Must be called before the first <c>Default*Def</c> call, not merely
    /// before the first world.</b> Those constructors bake the scale into their
    /// results at call time — contact speed, maximum linear speed, sleep
    /// threshold and density are all computed from it — so a def taken first
    /// and a unit set second gives a def tuned for the wrong scale, with
    /// nothing to indicate it.
    /// </remarks>
    [LibraryImport(Lib, EntryPoint = "b3SetLengthUnitsPerMeter")]
    internal static partial void SetLengthUnitsPerMeter(float lengthUnits);

    /// <inheritdoc cref="SetLengthUnitsPerMeter"/>
    [LibraryImport(Lib, EntryPoint = "b3GetLengthUnitsPerMeter")]
    internal static partial float GetLengthUnitsPerMeter();

    // --- Tier 1: world lifecycle and stepping -------------------------------

    /// <summary>
    /// The only supported way to obtain a <see cref="B3WorldDef"/>. See that
    /// type's remarks for why constructing one by hand is unsafe in Release.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "b3DefaultWorldDef")]
    internal static partial B3WorldDef DefaultWorldDef();

    /// <summary>
    /// Creates a world. Returns a zeroed id on failure — which in a Release
    /// build is the <em>only</em> signal, so callers must test it.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "b3CreateWorld")]
    internal static partial B3WorldId CreateWorld(in B3WorldDef def);

    /// <summary>
    /// Destroys a world. <b>Exactly once.</b>
    /// </summary>
    /// <remarks>
    /// The library decrements its global world count <em>before</em> validating
    /// the id, so a double destroy corrupts that count rather than being
    /// harmlessly ignored. The wrapper above this clears its id after calling,
    /// and there is deliberately no finalizer.
    /// </remarks>
    [LibraryImport(Lib, EntryPoint = "b3DestroyWorld")]
    internal static partial void DestroyWorld(B3WorldId worldId);

    [LibraryImport(Lib, EntryPoint = "b3World_IsValid")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool World_IsValid(B3WorldId id);

    /// <summary>Advances the world by one step.</summary>
    /// <remarks>
    /// <paramref name="timeStep"/> is a FIXED step — see
    /// <c>SpectraEngine.Core.Physics.FixedTickAccumulator</c> for why the frame
    /// delta must never reach here.
    /// </remarks>
    [LibraryImport(Lib, EntryPoint = "b3World_Step")]
    internal static partial void World_Step(B3WorldId worldId, float timeStep, int subStepCount);

    [LibraryImport(Lib, EntryPoint = "b3World_SetGravity")]
    internal static partial void World_SetGravity(B3WorldId worldId, B3Vec3 gravity);

    [LibraryImport(Lib, EntryPoint = "b3World_GetGravity")]
    internal static partial B3Vec3 World_GetGravity(B3WorldId worldId);

    /// <summary>
    /// How many worker threads the world actually runs on. Assert this is one:
    /// it is the observable proof that the serial branch was taken, rather than
    /// the library having spawned its own threads.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "b3World_GetWorkerCount")]
    internal static partial int World_GetWorkerCount(B3WorldId worldId);

    [LibraryImport(Lib, EntryPoint = "b3World_GetAwakeBodyCount")]
    internal static partial int World_GetAwakeBodyCount(B3WorldId worldId);

    [LibraryImport(Lib, EntryPoint = "b3GetWorldCount")]
    internal static partial int GetWorldCount();

    [LibraryImport(Lib, EntryPoint = "b3World_EnableSleeping")]
    internal static partial void World_EnableSleeping(
        B3WorldId worldId, [MarshalAs(UnmanagedType.U1)] bool flag);

    /// <summary>
    /// Rebuilds the static broadphase tree. Call once after a batch of chunk
    /// bodies changes, never per shape.
    /// </summary>
    /// <remarks>
    /// Upstream's header comment says only "This is for internal testing", but
    /// it is the sole static-tree control the API exposes and the implementation
    /// does a genuine full rebuild. Bound deliberately, with the caveat
    /// recorded: if it disappears under a moved pin, the static tree simply
    /// stays as the incremental inserts left it.
    /// </remarks>
    [LibraryImport(Lib, EntryPoint = "b3World_RebuildStaticTree")]
    internal static partial void World_RebuildStaticTree(B3WorldId worldId);

    // --- Tier 3: bodies -----------------------------------------------------

    [LibraryImport(Lib, EntryPoint = "b3DefaultBodyDef")]
    internal static partial B3BodyDef DefaultBodyDef();

    [LibraryImport(Lib, EntryPoint = "b3CreateBody")]
    internal static partial B3BodyId CreateBody(B3WorldId worldId, in B3BodyDef def);

    [LibraryImport(Lib, EntryPoint = "b3DestroyBody")]
    internal static partial void DestroyBody(B3BodyId bodyId);

    [LibraryImport(Lib, EntryPoint = "b3Body_IsValid")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool Body_IsValid(B3BodyId id);

    [LibraryImport(Lib, EntryPoint = "b3Body_GetType")]
    internal static partial B3BodyType Body_GetType(B3BodyId bodyId);

    [LibraryImport(Lib, EntryPoint = "b3Body_SetType")]
    internal static partial void Body_SetType(B3BodyId bodyId, B3BodyType type);

    [LibraryImport(Lib, EntryPoint = "b3Body_SetUserData")]
    internal static partial void Body_SetUserData(B3BodyId bodyId, nint userData);

    [LibraryImport(Lib, EntryPoint = "b3Body_GetUserData")]
    internal static partial nint Body_GetUserData(B3BodyId bodyId);

    [LibraryImport(Lib, EntryPoint = "b3Body_GetTransform")]
    internal static partial B3WorldTransform Body_GetTransform(B3BodyId bodyId);

    [LibraryImport(Lib, EntryPoint = "b3Body_SetTransform")]
    internal static partial void Body_SetTransform(B3BodyId bodyId, B3Pos position, B3Quat rotation);

    /// <summary>
    /// Drives a kinematic body towards a pose over one step, velocity-based.
    /// </summary>
    /// <remarks>
    /// Target, not teleport: the velocity it implies is what makes riders and
    /// friction behave. The achieved pose is documented as close but not exact,
    /// which is why move events for kinematic bodies are discarded rather than
    /// echoed back — feeding solver drift into an authored value would fight
    /// whatever is posing the body.
    /// </remarks>
    [LibraryImport(Lib, EntryPoint = "b3Body_SetTargetTransform")]
    internal static partial void Body_SetTargetTransform(
        B3BodyId bodyId, B3WorldTransform target, float timeStep,
        [MarshalAs(UnmanagedType.U1)] bool wake);

    [LibraryImport(Lib, EntryPoint = "b3Body_GetShapeCount")]
    internal static partial int Body_GetShapeCount(B3BodyId bodyId);

    [LibraryImport(Lib, EntryPoint = "b3Body_ApplyMassFromShapes")]
    internal static partial void Body_ApplyMassFromShapes(B3BodyId bodyId);

    [LibraryImport(Lib, EntryPoint = "b3Body_Enable")]
    internal static partial void Body_Enable(B3BodyId bodyId);

    [LibraryImport(Lib, EntryPoint = "b3Body_Disable")]
    internal static partial void Body_Disable(B3BodyId bodyId);

    [LibraryImport(Lib, EntryPoint = "b3Body_IsEnabled")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool Body_IsEnabled(B3BodyId bodyId);

    // --- Tier 4: hulls and shapes -------------------------------------------

    /// <summary>
    /// Builds a convex hull from a POINT CLOUD. Returns null on failure, which
    /// is a normal outcome — over a limit, or degenerate input.
    /// </summary>
    /// <remarks>
    /// <b>Points, not planes.</b> There is no half-space or face-polygon hull
    /// builder anywhere in the API, so a brush's planes are useless here and its
    /// face-polygon vertices are the input.
    /// <para>
    /// The return is <c>nint</c> deliberately: the hull is a variable-length
    /// struct with data hanging off the end, documented as not directly
    /// copyable, so it must never become a C# value type in a signature.
    /// </para>
    /// </remarks>
    [LibraryImport(Lib, EntryPoint = "b3CreateHull")]
    internal static unsafe partial nint CreateHull(B3Vec3* points, int pointCount, int maxVertexCount);

    /// <summary>
    /// Frees a hull. <b>Null is not tolerated</b> — the implementation
    /// dereferences its argument with no check, so a null here is an access
    /// violation rather than a no-op.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "b3DestroyHull")]
    internal static partial void DestroyHull(nint hull);

    [LibraryImport(Lib, EntryPoint = "b3DefaultShapeDef")]
    internal static partial B3ShapeDef DefaultShapeDef();

    [LibraryImport(Lib, EntryPoint = "b3DefaultFilter")]
    internal static partial B3Filter DefaultFilter();

    [LibraryImport(Lib, EntryPoint = "b3DefaultSurfaceMaterial")]
    internal static partial B3SurfaceMaterial DefaultSurfaceMaterial();

    /// <summary>
    /// Attaches a hull to a body. <b>The hull pointer is dereferenced before any
    /// null check</b>, so passing null is an access violation — and
    /// <see cref="CreateHull"/> returns null as a normal outcome, which makes
    /// checking it mandatory rather than defensive.
    /// </summary>
    [LibraryImport(Lib, EntryPoint = "b3CreateHullShape")]
    internal static partial B3ShapeId CreateHullShape(B3BodyId bodyId, in B3ShapeDef def, nint hull);

    /// <summary>
    /// Attaches a hull to a body under a transform and scale — how one brush's
    /// brush-local hull is placed into its chunk body's cell-local frame.
    /// </summary>
    /// <inheritdoc cref="CreateHullShape"/>
    [LibraryImport(Lib, EntryPoint = "b3CreateTransformedHullShape")]
    internal static partial B3ShapeId CreateTransformedHullShape(
        B3BodyId bodyId, in B3ShapeDef def, nint hull, B3Transform transform, B3Vec3 scale);

    [LibraryImport(Lib, EntryPoint = "b3DestroyShape")]
    internal static partial void DestroyShape(
        B3ShapeId shapeId, [MarshalAs(UnmanagedType.U1)] bool updateBodyMass);

    [LibraryImport(Lib, EntryPoint = "b3Shape_IsValid")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool Shape_IsValid(B3ShapeId id);

    [LibraryImport(Lib, EntryPoint = "b3Shape_SetUserData")]
    internal static partial void Shape_SetUserData(B3ShapeId shapeId, nint userData);

    [LibraryImport(Lib, EntryPoint = "b3Shape_GetUserData")]
    internal static partial nint Shape_GetUserData(B3ShapeId shapeId);

    [LibraryImport(Lib, EntryPoint = "b3Shape_GetBody")]
    internal static partial B3BodyId Shape_GetBody(B3ShapeId shapeId);

    [LibraryImport(Lib, EntryPoint = "b3ComputeHullAABB")]
    internal static partial B3Aabb ComputeHullAABB(nint hull, B3Transform transform);
}
