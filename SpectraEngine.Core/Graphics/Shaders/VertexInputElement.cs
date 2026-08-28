namespace SpectraEngine.Core.Graphics.Shaders;

/// <summary>
/// How often a vertex input advances: once per vertex, or once per instance.
/// </summary>
/// <remarks>
/// <b>Neither backend expresses this in shader text</b>, which is exactly why it
/// has to be reported. GLSL sets it with <c>glVertexAttribDivisor</c> and D3D
/// with <c>InputSlotClass</c>/<c>InstanceDataStepRate</c> on the input element,
/// so a shader declaring a per-instance input and a renderer building a
/// per-vertex layout for it compile, link and draw. What you get is every
/// instance reading vertex 0's copy of the data, which is a picture, not an
/// error.
/// </remarks>
public enum VertexInputRate
{
    /// <summary>Advances once per vertex. The default, and what every existing shader wants.</summary>
    PerVertex,

    /// <summary>Advances once per instance, with a step rate of 1.</summary>
    PerInstance,
}

/// <summary>
/// One vertex input a compiled shader declares: what it is called, where it
/// lives, how much room it takes and how often it advances.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a contract that used to be kept by hand.</b> The HLSL generator
/// emits vertex inputs as <c>TEXCOORD{Location}</c> and
/// <c>D3D11Mesh.CreateInputLayout</c> builds elements that match, with nothing
/// but a comment on each side connecting them. That agreement survived because
/// there was one layout in the engine and it never changed. Per-instance inputs
/// break it: they add a second buffer slot, a step rate, and types that occupy
/// more than one location, and none of those can be guessed from a mesh's
/// <see cref="VertexAttribute"/> list.
/// </para>
/// <para>
/// <b><see cref="LocationSpan"/> is the part that is easy to get wrong.</b> A
/// <c>mat4</c> is one field in the source and <em>four</em> consecutive
/// locations in both targets: GLSL assigns the next three implicitly, and HLSL
/// gives the four rows <c>TEXCOORDn</c> through <c>TEXCOORDn+3</c>. So a layout
/// built one-element-per-field binds a quarter of the matrix and leaves the next
/// three attributes reading whatever the previous draw left, and the following
/// field silently overlaps it. The span is carried rather than recomputed
/// because the rule belongs to the language, and a renderer re-deriving it from
/// a type name is a second copy of it.
/// </para>
/// </remarks>
/// <param name="Name">The field's name in the shader's vertex input struct.</param>
/// <param name="Location">
/// The first location it occupies, i.e. the GLSL <c>layout(location = N)</c> and
/// the HLSL <c>TEXCOORD</c> semantic index.
/// </param>
/// <param name="LocationSpan">
/// How many consecutive locations it occupies: 1 for scalars and vectors, 4 for
/// a <c>mat4</c>, 3 for a <c>mat3</c>, 2 for a <c>mat2</c>.
/// </param>
/// <param name="ComponentCount">
/// Floats per location, which is what picks the element's format. A
/// <c>mat4</c> reports 4, not 16: it is four four-component rows.
/// </param>
/// <param name="Rate">Per vertex or per instance.</param>
public readonly record struct VertexInputElement(
    string Name,
    uint Location,
    uint LocationSpan,
    uint ComponentCount,
    VertexInputRate Rate)
{
    /// <summary>
    /// The location just past this element, i.e. the first one a later field may
    /// use. Named rather than open-coded because <c>Location + LocationSpan</c>
    /// written at each call site is where an off-by-one lands.
    /// </summary>
    public uint LocationEnd => Location + LocationSpan;

    /// <summary>Whether this element and <paramref name="other"/> claim any location in common.</summary>
    public bool Overlaps(in VertexInputElement other) =>
        Location < other.LocationEnd && other.Location < LocationEnd;
}
