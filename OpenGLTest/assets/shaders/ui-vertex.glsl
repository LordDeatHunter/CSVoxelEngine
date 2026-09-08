#version 460 core
layout (location = 0) in vec3 vPos;
layout (location = 1) in vec4 vColor;
layout (location = 2) in vec2 vTex;
layout (location = 7) in uint vLayer; // array-texture slice (Vertex.Layer)

uniform mat4 uModel;
uniform mat4 uProjection;

out vec4 fColor;
out vec2 fTexCoord;
flat out uint fLayer;

void main()
{
    gl_Position = uProjection * uModel * vec4(vPos, 1.0);
    fColor = vColor;
    fTexCoord = vTex;
    fLayer = vLayer;
}
