using System;

namespace SpectraEngine.Core.Physics;

/// <summary>
/// The engine's view of a physics backend: the calls the engine loop makes,
/// in the order it makes them, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The interface lives in Core; every implementation does not.</b>
/// <see cref="Scene.Scene"/> itself has to drive the static-world sync from its
/// compile-harvest slot, so the seam cannot live above Core — but a real
/// backend carries a native dependency, and the compiler tests, the BSP tests
/// and a shader-only tool build must not need a physics DLL to resolve. Same
/// shape as <c>ISceneEditor</c> / <c>SceneManager.EditorFactory</c>, for a
/// different reason: gizmos must never ship in a game binary, whereas physics
/// must — this split is about native dependencies and test isolation, not about
/// what a shipped game contains.
/// </para>
/// <para>
/// <b>A host that wires nothing gets <see cref="NullScenePhysics"/>, and that
/// is a supported configuration rather than a degraded one.</b> A scene that
/// renders and does not simulate is exactly what Edit mode is, and it is what
/// every headless test wants.
/// </para>
/// <para>
/// <b>Render thread only</b>, like the scene it simulates. The backend may use
/// worker threads internally; the seam is single-threaded.
/// </para>
/// </remarks>
public interface IScenePhysics : IDisposable
{
    /// <summary>Whether this implementation actually simulates anything.</summary>
    /// <remarks>
    /// False for <see cref="NullScenePhysics"/>. Exists so the periodic stats
    /// line and the editor can say "no physics backend" rather than reporting a
    /// convincing row of zeroes, which reads as "physics is on and nothing is
    /// moving" — a much harder thing to debug than an honest absence.
    /// </remarks>
    bool IsSimulating { get; }

    /// <summary>Bodies currently live in the physics world.</summary>
    int BodyCount { get; }

    /// <summary>Static collision shapes built from the compiled world.</summary>
    int StaticShapeCount { get; }

    /// <summary>
    /// Brings static collision in line with the compiled static world.
    /// </summary>
    /// <remarks>
    /// <b>Called once per frame from the compile-harvest slot, deliberately
    /// NOT from inside the tick loop.</b> Collision geometry has to change in
    /// the same instant the render geometry does or the two disagree for a
    /// frame — which is a player walking into an invisible wall. And running it
    /// per tick would mean up to <see cref="PhysicsDefaults.MaxTicksPerFrame"/>
    /// body-and-shape churn batches per frame of which at most one can do work.
    /// </remarks>
    void SyncStaticWorld(Scene.Scene scene);

    /// <summary>
    /// Pushes this tick's target transforms for kinematic bodies — platforms,
    /// scripted part brushes, the character presence body.
    /// </summary>
    /// <remarks>
    /// Before <see cref="Step"/>, always: a door decides where it is this tick
    /// before the tick resolves against it. Target transforms rather than
    /// teleports, because the velocity they imply is what makes riders and
    /// friction behave.
    /// </remarks>
    void PushKinematicTargets(float fixedDt);

    /// <summary>Advances the simulation by exactly one fixed tick.</summary>
    /// <remarks>
    /// <b><paramref name="fixedDt"/> is fixed, never a frame delta.</b> A
    /// variable step makes the simulation a function of frame rate, which
    /// breaks determinism, replay and any future server reconciliation. The
    /// engine's accumulator is what guarantees this.
    /// </remarks>
    void Step(float fixedDt);

    /// <summary>
    /// Drains the events the last <see cref="Step"/> produced: body moves,
    /// contacts, sensor overlaps.
    /// </summary>
    /// <remarks>
    /// <b>Inside the tick loop, immediately after the step it belongs to.</b>
    /// Backend event buffers are overwritten by the next step, so draining
    /// outside the loop silently discards every tick's events but the last on
    /// any catch-up frame — a trigger that fires perfectly at 144 fps and drops
    /// volleys during a hitch. That is the kind of bug that gets reported as
    /// "triggers are unreliable" and never reproduces on the reporter's machine.
    /// </remarks>
    void DrainEvents();

    /// <summary>
    /// Publishes interpolated render poses for the frame, <paramref name="alpha"/>
    /// of the way from the previous tick to the current one.
    /// </summary>
    /// <remarks>
    /// A render-only overlay: it must never write back through a node's
    /// transform setters. Doing so would re-dirty the spatial index every frame
    /// with a value that is not an edit, and a script reading a node's position
    /// inside a tick must see the simulated value, not a display lerp.
    /// </remarks>
    void PublishRenderPoses(float alpha);
}
