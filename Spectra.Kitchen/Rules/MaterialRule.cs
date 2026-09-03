using Spectra.Kitchen.Cache;
using Spectra.Kitchen.Diagnostics;
using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Assets.Images;
using SpectraEngine.Core.Assets.Packs;
using SpectraEngine.Core.Graphics.Shaders;
using System;

namespace Spectra.Kitchen.Rules;

/// <summary>
/// Validates a <c>.spectramat</c> and packs it as source text, verbatim.
/// </summary>
/// <remarks>
/// <para><b>Validated source, never a binary re-encoding, and that is a decision
/// rather than a stage nobody got to.</b> The parser is hand-written and
/// deliberately forward-compatible - an unknown key warns rather than throws, so
/// a file written for a newer engine still loads everything this one understands
/// - and a binary form would fork that: the cooked shape would have to decide
/// what to do with a key it has no field for, and the only answers are to drop it
/// (so cooking silently deletes authored data) or to carry it as text beside the
/// fields (so the format is the text file plus a cache of itself). A material is
/// also tiny and parsed once at load; there is no runtime cost here to buy. What
/// cooking a material is FOR is the validation, and the bytes that come out are
/// the bytes that went in.</para>
/// <para><b>The entry kind changes even though the payload does not.</b> Before
/// this rule a material fell through to <c>RawCopyRule</c> and landed as
/// <c>PackEntryKind.Raw</c>, which is the pack saying nothing about it;
/// <c>PackEntryKind.Material</c> is the routing hint the format reserved, and it
/// is what makes <c>scook inspect</c> able to say that a pack's materials were
/// looked at rather than copied.</para>
/// <para><b>A texture reference REDIRECTS, and this rule does not know how.</b>
/// A material names <c>Textures/x.png</c> forever - that identity is not
/// rewritten by cooking - while the bytes may live at <c>Textures/x.simage</c>.
/// <see cref="ImageContentPath.Resolve"/> is the one expression of that rule and
/// this asks it through <see cref="RuleContentSource"/>, because a second
/// spelling of it here is exactly the disagreement that binds the magenta
/// placeholder into every packed material while every log line reads healthy.</para>
/// <para><b>Every probe is a recorded dependency, misses included.</b> A material
/// that looked for a texture and did not find one re-cooks the moment somebody
/// adds the file. Without that, a watch loop serves the stale cook and reports
/// success, which is the failure <c>IRuleContext</c> records negative
/// dependencies for.</para>
/// <para><b>What is fatal here is not decided here.</b> Severity is
/// <see cref="CookGate"/>'s, once, for this rule and for
/// <c>PackVerifier</c>'s material arm alike - see
/// <c>docs/formats-and-pipeline.md</c> 4.2 for why a missing texture stops a
/// build and degrades a frame.</para>
/// </remarks>
public sealed class MaterialRule : IRule
{
    /// <inheritdoc/>
    public RuleKind Kind => RuleKind.Material;

    /// <inheritdoc/>
    /// <remarks>
    /// Raise this whenever the bytes this rule emits for one source can change.
    /// They are the source's own bytes, so what would move it is a change to
    /// WHAT is emitted - a second output, or a decision to stop emitting the
    /// authored text.
    /// </remarks>
    public int Version => 1;

    /// <inheritdoc/>
    /// <remarks>
    /// None, and it is a real answer. The payload is the file, which no setting
    /// varies: not the profile, which only picks an encoder's search quality,
    /// and not the target list, which decides which shader blobs exist rather
    /// than which shader a material names. A material that has anything to say is
    /// never cached at all (see <c>CookSession</c>), so the one thing a shared
    /// cache entry could hide - a validation failure under one setting and not
    /// another - cannot happen through this door.
    /// </remarks>
    public CookSettingKeys SettingsRead => CookSettingKeys.None;

