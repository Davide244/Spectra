using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SpectraEngine.Core.Scene;
using System;
using System.Globalization;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// Turns a <see cref="SceneNodeKind"/> into the row's glyph and its tint.
/// </summary>
/// <remarks>
/// <b>Two converters rather than six style classes.</b> The alternative is a
/// class per kind bound from the node and a style rule per class per property,
/// which is twelve rules that have to stay in step with an enum; a lookup keyed
/// by the enum itself cannot drift from it.
/// <para>
/// Both resolve through the application's resources, so the actual geometry and
/// colours stay in <c>Theme/Icons.axaml</c> and <c>Theme/Tokens.axaml</c> with
/// everything else. A key that does not resolve returns null and the row simply
/// shows no icon, which is a visible gap rather than a crash in a list.
/// </para>
/// </remarks>
public sealed class SceneNodeKindIconConverter : IValueConverter
{
    /// <summary>The shared instance XAML binds to.</summary>
    public static SceneNodeKindIconConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is SceneNodeKind kind
            ? kind switch
            {
                SceneNodeKind.Group => "IconGroup",
                SceneNodeKind.Mesh => "IconMesh",
                SceneNodeKind.BrushWorld => "IconBrushWorld",
                SceneNodeKind.BrushPart => "IconBrushPart",
                SceneNodeKind.BrushSubtractive => "IconBrushSubtractive",
                SceneNodeKind.Light => "IconLight",
                _ => "IconEmpty",
            }
            : "IconEmpty";

        return Lookup<Geometry>(key);
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A row's icon is never edited.");

    internal static T? Lookup<T>(string key) where T : class =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? found)
            ? found as T
            : null;
}

/// <summary>The tint for a node kind's glyph. See <see cref="SceneNodeKindIconConverter"/>.</summary>
public sealed class SceneNodeKindBrushConverter : IValueConverter
{
    /// <summary>The shared instance XAML binds to.</summary>
    public static SceneNodeKindBrushConverter Instance { get; } = new();

    /// <inheritdoc/>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value is SceneNodeKind kind
            ? kind switch
            {
                SceneNodeKind.Group => "SpectraKindGroup",
                SceneNodeKind.Mesh => "SpectraKindMesh",
                SceneNodeKind.BrushWorld => "SpectraKindBrushWorld",
                SceneNodeKind.BrushPart => "SpectraKindBrushPart",
                SceneNodeKind.BrushSubtractive => "SpectraKindBrushSubtractive",
                SceneNodeKind.Light => "SpectraKindLight",
                _ => "SpectraTextMuted",
            }
            : "SpectraTextMuted";

        return SceneNodeKindIconConverter.Lookup<IBrush>(key);
    }

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A row's tint is never edited.");
}

/// <summary>Turns a row's depth into its indent.</summary>
/// <remarks>
/// <b>A flat list has no nesting to indent by</b>, which is the trade a
/// virtualizing tree makes: the panel sees a list of rows and the depth travels
/// on each one. The indent is therefore a left margin computed here rather than
/// something the control does on its own.
/// </remarks>
public sealed class TreeDepthIndentConverter : IValueConverter
{
    /// <summary>Pixels of indent per level of depth.</summary>
    public const double PerLevel = 13;

    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * PerLevel : 0, 0, 0, 0);

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A row's indent is never edited.");
}
