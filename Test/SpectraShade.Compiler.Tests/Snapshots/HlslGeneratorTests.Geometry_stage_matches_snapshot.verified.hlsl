struct VertexOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
};

struct VertexInput
{
    float3 position;
    float3 normal;
};

struct FragmentInput
{
    float3 normal;
};

cbuffer Camera : register(b0)
{
    float4x4 viewProjection;
    float extrusion;
};

[maxvertexcount(3)]
void main(triangle VertexOutput vertices[3], uint PrimitiveID : SV_PrimitiveID, inout TriangleStream<VertexOutput> _stream)
{
    VertexOutput _out = (VertexOutput)0;
    for (int i = 0; (i < 3); i = (i + 1))
    {
        _out.position = (vertices[i].position + (float4(vertices[i].normal, 0.0) * extrusion));
        _out.normal = vertices[i].normal;
        _stream.Append(_out);
    }
    _stream.RestartStrip();
}