    /// <summary>Whether <paramref name="contentPath"/> is a material file.</summary>
    public static bool Handles(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);
        return contentPath.EndsWith(MaterialParser.FileExtension, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public void Cook(IRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        byte[] source = context.Read(context.SourcePath);
        MaterialDefinition material = MaterialParser.ParseUtf8(source, context.SourcePath);

        // The parser's own complaints, carried rather than swallowed. It warns
        // instead of throwing so files stay forward-compatible, which means an
        // unusable line is otherwise a silently weaker material and nothing
        // anywhere says so.
        foreach (string warning in material.Warnings)
        {
            context.Report(CookDiagnostic.Warning(
                CookDiagnosticCodes.MaterialFileMalformed, warning, context.SourcePath));
        }

        var content = new RuleContentSource(context);

        foreach (MaterialTextureSlot slot in material.Textures)
        {
            string resolved = ImageContentPath.Resolve(content, slot.TexturePath);
            if (content.Exists(resolved)) continue;

            context.Report(CookDiagnostic.Error(
                CookDiagnosticCodes.MaterialTextureMissing,
                $"'{context.SourcePath}' binds sampler '{slot.Name}' to '{slot.TexturePath}', which is not in " +
                "the content root. The running engine would show the magenta placeholder and carry on; a " +
                "shipped build would ship that.",
                context.SourcePath));
        }

        ReportUnresolvableShader(context, content, material.ShaderName);

        // Verbatim, and AFTER the checks rather than instead of them: the
        // emission is what a validated file earns. A material that failed still
        // emits, because the cook is refused by the diagnostic and a pack is
        // never written from a failed cook - and suppressing the output here
        // would make a single broken material look like a missing asset in every
        // report downstream of it.
        context.Emit(context.SourcePath, source, PackEntryKind.Material);
    }

    // What the cooker can see is CONTENT, and it says so rather than guessing.
    // AssetManager resolves a named shader through a host callback first and
    // falls back to the built-in lit program with a warning, so a name nothing
    // provides renders a surface with a program its author did not choose - a
    // picture that is merely wrong, which is the class of failure a build step
    // exists to catch.
    private static void ReportUnresolvableShader(
        IRuleContext context, RuleContentSource content, string? shaderName)
    {
        // No key at all is the built-in, which is what the parser means by null
        // and what every material in the engine's own content says out loud.
        if (string.IsNullOrEmpty(shaderName)) return;
        if (IsBuiltIn(shaderName)) return;

        // Spelled as a path already - a project naming its own shader file - or
        // under the Shaders folder a built-in resolves from, in source or cooked
        // form. Cooked is probed too because a project may legitimately ship a
        // pre-compiled blob it did not author the source for.
        if (content.Exists(shaderName)) return;
        if (content.Exists($"{BaseShaders.ContentFolder}/{shaderName}{ShaderRule.SourceExtension}")) return;
        if (content.Exists($"{BaseShaders.ContentFolder}/{shaderName}{ShaderRule.CookedExtension}")) return;

        context.Report(CookDiagnostic.Error(
            CookDiagnosticCodes.MaterialShaderMissing,
            $"'{context.SourcePath}' names shader '{shaderName}', which is neither the built-in " +
            $"'{MaterialParser.BuiltInShaderName}' nor a shader in this project. The running engine would draw " +
            "with the built-in lit program instead and log a warning; a shipped build would render that.",
            context.SourcePath));
    }

    // The built-in lit program, by the name the parser documents, or by any of
    // the file names BaseShaders embeds - those ship inside the engine assembly,
    // so they are present in every build whether or not a pack carries them and
    // a cook must not report one as missing.
    private static bool IsBuiltIn(string shaderName)
    {
        if (string.Equals(shaderName, MaterialParser.BuiltInShaderName, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (string fileName in BaseShaders.FileNames)
        {
            ReadOnlySpan<char> stem = fileName.AsSpan(0, fileName.Length - BaseShaders.SourceExtension.Length);
            if (stem.Equals(shaderName, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
