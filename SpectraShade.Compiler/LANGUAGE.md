# SpectraShade Language Reference

SpectraShade is the shader language consumed by `SpectraShade.Compiler`. Source files use the
`.spectrashade` extension. A file produces a single shader program that the compiler lowers to
one or more backends (GLSL and HLSL today; SPIR-V planned) and packages into a multi-pipeline
`.specshadecomp` artifact.

The surface syntax blends GLSL's type vocabulary with C#-style attributes and `new` expressions.

---

## File Structure

```
import "path/to/other.spectrashade";   // zero or more

struct Foo { ... }                      // zero or more top-level structs

shader MyShader {                       // exactly one shader block
    // cbuffers, samplers, structs, helpers, stage entry points
}

struct Bar { ... }                      // structs may also follow the shader block
```

Ordering rules enforced by the parser:

- `import` directives must come before the `shader` block.
- `struct` declarations may appear either before or after the `shader` block.
- Exactly one `shader` block per file.

### Imports

```
import "common/Lighting.spectrashade";
```

Paths are quoted string literals, resolved relative to the compiler's import roots.

---

## Types

### Scalars
`bool`, `int`, `uint`, `float`, `double`, `void`

### Vectors
`vec2` `vec3` `vec4` (float), `ivec2..4`, `uvec2..4`, `bvec2..4`

### Matrices
`mat2`, `mat3`, `mat4` (column-major, float)

### Samplers
`sampler2D`, `sampler2DArray`, `sampler3D`, `samplerCube`

### Arrays
`T[N]` fixed-size array syntax, with the size on the **type**, not after the name.
Supported for uniform fields and sampler declarations:

```
[Binding(0)] cbuffer Lights {
    vec3[8] lightPositions;
    vec4[8] lightColors;
}
[Binding(1)] sampler2D[4] textures;
```

The C-style `vec3 lightPositions[8];` spelling that this section used to show is a
parse error: the parser reads a full type (brackets included) and then expects a
name. Index with `a[i]` as usual, including with a loop variable in a fragment
shader.

**Only `vec4[N]` and `mat4[N]` can be filled from the engine.** Every other
element type is padded to a 16-byte stride inside an HLSL constant buffer but
packed tightly in GLSL, so one byte buffer cannot serve both backends; the engine
refuses those rather than uploading something that is correct on OpenGL and
scrambled on D3D. See `ShaderProgram.SetUniform`.

### User structs
Declared with `struct Name { field; field; }`. Instantiate with `new Name()` — the expression
returns a default-initialized value that you populate field-by-field:

```
var output = new FragmentInput();
output.uv = input.uv;
```

### `var` (type inference)
`var x = expr;` infers the type from the initializer. The initializer is required.

---

## Attributes

Attributes use C# bracket syntax and attach to the declaration that immediately follows.

| Attribute | Applies to | Meaning |
|---|---|---|
| `[Vertex]` | function | Vertex stage entry point |
| `[Fragment]` | function | Fragment stage entry point |
| `[Geometry]` | function | Geometry stage entry point (return void, take array param) |
| `[Compute]` | function | Compute stage entry point (return void) |
| `[Location(N)]` | struct field / parameter | Vertex input location / varying slot |
| `[PerInstance]` | vertex input field | Advances once per instance, not per vertex |
| `[Binding(N)]` | `cbuffer`, sampler | Descriptor binding index |
| `[Target(N)]` | struct field | Fragment output render-target index (for MRT) |
| `[NumThreads(x,y,z)]` | `[Compute]` function | Compute workgroup dimensions |
| `[MaxVertexCount(N)]` | `[Geometry]` function | Max output vertices |
| `[InputPrimitive(T)]` | `[Geometry]` function | Input: Points, Lines, LinesAdjacency, Triangles, TrianglesAdjacency |
| `[OutputPrimitive(T)]` | `[Geometry]` function | Output: Points, LineStrip, TriangleStrip |
| `[EarlyDepthStencil]` | `[Fragment]` function | Enable early fragment tests |
| `[DepthWrite(mode)]` | `[Fragment]` function | Depth write hint: Less, Greater, Unchanged |
| `[Position]` | struct field | Marks clip-space position output (vertex/geometry) |

Multiple attributes stack: `[Fragment] [Target(0)] vec4 Main(...) { ... }`.

---

## Uniforms and Resources

### Constant buffers
```
[Binding(0)] cbuffer Camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
}
```

Fields of a `cbuffer` are addressable by bare name inside stage functions (`view`, not
`Camera.view`).

### Samplers / textures
```
[Binding(3)] sampler2D albedoTex;
```

Sample with method syntax:

```
var color = albedoTex.Sample(uv);        // returns vec4
```

---

## Stage Entry Points

A stage is any function carrying a stage attribute. The signature determines input/output:

```
[Vertex]
FragmentInput VertexMain(VertexInput input) { ... }

[Fragment] [Target(0)]
vec4 FragmentMain(FragmentInput input) { ... }
```

