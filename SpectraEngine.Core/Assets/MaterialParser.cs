using SpectraEngine.Core.Graphics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace SpectraEngine.Core.Assets;

/// <summary>
/// Reads <c>.spectramat</c> material files into a <see cref="MaterialDefinition"/>.
/// </summary>
/// <remarks>
/// <para><b>Why hand-parsed.</b> The format is line-oriented key/value text, so a
/// ~150-line reader beats pulling in a serializer: it is trivially AOT-safe (no
/// reflection, no source-generated contexts to keep in sync), it can report a
/// line number with every complaint, and an artist can diff it in a merge tool.
/// </para>
///
/// <para><b>File format.</b> One directive per line. Blank lines are ignored and
/// <c>//</c> starts a comment that runs to the end of the line (the same comment
/// syntax as <c>.spectrashade</c>; <c>#</c> is not a comment because it
/// introduces a hex colour). Every directive is
/// <c>name = value</c> or <c>kind name = value</c>, and names are matched to
/// shader uniforms case-sensitively:
/// </para>
/// <code>
/// // wall.spectramat
/// shader = lit                                     // optional; the built-in lit shader is the default
///
/// texture uDiffuse = Textures/wall_brick.png, linearmipmap, repeat
/// color   uBaseColor = #B4A08C                     // or: 0.706 0.627 0.549
/// float   uRoughness = 0.8
/// vec2    uTiling = 4 4
/// vec3    uEmissive = 0 0 0
/// vec4    uParams = 1 0 0 1
/// </code>
///
/// <list type="bullet">
/// <item><description><c>shader</c> — optional bare key naming the shader program.
/// <c>lit</c> (the default) selects the engine's built-in lit shader; other names
/// are resolved by the host through <see cref="AssetManager.ShaderResolver"/> and
/// fall back to the lit shader with a warning if nothing claims them.</description></item>
/// <item><description><c>texture &lt;sampler&gt; = &lt;path&gt;[, &lt;filter&gt;][, &lt;wrap&gt;]</c> —
/// the path is relative to the content root. Filter is <c>nearest</c>,
/// <c>linear</c>, or <c>linearmipmap</c> (default); wrap is <c>repeat</c>
/// (default) or <c>clamp</c>. Texture units are assigned by declaration order,
/// so the first <c>texture</c> line is unit 0.</description></item>
/// <item><description><c>float</c>, <c>vec2</c>, <c>vec3</c>, <c>vec4</c> —
/// whitespace- or comma-separated numbers, parsed with the invariant culture, so
/// <c>0.5</c> is a half everywhere on earth.</description></item>
/// <item><description><c>color</c> — either <c>#RRGGBB</c> / <c>#RRGGBBAA</c>
/// (byte values mapped linearly onto 0..1; the engine does no gamma conversion
/// anywhere, so what you type is what the shader gets) or 3–4 plain numbers.
/// Three components produce a <see cref="MaterialParameterKind.Vector3"/>, four a
/// <see cref="MaterialParameterKind.Vector4"/>.</description></item>
/// </list>
///
/// <para><b>Render flags</b> (blending, culling, depth state) are deliberately
/// absent: no pipeline consumes per-material render state today, and a key that
/// silently does nothing is worse than no key. An unknown key only warns, so
/// files may be written ahead of the engine without breaking.</para>
///
/// <para><b>Tolerance.</b> Nothing in a material file is fatal. An unreadable
/// line, an unknown key or kind, a malformed number, a duplicate name — each
/// costs that one directive, lands in <see cref="MaterialDefinition.Warnings"/>,
/// and parsing continues. Only the file being unreadable throws (from
/// <see cref="ParseFile"/>).</para>
///
/// <para><b>Threading.</b> Pure CPU and free of GPU state, like
/// <see cref="ImageDecoder"/> — every member here is callable from any thread.</para>
/// </remarks>
public static class MaterialParser
{
    /// <summary>Canonical extension of a material file, including the dot.</summary>
    public const string FileExtension = ".spectramat";

    /// <summary>Shader name that selects the engine's built-in lit shader.</summary>
    public const string BuiltInShaderName = "lit";

