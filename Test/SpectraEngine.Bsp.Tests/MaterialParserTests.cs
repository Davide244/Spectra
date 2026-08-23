using SpectraEngine.Core.Assets;
using SpectraEngine.Core.Graphics;
using System.Numerics;

namespace SpectraEngine.Bsp.Tests;

/// <summary>
/// The <c>.spectramat</c> text format, parsed in isolation — no renderer, no
/// asset manager, no disk. The contract these tests pin down is that the parser
/// is <em>total</em>: every input produces a usable definition, and anything it
/// could not understand shows up in
/// <see cref="MaterialDefinition.Warnings"/> instead of as an exception.
/// </summary>
public sealed class MaterialParserTests
{
    [Fact]
    public void Parses_every_field_kind()
    {
        const string source = """
            // A comment line, and a blank one below.

            shader = lit

            texture uDiffuse = Textures/wall_brick.png, nearest, clamp
            texture uMask    = Textures/gradient_mask.png   // options are optional
            float   uRoughness = 0.25
            color   uBaseColor = #8040FF
            color   uTint      = 0.1 0.2 0.3 0.4
            vec2    uTiling    = 4 8
            vec3    uEmissive  = 0.5, 0.25, 0.125
            vec4    uParams    = 1 0 0 1
            """;

        MaterialDefinition definition = MaterialParser.Parse(source, "test.spectramat");

        definition.Warnings.ShouldBeEmpty(Describe(definition));
        definition.ShaderName.ShouldBe("lit");

        // Units come from declaration order, so the first texture line is unit 0.
        definition.Textures.Count.ShouldBe(2);
        definition.TryGetTextureSlot("uDiffuse", out MaterialTextureSlot diffuse).ShouldBeTrue();
        diffuse.TexturePath.ShouldBe("Textures/wall_brick.png");
        diffuse.Unit.ShouldBe(0);
        diffuse.Filter.ShouldBe(TextureFilter.Nearest);
        diffuse.Wrap.ShouldBe(TextureWrap.Clamp);
        diffuse.ColorSpace.ShouldBe(TextureColorSpace.Srgb);

        definition.TryGetTextureSlot("uMask", out MaterialTextureSlot mask).ShouldBeTrue();
        mask.TexturePath.ShouldBe("Textures/gradient_mask.png");
        mask.Unit.ShouldBe(1);
        // Omitted options match what AssetManager.LoadTexture defaults to.
        mask.Filter.ShouldBe(TextureFilter.LinearMipmap);
        mask.Wrap.ShouldBe(TextureWrap.Repeat);
        mask.ColorSpace.ShouldBe(TextureColorSpace.Srgb);

        definition.Parameters.Count.ShouldBe(6);
        ShouldBe(definition, "uRoughness", MaterialParameterKind.Float, new Vector4(0.25f, 0f, 0f, 0f));
        // A 'color' is authored in sRGB and stored linear: the hex bytes map onto
        // 0..1 and then through the transfer curve. ColorSpaceTests pins the curve
        // itself against known values, so this only pins that it was applied.
        ShouldBe(definition, "uBaseColor", MaterialParameterKind.Vector3,
            ColorSpace.SrgbToLinear(new Vector4(128 / 255f, 64 / 255f, 1f, 0f)));
        // A four-component 'color' converts its RGB and leaves alpha alone,
        // because alpha is coverage rather than light.
        ShouldBe(definition, "uTint", MaterialParameterKind.Vector4,
            ColorSpace.SrgbToLinear(new Vector4(0.1f, 0.2f, 0.3f, 0.4f)));
        definition.TryGetParameter("uTint", out MaterialParameter tint).ShouldBeTrue();
        tint.Value.W.ShouldBe(0.4f);

        // ...and 'vec2'/'vec3'/'vec4' are numbers, not colours, so they pass
        // through untouched. That split is the whole rule of the format.
        ShouldBe(definition, "uTiling", MaterialParameterKind.Vector2, new Vector4(4f, 8f, 0f, 0f));
        ShouldBe(definition, "uEmissive", MaterialParameterKind.Vector3, new Vector4(0.5f, 0.25f, 0.125f, 0f));
        ShouldBe(definition, "uParams", MaterialParameterKind.Vector4, new Vector4(1f, 0f, 0f, 1f));
    }

    [Fact]
    public void Empty_and_comment_only_files_parse_to_an_empty_definition()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            // nothing but comments

