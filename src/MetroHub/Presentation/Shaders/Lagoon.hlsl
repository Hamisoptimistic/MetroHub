// Lagoon (Deep Reef) for MetroHub
// Ported from ittybitty.dev Lagoon by Blake Cyze (MIT)
// https://ittybitty.dev/shaders/lagoon
//
// Above view only. Deep Reef preset:
//   scale 0.7, depth 1.0, shallow #2ee6c8, deep #0a2f8f,
//   speed 0.6, refraction 0.6, caustics 0.8, foam 0.6, sparkle 0.7.
//
// Registers:
//   s0 = unused (the live-shader host has no wallpaper input)
//   c0 = FrameState: X=Time, Y=AspectRatio, Z=DeltaTime, W=0
//   c1 = EffectParams: X=Speed, Y=Scale, Z=Depth, W=Refraction
//   c2 = MouseState: unused (kept for the shared driver)
//   c3 = LightParams: X=Caustics, Y=Foam, Z=Sparkle, W=WakeStrength
//   c4..c19 = Wake[16]: eight pointer-wake segments, two registers each.
//       even = (fromX, fromY, toX, toY) in world units
//       odd  = (age, amplitude, 0, 0)
//
// The WebGL original steps a 256x256 height-field simulation. WPF ShaderEffect
// has no persistent render targets, so the driver records the pointer's recent
// path as short segments and this shader turns each segment into a travelling
// ring. That keeps the V-shaped hover wake and one-shot click rings without a
// simulation pass.

sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);
float4 EffectParams : register(c1);
float4 LightParams : register(c3);
float4 Drops[16] : register(c4);

static const float UNITS = 11.0;
static const float3 SAND = float3(0.8, 0.64, 0.4);
static const float3 WHITE = float3(1.0, 0.99, 0.95);
static const float3 SHALLOW = float3(0.0273, 0.7913, 0.5776); // #2ee6c8 linear
static const float3 DEEP = float3(0.0030, 0.0284, 0.2747);    // #0a2f8f linear

float hash12(float2 p)
{
    float3 q = frac(p.xyx * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

float2 hash22(float2 p)
{
    float3 q = frac(p.xyx * float3(0.1031, 0.103, 0.0973));
    q += dot(q, q.yzx + 33.33);
    return frac((q.xx + q.yz) * q.zy);
}

float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash12(i), hash12(i + float2(1.0, 0.0)), u.x),
                lerp(hash12(i + float2(0.0, 1.0)), hash12(i + float2(1.0, 1.0)), u.x), u.y);
}

float fbm(float2 p)
{
    float v = 0.0;
    float a = 0.5;
    [unroll]
    for (int i = 0; i < 4; ++i)
    {
        v += a * noise(p);
        // GLSL mat2(1.6, 1.2, -1.2, 1.6) * p, written out to avoid matrix ordering.
        p = float2(1.6 * p.x - 1.2 * p.y, 1.2 * p.x + 1.6 * p.y) + 5.1;
        a *= 0.5;
    }
    return v;
}

// Worley cells that drift over time. Only the gap to the second nearest
// point is used, so the cell id and nearest distance are dropped.
float cellEdge(float2 p, float t)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float d1 = 8.0;
    float d2 = 8.0;

    [unroll]
    for (int y = -1; y <= 1; ++y)
    {
        [unroll]
        for (int x = -1; x <= 1; ++x)
        {
            float2 g = float2(x, y);
            float2 o = 0.5 + 0.42 * sin(t + 6.2831 * hash22(i + g));
            float d = length(g + o - f);
            if (d < d1)
            {
                d2 = d1;
                d1 = d;
            }
            else if (d < d2)
            {
                d2 = d;
            }
        }
    }

    return d2 - d1;
}

// Step a 0..1 value into n flat bands with antialiased edges.
float toon(float x, float n)
{
    float y = x * n;
    float w = fwidth(y) * 0.8 + 0.02;
    return (floor(y) + smoothstep(0.5 - w, 0.5 + w, frac(y))) / n;
}

// A four-pointed star centred on q (in cell units), radius r.
float star(float2 q, float r)
{
    float2 a = abs(q) / r;
    float core = max(0.0, 1.0 - length(q) / (r * 0.3));
    float rays = exp(-a.x * 12.0) * max(0.0, 1.0 - a.y)
               + exp(-a.y * 12.0) * max(0.0, 1.0 - a.x);
    return clamp(core * core * 1.5 + rays * rays, 0.0, 1.0);
}

// Value noise and its slope, worked out exactly: one lookup gives both.
float3 noised(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float2 du = 6.0 * f * (1.0 - f);
    float a = hash12(i);
    float b = hash12(i + float2(1.0, 0.0));
    float c = hash12(i + float2(0.0, 1.0));
    float d = hash12(i + float2(1.0, 1.0));
    float k = a - b - c + d;
    return float3(
        a + (b - a) * u.x + (c - a) * u.y + k * u.x * u.y,
        du * (float2(b - a, c - a) + k * u.yx));
}

