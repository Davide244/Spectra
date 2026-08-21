#version 330 core

struct VertexInput
{
    vec3 position;
    vec3 normal;
    vec2 uv;
};

struct VertexOutput
{
    vec4 position;
    vec3 normal;
    vec2 uv;
};

struct FragmentInput
{
    vec3 normal;
    vec2 uv;
};

in vec3 v_normal;
in vec2 v_uv;

out vec4 fragColor;

// cbuffer Camera
uniform mat4 viewProjection;

void main()
{
    fragColor = vec4(1, 1, 1, 1);
    return;
}
