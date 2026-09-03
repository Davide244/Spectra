using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SpectraEngine.Editor.Shell;

/// <summary>What kind of thing one row in the content browser is.</summary>
public enum ContentKind
{
    /// <summary>A directory. Double-click descends.</summary>
    Folder,

    /// <summary>An image the shell can decode and show.</summary>
    Texture,

    /// <summary>A <c>.spectramat</c>.</summary>
    Material,

    /// <summary>An <c>.obj</c>, <c>.gltf</c> or similar.</summary>
    Model,

    /// <summary>A <c>.spectrashade</c>.</summary>
    Shader,

    /// <summary>Anything else. Listed rather than hidden.</summary>
    Other,
}

/// <summary>One entry in the content browser.</summary>
public sealed class ContentEntry : ObservableObject
{
    private Bitmap? _thumbnail;

    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required ContentKind Kind { get; init; }

    /// <summary>Size on disk, formatted, or empty for a folder.</summary>
    public required string SizeLabel { get; init; }

    /// <summary>
    /// The decoded preview, once a background decode has landed. Null until
    /// then, and null forever for anything that is not an image.
    /// </summary>
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (Set(ref _thumbnail, value))
                Raise(nameof(HasThumbnail));
        }
    }

    /// <summary>Whether a decoded preview is available.</summary>
    public bool HasThumbnail => _thumbnail is not null;

    public bool IsFolder => Kind == ContentKind.Folder;

    /// <summary>
    /// The glyph for this kind, pulled from the theme dictionary.
    /// </summary>
    /// <remarks>
    /// <b>Resolved here rather than by a selector or a converter</b>, because
    /// the alternative is a DataTemplate per kind - six near-identical copies of
    /// one tile - or a value converter class per lookup, which is what this
    /// codebase already refuses elsewhere for exactly this reason. A null
    /// resource simply draws nothing, and the name beneath still says what the
    /// file is.
    /// </remarks>
    public Geometry? Icon => Resource<Geometry>(Kind switch
    {
        ContentKind.Folder => "IconOpenFolder",
        ContentKind.Texture => "IconMesh",
        ContentKind.Material => "IconBrushPart",
        ContentKind.Model => "IconMesh",
        ContentKind.Shader => "IconBrushWorld",
        _ => "IconEmpty",
    });

    /// <summary>The kind's tint, from the same palette the scene tree uses.</summary>
    public IBrush? KindBrush => Resource<IBrush>(Kind switch
    {
        ContentKind.Folder => "SpectraMode",
        ContentKind.Texture => "SpectraKindMesh",
        ContentKind.Material => "SpectraKindBrushPart",
        ContentKind.Model => "SpectraKindBrushWorld",
        ContentKind.Shader => "SpectraKindLight",
        _ => "SpectraTextMuted",
    });

    private static T? Resource<T>(string key) where T : class
        => Application.Current?.TryFindResource(key, out object? value) == true ? value as T : null;
}

/// <summary>
/// The project's <c>Assets/</c> folder, browsed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The headline absence.</b> Nothing in the shell showed a texture, a
/// material or a model, and there was no milestone for one anywhere in the
/// roadmap either - a hole in the plan rather than only in the window. An
/// editor whose content root is real files on disk and which cannot show you
/// those files is asking you to keep a file manager open beside it.
/// </para>
/// <para>
/// <b>It reads the filesystem directly, and the shell decodes the
/// thumbnails.</b> Not through <c>AssetManager</c>: that is the render thread's,
/// its caches are keyed for rendering, and asking it for a preview would create
/// a GPU texture for a picture the user is only looking at. An Avalonia
/// <c>Bitmap</c> off a background thread costs nothing the engine can see.
/// </para>
/// <para>
/// <b>Every listing is a snapshot, and it says when it was taken.</b> There is
/// no file watcher here: one per folder is the shape the texture hot-reload
/// already uses and it is the right eventual answer, but a browser that
/// silently showed a stale folder would be worse than one with a refresh
/// button, which is what this has.
/// </para>
/// <para>UI thread, except the decode.</para>
/// </remarks>
public sealed class ContentBrowserModel : ObservableObject
{
    private readonly ILogger _logger;
    private string? _root;
    private string _currentPath = string.Empty;
    private string _breadcrumb = string.Empty;
    private bool _hasContent;
    private string _emptyMessage = "No project is open.";

    // Bumped on every navigation, so a decode that lands after the user has
    // moved on is dropped rather than writing a thumbnail into a row that is
    // no longer on screen. Same shape as the asset manager's per-asset
    // sequence ticket, and for the same reason.
    private int _generation;

    public ContentBrowserModel(ILogger logger) => _logger = logger;

    /// <summary>The entries in the current folder, folders first.</summary>
    public ObservableCollection<ContentEntry> Entries { get; } = [];

    /// <summary>Where the browser is, relative to the assets root.</summary>
    public string Breadcrumb
    {
        get => _breadcrumb;
        private set => Set(ref _breadcrumb, value);
    }

    /// <summary>Whether the current folder has anything in it.</summary>
    public bool HasContent
    {
        get => _hasContent;
        private set => Set(ref _hasContent, value);
    }

