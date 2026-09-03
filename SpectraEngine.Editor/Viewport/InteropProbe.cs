using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// Reports what this machine's compositor can actually accept from the engine:
/// which adapter it runs on, which shared-texture handle kinds it imports, and
/// how each of those can be synchronised.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is step zero of replacing the viewport's native child window, and it
/// blocks the rest.</b> The viewport is a real Win32 child today, which
/// composites above everything Avalonia draws and is the single fact behind
/// every layout limitation the shell has: no overlays over the 3D view, no
/// split views, no dockable viewport, no drag-and-drop into the scene. The
/// replacement hands the compositor a texture the engine rendered - and whether
/// that is possible AT ALL is a per-driver fact.
/// </para>
/// <para>
/// <b>It is a probe rather than an assumption because Avalonia's Windows
/// interop is ANGLE - GL ES over D3D11 - and whether it accepts a handle
/// created by a D3D12 device is not something to guess at.</b> Three routes
/// follow from the answer: import the D3D12 handle directly (which also means a
/// second synchronisation path, since D3D12 has no keyed mutex); bridge through
/// a D3D11On12 device, paying one copy per frame to keep exactly one
/// synchronisation implementation in the codebase; or refuse and keep the
/// native child, with the reason logged.
/// </para>
/// <para>
/// <b>A capability flag is not proof, so the probe imports real textures.</b>
/// The capability query answers what a compositor advertises, and a machine can
/// advertise a handle kind it cannot actually synchronise. So after the report
/// it creates four textures and hands each one over for real: D3D11 with an NT
/// handle, D3D11 with a global shared handle, D3D12 with an NT handle, and
/// D3D11On12 with an NT handle. Each route catches its own failures and the
/// next one still runs, because the machine's behaviour is the unknown being
/// measured and a probe that stops at the first refusal measures one thing
/// instead of four.
/// </para>
/// <para>
/// <b>It reports rather than decides.</b> The answer belongs in a commit
/// message and a roadmap entry, taken on real hardware - ideally NVIDIA, AMD,
/// Intel, a hybrid laptop and a remote-desktop session, because those are the
/// five configurations where the answer plausibly differs.
/// </para>
/// </remarks>
internal static class InteropProbe
{
    /// <summary>The switch that runs this instead of opening the editor.</summary>
    public const string Switch = "--interop-probe";

    /// <summary>Whether the command line asked for the probe.</summary>
    public static bool Requested(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Runs the probe against <paramref name="window"/>'s compositor and logs
    /// the result.
    /// </summary>
    /// <remarks>
    /// <b>It needs a real window.</b> The compositor is created by the platform
    /// when a top level exists, and the GPU interop is negotiated with the
    /// render backend that window is attached to - so there is no headless form
    /// of this question.
    /// </remarks>
    public static async Task RunAsync(Window window, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            Compositor? compositor = ElementComposition.GetElementVisual(window)?.Compositor;

            if (compositor is null)
            {
                logger.LogWarning(
                    "Interop probe: the window has no compositor. The composited viewport is not " +
                    "available on this platform and the native child is the only path.");
                return;
            }

            ICompositionGpuInterop? interop = await compositor.TryGetCompositionGpuInterop();

            if (interop is null)
            {
                logger.LogWarning(
                    "Interop probe: this compositor exposes no GPU interop. The composited viewport " +
                    "cannot be built here; the native child stays.");
                return;
            }

            logger.LogInformation("Interop probe:\n{Report}", Describe(interop));

            // After the report, because the routes only make sense against the
            // list of kinds this compositor claims to take.
            logger.LogInformation(
                "Interop probe routes:\n{Report}", await RunRoutesAsync(compositor, interop, logger));
        }
        catch (Exception ex)
        {
            // A probe that takes the shell down with it would be worse than no
            // probe: this runs on a machine whose driver is exactly the unknown
            // being measured.
            logger.LogError(ex, "Interop probe: the query itself failed");
        }
    }

