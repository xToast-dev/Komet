// Distance haze: far terrain fades towards a colour, the sky stays as it is.
//
// Komet puts this in front of the file (do not declare these again):
//   #version 330 core
//   #define KOMET 1                  always
//   #define KOMET_VULKAN 1           only while Komet's Vulkan renderer draws
//   #define MYSHADERS_QUALITY n      from komet.json ("define", given to "pass:haze")
//   in vec2 uv;                      0..1, (0, 0) bottom left
//   out vec4 color;                  what you write replaces the pixel
//   uniform sampler2D scene;         the picture so far
//   uniform sampler2D depth;         the scene's depth, 0..1, 1 = sky
//   uniform vec2 resolution;         in pixels
//   uniform float time;              seconds
// Line numbers in errors are this file's.

uniform float myshadersHaze;  // komet.json slider "hazeStrength"
uniform float myshadersHour;  // from the optional code (code/): 0..24; never set without it

const vec3 dayHaze = vec3(0.72, 0.80, 0.90);
const vec3 nightHaze = vec3(0.06, 0.08, 0.14);

void main()
{
    vec3 c = texture(scene, uv).rgb;
    float d = texture(depth, uv).r;

    // depth is non-linear: almost everything far away sits above 0.99. 1.0 is the sky (nothing drawn).
    float far = smoothstep(0.990, 0.9995, d) * (1.0 - step(0.99999, d));

    vec3 tint = dayHaze;
#ifdef MYSHADERS_CODE
    // the code part sets MYSHADERS_CODE and myshadersHour: night haze is dark
    float day = smoothstep(5.0, 8.0, myshadersHour) * (1.0 - smoothstep(18.0, 21.0, myshadersHour));
    tint = mix(nightHaze, dayHaze, day);
#endif

    color = vec4(mix(c, tint, far * myshadersHaze), 1.0);
}
