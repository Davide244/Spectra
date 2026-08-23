#version 330 core

layout(triangles) in;
layout(triangle_strip, max_vertices = 3) out;

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

in vec3 v_normal[];

out vec3 g_normal;

// cbuffer Camera
uniform mat4 viewProjection;
uniform float extrusion;

void main()
{
    for (int i = 0; (i < 3); i = (i + 1))
    {
        gl_Position = (gl_in[i].gl_Position + (vec4(v_normal[i], 0.0) * extrusion));
        g_normal = v_normal[i];
        EmitVertex();
    }
    EndPrimitive();
}
