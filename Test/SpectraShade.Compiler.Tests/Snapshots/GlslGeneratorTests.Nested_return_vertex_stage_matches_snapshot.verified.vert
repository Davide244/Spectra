#version 330 core

struct VertexInput
{
    vec3 position;
    vec2 uv;
};

struct VertexOutput
{
    vec4 position;
    vec2 uv;
};

struct FragmentInput
{
    vec2 uv;
};

layout(location = 0) in vec3 a_position;
layout(location = 1) in vec2 a_uv;

out vec2 v_uv;

// cbuffer Params
uniform mat4 viewProjection;
uniform float threshold;

VertexOutput BuildOutput(vec4 clipPosition, vec2 uv)
{
    VertexOutput _ss_output;
    _ss_output.position = clipPosition;
    _ss_output.uv = uv;
    return _ss_output;
}

void main()
{
    if ((a_position.x < threshold))
    {
        VertexOutput _ss_ret0 = BuildOutput((vec4(a_position, 1.0) * viewProjection), (vec2(1.0, 1.0) - a_uv));
        gl_Position = _ss_ret0.position;
        v_uv = _ss_ret0.uv;
        return;
    }
    VertexOutput _ss_output;
    _ss_output.position = (vec4(a_position, 1.0) * viewProjection);
    _ss_output.uv = a_uv;
    gl_Position = _ss_output.position;
    v_uv = _ss_output.uv;
    return;
}
