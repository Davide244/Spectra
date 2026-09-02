using SpectraEngine.Core.Projects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Spectra.Kitchen.Tests;

/// <summary>
/// A real project folder in a temporary directory, scaffolded the way the editor
/// scaffolds one.
/// </summary>
/// <remarks>
/// Deliberately a real <see cref="ProjectLayout"/> on a real filesystem rather
/// than an in-memory stand-in: the cook's whole input is a folder, and the parts
/// most likely to be wrong (path normalisation, separator direction, walk order)
/// are exactly the parts a fake would paper over.
/// </remarks>
internal sealed class TempProject : IDisposable
{
    private readonly List<IDisposable> _open = [];

    public TempProject(string name = "TestGame")
    {
        Root = Path.Combine(Path.GetTempPath(), $"spectra_cook_{Guid.NewGuid():N}");
        Layout = ProjectLayout.Create(Root, name);
    }

    /// <summary>The project folder.</summary>
    public string Root { get; }

    /// <summary>The opened project.</summary>
    public ProjectLayout Layout { get; }

    /// <summary>Where a cook writes when nothing overrides it.</summary>
    public string CookedPath => Layout.CookedPath;

    /// <summary>Writes an asset at a content-relative path, creating folders as needed.</summary>
    public byte[] WriteAsset(string contentPath, byte[] bytes)
    {
        string full = Path.Combine(
            Layout.AssetsPath, contentPath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return bytes;
    }

    /// <summary>Writes a text asset.</summary>
    public byte[] WriteAsset(string contentPath, string text) =>
        WriteAsset(contentPath, Encoding.UTF8.GetBytes(text));

    /// <summary>Deterministic bytes, so a comparison failure names a byte rather than a length.</summary>
    public static byte[] Bytes(int length, byte seed = 0)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)((i * 31) + seed);
        return bytes;
    }

    /// <summary>Registers something to close before the folder is deleted.</summary>
    public T Track<T>(T disposable) where T : IDisposable
    {
        _open.Add(disposable);
        return disposable;
    }

    public void Dispose()
    {
        // Mapped packs first: a mapped view keeps its file open, and a directory
        // holding one cannot be deleted on Windows.
        for (int i = _open.Count - 1; i >= 0; i--) _open[i].Dispose();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Failing a test on its own cleanup helps nobody; a leaked mapping
            // shows up as the assertion it actually broke.
        }
    }
}
