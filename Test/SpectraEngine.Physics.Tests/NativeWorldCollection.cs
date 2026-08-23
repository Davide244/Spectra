using Xunit;

namespace SpectraEngine.Physics.Tests;

/// <summary>
/// Serialises every test class that creates a Box3D world.
/// </summary>
/// <remarks>
/// <para>
/// <b>The world count is process-global native state, and two classes were
/// racing on it.</b> <c>b3GetWorldCount</c> counts the worlds alive in the
/// loaded library, not the ones a given test made, and both
/// <see cref="Box3DWorldTests"/> and <see cref="Box3DScenePhysicsTests"/> assert
/// a delta across it: read the count, build a world, tear it down, assert the
/// count came back. xUnit runs test classes in parallel, so a world created or
/// destroyed by the other class in between moves the number under the
/// assertion.
/// </para>
/// <para>
/// The symptom was a single failure roughly once in forty runs, never in a row,
/// which is why it went unexplained for so long. Captured at last as
/// <c>A_world_comes_up_steps_and_goes_down</c> expecting 1 and reading 0: the
/// baseline had been read while another class still had a world alive, and that
/// world was gone by the time the assertion ran.
/// </para>
/// <para>
/// <b>Serialising is the fix rather than relaxing the assertion.</b> The
/// assertion is the point: it is what catches a leaked native world, which is
/// the bug this binding most needs guarded. <see cref="BrushHullTests"/> joins
/// the collection because it also builds worlds, so it perturbs the count even
/// though it never reads it. The whole suite runs in about a tenth of a second,
/// so the lost parallelism costs nothing measurable.
/// </para>
/// </remarks>
[CollectionDefinition(Name)]
public sealed class NativeWorldCollection
{
    /// <summary>Name to put on each participating class's <c>[Collection]</c>.</summary>
    public const string Name = "Box3D native world";
}
