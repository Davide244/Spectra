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

in vec2 v_uv;

out vec4 fragColor;

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
    if ((v_uv.x > threshold))
    {
        if ((v_uv.y > threshold))
        {
            fragColor = vec4(1, 0, 0, 1);
            return;
        }
        fragColor = vec4(0, 1, 0, 1);
        return;
    }
    fragColor = vec4(0, 0, 1, 1);
    return;
}
