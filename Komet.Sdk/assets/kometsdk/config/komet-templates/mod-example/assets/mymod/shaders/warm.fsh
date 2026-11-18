// A pass Komet draws over the finished picture. Komet puts uv, color, scene, depth, resolution and time in front of this
// file; the settings' uniforms are declared here.
uniform float mymodWarmth;

void main()
{
    vec3 c = texture(scene, uv).rgb;
#ifdef KOMET_VULKAN
    // only under Komet's Vulkan renderer (an example of a path of its own)
#endif
    vec3 warm = c * vec3(1.35, 1.1, 0.7);
    color = vec4(mix(c, warm, mymodWarmth), 1.0);
}
