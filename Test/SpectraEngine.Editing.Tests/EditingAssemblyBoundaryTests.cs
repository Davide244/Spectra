using SpectraEngine.Editing.Input;
using System;
using System.Linq;
using System.Reflection;

namespace SpectraEngine.Editing.Tests;

/// <summary>
/// Guards the seam the whole editing layer is built on: SpectraEngine.Editing
/// must not depend on the windowing/input backend. Silk.NET flows into the
/// project transitively through SpectraEngine.Core, so nothing stops a careless
/// <c>using Silk.NET.Input;</c> at compile time — this test is what does. If it
/// fails, some editing type started naming a backend type and re-hosting the
/// editor (in Uno or anything else) just stopped being a swap.
/// </summary>
public sealed class EditingAssemblyBoundaryTests
{
    [Fact]
    public void The_editing_assembly_references_no_backend_assembly()
    {
        Assembly editing = typeof(EditorInputFrame).Assembly;

        // GetReferencedAssemblies lists what the compiler actually emitted a
        // reference for — i.e. what the assembly's metadata genuinely uses,
        // not what was merely available to it.
        string[] offenders = editing.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name =>
                name.StartsWith("Silk.NET", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Silk.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        offenders.ShouldBeEmpty(
            $"SpectraEngine.Editing must stay backend-free but references: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void The_editing_assembly_still_references_the_engine_core()
    {
        // Sanity check on the assertion above: if the reference list were empty
        // for some unrelated reason, the backend test would pass vacuously.
        Assembly editing = typeof(EditorInputFrame).Assembly;

        editing.GetReferencedAssemblies()
            .Any(reference => reference.Name == "SpectraEngine.Core")
            .ShouldBeTrue();
    }
}
