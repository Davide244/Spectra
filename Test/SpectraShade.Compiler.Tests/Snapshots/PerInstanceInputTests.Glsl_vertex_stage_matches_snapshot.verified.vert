#version 330 core

struct VertexInput
{
    vec3 position;
    vec3 normal;
    vec2 uv;
    mat4 model;
    vec4 tint;
};

struct VertexOutput
{
    vec4 position;
    vec3 normal;
    vec2 uv;
    vec4 tint;
};

struct FragmentInput
{
    vec3 normal;
    vec2 uv;
    vec4 tint;
};

layout(location = 0) in vec3 a_position;
layout(location = 1) in vec3 a_normal;
layout(location = 2) in vec2 a_uv;
layout(location = 3) in mat4 a_model;
layout(location = 7) in vec4 a_tint;

out vec3 v_normal;
out vec2 v_uv;
out vec4 v_tint;

// cbuffer Camera
uniform mat4 viewProjection;

void main()
{
    VertexOutput _ss_output;
    _ss_output.position = ((viewProjection * a_model) * vec4(a_position, 1.0));
    _ss_output.normal = a_normal;
    _ss_output.uv = a_uv;
    _ss_output.tint = a_tint;
    gl_Position = _ss_output.position;
    v_normal = _ss_output.normal;
    v_uv = _ss_output.uv;
    v_tint = _ss_output.tint;
    return;
}
