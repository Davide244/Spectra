using SpectraEngine.Core.Graphics.Shaders;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Core.Graphics;

/// <summary>
/// One attribute of a vertex layout: which location it feeds, how many floats
/// it carries, which buffer it comes from and how often it advances.
/// </summary>
/// <remarks>
/// <b>One attribute per LOCATION, never per shader field.</b> A <c>mat4</c> is a
/// single field in a shader and four entries here, because that is what both
/// APIs want: GL binds an attribute pointer per location and D3D an input
/// element per semantic index. <see cref="FromShaderInputs"/> is what performs
/// that expansion, from the signature the compiler now reports, so no caller
/// has to know the rule.
/// </remarks>
public readonly struct VertexAttribute
{
    /// <summary>The per-vertex buffer's slot. The mesh's own vertices.</summary>
    public const uint VertexSlot = 0;

    /// <summary>The per-instance buffer's slot.</summary>
    public const uint InstanceSlot = 1;

    public uint Location { get; }
    public uint ComponentCount { get; }

    /// <summary>
    /// Which bound buffer this attribute reads from:
    /// <see cref="VertexSlot"/> or <see cref="InstanceSlot"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="InputRate"/> on purpose. The rate is what the
    /// hardware does between elements; the slot is which buffer they come from.
    /// They travel together in every layout the engine builds, but conflating
    /// them into one flag would make a per-vertex attribute in a second buffer
    /// inexpressible, and that is an ordinary thing to want.
    /// </remarks>
    public uint InputSlot { get; }

    /// <summary>Whether this attribute advances per vertex or per instance.</summary>
    public VertexInputRate InputRate { get; }

    public VertexAttribute(
        uint location,
        uint componentCount,
        uint inputSlot = VertexSlot,
        VertexInputRate inputRate = VertexInputRate.PerVertex)
    {
        Location = location;
        ComponentCount = componentCount;
        InputSlot = inputSlot;
        InputRate = inputRate;
    }

    private static readonly VertexAttribute[] _standardLayout =
    [
        new(location: 0, componentCount: 3),
        new(location: 1, componentCount: 3),
        new(location: 2, componentCount: 2),
    ];

    /// <summary>
    /// The engine's standard interleaved vertex layout (8 floats): position (3),
    /// normal (3), uv (2). Matches <c>Primitives.Cube</c> and
    /// <c>CsgWorld.BuildMesh</c> output.
    /// </summary>
    public static ReadOnlySpan<VertexAttribute> StandardLayout => _standardLayout;

    private static readonly VertexAttribute[] _standardInstanceLayout =
    [
        new(location: 3, componentCount: 4, InstanceSlot, VertexInputRate.PerInstance),
        new(location: 4, componentCount: 4, InstanceSlot, VertexInputRate.PerInstance),
        new(location: 5, componentCount: 4, InstanceSlot, VertexInputRate.PerInstance),
        new(location: 6, componentCount: 4, InstanceSlot, VertexInputRate.PerInstance),
    ];

    /// <summary>
    /// The engine's standard per-instance layout: one <c>mat4</c> world matrix
    /// at locations 3 through 6, sixteen floats per instance.
    /// </summary>
    /// <remarks>
    /// Locations 3 to 6 rather than 0 to 3 because they sit past
    /// <see cref="StandardLayout"/>, which is what lets one shader declare both
    /// and one input layout describe both.
    /// </remarks>
    public static ReadOnlySpan<VertexAttribute> StandardInstanceLayout => _standardInstanceLayout;

    /// <summary>Floats per instance in <see cref="StandardInstanceLayout"/>.</summary>
    public const int StandardInstanceFloats = 16;

    /// <summary>
    /// Expands a compiled shader's declared vertex inputs into one attribute per
    /// location, assigning per-vertex inputs to <see cref="VertexSlot"/> and
    /// per-instance inputs to <see cref="InstanceSlot"/>.
    /// </summary>
    /// <remarks>
    /// <b>The bridge from what a shader says to what a backend binds.</b> The
    /// expansion rule (a <c>mat4</c> is four locations of four components) lives
    /// here and in the compiler's resolver, and nowhere else; a backend
    /// re-deriving it from a type name would be the third copy, which is how the
    /// two stop agreeing.
    /// </remarks>
    /// <summary>
    /// The subset of <paramref name="attributes"/> bound to
    /// <paramref name="slot"/>, in order.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="FromShaderInputs"/>: a shader reports one
    /// layout covering both buffers, and the two buffers are created from its
    /// two halves. <see cref="Renderer.CreateInstanceBuffer"/> refuses a layout
    /// carrying anything but instance-slot attributes, so this is how a caller
    /// gets one from a shader's full signature.
    /// </remarks>
    public static VertexAttribute[] ForSlot(ReadOnlySpan<VertexAttribute> attributes, uint slot)
    {
        int count = 0;
        for (int i = 0; i < attributes.Length; i++)
            if (attributes[i].InputSlot == slot)
                count++;

        var result = new VertexAttribute[count];
        int next = 0;
        for (int i = 0; i < attributes.Length; i++)
            if (attributes[i].InputSlot == slot)
                result[next++] = attributes[i];

        return result;
    }

    public static VertexAttribute[] FromShaderInputs(IReadOnlyList<VertexInputElement> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        int total = 0;
        for (int i = 0; i < inputs.Count; i++)
            total += (int)inputs[i].LocationSpan;

        var attributes = new VertexAttribute[total];
        int next = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            VertexInputElement input = inputs[i];
            bool perInstance = input.Rate == VertexInputRate.PerInstance;

            for (uint row = 0; row < input.LocationSpan; row++)
            {
                attributes[next++] = new VertexAttribute(
                    input.Location + row,
                    input.ComponentCount,
                    perInstance ? InstanceSlot : VertexSlot,
                    input.Rate);
            }
        }

        return attributes;
    }
}