    /// <summary>What to say when it does not.</summary>
    public string EmptyMessage
    {
        get => _emptyMessage;
        private set => Set(ref _emptyMessage, value);
    }

    /// <summary>Whether the browser can go up a level.</summary>
    public bool CanGoUp => _root is not null &&
        !string.Equals(_currentPath, _root, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns one browsed entry into a drag payload, or refuses it.
    /// </summary>
    /// <remarks>
    /// <b>Here rather than in the panel, because the ROOT lives here.</b> The
    /// conversion from an absolute path to the engine's own content-relative
    /// identity needs the root the browser was pointed at, and a panel that
    /// reached for it would be the second place that knows where a project's
    /// assets are.
    /// </remarks>
    public bool TryDescribe(
        ContentEntry entry, [NotNullWhen(true)] out ContentDragPayload? payload)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ContentDragPayload.TryCreate(_root, entry.FullPath, entry.Kind, out payload);
    }

    /// <summary>Points the browser at a project's assets folder, or at nothing.</summary>
    public void SetRoot(string? assetsPath)
    {
        _root = assetsPath;
        Navigate(assetsPath);
    }

    /// <summary>Goes up one level, if there is one.</summary>
    public void GoUp()
    {
        if (!CanGoUp)
            return;

        Navigate(Path.GetDirectoryName(_currentPath));
    }

    /// <summary>Re-reads the current folder.</summary>
    public void Refresh() => Navigate(_currentPath);

    /// <summary>Descends into <paramref name="entry"/> if it is a folder.</summary>
    public void Open(ContentEntry entry)
    {
        if (entry.IsFolder)
            Navigate(entry.FullPath);
    }

    private void Navigate(string? path)
    {
        // Every in-flight decode is now stale.
        int generation = ++_generation;

        Entries.Clear();
        _currentPath = path ?? string.Empty;
        Raise(nameof(CanGoUp));

        if (string.IsNullOrEmpty(path))
        {
            Breadcrumb = string.Empty;
            HasContent = false;
            EmptyMessage = "No project is open.";
            return;
        }

        Breadcrumb = _root is null
            ? path
            : "Assets" + path[_root.Length..].Replace(Path.DirectorySeparatorChar, '/');

        if (!Directory.Exists(path))
        {
            HasContent = false;
            EmptyMessage = $"{Breadcrumb} does not exist on disk.";
            return;
        }

        try
        {
            // Folders first, then files, each alphabetically. Not by date or by
            // kind: a person looking for a file knows its name, and any other
            // order means hunting.
            foreach (string dir in Directory.EnumerateDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                Entries.Add(new ContentEntry
                {
                    Name = Path.GetFileName(dir),
                    FullPath = dir,
                    Kind = ContentKind.Folder,
                    SizeLabel = string.Empty,
                });
            }

            foreach (string file in Directory.EnumerateFiles(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                ContentKind kind = Classify(file);
                var entry = new ContentEntry
                {
                    Name = Path.GetFileName(file),
                    FullPath = file,
                    Kind = kind,
                    SizeLabel = FormatSize(file),
                };

                Entries.Add(entry);

                if (kind == ContentKind.Texture)
                    _ = LoadThumbnailAsync(entry, generation);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder the editor cannot read is a report, not a crash: the
            // browser is a convenience and the project still opens without it.
            _logger.LogWarning(ex, "Could not list {Path}", path);
            HasContent = false;
            EmptyMessage = $"Could not read {Breadcrumb}.";
            return;
        }

        HasContent = Entries.Count > 0;
        EmptyMessage = "This folder is empty.";
    }

    private async Task LoadThumbnailAsync(ContentEntry entry, int generation)
    {
        try
        {
            // DecodeToWidth rather than a full decode: a 4K texture decoded at
            // full size costs 32 MB of managed memory to draw at 72 pixels, and
            // a folder of them is how a content browser becomes the reason an
            // editor runs out of memory.
            Bitmap bitmap = await Task.Run(() =>
            {
                using FileStream stream = File.OpenRead(entry.FullPath);
                return Bitmap.DecodeToWidth(stream, ThumbnailWidth);
            }).ConfigureAwait(true);

            if (Volatile.Read(ref _generation) != generation)
            {
                bitmap.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => entry.Thumbnail = bitmap);
        }
        catch (Exception ex)
        {
            // A file that is not really an image, a truncated download, a lock.
            // The row keeps its kind glyph, which is a correct answer.
            _logger.LogDebug(ex, "No preview for {Path}", entry.FullPath);
        }
    }

    /// <summary>The decoded width of a preview, in pixels.</summary>
    public const int ThumbnailWidth = 96;

    private static ContentKind Classify(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" => ContentKind.Texture,
        ".spectramat" => ContentKind.Material,
        ".obj" or ".gltf" or ".glb" or ".fbx" or ".mtl" => ContentKind.Model,
        ".spectrashade" => ContentKind.Shader,
        _ => ContentKind.Other,
    };

    private static string FormatSize(string path)
    {
        try
        {
            long bytes = new FileInfo(path).Length;
            return bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
                _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
            };
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