            """, "empty.spectramat");

        definition.Warnings.ShouldBeEmpty(Describe(definition));
        // No shader named means "use the built-in lit shader", not "broken".
        definition.ShaderName.ShouldBeNull();
        definition.Textures.ShouldBeEmpty();
        definition.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Unknown_key_warns_and_the_rest_of_the_file_still_loads()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            doubleSided = true
            reflections = 4
            color uBaseColor = 1 1 1
            """, "future.spectramat");

        // Forward compatibility: a file written for a newer engine still yields
        // everything this one understands.
        definition.Warnings.Count.ShouldBe(2, Describe(definition));
        definition.Warnings.ShouldContain(w => w.Contains("doubleSided") && w.Contains("unknown key"));
        definition.Warnings.ShouldAllBe(w => w.StartsWith("future.spectramat("));
        definition.TryGetParameter("uBaseColor", out _).ShouldBeTrue();
    }

    [Fact]
    public void Unknown_parameter_kind_and_unknown_texture_option_warn_without_losing_the_directive_line()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            mat4    uWeird   = 1 2 3 4
            texture uDiffuse = Textures/dev_grid.png, trilinear
            """, "odd.spectramat");

        definition.Warnings.ShouldContain(w => w.Contains("unknown parameter kind 'mat4'"));
        definition.Warnings.ShouldContain(w => w.Contains("unknown option 'trilinear'"));

        // The bad option is dropped, the texture itself is not.
        definition.TryGetTextureSlot("uDiffuse", out MaterialTextureSlot slot).ShouldBeTrue();
        slot.Filter.ShouldBe(TextureFilter.LinearMipmap);
        slot.ColorSpace.ShouldBe(TextureColorSpace.Srgb);
        definition.Parameters.ShouldBeEmpty();
    }

    [Fact]
    public void Malformed_values_warn_and_are_skipped()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            float uA = notanumber
            vec3  uB = 1 2
            color uC = #12345
            color uD = 1 2
            texture uE =
            uF
            = 3
            vec2 too many names = 1 2
            float uOk = 2.5
            """, "bad.spectramat");

        definition.Warnings.Count.ShouldBe(8, Describe(definition));
        definition.Warnings.ShouldContain(w => w.Contains("uA") && w.Contains("not a list of up to 4 numbers"));
        definition.Warnings.ShouldContain(w => w.Contains("vec3 uB") && w.Contains("needs 3"));
        definition.Warnings.ShouldContain(w => w.Contains("uC") && w.Contains("#RRGGBB"));
        definition.Warnings.ShouldContain(w => w.Contains("color uD") && w.Contains("3 or 4"));
        definition.Warnings.ShouldContain(w => w.Contains("uE") && w.Contains("no file path"));
        definition.Warnings.ShouldContain(w => w.Contains("expected 'name = value'"));
        definition.Warnings.ShouldContain(w => w.Contains("no name before '='"));
        definition.Warnings.ShouldContain(w => w.Contains("more than a kind and a name"));

        // One good line survives eight bad ones: the parser never gives up on a
        // file, because a half-correct material still beats a crash.
        definition.Parameters.ShouldHaveSingleItem().Name.ShouldBe("uOk");
        definition.Textures.ShouldBeEmpty();
    }

    [Fact]
    public void Duplicate_declarations_warn_and_the_last_one_wins()
    {
        MaterialDefinition definition = MaterialParser.Parse("""
            shader = lit
            texture uDiffuse = Textures/a.png
            texture uOther   = Textures/b.png
            float   uScale   = 1
            texture uDiffuse = Textures/c.png, nearest
            float   uScale   = 2
            shader = unlit
            """, "dupes.spectramat");

        definition.Warnings.Count.ShouldBe(3, Describe(definition));
        definition.ShaderName.ShouldBe("unlit");

        definition.Textures.Count.ShouldBe(2);
        definition.TryGetTextureSlot("uDiffuse", out MaterialTextureSlot diffuse).ShouldBeTrue();
        diffuse.TexturePath.ShouldBe("Textures/c.png");
        diffuse.Filter.ShouldBe(TextureFilter.Nearest);
        // Keeping the original unit matters: renumbering would silently move
        // every sampler declared after it onto a different texture unit.
        diffuse.Unit.ShouldBe(0);
        definition.TryGetTextureSlot("uOther", out MaterialTextureSlot other).ShouldBeTrue();
        other.Unit.ShouldBe(1);

        definition.TryGetParameter("uScale", out MaterialParameter scale).ShouldBeTrue();
        scale.AsFloat.ShouldBe(2f);
    }

    [Fact]
    public void Numbers_parse_with_the_invariant_culture()
    {
        // The demo host runs with InvariantGlobalization, but the editor and the
        // tools will not; "0.5" must be a half on a machine whose locale uses a
        // comma as the decimal separator, and the comma must stay a separator.
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            MaterialDefinition definition = MaterialParser.Parse("vec3 uC = 0.5, 1.25, 2", "culture.spectramat");

            definition.Warnings.ShouldBeEmpty(Describe(definition));
            definition.TryGetParameter("uC", out MaterialParameter c).ShouldBeTrue();
            c.AsVector3.ShouldBe(new Vector3(0.5f, 1.25f, 2f));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Every_shipped_material_file_parses_cleanly()
    {
        string folder = Path.Combine(ContentRoot.Path, "Materials");
        string[] files = Directory.GetFiles(folder, "*" + MaterialParser.FileExtension);

        // The repo's own content is the format's regression suite: a typo in a
        // shipped .spectramat has to fail the build, not just look wrong in-game.
        files.ShouldNotBeEmpty($"no material files found in {folder}");
        foreach (string file in files)
        {
            MaterialDefinition definition = MaterialParser.ParseFile(file);
            definition.Warnings.ShouldBeEmpty($"{file}: {Describe(definition)}");
            definition.Textures.ShouldNotBeEmpty(file);

            foreach (MaterialTextureSlot slot in definition.Textures)
            {
                string texture = ContentRoot.ResolveAbsolute(ContentRoot.Path, slot.TexturePath);
                File.Exists(texture).ShouldBeTrue($"{file} references a missing texture: {slot.TexturePath}");
            }
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static void ShouldBe(
        MaterialDefinition definition, string name, MaterialParameterKind kind, Vector4 value)
    {
        definition.TryGetParameter(name, out MaterialParameter parameter)
            .ShouldBeTrue($"'{name}' was not parsed");
        parameter.Kind.ShouldBe(kind, name);
        // Exact comparison is right here: every literal in these files
        // round-trips bit-for-bit through float.Parse, and a tolerance would
        // hide a parser that dropped or reordered a component.
        parameter.Value.ShouldBe(value, name);
    }

    private static string Describe(MaterialDefinition definition)
        => definition.Warnings.Count == 0
            ? "(no warnings)"
            : string.Join(Environment.NewLine, definition.Warnings);
}
