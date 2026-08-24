using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;
using System;
using DxgiApi = Silk.NET.DXGI.DXGI;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Picking which graphics adapter a D3D backend runs on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared because both D3D backends need exactly the same decision.</b>
/// D3D11 takes an adapter and <c>DriverType.Unknown</c>; D3D12 takes the same
/// adapter as an <c>IUnknown</c>. Neither has anything else to say about it.
/// </para>
/// <para>
/// <b>The reason this exists is measurement, not preference.</b> A desktop with
/// a discrete card and an integrated one is the cheapest low-power test rig
/// available, and until something can be pointed at the integrated part, "how
/// does this run on weak hardware" can only be guessed at.
/// </para>
/// </remarks>
internal static unsafe class DxgiAdapters
{
    /// <summary>
    /// Finds the hardware adapter whose description contains
    /// <paramref name="wanted"/>, or returns null for the system default.
    /// </summary>
    /// <remarks>
    /// Software adapters are skipped: WARP describes itself as a hardware-like
    /// device and would otherwise match a search for almost anything, then run
    /// at a hundredth of the speed and be blamed on the engine.
    /// </remarks>
    internal static ComPtr<IDXGIAdapter> Find(DxgiApi dxgi, string? wanted, ILogger logger, out string chosenName)
    {
        chosenName = "system default";
        if (string.IsNullOrWhiteSpace(wanted)) return default;

        IDXGIFactory1* factory = null;
        Guid factoryGuid = IDXGIFactory1.Guid;
        if (dxgi.CreateDXGIFactory1(&factoryGuid, (void**)&factory) < 0)
        {
            logger.LogWarning("Could not enumerate adapters; using the system default.");
            return default;
        }

        var factoryPtr = ComOwnership.Own(factory);
        try
        {
            for (uint index = 0; ; index++)
            {
                IDXGIAdapter1* adapter = null;
                if (((IDXGIFactory1*)factoryPtr.Handle)->EnumAdapters1(index, &adapter) < 0)
                    break;

                var owned = ComOwnership.Own(adapter);
                AdapterDesc1 desc = default;
                ((IDXGIAdapter1*)owned.Handle)->GetDesc1(&desc);

                string name = DescriptionOf(ref desc);
                bool software = (desc.Flags & (uint)AdapterFlag.Software) != 0;

                if (!software && name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    chosenName = name;
                    logger.LogInformation("Graphics adapter: {Adapter} (matched '{Wanted}')", name, wanted);

                    // Handed to the caller as the base interface both backends
                    // take. QueryInterface rather than a cast: the caller owns
                    // what it gets and releases it.
                    IDXGIAdapter* asBase = null;
                    Guid baseGuid = IDXGIAdapter.Guid;
                    if (((IDXGIAdapter1*)owned.Handle)->QueryInterface(&baseGuid, (void**)&asBase) >= 0)
                    {
                        ComOwnership.Release(ref owned);
                        return ComOwnership.Own(asBase);
                    }
                }

                logger.LogDebug("Graphics adapter {Index}: {Adapter}{Software}", index, name, software ? " (software)" : "");
                ComOwnership.Release(ref owned);
            }

            logger.LogWarning(
                "No graphics adapter matched '{Wanted}'; using the system default.", wanted);
            return default;
        }
        finally
        {
            ComOwnership.Release(ref factoryPtr);
        }
    }

    // The description is a fixed 128-char UTF-16 buffer inside the struct.
    private static string DescriptionOf(ref AdapterDesc1 desc)
    {
        fixed (char* p = desc.Description)
        {
            var span = new ReadOnlySpan<char>(p, 128);
            int end = span.IndexOf('\0');
            return new string(end < 0 ? span : span[..end]);
        }
    }
}
