#version 330 core

struct VertexInput
{
    vec3 position;
    vec3 normal;
};

struct VertexOutput
{
    vec4 position;
    vec3 normal;
};

struct FragmentInput
{
    vec3 normal;
};

in vec3 g_normal;

out vec4 fragColor;

// cbuffer Camera
uniform mat4 viewProjection;
uniform float extrusion;

void main()
{
    fragColor = vec4(g_normal, 1.0);
    return;
}
