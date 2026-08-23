using Microsoft.Extensions.Logging;
using Silk.NET.Core.Native;
using Silk.NET.DXGI;
using System;
using System.Text;
using DxgiApi = Silk.NET.DXGI.DXGI;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Drains DXGI's own debug message queue into the logger, the way each D3D
/// backend already drains its device's.
/// </summary>
/// <remarks>
/// <b>DXGI's validation is a separate queue from D3D's.</b> Swap-chain
/// failures — every <c>ResizeBuffers</c> and <c>Present</c> rejection — are
/// reported here and <em>nowhere else</em>, so a backend that only drains
/// <c>ID3D12InfoQueue</c> sees a bare <c>DXGI_ERROR_INVALID_CALL</c> with no
/// explanation. That gap is what made the fullscreen resize crash a guessing
/// game; this closes it.
/// <para>
/// Optional throughout: the queue only exists when the factory was created with
/// <c>DXGI_CREATE_FACTORY_DEBUG</c> and the Graphics Tools feature is
/// installed. Without it every method here is a cheap no-op, exactly like the
/// device info queues.
/// </para>
/// </remarks>
internal sealed unsafe class DxgiDebugMessages : IDisposable
{
    /// <summary>DXGI_CREATE_FACTORY_DEBUG — pass to CreateDXGIFactory2 to get a queue at all.</summary>
    internal const uint CreateFactoryDebug = 0x1;

    // DXGI_DEBUG_ALL: the producer GUID that selects every DXGI message
    // source at once (DXGI itself, the D3D11/12 DXGI layers, the app).
    private static readonly Guid DebugAll =
        new(0xe48ae283, 0xda80, 0x490b, 0x87, 0xe6, 0x43, 0xe9, 0xa9, 0xcf, 0xda, 0x08);

    private ComPtr<IDXGIInfoQueue> _queue;

    /// <summary>True when a real queue was acquired and <see cref="Drain"/> has something to do.</summary>
    internal bool IsAvailable => _queue.Handle is not null;

    /// <summary>
    /// Tries to acquire the DXGI debug queue. Returns an instance either way —
    /// an unavailable one simply no-ops — so callers need no null checks.
    /// </summary>
    internal static DxgiDebugMessages Acquire(DxgiApi dxgi)
    {
        var messages = new DxgiDebugMessages();

        IDXGIInfoQueue* queue = null;
        Guid guid = IDXGIInfoQueue.Guid;
        if (dxgi.GetDebugInterface1(0u, &guid, (void**)&queue) >= 0 && queue is not null)
            messages._queue = ComOwnership.Own(queue);

        return messages;
    }

    /// <summary>
    /// Pops every message DXGI has accumulated and logs it under
    /// <paramref name="backend"/>. Render thread, once per frame — same slot as
    /// the device info queue it accompanies.
    /// </summary>
    /// <returns>How many messages were error or corruption severity.</returns>
    internal int Drain(ILogger logger, string backend)
    {
        if (_queue.Handle is null) return 0;

        int errors = 0;

        var queue = (IDXGIInfoQueue*)_queue.Handle;
        ulong count = queue->GetNumStoredMessages(DebugAll);
        for (ulong i = 0; i < count; i++)
        {
            // Two-call pattern: ask for the size, then for the message. The
            // description is a trailing variable-length blob, which is why the
            // struct is read out of a byte buffer rather than declared.
            nuint byteLength = 0;
            if (queue->GetMessageA(DebugAll, i, null, &byteLength) < 0 || byteLength == 0)
                continue;

            byte[] storage = new byte[(int)byteLength];
            fixed (byte* p = storage)
            {
                var message = (InfoQueueMessage*)p;
                if (queue->GetMessageA(DebugAll, i, message, &byteLength) < 0)
                    continue;

                string text = Encoding.ASCII
                    .GetString(message->PDescription, (int)message->DescriptionByteLength)
                    .TrimEnd('\0');

                switch (message->Severity)
                {
                    case InfoQueueMessageSeverity.InfoQueueMessageSeverityCorruption:
                    case InfoQueueMessageSeverity.InfoQueueMessageSeverityError:
                        errors++;
                        logger.LogError("{Backend} DXGI debug layer: {Message}", backend, text);
                        break;
                    case InfoQueueMessageSeverity.InfoQueueMessageSeverityWarning:
                        logger.LogWarning("{Backend} DXGI debug layer: {Message}", backend, text);
                        break;
                    default:
                        logger.LogDebug("{Backend} DXGI debug layer: {Message}", backend, text);
                        break;
                }
            }
        }

        if (count > 0)
            queue->ClearStoredMessages(DebugAll);

        return errors;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Release rather than Dispose: both renderers can reach a second
        // Shutdown, and a disposed ComPtr keeps its handle. See ComOwnership.
        ComOwnership.Release(ref _queue);
    }
}