    private static string Describe(ICompositionGpuInterop interop)
    {
        var sb = new StringBuilder();

        sb.Append("  adapter LUID  ").Append(Format(interop.DeviceLuid)).Append('\n');
        sb.Append("  adapter UUID  ").Append(Format(interop.DeviceUuid)).Append('\n');

        IReadOnlyList<string> kinds = interop.SupportedImageHandleTypes;

        if (kinds.Count == 0)
        {
            sb.Append("  image handles (none) - nothing can be imported, so the composited\n");
            sb.Append("                 viewport is not available on this machine.\n");
            return sb.ToString();
        }

        sb.Append("  image handles ").Append(kinds.Count).Append('\n');

        foreach (string kind in kinds)
        {
            sb.Append("    ").Append(kind.PadRight(38));

            try
            {
                // The synchronisation capabilities are what decide HOW a frame
                // is handed over, and they are per handle kind rather than per
                // device: a machine can accept a handle it cannot synchronise
                // with a keyed mutex, which is exactly the case that decides
                // between the direct route and the D3D11On12 bridge.
                sb.Append(interop.GetSynchronizationCapabilities(kind));
            }
            catch (Exception ex)
            {
                sb.Append("query failed: ").Append(ex.GetType().Name);
            }

            sb.Append('\n');
        }

        // The one sentence a reader actually needs, rather than leaving them to
        // work it out from the enum names.
        sb.Append("  verdict       ").Append(Verdict(kinds)).Append('\n');
        return sb.ToString();
    }

