struct VertexOutput
{
    float4 position : SV_Position;
    float3 normal : TEXCOORD0;
    float2 uv : TEXCOORD1;
};

struct VertexInput
{
    float3 position;
    float3 normal;
    float2 uv;
};

struct FragmentInput
{
    float3 normal;
    float2 uv;
};

cbuffer Camera : register(b0)
{
    float4x4 viewProjection;
};

float4 main(VertexOutput input) : SV_Target0
{
    return float4(1.0, 1.0, 1.0, 1.0);
}
