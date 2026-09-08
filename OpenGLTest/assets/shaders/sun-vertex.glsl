#version 330 core
// The sun quad is built in world space around the sun direction, not around the camera:
// uViewProj is the view matrix with its translation removed times the projection, so the
// quad sits at infinity and never parallaxes as the player moves.
layout (location = 0) in vec3 vPosition;
layout (location = 2) in vec2 vTexCoord;
layout (location = 7) in uint vLayer; // array-texture slice (Vertex.Layer)

uniform mat4 uViewProj;
uniform vec3 uSunDir;
uniform float uHalfSize;

out vec2 fTexCoord;
flat out uint fLayer;

void main()
{
    // A camera-independent basis around the sun direction, so the disc never rolls with
    // the view. The sun's Z tilt keeps uSunDir clear of +Y, so the cross is well defined.
    vec3 right = normalize(cross(uSunDir, vec3(0.0, 1.0, 0.0)));
    vec3 up = cross(right, uSunDir);

    vec3 world = uSunDir * 100.0 + (right * vPosition.x + up * vPosition.y) * uHalfSize;

    gl_Position = uViewProj * vec4(world, 1.0);
    fTexCoord = vTexCoord;
    fLayer = vLayer;
}
