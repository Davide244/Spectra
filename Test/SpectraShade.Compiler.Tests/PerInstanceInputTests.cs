using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Graphics.Shaders;
using SpectraShade.Compiler.Analysis;
using SpectraShade.Compiler.CodeGen;
using SpectraShade.Compiler.Lexing;
using SpectraShade.Compiler.Syntax;

namespace SpectraShade.Compiler.Tests;

/// <summary>
/// Vertex inputs that advance once per instance, and the layout the compiled
/// output reports for them.
/// </summary>
/// <remarks>
/// <b>Nothing here has a symptom at compile time, which is the whole point.</b>
/// Neither target expresses the rate in shader text: GL sets it with
/// <c>glVertexAttribDivisor</c> and D3D with <c>InputSlotClass</c>, so a shader
/// that declares a per-instance input and a renderer that builds a per-vertex
/// layout for it compile, link and draw. Every instance then reads vertex
/// zero's copy, which is a picture rather than an error.
/// <para>
/// The multi-location span of a matrix is the same kind of trap one level down.
/// One field becomes four consecutive attributes in both targets, so a layout
/// built one-element-per-field binds a quarter of the matrix and the next field
/// silently overlaps the rest of it.
/// </para>
/// </remarks>
public sealed class PerInstanceInputTests
{
    private const string Fixture = "InstancedVertex.spectrashade";

    // --- What the compiled output reports ------------------------------------

    [Fact]
    public void The_declared_inputs_are_reported_in_declaration_order()
    {
        PipelineBlob blob = CompileGlsl(Fixture);

        blob.VertexInputs.Select(v => v.Name)
            .ShouldBe(["position", "normal", "uv", "model", "tint"]);
    }

    [Fact]
    public void A_per_instance_field_is_reported_as_per_instance()
    {
        PipelineBlob blob = CompileGlsl(Fixture);

        Input(blob, "position").Rate.ShouldBe(VertexInputRate.PerVertex);
        Input(blob, "normal").Rate.ShouldBe(VertexInputRate.PerVertex);
        Input(blob, "uv").Rate.ShouldBe(VertexInputRate.PerVertex);
        Input(blob, "model").Rate.ShouldBe(VertexInputRate.PerInstance);
        Input(blob, "tint").Rate.ShouldBe(VertexInputRate.PerInstance);
    }

    [Fact]
    public void A_matrix_reports_four_locations_and_four_components_each()
    {
        // Four, not sixteen: it is four four-component rows, and the component
        // count is what picks each element's format.
        PipelineBlob blob = CompileGlsl(Fixture);
        VertexInputElement model = Input(blob, "model");

        model.Location.ShouldBe(3u);
        model.LocationSpan.ShouldBe(4u);
        model.ComponentCount.ShouldBe(4u);
        model.LocationEnd.ShouldBe(7u);
    }

    [Fact]
    public void A_vector_occupies_exactly_one_location()
    {
        PipelineBlob blob = CompileGlsl(Fixture);

        Input(blob, "position").LocationSpan.ShouldBe(1u);
        Input(blob, "position").ComponentCount.ShouldBe(3u);
        Input(blob, "uv").ComponentCount.ShouldBe(2u);
    }

    [Fact]
    public void The_field_after_a_matrix_starts_past_it()
    {
        // 7, not 4. This is the arithmetic a renderer would otherwise have to
        // redo from a type name, which is the second copy of the rule.
        PipelineBlob blob = CompileGlsl(Fixture);

        Input(blob, "tint").Location.ShouldBe(7u);
        Input(blob, "model").Overlaps(Input(blob, "tint")).ShouldBeFalse();
    }

    [Fact]
    public void Both_backends_report_the_same_layout()
    {
        // It describes the source, not the target, so a difference here means
        // one generator resolved locations its own way.
        PipelineBlob glsl = CompileGlsl(Fixture);
        PipelineBlob hlsl = CompileHlsl(Fixture);

        hlsl.VertexInputs.ShouldBe(glsl.VertexInputs);
    }

    [Fact]
    public void A_shader_with_no_per_instance_input_reports_all_per_vertex()
    {
        PipelineBlob blob = CompileGlsl("SimpleVertex.spectrashade");

        blob.VertexInputs.Count.ShouldBe(3);
        blob.VertexInputs.ShouldAllBe(v => v.Rate == VertexInputRate.PerVertex);
        blob.VertexInputs.ShouldAllBe(v => v.LocationSpan == 1);
    }

    // --- What each backend emits ---------------------------------------------

