namespace SpectraEngine.Core.Physics;

/// <summary>
/// The physics backend a host gets when it wires none: every call is a no-op.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a stub or a placeholder — a supported configuration.</b> A scene that
/// renders and does not simulate is exactly what Edit mode is, what every
/// headless test wants, and what a shader-only tool build needs so it does not
/// have to resolve a native physics library to start.
/// </para>
/// <para>
/// <b>It reports <see cref="IsSimulating"/> false rather than pretending.</b>
/// A null backend that answered "0 bodies, 0 shapes" without saying it is null
/// is indistinguishable, in a log, from a real backend with an empty world —
/// and "physics is on and nothing is moving" is a much harder thing to debug
/// than an honest absence.
/// </para>
/// </remarks>
public sealed class NullScenePhysics : IScenePhysics
{
    /// <summary>The shared instance. Stateless, so one is enough.</summary>
    public static NullScenePhysics Instance { get; } = new();

    private NullScenePhysics()
    {
    }

    /// <inheritdoc/>
    public bool IsSimulating => false;

    /// <inheritdoc/>
    public int BodyCount => 0;

    /// <inheritdoc/>
    public int StaticShapeCount => 0;

    /// <inheritdoc/>
    public void SyncStaticWorld(Scene.Scene scene)
    {
    }

    /// <inheritdoc/>
    public void PushKinematicTargets(float fixedDt)
    {
    }

    /// <inheritdoc/>
    public void Step(float fixedDt)
    {
    }

    /// <inheritdoc/>
    public void DrainEvents()
    {
    }

    /// <inheritdoc/>
    public void PublishRenderPoses(float alpha)
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Deliberately does nothing AND is safe to call repeatedly: the shared
        // instance outlives any one scene, so a host tearing a scene down must
        // not be able to poison the next one.
    }
}
