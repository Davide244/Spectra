using Microsoft.Extensions.Logging;
using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// Watches SpectraShade source files on disk and, when one changes, recompiles
/// it through <see cref="IShaderCompiler"/> and reapplies it to the
/// <see cref="ShaderProgram"/> that was created from it — keeping the program's
/// object identity stable so every <see cref="Material"/> referencing it picks
/// up the new code automatically.
/// </summary>
/// <remarks>
/// File events arrive on a thread-pool thread; GL calls have to run on the
/// render thread. The watcher therefore only enqueues paths, and <see cref="PumpPendingReloads"/>
/// must be drained once per frame on the renderer's thread.
/// </remarks>
public sealed class ShaderHotReloader : IDisposable
{
    private readonly ILogger _logger;
    private readonly IShaderCompiler _compiler;
    private readonly GraphicsBackend _backend;
    private readonly Dictionary<string, Registration> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _pending = new();
    private bool _disposed;

    public ShaderHotReloader(ILogger logger, IShaderCompiler compiler, GraphicsBackend backend)
    {
        _logger = logger;
        _compiler = compiler;
        _backend = backend;
    }

    /// <summary>Registers <paramref name="program"/> for reloads sourced from <paramref name="absolutePath"/>.</summary>
    public void Register(string absolutePath, ShaderProgram program)
    {
        string normalized = Path.GetFullPath(absolutePath);
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(normalized)!, Path.GetFileName(normalized))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, e) => _pending.Enqueue(Path.GetFullPath(e.FullPath));

        _watchers[normalized] = new Registration(program, watcher);
        _logger.LogInformation("Watching shader source: {Path}", normalized);
    }

    /// <summary>
    /// Drains the pending-reload queue and applies each change. Must be called
    /// on the render thread (touches GL through the shader program).
    /// </summary>
    public void PumpPendingReloads()
    {
        // Coalesce — saves often trigger multiple Changed events for one file.
        HashSet<string>? seen = null;
        while (_pending.TryDequeue(out string? path))
        {
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            seen.Add(path);
        }
        if (seen is null) return;

        foreach (string path in seen)
        {
            if (!_watchers.TryGetValue(path, out Registration reg))
                continue;
            TryReload(path, reg.Program);
        }
    }

    private void TryReload(string path, ShaderProgram program)
    {
        // Editors often hold a brief write lock — retry once on transient
        // sharing-violation rather than dropping the reload.
        string source;
        try
        {
            source = ReadAllTextWithRetry(path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("Shader reload skipped ({Path}): could not read file ({Message})",
                path, ex.Message);
            return;
        }

        CompiledShaderFile compiled;
        try
        {
            compiled = _compiler.Compile(source, [_backend]);
        }
        catch (Exception ex)
        {
            _logger.LogError("Shader compile failed ({Path}): {Message}", path, ex.Message);
            return;
        }

        var blob = compiled.GetPipeline(_backend);
        if (blob is null)
        {
            _logger.LogError("Shader compile produced no pipeline for {Backend} ({Path})", _backend, path);
            return;
        }

        if (!program.TryReload(blob, out string? error))
        {
            _logger.LogError("Shader reload failed ({Path}): {Error}", path, error);
            return;
        }

        _logger.LogInformation("Shader reloaded: {Path}", path);
    }

    private static string ReadAllTextWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(20);
            }
        }
        throw new IOException($"Could not read {path} after retries.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var reg in _watchers.Values)
            reg.Watcher.Dispose();
        _watchers.Clear();
    }

    private readonly record struct Registration(ShaderProgram Program, FileSystemWatcher Watcher);
}
