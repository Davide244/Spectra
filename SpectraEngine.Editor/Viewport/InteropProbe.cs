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
                "over; whether a D3D12-created handle is accepted is the remaining question and " +
                "needs the same probe run with a D3D12 texture actually imported.",

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
}
