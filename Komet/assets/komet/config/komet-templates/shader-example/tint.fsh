// Komet's example pass: the left half of the picture in red, the sky at half strength (depth 1 = sky).
// Komet puts the prelude in front: uv, color, scene, depth, resolution, time. exampleStrength is the pack's slider.
uniform float exampleStrength;

void main()
{
    vec3 c = texture(scene, uv).rgb;
    float sky = texture(depth, uv).r >= 1.0 ? 0.5 : 1.0;
    vec3 red = vec3(dot(c, vec3(0.3, 0.59, 0.11)) * 1.6, 0.0, 0.0);
    color = vec4(mix(c, red, exampleStrength * step(uv.x, 0.5) * sky), 1.0);
}