    [Fact]
    public void Glsl_declares_the_matrix_at_its_own_location()
    {
        // GLSL takes the mat4 whole and assigns 4, 5 and 6 itself. The rate is
        // absent by design: glVertexAttribDivisor carries it.
        string glsl = GlslStage(Fixture);

        glsl.ShouldContain("layout(location = 3) in mat4 a_model;", Case.Sensitive);
        glsl.ShouldContain("layout(location = 7) in vec4 a_tint;", Case.Sensitive);
        glsl.ShouldNotContain("PerInstance");
    }

    [Fact]
    public void Hlsl_gives_the_matrix_one_semantic_and_takes_four()
    {
        // float4x4 : TEXCOORD3 occupies TEXCOORD3..TEXCOORD6, which is why the
        // next field is TEXCOORD7 rather than TEXCOORD4.
        string hlsl = HlslStage(Fixture);

        hlsl.ShouldContain("float4x4 model : TEXCOORD3;", Case.Sensitive);
        hlsl.ShouldContain("float4 tint : TEXCOORD7;", Case.Sensitive);
        hlsl.ShouldNotContain("PerInstance");
    }

    // --- The whole generated stage, for review -------------------------------

    [Fact]
    public Task Glsl_vertex_stage_matches_snapshot() =>
        Verify(GlslStage(Fixture), extension: "vert");

    [Fact]
    public Task Hlsl_vertex_stage_matches_snapshot() =>
        Verify(HlslStage(Fixture), extension: "hlsl");

    // --- What the analyzer refuses -------------------------------------------

    [Fact]
    public void A_matrix_input_without_an_explicit_location_is_refused()
    {
        // The field-index fallback is an overlap by construction here: a mat4
        // at index 1 owns 1 through 4 while the next field believes it owns 2.
        Diagnostic[] errors = Errors("""
            struct VertexInput {
                [Location(0)] vec3 position;
                mat4 model;
            }
            """);

        errors.ShouldContain(d => d.Message.Contains("occupies 4 locations"));
    }

    [Fact]
    public void A_per_instance_field_without_an_explicit_location_is_refused()
    {
        // Per-instance data is placed past the per-vertex attributes on
        // purpose; defaulting to the field index would land it on top of them.
        Diagnostic[] errors = Errors("""
            struct VertexInput {
                [Location(0)] vec3 position;
                [PerInstance] vec4 tint;
            }
            """);

        errors.ShouldContain(d => d.Message.Contains("needs an explicit [Location"));
    }

    [Fact]
    public void Two_fields_claiming_one_location_are_refused()
    {
        Diagnostic[] errors = Errors("""
            struct VertexInput {
                [Location(0)] vec3 position;
                [Location(0)] vec3 normal;
            }
            """);

        errors.ShouldContain(d => d.Message.Contains("overlaps"));
    }

    [Fact]
    public void A_field_landing_inside_a_matrix_is_refused()
    {
        // The overlap that has no chance of being noticed by eye: location 5 is
        // legal, unused-looking, and the third row of the matrix at 3.
        Diagnostic[] errors = Errors("""
            struct VertexInput {
                [Location(0)] vec3 position;
                [Location(3)][PerInstance] mat4 model;
                [Location(5)] vec2 uv;
            }
            """);

        errors.ShouldContain(d => d.Message.Contains("overlaps"));
    }

    [Fact]
    public void Per_instance_on_something_that_is_not_a_vertex_input_is_refused()
    {
        // Both generators ignore it, so without this the author is left
        // believing they asked for something.
        Diagnostic[] errors = Errors(
            vertexInput: """
            struct VertexInput {
                [Location(0)] vec3 position;
            }
            """,
            extraStruct: """
            struct Extra {
                [PerInstance] vec4 tint;
            }
            """);

        errors.ShouldContain(d => d.Message.Contains("only valid on a vertex input"));
    }

    [Fact]
    public void A_type_that_cannot_be_a_vertex_input_is_refused()
    {
        Diagnostic[] errors = Errors("""
            struct VertexInput {
                [Location(0)] vec3 position;
                [Location(1)] sampler2D wrong;
            }
            """);

        errors.ShouldContain(d => d.Message.Contains("cannot be a vertex input"));
    }

    [Fact]
    public void The_engines_own_shaders_still_analyze_clean()
    {
        // The validation is new and every base shader predates it, so this is
        // the check that it did not start refusing what already shipped.
        foreach (string fixture in new[]
                 {
                     "SimpleVertex.spectrashade",
                     "NestedReturns.spectrashade",
                     "GeometryExtrude.spectrashade",
                     "ArrayUniforms.spectrashade",
                 })
        {
            CompilationUnit unit = Parse(TestFixtures.Load(fixture));
            var analyzer = new SemanticAnalyzer();
            analyzer.Analyze(unit).ShouldBeTrue(fixture);
        }
    }

