struct VertexInput
{
    float3 position : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float2 uv : TEXCOORD2;
    float4x4 model : TEXCOORD3;
    float4 tint : TEXCOORD7;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
    float2 uv : TEXCOORD1;
    float4 tint : TEXCOORD2;
};

struct FragmentInput
{
    float3 normal;
    float2 uv;
    float4 tint;
};

cbuffer Camera : register(b0)
{
    float4x4 viewProjection;
};

VertexOutput main(VertexInput input)
{
    VertexOutput output = (VertexOutput)0;
    output.position = mul(mul(viewProjection, input.model), float4(input.position, 1.0));
    output.normal = input.normal;
    output.uv = input.uv;
    output.tint = input.tint;
    return output;
}
