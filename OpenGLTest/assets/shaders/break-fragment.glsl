#version 330 core

uniform float uProgress;

in vec3 vLocal;
out vec4 FragColor;

void main() {
    // Distance from face center (0) to face edge (1), regardless of face orientation:
    // the component with the largest offset from the cube center is the face normal
    // axis; the remaining two span the face.
    vec3 a = abs(vLocal - 0.5) * 2.0;
    float r;
    if (a.x >= a.y && a.x >= a.z)      r = max(a.y, a.z);
    else if (a.y >= a.x && a.y >= a.z) r = max(a.x, a.z);
    else                               r = max(a.x, a.y);

    // Dark fill grows outward from the face center as progress goes 0 -> 1
    if (r > uProgress) discard;

    // Bright leading edge, dark fill behind it
    float edge = smoothstep(uProgress - 0.12, uProgress, r);
    vec3 color = mix(vec3(0.05), vec3(1.0, 0.85, 0.35), edge);
    float alpha = mix(0.45, 0.85, edge);
    FragColor = vec4(color, alpha);
}
