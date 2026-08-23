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

layout(location = 0) in vec3 a_position;
layout(location = 1) in vec3 a_normal;

out vec3 v_normal;

// cbuffer Camera
uniform mat4 viewProjection;
uniform float extrusion;

void main()
{
    VertexOutput _ss_output;
    _ss_output.position = (vec4(a_position, 1.0) * viewProjection);
    _ss_output.normal = a_normal;
    gl_Position = _ss_output.position;
    v_normal = _ss_output.normal;
    return;
}