    // --- The compiler-generated instanced variant ----------------------------

    // One source, two vertex stages. The author marks a uniform and writes no
    // second shader; see InstancedVariant for why a twin file does not scale.
    private const string Marked = """
        struct VertexInput {
            [Location(0)] vec3 position;
            [Location(1)] vec3 normal;
            [Location(2)] vec2 uv;
        }

        struct VertexOutput {
            [Position] vec4 position;
        }

        shader Marked {
            [Binding(0)] cbuffer Transforms {
                [PerInstance] mat4 uModel;
                mat4 uViewProjection;
            }

            [Vertex]
            VertexOutput VertexMain(VertexInput input) {
                var output = new VertexOutput();
                output.position = uViewProjection * uModel * vec4(input.position, 1.0);
                return output;
            }

            [Fragment] [Target(0)]
            vec4 FragmentMain(VertexOutput input) {
                return vec4(1.0, 1.0, 1.0, 1.0);
            }
        }
        """;

    [Fact]
    public void A_marked_uniform_produces_a_second_vertex_stage()
    {
        PipelineBlob blob = CompileSource(Marked);

        blob.VertexData.ShouldNotBeNull();
        blob.InstancedVertexData.ShouldNotBeNull();
        blob.InstancedVertexData.ShouldNotBe(blob.VertexData);
    }

    [Fact]
    public void A_shader_with_no_marked_uniform_produces_no_variant()
    {
        // Most shaders do not want this, and every one of them goes through the
        // same path, so "no variant" has to be an ordinary answer.
        CompileSource(TestFixtures.Load("SimpleVertex.spectrashade"))
            .InstancedVertexData.ShouldBeNull();
    }

    [Fact]
    public void The_ordinary_stage_is_left_exactly_as_it_was()
    {
        // The whole point of emitting two: a single draw keeps the uniform path
        // and pays nothing for the variant existing.
        string withMark = Stage(CompileSource(Marked).VertexData!);
        string withoutMark = Stage(CompileSource(Marked.Replace("[PerInstance] mat4 uModel;", "mat4 uModel;")).VertexData!);

        withMark.ShouldBe(withoutMark);
    }

    [Fact]
    public void The_variant_takes_the_matrix_as_a_vertex_input()
    {
        PipelineBlob blob = CompileSource(Marked);

        // Location 3: the first one free past position, normal and uv.
        blob.InstancedVertexInputs.Count.ShouldBe(4);
        VertexInputElement model = blob.InstancedVertexInputs.First(v => v.Name == "uModel");
        model.Rate.ShouldBe(VertexInputRate.PerInstance);
        model.Location.ShouldBe(3u);
        model.LocationSpan.ShouldBe(4u);

        blob.VertexInputs.ShouldAllBe(v => v.Rate == VertexInputRate.PerVertex);
    }

    [Fact]
    public void The_variant_drops_the_uniform_but_keeps_its_neighbours()
    {
        string glsl = Stage(CompileSource(Marked).InstancedVertexData!);

        glsl.ShouldContain("layout(location = 3) in mat4 a_uModel;", Case.Sensitive);
        glsl.ShouldContain("uniform mat4 uViewProjection;", Case.Sensitive);
        glsl.ShouldNotContain("uniform mat4 uModel;", Case.Sensitive);
    }

    [Fact]
    public void The_variant_binds_the_name_so_the_body_is_untouched()
    {
        // The rewrite is three edits and no expression rewriting: a leading
        // local keeps every existing bare reference resolving, which is what
        // lets both code generators stay unaware the feature exists.
        string glsl = Stage(CompileSource(Marked).InstancedVertexData!);

        glsl.ShouldContain("mat4 uModel = a_uModel;", Case.Sensitive);
        glsl.ShouldContain("(uViewProjection * uModel)", Case.Sensitive);
    }

    [Fact]
    public void Hlsl_gets_the_same_treatment()
    {
        // Through the public compiler, which is what attaches the variant: a
        // generator called directly emits one stage and knows nothing about it.
        PipelineBlob blob = new SpectraShadeCompiler()
            .Compile(Marked, [GraphicsBackend.D3D11])
            .GetPipeline(GraphicsBackend.D3D11)
            .ShouldNotBeNull();

        string hlsl = Stage(blob.InstancedVertexData.ShouldNotBeNull());

        hlsl.ShouldContain("float4x4 uModel : TEXCOORD3;", Case.Sensitive);
        hlsl.ShouldContain("float4x4 uModel = input.uModel;", Case.Sensitive);
    }

