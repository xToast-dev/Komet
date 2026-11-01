// Pragmatic for Vintage Story: the world the player explored, seen from a few thousand blocks up. The planet has the
// world's true size (its map width is the circumference, worldScale.x the radius in blocks); a camera hangs above and
// behind the player and looks over their land toward the curved horizon, the air glowing at its rim in the teal of
// temporal stability. Every chunk is a voxel: the world map's colour there (worldAt), lit on the sun's side of its edge.
// Land they have not seen lies under fog. The sun
// stands as the game's hour and season put it, the weather where the player stands fills the sky with clouds. Without a
// world map (none, or switched off by the server) there is no planet (planet is 0): only the sky. Komet adds the prelude
// (uniforms, uv, color, world()) - see docs/site/articles/styles.md.

float hash(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float noise(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p)
{
    float sum = 0.0, amp = 0.5;
    for (int i = 0; i < 5; i++)
    {
        sum += amp * noise(p);
        p = p * 2.03 + vec2(1.7, 9.2);
        amp *= 0.5;
    }
    return sum;
}

// One layer of stars: cells of `size`, a star in some of them, twinkling; warm and cold ones
vec3 stars(vec2 p, float size, float density, float seed)
{
    vec2 cell = floor(p / size), local = fract(p / size) - 0.5;
    float h = hash(cell + seed);
    if (h > density) return vec3(0.0);
    vec2 at = vec2(hash(cell + seed + 1.3), hash(cell + seed + 7.1)) - 0.5;
    float d = length(local - at * 0.8);
    float twinkle = 0.65 + 0.35 * sin(time * (1.0 + 3.0 * hash(cell + 3.3)) + h * 40.0);
    vec3 tint = mix(vec3(1.0, 0.86, 0.66), vec3(0.80, 0.90, 1.0), hash(cell + 9.9));
    return tint * smoothstep(0.08, 0.0, d) * twinkle * (0.35 + 1.1 * hash(cell + 5.5));
}

// Land the player has not explored lies under fog, as Cube World veils its map, in Vintage Story's colours: slow banks of
// stepped voxel puffs in the warm grey-beige of the game's mist and its parchment map, a fine paper grain in it, a little
// light of its own. c: the chunk
// (absolute), f: where in it. Returns the fog's colour; lit says how much the sun still reaches it
vec3 fog(vec2 c, vec2 f, out float lit)
{
    vec2 drift = vec2(time * 0.12, time * 0.05);
    float wide = fbm(c * 0.05 + drift), fine = fbm(c * 0.16 - drift * 1.6);
    float dense = smoothstep(0.12, 0.7, wide * 0.7 + fine * 0.45); // a closed deck, few gaps
    float puff = floor(dense * 4.0) / 4.0; // stepped like voxels
    float above = floor(smoothstep(0.12, 0.7, fbm((c - vec2(0.0, 1.0)) * 0.05 + drift) * 0.7 + fbm((c - vec2(0.0, 1.0)) * 0.16 - drift * 1.6) * 0.45) * 4.0) / 4.0;
    vec3 low = vec3(0.38, 0.32, 0.24), high = vec3(0.84, 0.75, 0.59);
    vec3 tint = mix(low, high, puff);
    tint *= 1.0 + 0.22 * step(f.y, 0.2) * step(above + 0.01, puff); // a puff's lit top edge where it rises over the next
    tint *= 1.0 - 0.25 * step(0.8, f.y) * step(puff + 0.01, above); // and its shadow where the next one rises over it
    lit = 0.5 + 0.4 * puff;
    float grain = hash(floor((c + f) * 8.0)); // the paper's grain, eight to a chunk
    return tint * (0.95 + 0.06 * hash(c) + 0.05 * grain);
}

void main()
{
    float aspect = resolution.x / resolution.y;
    vec2 p = (uv - 0.5) * vec2(aspect, 1.0);
    vec2 pan = camera.xy * vec2(0.32, 0.22);
    vec2 q = p / camera.z + pan;

    // a dusky sky: deep blue above, a violet-brown haze toward the world
    vec3 col = mix(vec3(0.010, 0.012, 0.030), vec3(0.060, 0.045, 0.070), smoothstep(1.3, -0.3, length(q - vec2(-0.6, -0.5))));
    col += stars(p + pan * 0.25, 0.022, 0.15, 1.0);
    col += stars(p + pan * 0.5, 0.045, 0.09, 7.0) * 1.3;

    // a shooting star every eleven seconds
    float s = mod(time, 11.0);
    if (s < 0.7)
    {
        vec2 head = vec2(0.75, 0.42) - vec2(0.9, 0.45) * (s / 0.7);
        vec2 dir = normalize(vec2(0.9, 0.45));
        vec2 rel = p - head;
        float along = dot(rel, dir), across = length(rel - dir * along);
        col += vec3(1.0, 0.92, 0.8) * smoothstep(0.004, 0.0, across) * smoothstep(0.12, 0.0, along) * step(0.0, along) * (1.0 - s / 0.7);
    }

    // the moon, high in the corner: drawn before the air, so the glow at the horizon lies over it; a soft rim, a faint halo
    vec2 m = q * 0.85 - vec2(0.60, 0.30) + pan * 0.4;
    float mr = length(m), face = smoothstep(0.046, 0.0435, mr);
    col += vec3(0.55, 0.55, 0.6) * exp(-max(mr - 0.045, 0.0) * 60.0) * 0.08; // its light in the sky around
    if (face > 0.0)
    {
        vec3 mn = vec3(m / 0.045, sqrt(max(0.0, 1.0 - mr * mr / 0.002025)));
        float crater = smoothstep(0.55, 0.45, fbm(m * 60.0));
        vec3 moonSun = normalize(vec3(0.75, 0.5, 0.42)); // the moon keeps its own light: a dark disc in the game's night looks wrong
        vec3 moon = vec3(0.78, 0.74, 0.66) * (0.75 + 0.25 * crater) * (0.05 + 0.95 * clamp(dot(mn, moonSun), 0.0, 1.0));
        col = mix(col, moon, face);
    }

    // the world: a sphere of radius 1 that is worldScale.x blocks; the camera over the player, all in the player's frame
    float blocks = max(worldScale.x, 1000.0), reach = max(worldScale.y, 512.0);
    vec3 up = vec3(0.0, 1.0, 0.0), east = vec3(1.0, 0.0, 0.0), north = vec3(0.0, 0.0, -1.0);
    float height = clamp(reach * 0.12, 320.0, 16000.0); // low enough to see the chunks, higher over a larger explored land
    float back = height * 2.0, aside = height * 0.6 + camera.x * height * 0.25; // behind the place the globe looks at, looking past it
    vec3 eye = normalize(up - north * (back / blocks)) * (1.0 + height / blocks);
    vec3 aim = normalize(up - east * (aside / blocks));
    vec3 forward = normalize(aim - eye), right = normalize(cross(forward, up)), lift = cross(right, forward);
    float lens = 2.0 * tan(radians(27.0)) / max(camera.z, 0.5);
    vec3 ray = normalize(forward + right * p.x * lens + lift * (p.y + camera.y * 0.1) * lens);
    float b = dot(eye, ray), c = dot(eye, eye) - 1.0, disc = b * b - c;
    float t = disc > 0.0 ? -b - sqrt(disc) : -1.0;
    bool hit = t > 0.0 && planet > 0.001;

    // the game's sun: overhead at noon, its height by the season
    float hour = worldPlace.w > 0.5 ? worldPlace.y : -0.6, season = worldPlace.w > 0.5 ? worldPlace.z : 0.2;
    // the camera looks where the player looks (worldYaw): the world's own east and north as the camera's frame sees them
    float cy = cos(worldYaw), sy = sin(worldYaw);
    vec3 worldEast = east * cy - north * sy, worldNorth = east * sy + north * cy;
    vec3 sun = normalize(up * cos(hour) * cos(season) - worldEast * sin(hour) * cos(season) + worldNorth * sin(season));
    vec3 sunlight = vec3(1.0, 0.93, 0.82);
    vec3 air = mix(vec3(0.42, 0.62, 0.86), vec3(0.32, 0.78, 0.68), 0.3); // blue sky with a breath of temporal teal
    float daylight = smoothstep(-0.15, 0.25, dot(up, sun));

    // the air: above the horizon it glows where the ray grazes the planet, more by day
    float graze = length(eye - ray * dot(eye, ray)); // the ray's closest approach to the centre
    float thin = height / blocks * 0.35 + 0.0004;
    float halo = exp(-max(graze - 1.0, 0.0) / thin) * step(0.0, -dot(eye, ray));
    col = mix(col, air * (0.25 + 0.75 * daylight), halo * 0.85 * planet);

    if (hit)
    {
        vec3 n = normalize(eye + ray * t);
        // where on the land: blocks east and south of the atlas' centre (the planet turns under the camera as
        // the player walks: the top is where they stand), and the chunk (one voxel) it falls in
        vec2 local = vec2(dot(n, east), -dot(n, north)) * blocks; // right and back as the camera sees them
        vec2 at = vec2(cy * local.x + sy * local.y, cy * local.y - sy * local.x) + worldScale.zw;
        vec2 chunk = floor((at + worldChunk.xy) / 32.0), f = fract((at + worldChunk.xy) / 32.0);
        vec2 middle = chunk * 32.0 + 16.0 - worldChunk.xy; // the chunk's centre, from the atlas' centre
        vec2 uvMap = 0.5 + middle / (2.0 * reach);
        vec4 seen = (uvMap.x > 0.0 && uvMap.x < 1.0 && uvMap.y > 0.0 && uvMap.y < 1.0) ? worldAt(uvMap) : vec4(0.0);
        float land = 1.0, fogLit;
        vec3 surface = fog(chunk + worldChunk.zw, f, fogLit); // unexplored: under fog
        float veiled = 1.0;
        if (seen.a > 0.5)
        {
            surface = seen.rgb * (0.94 + 0.08 * hash(chunk)); // each chunk a touch apart, as voxels are
            veiled = 0.0;
            // where the explored land ends, an ink line as on a drawn map, and a little of the fog over the last chunk
            float edge = 0.0, ink = 0.0;
            for (int k = 0; k < 4; k++)
            {
                vec2 step4 = vec2(k == 0 ? 1.0 : k == 1 ? -1.0 : 0.0, k == 2 ? 1.0 : k == 3 ? -1.0 : 0.0);
                vec2 nuv = 0.5 + (middle + step4 * 32.0) / (2.0 * reach);
                float open = 1.0 - step(0.5, worldAt(clamp(nuv, 0.0, 1.0)).a);
                float toward = dot(step4, f - 0.5) + 0.5; // 1 at the side facing that neighbour
                edge = max(edge, open);
                ink = max(ink, open * smoothstep(0.8, 0.95, toward));
            }
            veiled = edge * 0.3;
            surface = mix(surface, fog(chunk + worldChunk.zw, f, fogLit), veiled);
            surface = mix(surface, vec3(0.20, 0.15, 0.10), ink * 0.8);
            land = step(seen.b, max(seen.r, seen.g) + 0.06); // blue chunks are water: the sun glints on them
        }
        float bright = (1.0 - smoothstep(0.0, 0.08, f.x)) * 0.6 + (1.0 - smoothstep(0.0, 0.08, f.y));
        float dark = smoothstep(0.92, 1.0, f.x) * 0.6 + smoothstep(0.92, 1.0, f.y);
        surface *= 1.0 + (0.10 * bright - 0.18 * dark) * land * (1.0 - veiled);

        // the weather here: clouds of four chunks a block, thicker and darker as the game's weather is
        float cover = worldWeather.w > 0.5 ? worldWeather.x : 0.35, storm = worldWeather.w > 0.5 ? worldWeather.y : 0.0;
        vec2 drift = vec2(time * 3.0, 0.0);
        float low = 0.8 - 0.55 * cover, high = 1.0 - 0.45 * cover;
        float cloud = smoothstep(low, high, fbm(floor((at + worldChunk.xy + drift) / 128.0) * 0.12));
        float shade = smoothstep(low, high, fbm(floor((at + worldChunk.xy + drift + vec2(-96.0, 64.0)) / 128.0) * 0.12));
        surface *= 1.0 - 0.35 * shade * (1.0 - cloud);

        float ndl = dot(n, sun);
        float day = smoothstep(-0.12, 0.3, ndl);
        float lit = clamp(ndl * 0.8 + 0.2, 0.0, 1.0) * day;
        vec3 terrain = surface * (0.035 + 1.0 * lit) * sunlight + surface * vec3(0.05, 0.06, 0.09) * (1.0 - day); // moonlight
        terrain = mix(terrain, surface * (0.55 + 0.6 * lit * fogLit), veiled); // fog glows a little of its own
        cloud *= 1.0 - veiled; // over the fog no clouds: it is the cloud
        vec3 clouds = mix(vec3(0.92, 0.90, 0.86), vec3(0.42, 0.44, 0.48), storm) * (0.05 + 0.95 * lit);
        vec3 ground = mix(terrain, clouds, cloud * (0.55 + 0.3 * storm));
        vec3 glint = normalize(sun - ray);
        ground += sunlight * pow(max(dot(n, glint), 0.0), 90.0) * (1.0 - land) * (1.0 - cloud) * day * 0.5; // the sun on water

        // the air between: far land fades into it, toward the horizon most
        float haze = 1.0 - exp(-t * blocks / (height * 9.0));
        ground = mix(ground, air * (0.12 + 0.55 * daylight), haze * 0.75);
        col = mix(col, ground, planet);
    }

    col = col / (1.0 + col * 0.18); // soft highlights
    color = vec4(col, fade);
}