    private static string Verdict(IReadOnlyList<string> kinds)
    {
        bool nt = kinds.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle);
        bool global = kinds.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle);

        return (nt, global) switch
        {
            (true, _) =>
                "D3D11 NT handles import. The D3D11 backend can hand its resolve target straight " +
                "over; whether a D3D12-created handle is accepted is the remaining question, and " +
                "the route measurements below are what answer it.",

            (false, true) =>
                "Only the legacy global shared handle imports. Workable for D3D11, and it carries " +
                "no keyed mutex, so the hand-over needs a fence or a flush per frame.",

            _ =>
                "No D3D11 handle kind is accepted. Either this compositor is not on D3D at all " +
                "(a Vulkan or software backend), or the composited viewport wants a different " +
                "image type entirely - read the list above rather than assuming.",
        };
    }

    private static string Format(byte[]? bytes) =>
        bytes is null or { Length: 0 }
            ? "(none)"
            : string.Concat(bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    // --- the measured routes -------------------------------------------------

    private const string Route1 = "1  D3D11 texture, NT handle";
    private const string Route2 = "2  D3D11 texture, global shared handle";
    private const string Route3 = "3  D3D12 resource, NT handle";
    private const string Route4 = "4  D3D11On12 texture, NT handle";

    /// <summary>
    /// A single hand-over attempt: what was tried, how far it got, and why it
    /// stopped.
    /// </summary>
    /// <remarks>
    /// <b><paramref name="KeyedMutex"/> is a fact about the texture this side
    /// created</b>, not about the compositor, and it is reported because it is
    /// what makes a route's failure legible: a D3D12 resource carries no keyed
    /// mutex and cannot be made to, so an E_NOINTERFACE out of the import is a
    /// different thing from a driver refusing the handle.
    /// </remarks>
    private sealed record RouteResult(
        string Route,
        bool Imported,
        bool Updated,
        string Synchronization,
        bool KeyedMutex,
        string? Failure);

    /// <summary>
    /// Nothing here may take the shell down and nothing here may hang it: the
    /// driver under test is the unknown.
    /// </summary>
    private static async Task<string> RunRoutesAsync(
        Compositor compositor, ICompositionGpuInterop interop, ILogger logger)
    {
        InteropProbeTextures textures;
        try
        {
            textures = new InteropProbeTextures(interop.DeviceLuid, logger);
        }
        catch (Exception ex)
        {
            return "  no route could be measured: the probe could not open a graphics device.\n" +
                   $"  {Explain(ex)}\n";
        }

        var results = new List<RouteResult>(4);
        try
        {
            results.Add(await RunRouteAsync(
                compositor, interop, Route1, () => textures.CreateD3D11NtHandleTexture()));
            results.Add(await RunRouteAsync(compositor, interop, Route2, textures.CreateD3D11GlobalHandleTexture));
            results.Add(await RunRouteAsync(compositor, interop, Route3, textures.CreateD3D12Texture));
            results.Add(await RunRouteAsync(compositor, interop, Route4, textures.CreateD3D11On12Texture));

            return DescribeRoutes(textures.AdapterName, results);
        }
        finally
        {
            textures.Dispose();
        }
    }

    private static async Task<RouteResult> RunRouteAsync(
        Compositor compositor,
        ICompositionGpuInterop interop,
        string route,
        Func<SharedProbeTexture> create)
    {
        SharedProbeTexture? texture = null;
        ICompositionImportedGpuImage? image = null;
        CompositionDrawingSurface? surface = null;
        bool imported = false;
        bool updated = false;
        bool keyedMutex = false;
        string sync = "(not reached)";

        try
        {
            texture = create();
            keyedMutex = texture.KeyedMutex;
            sync = SynchronizationOf(interop, texture.HandleKind);

            image = interop.ImportImage(
                new PlatformHandle(texture.Handle, texture.HandleKind),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = texture.Width,
                    Height = texture.Height,
                    Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,

                    // A D3D render target's first row is its top one, unlike a
                    // GL framebuffer's; getting this wrong flips the picture
                    // rather than failing the import.
                    TopLeftOrigin = true,
                });

            await Bounded(image.ImportCompleted, "the import");
            imported = true;

            surface = compositor.CreateDrawingSurface();
            await Bounded(
                surface.UpdateWithKeyedMutexAsync(image, texture.AcquireKey, texture.ReleaseKey),
                "the keyed-mutex hand-over");
            updated = true;

            return new RouteResult(route, imported, updated, sync, keyedMutex, null);
        }
        catch (Exception ex)
        {
            return new RouteResult(route, imported, updated, sync, keyedMutex, Explain(ex));
        }
        finally
        {
            // The image goes first: it is the compositor's view of the texture,
            // and destroying the texture underneath it is exactly the crash
            // this whole path exists to avoid in production.
            if (image is not null)
            {
                try { await image.DisposeAsync(); }
                catch (Exception) { /* a lost device cannot be released cleanly either */ }
            }

            surface?.Dispose();
            texture?.Dispose();
        }
    }

    /// <summary>
    /// Awaits <paramref name="task"/> with a ceiling, because a keyed-mutex
    /// acquire against a resource that carries no keyed mutex has nothing to
    /// time it out.
    /// </summary>
    /// <remarks>
    /// The abandoned task is deliberately left running: the probe closes its
    /// window immediately afterwards, and cancelling a compositor hand-over
    /// mid-flight is not something the API offers.
    /// </remarks>
    private static async Task Bounded(Task task, string what)
    {
        Task first = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        if (first != task)
            throw new TimeoutException($"{what} did not complete within 10 s");

        await task;
    }

    private static string SynchronizationOf(ICompositionGpuInterop interop, string kind)
    {
        try
        {
            return interop.GetSynchronizationCapabilities(kind).ToString();
        }
        catch (Exception ex)
        {
            return $"query failed: {ex.GetType().Name}";
        }
    }

    private static string DescribeRoutes(string adapter, IReadOnlyList<RouteResult> results)
    {
        var sb = new StringBuilder();
        sb.Append("  adapter       ").Append(adapter).Append('\n');

        foreach (RouteResult r in results)
        {
            sb.Append("  ").Append(r.Route.PadRight(40));
            sb.Append(r.Imported ? "import ok   " : "import NO   ");
            sb.Append(r.Updated ? "update ok   " : "update NO   ");
            sb.Append("compositor takes ").Append(r.Synchronization);
            sb.Append(r.KeyedMutex ? ", texture has one" : ", texture has none").Append('\n');

            if (r.Failure is { } failure)
                sb.Append("      ").Append(failure).Append('\n');
        }

        sb.Append("  verdict       ").Append(RouteVerdict(results)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// The one sentence the milestone is waiting on: which D3D12 route works.
    /// </summary>
    private static string RouteVerdict(IReadOnlyList<RouteResult> results)
    {
        bool direct = Succeeded(results, Route3);
        bool bridged = Succeeded(results, Route4);
        bool d3d11 = Succeeded(results, Route1) || Succeeded(results, Route2);

        if (direct)
        {
            return "route 3 is viable: a D3D12-created handle imports AND hands over here, so the " +
                   "composited viewport can take the D3D12 backend's texture directly, with no bridge.";
        }

        if (bridged)
        {
            return "route 4 is the viable D3D12 route: the D3D12 handle itself does not complete a " +
                   "hand-over, but a D3D11On12 device over the same D3D12 device does, so the " +
                   "composited viewport costs one copy per frame on that backend.";
        }

        if (d3d11)
        {
            return "no D3D12 route works here: only a native D3D11 device's texture completes a " +
                   "hand-over, so a composited viewport would be D3D11-only and D3D12 keeps the " +
                   "native child.";
        }

        return "no route completed a hand-over. Nothing on this machine can be composited yet; the " +
               "native child stays, and the failure text above is the reason rather than a guess.";
    }

    private static bool Succeeded(IReadOnlyList<RouteResult> results, string route) =>
        results.Any(r => r.Route == route && r.Updated);

    // An HRESULT is the whole content of most failures here, and a bare
    // "Exception has been thrown" says nothing about a driver.
    private static string Explain(Exception ex)
    {
        Exception real = ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
            ? aggregate.InnerExceptions[0]
            : ex;

        string code = real.HResult == 0
            ? string.Empty
            : $" (hr=0x{real.HResult:X8})";

        return $"{real.GetType().Name}: {real.Message}{code}";
    }
}
