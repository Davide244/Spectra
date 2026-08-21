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

layout(location = 0) in vec3 a_position;
layout(location = 1) in vec3 a_normal;
layout(location = 2) in vec2 a_uv;

out vec3 v_normal;
out vec2 v_uv;

// cbuffer Camera
uniform mat4 viewProjection;

void main()
{
    VertexOutput _ss_output;
    _ss_output.position = (vec4(a_position, 1) * viewProjection);
    _ss_output.normal = a_normal;
    _ss_output.uv = a_uv;
    gl_Position = _ss_output.position;
    v_normal = _ss_output.normal;
    v_uv = _ss_output.uv;
    return;
}
