// Colour grading: exposure, saturation and, at quality "fine", a light sharpen.
// The prelude Komet puts in front is listed in haze.fsh.

uniform float myshadersExposure;    // komet.json slider, in stops
uniform float myshadersSaturation;  // komet.json slider, 1 = unchanged

// A define from komet.json may be missing (pack switched off, older Komet): give it a default
#ifndef MYSHADERS_QUALITY
#define MYSHADERS_QUALITY 1
#endif

vec3 sharpened(vec3 c)
{
    vec2 px = 1.0 / resolution;
    vec3 around = texture(scene, uv + vec2(px.x, 0.0)).rgb + texture(scene, uv - vec2(px.x, 0.0)).rgb
                + texture(scene, uv + vec2(0.0, px.y)).rgb + texture(scene, uv - vec2(0.0, px.y)).rgb;
    return c + (c - around * 0.25) * 0.35;
}

void main()
{
    vec3 c = texture(scene, uv).rgb;
#if MYSHADERS_QUALITY >= 1
    c = sharpened(c);
#endif

    c *= exp2(myshadersExposure);
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = mix(vec3(luma), c, myshadersSaturation);

#ifdef KOMET_VULKAN
    // Only under Komet's Vulkan renderer. The picture, uv and depth are the same as on OpenGL, so most mods need
    // nothing here; use it for work that pays off only there, or to work around a difference you found.
#endif

    color = vec4(max(c, 0.0), 1.0);
}
