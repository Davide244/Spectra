using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace SpectraEngine.Core;

/// <summary>
/// Registers the Silk.NET windowing and input backends explicitly, so window
/// and input creation never depends on Silk.NET's reflection-based platform
/// discovery.
/// </summary>
/// <remarks>
/// <para>
/// Silk.NET 2.x normally finds its first-party backends lazily inside
/// <see cref="Window.Create"/> / <c>IView.CreateInput</c>: it
/// <c>Assembly.Load</c>s <c>Silk.NET.Windowing.Glfw</c> and
/// <c>Silk.NET.Input.Glfw</c> by name, reads a platform-type attribute off the
/// assembly and <c>Activator.CreateInstance</c>s it. Nothing statically
/// references those assemblies, so a NativeAOT (or trimmed) publish drops them,
/// the discovery finds nothing, and the process dies at window creation with
/// <c>"Couldn't find a suitable window platform. (none registered)"</c>. The
/// publish itself is clean — this only ever shows up at run time, which is what
/// makes it worth pinning down here rather than leaving to chance.
/// </para>
/// <para>
/// The fix has two halves. <c>RegisterPlatform()</c> inserts each platform
/// directly, and the <c>using</c>s above are the static reference that keeps
/// both assemblies in the trimmed image at all. Then
/// <c>ShouldLoadFirstPartyPlatforms(false)</c> retires the reflection path
/// outright, so JIT and AOT runs take the identical code path instead of
/// differing in a way only one of them exercises.
/// </para>
/// <para>
/// Order matters: registration must come first, because disabling discovery
/// only marks the first-party platforms as already-loaded — it never supplies
/// them.
/// </para>
/// <para>
/// No JIT/AOT guard, deliberately. Every call here is idempotent (the windowing
/// registry keys on platform type, the input one short-circuits on an
/// already-registered platform), so this is safe to call repeatedly and from
/// any host — engine, tests, or a future editor shell.
/// </para>
/// <para>
/// Threading: call this from the thread that will create the window (the
/// main/OS-event thread). It only mutates process-global lists, but everything
/// downstream of it is main-thread-only.
/// </para>
/// </remarks>
public static class SilkPlatform
{
    /// <summary>
    /// Registers the GLFW windowing and input platforms and disables Silk.NET's
    /// reflection-based backend discovery. Must run before the first
    /// <see cref="Window.Create"/> or <c>CreateInput</c> call. Safe to call more
    /// than once; every call after the first is a no-op.
    /// </summary>
    public static void EnsureRegistered()
    {
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform();

        Window.ShouldLoadFirstPartyPlatforms(false);
        InputWindowExtensions.ShouldLoadFirstPartyPlatforms(false);
    }
}
