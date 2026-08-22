using System;
using System.Linq;
using System.Reflection;
using SpectraEngine.Physics.Box3D;
using SpectraEngine.Physics.Box3D.Native;

namespace SpectraEngine.Physics.Tests;

/// <summary>
/// Guards the physics binding's boundary: it must not depend on the windowing
/// or input backend.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the editing assembly's boundary test, but for a <b>different
/// reason</b> — worth stating so nobody generalises the wrong rule. Editing is
/// kept backend-free so the viewport can be re-hosted; physics is kept
/// backend-free so it can run <em>headless</em>, on a dedicated server with no
/// window, no input and no graphics device at all.
/// </para>
/// <para>
/// Silk.NET flows in transitively through <c>SpectraEngine.Core</c>, so nothing
/// stops a careless <c>using</c> at compile time. This test is what does.
/// </para>
/// </remarks>
public sealed class PhysicsAssemblyBoundaryTests
{
    [Fact]
    public void The_physics_assembly_references_no_backend_assembly()
    {
        Assembly physics = typeof(BrushHullBuilder).Assembly;

        // GetReferencedAssemblies lists what the compiler actually emitted a
        // reference for — what the metadata genuinely uses, not what happened
        // to be available.
        string[] offenders = physics.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Silk.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        offenders.ShouldBeEmpty(
            "SpectraEngine.Physics.Box3D must stay headless-capable but references: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void The_physics_assembly_still_references_the_engine_core()
    {
        // Sanity check on the assertion above: an empty reference list for some
        // unrelated reason would make the backend test pass vacuously.
        Assembly physics = typeof(BrushHullBuilder).Assembly;

        physics.GetReferencedAssemblies()
            .Any(reference => reference.Name == "SpectraEngine.Core")
            .ShouldBeTrue();
    }

    [Fact]
    public void Runtime_marshalling_is_disabled_for_the_binding()
    {
        // The load-bearing assembly setting: with it, a non-blittable P/Invoke
        // is a COMPILE error rather than a silent reinterpretation at the
        // boundary. Losing it would not break any test here — it would quietly
        // re-enable the marshaller and let a C# bool in a struct widen from one
        // byte to four, shifting every field after it. So it is asserted.
        Assembly physics = typeof(B3Vec3).Assembly;

        physics.GetCustomAttributes()
            .Any(a => a.GetType().Name == "DisableRuntimeMarshallingAttribute")
            .ShouldBeTrue(
                "SpectraEngine.Physics.Box3D must keep [assembly: DisableRuntimeMarshalling] — " +
                "without it, a layout mistake stops being a compile error");
    }
}