// The water at rest: two layers of soft noise drifting across each other.
float2 calm(float2 p, float t)
{
    float2 a = noised(p * 0.9 + float2(t * 0.22, t * 0.07)).yz * 0.9;
    float2 b = noised(p * 1.7 - float2(t * 0.1, -t * 0.2) + 4.0).yz * 0.85;
    return (a + b) * 0.12;
}

// The pointer's wake: each segment is a short piece of the pointer's recent
// path. Distance to the segment gives a capsule-shaped wave front, so a fast
// stroke leaves the V-shaped trail the original simulation produces. A click
// is a zero-length segment and sends out a single ring.
float2 wake(float2 p)
{
    float2 slope = float2(0.0, 0.0);

    [unroll]
    for (int i = 0; i < 8; ++i)
    {
        float4 seg = Drops[i * 2];
        float4 state = Drops[i * 2 + 1];
        float amp = state.y;

        [branch]
        if (amp > 0.001)
        {
            float2 a = seg.xy;
            float2 ba = seg.zw - a;
            float2 pa = p - a;
            float h = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-5));
            float2 q = pa - ba * h;
            float r = length(q);

            // Wavelength ~0.5 world units, wave speed ~1.4 units/s.
            float phase = r * 13.0 - state.x * 18.0;
            float fade = amp * exp(-r * 1.6);
            slope += (q / max(r, 1e-4)) * cos(phase) * fade * 0.38;
        }
    }

    return slope;
}

// How deep the lagoon floor lies at p: below 0 is a sandbar above water.
float bed(float2 p)
{
    return (fbm(p * 0.16 + float2(3.1, 7.7)) - 0.34) * 2.6;
}

// The lagoon floor at depth d: rippled sand, and weed in the deeps.
float3 seabed(float2 p, float d)
{
    float3 c = SAND;

    float r = sin(dot(p, float2(0.35, 0.94)) * 7.0 + noise(p * 0.9) * 6.0);
    c *= 0.84 + 0.2 * smoothstep(-0.3, 0.7, r);

    c = lerp(c, float3(0.62, 0.62, 0.42), smoothstep(0.3, 0.9, d) * 0.5);

    float weed = smoothstep(0.6, 0.72, noise(p * 0.9 + 11.0)) * smoothstep(0.35, 0.7, d);
    c = lerp(c, float3(0.2, 0.42, 0.22), weed * 0.8);

    return c;
}

// Caustics: the edges of drifting cells, cut at three widths to give a
// warm rim, a white core and a cool edge.
float3 caustics(float2 p, float t)
{
    float e = cellEdge(p, t * 0.8);
    return float3(
        1.0 - smoothstep(0.0, 0.11, e),
        1.0 - smoothstep(0.0, 0.085, e),
        1.0 - smoothstep(0.0, 0.07, e));
}

// Sparkles: in a grid of cells, one star each where the sun catches the
// surface at the cell's own point, twinkling in and out.
float sparkle(
    float2 uv,
    float t,
    float3 h,
    float aspect,
    float scale,
    float sparkleAmount,
    float2 rippleSlope)
{
    // The 1080 reference cancels out: the grid is uv / 0.045 either way.
    float2 px = float2(uv.x, 1.0 - uv.y) * 1080.0;
    float2 grid = px / (1080.0 * 0.045);
    float2 cell = floor(grid);

    [branch]
    if (hash12(cell + 1.3) < 1.0 - sparkleAmount * 0.8)
    {
        return 0.0;
    }

    // Centred well inside the cell, so no star is cut off at its edge.
    float2 at = 0.4 + 0.2 * hash22(cell);
    float2 cpx = (cell + at) * 1080.0 * 0.045;
    float2 cp = (cpx / 1080.0 - 0.5) * float2(aspect, 1.0) * (UNITS / scale);
    float2 cg = calm(cp, t) + rippleSlope;

    float catchLight = smoothstep(
        0.6,
        0.9,
        pow(max(dot(normalize(float3(-cg.x, 1.0, -cg.y)), h), 0.0), 60.0));

    float twinkle = pow(max(0.0, sin(t * (2.0 + 3.0 * hash12(cell + 7.0)) + 6.28 * hash12(cell))), 3.0);
    return star(frac(grid) - at, 0.38 + 0.08 * hash12(cell + 4.0)) * catchLight * twinkle;
}

float3 linearToSrgb(float3 c)
{
    c = max(c, 0.0);
    return lerp(c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - 0.055, step(0.0031308, c));
}

