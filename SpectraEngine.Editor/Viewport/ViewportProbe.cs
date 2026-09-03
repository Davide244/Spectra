using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Diagnostics;
using SpectraEngine.Core.Graphics;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace SpectraEngine.Editor.Viewport;

/// <summary>
/// Asks this machine what it can actually do with a composited viewport, and
/// rehearses the one thing that matters before an engine is running against it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dry run is the point.</b> The capability query says what a compositor
/// advertises, and a machine can advertise a handle kind it will then refuse to
/// import - which, discovered after <see cref="EditorSession"/> has started a
/// render thread, a swap-chain-free device and a scene, is an editor with a
/// blank pane and a session that has to be torn down out of order. So a real
/// keyed-mutex shared texture is created and handed over first, at one texel,
/// which costs a device open and nothing else.
/// </para>
/// <para>
/// <b>Everything here is best-effort and reports rather than throws.</b> The
/// driver underneath is exactly the unknown being measured; a probe that took
/// the shell down with it would be worse than no probe, and every failure it
/// can suffer has the same answer - the native child, with the reason named.
/// </para>
/// <para>
/// <b>Threading:</b> UI thread. The compositor's interop negotiation and its
/// import verify that themselves.
/// </para>
/// </remarks>
internal static class ViewportProbe
{
    /// <summary>
    /// One texel. The question is whether the compositor accepts the import at
    /// all, and the answer does not depend on how big the picture is.
    /// </summary>
    private const int DryRunSize = 1;

    /// <summary>
    /// How long the rehearsal may take before it is called a refusal.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a tuning: a hand-over against a resource that
    /// cannot be synchronised has nothing to time it out, and a launch that
    /// hangs before a window has changed is indistinguishable from a hung
    /// editor.
    /// </remarks>
    private static readonly TimeSpan DryRunDeadline = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Measures the machine behind <paramref name="anchor"/>'s compositor.
    /// </summary>
    /// <param name="anchor">Any visual already attached to the window's tree.</param>
    /// <param name="backend">The backend this session will run on, for the colour verdict.</param>
    /// <param name="logger">Owned by the caller.</param>
    internal static async Task<ViewportCapabilities> MeasureAsync(
        Visual anchor, GraphicsBackend backend, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(logger);

        ViewportCapabilities capabilities = ViewportCapabilities.NotMeasured;

        try
        {
            if (ElementComposition.GetElementVisual(anchor)?.Compositor is not { } compositor)
                return capabilities;

            capabilities = capabilities with { HasCompositor = true };

            ICompositionGpuInterop? interop = await compositor.TryGetCompositionGpuInterop();
            if (interop is null)
                return capabilities;

            string kind = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle;
            capabilities = capabilities with
            {
                HasGpuInterop = true,
                AdapterLuid = FormatLuid(interop.DeviceLuid),
                SupportsD3D11NtHandle = interop.SupportedImageHandleTypes.Contains(kind),
            };

            if (!capabilities.SupportsD3D11NtHandle)
                return capabilities;

            // Per handle kind, never per device: a machine can accept a handle
            // it cannot synchronise with a keyed mutex, and a keyed mutex is the
            // only hand-over the engine implements.
            capabilities = capabilities with
            {
                SupportsKeyedMutex = HasKeyedMutex(interop, kind, logger),
            };

            if (!capabilities.SupportsKeyedMutex)
                return capabilities;

            return await DryRunAsync(interop, capabilities, backend, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Measuring the composited viewport's machine failed");
            return capabilities;
        }
    }

    private static bool HasKeyedMutex(ICompositionGpuInterop interop, string kind, ILogger logger)
    {
        try
        {
            return interop.GetSynchronizationCapabilities(kind)
                .HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "The compositor could not report its synchronisation capabilities for {Kind}", kind);
            return false;
        }
    }

    /// <summary>
    /// Creates a real shared texture on the compositor's own adapter and offers
    /// it, so a refusal happens here rather than under a running engine.
    /// </summary>
    private static async Task<ViewportCapabilities> DryRunAsync(
        ICompositionGpuInterop interop,
        ViewportCapabilities capabilities,
        GraphicsBackend backend,
        ILogger logger)
    {
        InteropProbeTextures? textures = null;
        SharedProbeTexture? texture = null;
        ICompositionImportedGpuImage? image = null;

        try
        {
            textures = new InteropProbeTextures(interop.DeviceLuid, logger);
            capabilities = capabilities with
            {
                AdapterName = textures.AdapterName,
                DriverVersion = textures.DriverVersion,

                // Not about the compositor at all: whether the last
                // --viewport-compare on this backend agreed that the shared
                // route's colours are identical to an ordinary target's. Nothing
                // else in the shell can see a double sRGB encode. The stamp
                // cannot be adapter-scoped because the producer never names the
                // adapter it opened - see ViewportCompareStamp.
                CompareGreen = ViewportCompareStamp.IsGreenFor(ViewportCompareStamp.Load(), backend),
            };

            texture = textures.CreateD3D11NtHandleTexture(DryRunSize);
            image = interop.ImportImage(
                new PlatformHandle(texture.Handle, texture.HandleKind),
                new PlatformGraphicsExternalImageProperties
                {
                    Width = texture.Width,
                    Height = texture.Height,
                    Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                    TopLeftOrigin = true,
                });

            await Bounded(image.ImportCompleted);

            // The import is the whole question. A hand-over as well would need
            // the compositor's turn on a mutex this side has already released
            // into, which is a second protocol to get right for no extra
            // information: an import that completes is the thing that was
            // refused when it was refused.
            logger.LogInformation(
                "Composited viewport rehearsal: a {Size}x{Size} shared texture imported on {Adapter} " +
                "(driver {Driver}).",
                DryRunSize, DryRunSize, textures.AdapterName,
                textures.DriverVersion.Length > 0 ? textures.DriverVersion : "unknown");

            return capabilities with { DryRunImported = true };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The composited viewport's rehearsal import was refused");
            return capabilities;
        }
        finally
        {
            // The image goes first: it is the compositor's view of the texture,
            // and destroying the texture underneath it is exactly the crash the
            // retirement handshake exists to avoid in a live session.
            if (image is not null)
            {
                try
                {
                    await image.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Releasing the rehearsal import failed");
                }
            }

            texture?.Dispose();
            textures?.Dispose();
        }
    }

    private static async Task Bounded(Task task)
    {
        Task first = await Task.WhenAny(task, Task.Delay(DryRunDeadline));
        if (first != task)
            throw new TimeoutException($"the rehearsal import did not complete within {DryRunDeadline}");

        await task;
    }

    /// <summary>
    /// The adapter LUID as the settings file spells it, and the empty string
    /// when the compositor reported none.
    /// </summary>
    /// <remarks>
    /// An empty LUID must stay empty rather than becoming a placeholder: it is
    /// compared against a recorded one, and a placeholder that matched itself
    /// would let a history earned on one machine be trusted on another.
    /// </remarks>
    private static string FormatLuid(byte[]? luid) =>
        luid is null or { Length: 0 }
            ? string.Empty
            : string.Concat(luid.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}