    // Matches the sampling defaults callers get from AssetManager.LoadTexture,
    // so an option-free texture line behaves exactly like a code-authored load.
    private const TextureFilter DefaultFilter = TextureFilter.LinearMipmap;
    private const TextureWrap DefaultWrap = TextureWrap.Repeat;

    /// <summary>
    /// Reads and parses the material file at <paramref name="absolutePath"/>.
    /// Any thread.
    /// </summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static MaterialDefinition ParseFile(string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(absolutePath);
        return Parse(File.ReadAllText(absolutePath), absolutePath);
    }

    /// <summary>
    /// Parses material-file text. <paramref name="originForErrors"/> only labels
    /// the messages in <see cref="MaterialDefinition.Warnings"/>. Any thread.
    /// </summary>
    public static MaterialDefinition Parse(string text, string originForErrors = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(text);

        var textures = new List<MaterialTextureSlot>();
        var parameters = new List<MaterialParameter>();
        var warnings = new List<string>();
        string? shaderName = null;

        int lineNumber = 0;
        foreach (ReadOnlySpan<char> rawLine in text.AsSpan().EnumerateLines())
        {
            lineNumber++;
            ReadOnlySpan<char> line = StripComment(rawLine).Trim();
            if (line.IsEmpty) continue;

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                Warn(warnings, originForErrors, lineNumber,
                    $"expected 'name = value' or 'kind name = value', got '{line.ToString()}'");
                continue;
            }

            ReadOnlySpan<char> left = line[..equals].Trim();
            ReadOnlySpan<char> value = line[(equals + 1)..].Trim();
            if (left.IsEmpty)
            {
                Warn(warnings, originForErrors, lineNumber, "directive has no name before '='");
                continue;
            }

            // Left side is either one token (a bare key like 'shader') or two
            // (a typed parameter: kind then name). Anything else is a typo.
            int split = IndexOfWhitespace(left);
            if (split < 0)
            {
                ParseBareKey(left, value, ref shaderName, warnings, originForErrors, lineNumber);
                continue;
            }

            ReadOnlySpan<char> kind = left[..split];
            ReadOnlySpan<char> name = left[(split + 1)..].TrimStart();
            if (IndexOfWhitespace(name) >= 0)
            {
                Warn(warnings, originForErrors, lineNumber,
                    $"'{left.ToString()}' has more than a kind and a name before '='");
                continue;
            }

            ParseTypedDirective(
                kind, name, value, textures, parameters, warnings, originForErrors, lineNumber);
        }

        return new MaterialDefinition(originForErrors, shaderName, textures, parameters, warnings);
    }

    // ---- directives ------------------------------------------------------

    private static void ParseBareKey(
        ReadOnlySpan<char> key,
        ReadOnlySpan<char> value,
        ref string? shaderName,
        List<string> warnings,
        string origin,
        int line)
    {
        if (key.Equals("shader", StringComparison.OrdinalIgnoreCase))
        {
            if (value.IsEmpty)
            {
                Warn(warnings, origin, line, "'shader' has no value; using the built-in lit shader");
                return;
            }
            if (shaderName is not null)
                Warn(warnings, origin, line, $"'shader' set again; '{value.ToString()}' replaces '{shaderName}'");

            shaderName = value.ToString();
            return;
        }

        // Unknown keys are the format's forward-compatibility hinge: a file
        // written for a newer engine still loads everything this one understands.
        Warn(warnings, origin, line, $"unknown key '{key.ToString()}' ignored");
    }

    private static void ParseTypedDirective(
        ReadOnlySpan<char> kind,
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> value,
        List<MaterialTextureSlot> textures,
        List<MaterialParameter> parameters,
        List<string> warnings,
        string origin,
        int line)
    {
        if (kind.Equals("texture", StringComparison.OrdinalIgnoreCase))
        {
            ParseTexture(name, value, textures, warnings, origin, line);
            return;
        }

        MaterialParameterKind parameterKind;
        int required;
        if (kind.Equals("float", StringComparison.OrdinalIgnoreCase)) { parameterKind = MaterialParameterKind.Float; required = 1; }
        else if (kind.Equals("vec2", StringComparison.OrdinalIgnoreCase)) { parameterKind = MaterialParameterKind.Vector2; required = 2; }
        else if (kind.Equals("vec3", StringComparison.OrdinalIgnoreCase)) { parameterKind = MaterialParameterKind.Vector3; required = 3; }
        else if (kind.Equals("vec4", StringComparison.OrdinalIgnoreCase)) { parameterKind = MaterialParameterKind.Vector4; required = 4; }
        else if (kind.Equals("color", StringComparison.OrdinalIgnoreCase) ||
                 kind.Equals("colour", StringComparison.OrdinalIgnoreCase))
        {
            ParseColor(name, value, parameters, warnings, origin, line);
            return;
        }
        else
        {
            Warn(warnings, origin, line, $"unknown parameter kind '{kind.ToString()}' ignored");
            return;
        }

        Span<float> components = stackalloc float[4];
        if (!TryParseNumbers(value, components, out int count))
        {
            Warn(warnings, origin, line,
                $"'{name.ToString()}' is not a list of up to 4 numbers: '{value.ToString()}'");
            return;
        }
        if (count != required)
        {
            Warn(warnings, origin, line,
                $"'{kind.ToString()} {name.ToString()}' needs {required} number(s), got {count}");
            return;
        }

        AddParameter(
            parameters, warnings, origin, line,
            new MaterialParameter(name.ToString(), parameterKind,
                new Vector4(components[0], components[1], components[2], components[3])));
    }

    private static void ParseColor(
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> value,
        List<MaterialParameter> parameters,
        List<string> warnings,
        string origin,
        int line)
    {
        Span<float> components = stackalloc float[4];
        int count;

        if (!value.IsEmpty && value[0] == '#')
        {
            if (!TryParseHexColor(value[1..], components, out count))
            {
                Warn(warnings, origin, line,
                    $"'{name.ToString()}' is not a #RRGGBB or #RRGGBBAA colour: '{value.ToString()}'");
                return;
            }
        }
        else if (!TryParseNumbers(value, components, out count))
        {
            Warn(warnings, origin, line,
                $"'{name.ToString()}' is neither a hex colour nor a number list: '{value.ToString()}'");
            return;
        }

        if (count is not (3 or 4))
        {
            Warn(warnings, origin, line, $"'color {name.ToString()}' needs 3 or 4 components, got {count}");
            return;
        }

        AddParameter(
            parameters, warnings, origin, line,
            new MaterialParameter(
                name.ToString(),
                count == 3 ? MaterialParameterKind.Vector3 : MaterialParameterKind.Vector4,
                new Vector4(components[0], components[1], components[2], components[3])));
    }

    private static void ParseTexture(
        ReadOnlySpan<char> name,
        ReadOnlySpan<char> value,
        List<MaterialTextureSlot> textures,
        List<string> warnings,
        string origin,
        int line)
    {
        // The path is everything up to the first comma, so a folder or file name
        // containing a space still works; the options after it never do.
        int comma = value.IndexOf(',');
        ReadOnlySpan<char> path = (comma < 0 ? value : value[..comma]).Trim();
        if (path.IsEmpty)
        {
            Warn(warnings, origin, line, $"texture '{name.ToString()}' has no file path");
            return;
        }

        TextureFilter filter = DefaultFilter;
        TextureWrap wrap = DefaultWrap;
        ReadOnlySpan<char> options = comma < 0 ? default : value[(comma + 1)..];
        while (!options.IsEmpty)
        {
            int next = options.IndexOf(',');
            ReadOnlySpan<char> option = (next < 0 ? options : options[..next]).Trim();
            options = next < 0 ? default : options[(next + 1)..];
            if (option.IsEmpty) continue;

            if (TryParseFilter(option, out TextureFilter parsedFilter)) filter = parsedFilter;
            else if (TryParseWrap(option, out TextureWrap parsedWrap)) wrap = parsedWrap;
            else
                Warn(warnings, origin, line,
                    $"texture '{name.ToString()}' has unknown option '{option.ToString()}' (expected nearest/linear/linearmipmap or repeat/clamp)");
        }

        string slotName = name.ToString();
        for (int i = 0; i < textures.Count; i++)
        {
            if (textures[i].Name != slotName) continue;

            // Keep the original unit: the sampler already has one, and shifting
            // it would silently renumber every slot declared after it.
            Warn(warnings, origin, line, $"texture '{slotName}' declared more than once; the last one wins");
            textures[i] = new MaterialTextureSlot(slotName, path.ToString(), textures[i].Unit, filter, wrap);
            return;
        }

        textures.Add(new MaterialTextureSlot(slotName, path.ToString(), textures.Count, filter, wrap));
    }

    private static void AddParameter(
        List<MaterialParameter> parameters,
        List<string> warnings,
        string origin,
        int line,
        MaterialParameter parameter)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].Name != parameter.Name) continue;

            Warn(warnings, origin, line, $"parameter '{parameter.Name}' declared more than once; the last one wins");
            parameters[i] = parameter;
            return;
        }

        parameters.Add(parameter);
    }

    // ---- scanning helpers ------------------------------------------------

    private static ReadOnlySpan<char> StripComment(ReadOnlySpan<char> line)
    {
        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static int IndexOfWhitespace(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (char.IsWhiteSpace(span[i])) return i;
        }
        return -1;
    }

    // Numbers separated by whitespace and/or commas, invariant culture: a
    // material file must read the same on a machine whose locale uses a comma
    // as the decimal separator.
    private static bool TryParseNumbers(ReadOnlySpan<char> value, Span<float> destination, out int count)
    {
        destination.Clear();
        count = 0;

        ReadOnlySpan<char> remaining = value;
        while (true)
        {
            while (!remaining.IsEmpty && (char.IsWhiteSpace(remaining[0]) || remaining[0] == ','))
                remaining = remaining[1..];
            if (remaining.IsEmpty) break;

            int end = 0;
            while (end < remaining.Length && !char.IsWhiteSpace(remaining[end]) && remaining[end] != ',')
                end++;

            if (count == destination.Length) return false; // more components than any kind accepts
            if (!float.TryParse(remaining[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return false;

            destination[count++] = parsed;
            remaining = remaining[end..];
        }

        return count > 0;
    }

    private static bool TryParseHexColor(ReadOnlySpan<char> digits, Span<float> destination, out int count)
    {
        destination.Clear();
        count = 0;
        if (digits.Length is not (6 or 8)) return false;

        int components = digits.Length / 2;
        for (int i = 0; i < components; i++)
        {
            if (!byte.TryParse(digits.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                return false;
            destination[i] = b / 255f;
        }

        count = components;
        return true;
    }

    private static bool TryParseFilter(ReadOnlySpan<char> token, out TextureFilter filter)
    {
        // Hand-written instead of Enum.TryParse: no reflection anywhere near the
        // AOT boundary, and it rejects the numeric spellings Enum.TryParse takes.
        if (token.Equals("nearest", StringComparison.OrdinalIgnoreCase)) { filter = TextureFilter.Nearest; return true; }
        if (token.Equals("linear", StringComparison.OrdinalIgnoreCase)) { filter = TextureFilter.Linear; return true; }
        if (token.Equals("linearmipmap", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("mipmap", StringComparison.OrdinalIgnoreCase)) { filter = TextureFilter.LinearMipmap; return true; }

        filter = DefaultFilter;
        return false;
    }

    private static bool TryParseWrap(ReadOnlySpan<char> token, out TextureWrap wrap)
    {
        if (token.Equals("repeat", StringComparison.OrdinalIgnoreCase)) { wrap = TextureWrap.Repeat; return true; }
        if (token.Equals("clamp", StringComparison.OrdinalIgnoreCase)) { wrap = TextureWrap.Clamp; return true; }

        wrap = DefaultWrap;
        return false;
    }

    private static void Warn(List<string> warnings, string origin, int line, string message)
        => warnings.Add($"{origin}({line}): {message}");
}
