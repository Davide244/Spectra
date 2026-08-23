# SpectraShade Feature Roadmap

## Currently Implemented
- [x] Vertex + Fragment stages (as attributed methods)
- [x] Scalar types (bool, int, uint, float, double)
- [x] Vector types (vec2/3/4, ivec, uvec, bvec)
- [x] Matrix types (mat2/3/4)
- [x] Sampler types (sampler2D, sampler3D, samplerCube)
- [x] Structs
- [x] cbuffer uniform blocks
- [x] C#-style attributes ([Vertex], [Location(N)], [Binding(N)], [Target(N)])
- [x] Type inference (var)
- [x] Import system
- [x] Built-in math functions (Math.Normalize, Math.Dot, etc.)
- [x] Method-style texture sampling (tex.Sample(uv))
- [x] Position built-in (vertex output)
- [x] Control flow (if/else, for, while, return, break, continue, discard)
- [x] Swizzling (.xyz, .rgba)
- [x] Shared functions across stages
- [x] GLSL code generation
- [x] Multi-pipeline compiled shader format (.specshadecomp)

## High Priority — Required for Real Rendering
- [x] Geometry stage ([Geometry], [MaxVertexCount(N)], [InputPrimitive(...)], [OutputPrimitive(...)])
- [x] Compute shaders ([Compute] attribute, [NumThreads(x,y,z)], Barrier(), MemoryBarrier())
- [x] Multiple render targets (struct with multiple [Target(N)] fields, index uniqueness validation)
- [x] Depth testing hints ([DepthWrite], [EarlyDepthStencil])
- [x] Array uniforms (`vec4[8] colors;` in cbuffers, `sampler2D[4] textures;`) — size on the type, not after the name. The engine can fill `vec4[N]` and `mat4[N]`; other element types are language-level only, because their D3D and GL strides differ.
- [x] Texture arrays (sampler2DArray type)

## Medium Priority — Competitive Rendering
- [ ] Storage buffers / SSBOs (read/write GPU buffers for compute)
- [ ] Image load/store (compute writes to textures)
- [ ] Tessellation stages ([Hull], [Domain] attributes)
- [ ] Interpolation qualifiers ([Flat], [Smooth], [NoPerspective] on varying fields)
- [ ] Precision qualifiers ([HighP], [MediumP], [LowP] for mobile)
- [ ] Push constants (Vulkan fast uniform path)
- [ ] Specialization constants (compile-time variants)
- [x] HLSL code generation (SM5, D3D11/D3D12 — vertex, fragment, geometry, compute)
- [ ] SPIR-V code generation

## Lower Priority — Advanced Features
- [ ] Subpass inputs (Vulkan render pass optimization)
- [ ] Mesh shaders ([Mesh], [Task] attributes — next-gen pipeline)
- [ ] Ray tracing stages ([RayGeneration], [ClosestHit], [Miss], [AnyHit])
- [ ] Wave/subgroup intrinsics (GPU SIMD: Wave.Sum, Wave.Broadcast, etc.)
- [ ] Atomics (Atomic.Add, Atomic.CompareExchange for compute sync)
- [ ] Shared workgroup memory ([Shared] qualifier for compute)
- [ ] Derivative functions (Math.Ddx, Math.Ddy for LOD/edge detection)
- [ ] Dual-source blending ([Target(0, Index(1))])

## Tooling
- [x] LSP server (diagnostics, completions — hover/definition/rename still open)
- [x] TextMate grammar (syntax highlighting)
- [ ] VS Code extension
- [x] Visual Studio VSIX extension (highlighting, item templates, bundled LSP)
- [x] CLI compiler tool (`ssc input.spectrashade -o output.specshadecomp`)