    [Fact]
    public void A_per_instance_uniform_that_is_not_a_matrix_is_refused()
    {
        // Its location span and buffer stride differ from what the instance
        // layout describes, so the variant would be built wrong silently.
        var analyzer = new SemanticAnalyzer();
        analyzer.Analyze(Parse(Marked.Replace("[PerInstance] mat4 uModel;", "[PerInstance] vec3 uModel;")));

        analyzer.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("must be mat4"));
    }

    [Fact]
    public void A_second_per_instance_uniform_is_refused()
    {
        // InstancedVariant takes the first and would ignore the rest, which is
        // a shader that compiles and quietly does half of what it says.
        var analyzer = new SemanticAnalyzer();
        analyzer.Analyze(Parse(Marked.Replace(
            "mat4 uViewProjection;", "[PerInstance] mat4 uOther; mat4 uViewProjection;")));

        analyzer.Diagnostics.ShouldContain(d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("a second"));
    }

    [Fact]
    public void The_engines_shadow_pass_carries_a_variant()
    {
        // The end-to-end claim: ShadowDepth marks its uModel, so the batched
        // twin the renderer draws is generated rather than authored. A file
        // named ShadowDepthInstanced no longer exists.
        PipelineBlob blob = CompileSource(BaseShaders.ShadowDepth);

        blob.InstancedVertexData.ShouldNotBeNull();
        blob.InstancedVertexInputs.ShouldContain(v =>
            v.Name == "uModel" && v.Rate == VertexInputRate.PerInstance && v.LocationSpan == 4);
    }

    // --- helpers -------------------------------------------------------------

    private static VertexInputElement Input(PipelineBlob blob, string name) =>
        blob.VertexInputs.First(v => v.Name == name);

    private static CompilationUnit Parse(string source)
    {
        var parser = new Parser(new Lexer(source, "test.spectrashade").Tokenize());
        CompilationUnit unit = parser.Parse();
        parser.Diagnostics.ShouldBeEmpty();
        return unit;
    }

    private static PipelineBlob CompileGlsl(string fixtureName) =>
        new GlslGenerator().Generate(Analyzed(fixtureName));

    private static PipelineBlob CompileHlsl(string fixtureName) =>
        new HlslGenerator(GraphicsBackend.D3D11).Generate(Analyzed(fixtureName));

    private static CompilationUnit Analyzed(string fixtureName)
    {
        CompilationUnit unit = Parse(TestFixtures.Load(fixtureName));
        var analyzer = new SemanticAnalyzer();
        analyzer.Analyze(unit).ShouldBeTrue();
        return unit;
    }

    // Normalized to LF: the generators emit Environment.NewLine, so multi-line
    // ShouldContain checks would otherwise be OS-dependent.
    private static string Stage(byte[] data) =>
        Encoding.UTF8.GetString(data).ReplaceLineEndings("\n");

    private static CompilationUnit AnalyzedSource(string source)
    {
        CompilationUnit unit = Parse(source);
        var analyzer = new SemanticAnalyzer();
        analyzer.Analyze(unit).ShouldBeTrue();
        return unit;
    }

    private static PipelineBlob CompileSource(string source) =>
        new GlslGenerator().Generate(AnalyzedSource(source)) is var glsl
            && InstancedVariant.TryBuild(AnalyzedSource(source), out CompilationUnit? instanced)
            ? InstancedBlob.With(glsl, new GlslGenerator().Generate(instanced))
            : glsl;

    private static string GlslStage(string fixtureName) =>
        Encoding.UTF8.GetString(CompileGlsl(fixtureName).VertexData!).Replace("\r\n", "\n");

    private static string HlslStage(string fixtureName) =>
        Encoding.UTF8.GetString(CompileHlsl(fixtureName).VertexData!).Replace("\r\n", "\n");

    // Wraps a vertex input struct in the smallest shader that analyzes, so each
    // refusal test shows only the declaration under test.
    private static Diagnostic[] Errors(string vertexInput, string? extraStruct = null)
    {
        string source = $$"""
            {{vertexInput}}

            {{extraStruct ?? ""}}

            struct VertexOutput {
                [Position] vec4 position;
            }

            shader Probe {
                [Vertex]
                VertexOutput VertexMain(VertexInput input) {
                    var output = new VertexOutput();
                    output.position = vec4(1.0, 1.0, 1.0, 1.0);
                    return output;
                }

                [Fragment] [Target(0)]
                vec4 FragmentMain(VertexOutput input) {
                    return vec4(1.0, 1.0, 1.0, 1.0);
                }
            }
            """;

        var analyzer = new SemanticAnalyzer();
        analyzer.Analyze(Parse(source));
        return [.. analyzer.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
    }
}