- **Vertex input** comes in as a struct whose fields carry `[Location(N)]`, or as individual
  parameters each carrying `[Location(N)]`.
- **Varyings** between vertex and fragment flow through the vertex return struct — its fields
  are matched to the fragment input struct by name.
- **Fragment output** is either a single `vec4`, or a struct whose fields each carry
  `[Target(N)]` for multiple render targets (MRT). Target indices must be unique.

#### Per-instance inputs

A vertex input marked `[PerInstance]` advances once per draw instance instead of once per
vertex. It is how one mesh is drawn N times with N transforms:

```
struct VertexInput {
    [Location(0)] vec3 position;
    [Location(1)] vec3 normal;
    [Location(2)] vec2 uv;

    [Location(3)][PerInstance] mat4 model;
    [Location(7)][PerInstance] vec4 tint;
}
```

Two rules, both enforced, because breaking either produces a shader that compiles and links
on every backend and simply draws the wrong thing:

- **A matrix occupies several consecutive locations**, one per row: `mat4` takes four, `mat3`
  three, `mat2` two. So `model` above owns locations 3 to 6 and the next free one is **7, not
  4**. Any type spanning more than one location must therefore carry an explicit
  `[Location(N)]`, and so must every `[PerInstance]` field; the field-index fallback cannot
  express either. Overlapping locations are an error.
- **The rate is not in the generated code, and it is not supposed to be.** Neither target
  expresses it in shader text: OpenGL sets it with `glVertexAttribDivisor` and D3D with
  `InputSlotClass`/`InstanceDataStepRate` on the input element. The compiled output reports
  it instead, per input, alongside the location and the span, so the renderer builds the
  layout from the shader rather than by agreement with it.

### Geometry stage

The geometry stage takes an array of vertex outputs and emits primitives:

```
[Geometry]
[InputPrimitive(Triangles)]
[OutputPrimitive(TriangleStrip)]
[MaxVertexCount(3)]
void GeometryMain(VertexOutput[] vertices) {
    for (int i = 0; i < 3; i = i + 1) {
        Position = vertices[i].position;
        // set varying outputs...
        EmitVertex();
    }
    EndPrimitive();
}
```

### Compute stage

Compute shaders run general-purpose GPU workloads without rasterization:

```
[Compute]
[NumThreads(8, 8, 1)]
void ComputeMain() {
    var id = GlobalInvocationID;
    // ...
    Barrier();
}
```

Compute built-in variables: `GlobalInvocationID`, `LocalInvocationID`, `WorkGroupID`,
`LocalInvocationIndex`, `NumWorkGroups`, `WorkGroupSize`.

Synchronization: `Barrier()`, `MemoryBarrier()`.

### Built-in outputs

- `Position` — assignable `vec4` inside `[Vertex]` and `[Geometry]` functions; corresponds
  to clip-space position (`gl_Position` / `SV_POSITION`).
- `PrimitiveID` — readable `int` in `[Geometry]` functions.

### Shared helpers
Ordinary functions declared in the shader body are callable from any stage:

```
vec3 ApplyNormalMap(vec3 n, vec3 sampled) { ... }
```

---

## Expressions and Statements

### Control flow
`if` / `else`, `for`, `while`, `return`, `break`, `continue`, `discard`.

### Operators
- Arithmetic: `+ - * / %`
- Comparison: `== != < <= > >=`
- Logical: `&& ||` (spelled `and`/`or` at the token level — see `TokenKind`)
- Bitwise: `& | ^ ~ << >>`
- Assignment: `= += -= *= /=`
- Unary: `-`, `!`, `~`

### Swizzles
Standard GLSL-style: `v.xyz`, `v.rgba`, `v.xxyy`, etc.

### Constructors
Built-in type constructors look like calls: `vec3(1.0, 0.0, 0.0)`, `mat4(1.0)`.
User-defined structs use `new`: `new FragmentInput()`.

### Built-in functions
Math intrinsics live on the `Math.` namespace:

```
Math.Normalize(v)
Math.Dot(a, b)
Math.Cross(a, b)
Math.Max(x, y)  Math.Min(x, y)  Math.Clamp(x, lo, hi)
Math.Pow(x, n)  Math.Sqrt(x)    Math.Abs(x)
Math.Mix(a, b, t)
```

Texture sampling is method-style on sampler values (`tex.Sample(uv)`), not a free function.

---

## Worked Example

See `SpectraEngine.Core/Graphics/BaseShaders/Lit.spectrashade` for the engine's forward
lit shader, which exercises `cbuffer`, samplers, varying structs, `Position`, `new`,
`var`, swizzles, and `Math.*`.

---

## Compilation Output

The compiler emits `.specshadecomp`, a multi-pipeline container bundling compiled bytecode
for each enabled backend. Current backend status:

| Backend | Status |
|---|---|
| GLSL (OpenGL) | Implemented |
| HLSL SM5 (D3D11/D3D12) | Implemented |
| SPIR-V (Vulkan) | Planned |

See FEATURES.md for the up-to-date roadmap.
