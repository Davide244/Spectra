# SpectraShade Language Reference

SpectraShade is the shader language consumed by `SpectraShade.Compiler`. Source files use the
`.spectrashade` extension. A file produces a single shader program that the compiler lowers to
one or more backends (GLSL today; HLSL and SPIR-V planned) and packages into a multi-pipeline
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
`sampler2D`, `sampler3D`, `samplerCube`

### Arrays
`T[N]` fixed-size array syntax is accepted by the parser; see FEATURES.md for the currently
supported uniform/texture-array subset.

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
| `[Location(N)]` | struct field / parameter | Vertex input location / varying slot |
| `[Binding(N)]` | `cbuffer`, sampler | Descriptor binding index |
| `[Target(N)]` | function / field | Fragment output render-target index |

Multiple attributes stack: `[Fragment] [Target(0)] vec4 Main(...) { ... }`.

Additional stage and qualifier attributes (`[Geometry]`, `[Compute]`, `[NumThreads]`,
`[DepthWrite]`, interpolation qualifiers, precision qualifiers, etc.) are on the roadmap — see
FEATURES.md.

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
- **Fragment output** is either a single `vec4` annotated with `[Target(N)]` on the function,
  or a struct whose fields each carry `[Target(N)]` (roadmap: MRT).

### Built-in outputs

- `Position` — assignable `vec4` inside `[Vertex]` functions; corresponds to clip-space
  position (`gl_Position` / `SV_POSITION`).

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

See `samples/BasicLit.spectrashade` for a full PBR-ish forward shader that exercises
`cbuffer`, samplers, varying structs, `Position`, `new`, `var`, swizzles, and `Math.*`.

---

## Compilation Output

The compiler emits `.specshadecomp`, a multi-pipeline container bundling compiled bytecode
for each enabled backend. Current backend status:

| Backend | Status |
|---|---|
| GLSL | Implemented |
| HLSL | Planned |
| SPIR-V | Planned |

See FEATURES.md for the up-to-date roadmap.