float4 above(
    float2 uv,
    float t,
    float aspect,
    float scale,
    float depth,
    float refraction,
    float causticsAmount,
    float foamAmount,
    float sparkleAmount,
    float wakeStrength)
{
    float2 p = (float2(uv.x, 1.0 - uv.y) - 0.5) * float2(aspect, 1.0) * (UNITS / scale);

    float3 sunDir = normalize(float3(0.35, 1.0, 0.25));
    float3 h = normalize(sunDir + float3(0.0, 1.0, 0.0));

    float2 rip = wake(p) * wakeStrength;
    float2 g = calm(p, t) + rip;
    float3 n = normalize(float3(-g.x, 1.0, -g.y));

    // The surface bends the floor. This is the pixel-space bend from the
    // original, converted to world units so it is resolution independent.
    float2 bend = -g * refraction * 0.05 * (UNITS / scale);
    float2 pr = p - bend;
    float d = bed(pr);
    float3 floorColour = seabed(pr, d);
    float water = max(d, 0.0) * depth * 1.9;

    // Sandbars above the water: dry sand, darker where the waves wet it,
    // and a line of surf that laps up the beach and back.
    if (d < 0.0)
    {
        float3 sand = floorColour * lerp(1.0, 0.72, smoothstep(-0.14, 0.0, d + 0.02 * sin(t * 1.5 + p.x)));
        float lap = -0.06 - 0.05 * (0.5 + 0.5 * sin(t * 1.3 + dot(p, float2(0.4, 0.3))));
        float surf = smoothstep(lap - 0.012, lap, d)
                   * (1.0 - smoothstep(lap + 0.02, lap + 0.035, d))
                   * foamAmount;
        return float4(lerp(sand, WHITE, surf * 0.95), 1.0);
    }

    // Light: caustics on the floor, bent by the surface; crisp highlights
    // where it tilts towards the sun; star glints.
    float clear = exp(-water * 2.0);
    float3 web = caustics(p * 0.8 + g * 2.5, t) * causticsAmount * lerp(0.3, 1.0, clear) * 0.8;
    float glare = smoothstep(0.85, 0.9, pow(max(dot(n, h), 0.0), 240.0)) * 0.4;
    float glint = sparkle(uv, t, h, aspect, scale, sparkleAmount, rip) * 1.8;
    float lit = clamp(dot(rip, float2(-0.6, -0.8)) * 1.5, -1.0, 1.0);

    // How much of the floor's light makes it back up, stepped into shelves:
    // clear over the sand, turquoise, then deep blue.
    float k = lerp(1.0 - clear, 1.0 - toon(clamp(clear, 0.001, 1.0), 4.0), 0.7);
    float3 hue = SHALLOW / max(max(SHALLOW.r, max(SHALLOW.g, SHALLOW.b)), 1e-3);
    float3 through = (floorColour + web) * lerp(float3(1.0, 1.0, 1.0), hue, 0.3 + 0.7 * smoothstep(0.0, 0.5, k));
    float3 body = lerp(SHALLOW, DEEP, smoothstep(0.25, 1.0, k));
    float3 c = lerp(through, body, 0.05 + 0.6 * k * k + 0.2 * k) * (1.0 + 0.16 * lit) + WHITE * glare;

    // Foam: noise cut off lower towards the sandbars, so it thickens there,
    // plus rings that roll in over the shallows.
    float edge = 1.0 - smoothstep(0.0, 0.3, water);
    float cut = lerp(0.85, 0.5, edge * foamAmount);
    float froth = smoothstep(cut, cut + 0.02, noise(p * 2.6 + g * 4.0 + float2(t * 0.2, -t * 0.12)));
    float ring = abs(frac(water * 8.0 - t * 0.35) - 0.5);
    float rings = (1.0 - smoothstep(0.08, 0.12, ring)) * (1.0 - smoothstep(0.12, 0.3, water));
    c = lerp(c, WHITE, clamp((froth * edge + rings) * min(foamAmount * 1.6, 1.0), 0.0, 1.0) * 0.95);

    return float4(c + WHITE * glint, 1.0);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 0.6;
    float scale = EffectParams.y > 0.0 ? EffectParams.y : 0.7;
    float depth = EffectParams.z >= 0.0 ? EffectParams.z : 1.0;
    float refraction = EffectParams.w > 0.0 ? EffectParams.w : 0.6;

    float causticsAmount = LightParams.x > 0.0 ? LightParams.x : 0.8;
    float foamAmount = LightParams.y > 0.0 ? LightParams.y : 0.6;
    float sparkleAmount = LightParams.z > 0.0 ? LightParams.z : 0.7;
    float wakeStrength = LightParams.w > 0.0 ? LightParams.w : 1.0;

    float t = FrameState.x * speed;

    float4 o = above(uv, t, aspect, scale, depth, refraction,
                     causticsAmount, foamAmount, sparkleAmount,
                     wakeStrength);

    // A painter's palette: a touch more saturated than life.
    float lum = dot(o.rgb, float3(0.2126, 0.7152, 0.0722));
    float3 c = max(lerp(float3(lum, lum, lum), o.rgb, 1.2), 0.0);
    c = linearToSrgb(clamp(c, 0.0, 1.2));
    c += (hash12(uv * 1080.0) - 0.5) / 255.0;

    return float4(clamp(c, 0.0, 1.0), 1.0);
}
