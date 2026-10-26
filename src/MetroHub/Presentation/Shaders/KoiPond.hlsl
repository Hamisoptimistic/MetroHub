// KoiPond (Japanese Garden Koi Pond) for MetroHub
// Based on ittybitty.dev Koi Pond by Blake Cyze
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Speed (0.7), Y=FishCount (4.0), Z=Brightness (1.0), W=LilyPads (5.0)
float4 MouseState : register(c2);   // X=MouseX, Y=MouseY, Z=LeftDown, W=0

float hash11(float p)
{
    p = frac(p * 0.1031);
    p *= p + 33.33;
    p *= p + p;
    return frac(p);
}

float hash12(float2 p)
{
    float3 q = frac(p.xyx * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash12(i), hash12(i + float2(1.0, 0.0)), u.x),
                lerp(hash12(i + float2(0.0, 1.0)), hash12(i + float2(1.0, 1.0)), u.x), u.y);
}

float bodyWidth(float u, float w)
{
    float x = saturate(u / 0.8);
    return w * lerp(0.55, 1.0, smoothstep(0.0, 0.3, x))
             * lerp(1.0, 0.18, pow(smoothstep(0.3, 1.0, x), 0.8))
             * step(u, 0.82);
}

float3 linearToSrgb(float3 c)
{
    c = max(c, 0.0);
    return lerp(c * 12.92, 1.055 * pow(c, (1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

// Procedural Koi Spine Kinematics
float2 getSpinePoint(int i, int k, float t, float aspect, float len, out float2 outHead, out float2 outDir)
{
    float fi = float(i);
    float w1 = 0.18 + 0.04 * sin(fi * 1.7 + 0.2);
    float w2 = 0.24 + 0.03 * cos(fi * 2.3 + 0.5);
    float p0 = fi * 1.57;

    // Head trajectory looping smoothly through the pond
    float hx = aspect * (0.5 + 0.36 * sin(t * w1 + p0));
    float hy = 0.5 + 0.36 * sin(t * w2 * 1.3 + p0 * 1.2);
    outHead = float2(hx, hy);

    // Tangent derivative vector
    float dx = aspect * 0.36 * w1 * cos(t * w1 + p0);
    float dy = 0.36 * w2 * 1.3 * cos(t * w2 * 1.3 + p0 * 1.2);
    float spd = max(length(float2(dx, dy)), 1e-4);
    float2 dir = float2(dx, dy) / spd;
    outDir = dir;
    float2 side = float2(-dir.y, dir.x);

    // Spine flexing along body with graceful tail undulation
    float fracK = float(k) / 9.0;
    float dist = fracK * len;
    float tailBeat = t * 4.5 + fi * 3.0;
    float wiggle = sin(tailBeat - fracK * 4.5) * len * 0.15 * pow(fracK, 1.35);

    return outHead - dir * dist + side * wiggle;
}

float3 evalSpine(float2 q, int i, float t, float aspect, float len, out float2 outHead, out float2 outDir)
{
    float2 prev = getSpinePoint(i, 0, t, aspect, len, outHead, outDir);
    float best = 1e4;
    float along = 0.0;
    float2 nearest = float2(0.0, 0.0);
    float2 tangent = float2(1.0, 0.0);

    for (int k = 1; k <= 9; ++k)
    {
        float2 cur = getSpinePoint(i, k, t, aspect, len, outHead, outDir);
        float2 pa = q - prev;
        float2 ba = cur - prev;
        float h = dot(pa, ba) / max(dot(ba, ba), 1e-8);
        h = clamp(h, 0.0, k == 9 ? 3.5 : 1.0);
        float2 off = pa - ba * h;
        float d = dot(off, off);
        if (d < best)
        {
            best = d;
            along = (float(k - 1) + h) / 9.0;
            nearest = off;
            tangent = ba;
        }
        prev = cur;
    }
    tangent = normalize(tangent);
    return float3(sqrt(best), along, dot(nearest, float2(-tangent.y, tangent.x)));
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 0.7;
    int fishCount = EffectParams.y > 0.0 ? (int)EffectParams.y : 4;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.5;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float t = FrameState.x * speed;
    float px = 1.0 / 1080.0;

    float2 p = float2(uv.x * aspect, 1.0 - uv.y);

    // Interactive mouse pointer ripples
    float2 hand = float2(MouseState.x * aspect, 1.0 - MouseState.y);
    float2 hd = p - hand;
    float hr = length(hd);
    float2 ripple = (float2(noise(p * 9.0 + t * 0.3), noise(p * 9.0 + 5.0 - t * 0.25)) - 0.5) * 0.005;
    ripple += (hd / (hr + 1e-4)) * sin(hr * 80.0 - t * 7.0) * exp(-hr * 8.0) * 0.003;
    float2 q = p + ripple;

    // 1. POND FLOOR & DRIFTING CAUSTICS (#12302e Linear Palette)
    float2x2 turn = float2x2(0.8, 0.6, -0.6, 0.8);
    float floorTex = noise(mul(q * 22.0, turn)) * 0.6 + noise(mul(q * 55.0, turn)) * 0.4;
    float3 waterCol = float3(0.0060, 0.0296, 0.0273); // ittybitty deep emerald water
    float3 colour = waterCol * (0.65 + 0.55 * floorTex);

    // Drifting sunlight caustic lines
    float2 cq = mul(q * 14.0, turn);
    float c1 = noise(cq + float2(t * 0.22, 0.0));
    float c2 = noise(cq * 1.2 + float2(4.0, -t * 0.18));
    float caustic = pow(max(1.0 - abs(c1 - c2) * 3.0, 0.0), 5.0);
    colour += waterCol * caustic * 1.25;

    // 2. FISH BODIES, SOFT SHADOWS, FINS & SCALES
    float len = 0.17;
    float bw = len * 0.13;
    float reach = len * 1.35 + 0.035;

    for (int i = 0; i < 4; ++i)
    {
        float fi = float(i);
        float2 headAt;
        float2 dir;

        // Head bounding circle optimization (skips empty water pixels)
        getSpinePoint(i, 0, t, aspect, len, headAt, dir);
        float2 fromHead = q - headAt;
        if (dot(fromHead, fromHead) > reach * reach) continue;

        // Soft floor shadow
        float3 ss = evalSpine(q - float2(0.012, -0.018), i, t, aspect, len, headAt, dir);
        float sv = saturate((ss.y - 0.72) / 0.28);
        float shadowFin = bw * (0.2 + 1.25 * pow(sv, 0.85)) * step(0.72, ss.y) * step(ss.y, 0.95);
        float shadow = smoothstep(0.012, -0.004, ss.x - max(bodyWidth(ss.y, bw), shadowFin * 0.8)) * 0.4;
        colour *= (1.0 - shadow);

        // Fish Body & Fins
        float3 sp = evalSpine(q, i, t, aspect, len, headAt, dir);
        float u = sp.y;
        float an = sp.z / bw;
        float2 side = float2(-dir.y, dir.x);

        // Translucent Pectoral Fins
        float flap = 0.25 * sin(t * 3.8 + fi * 3.0);
        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            float fs = float(sgn);
            float2 root = headAt - dir * len * 0.24 + side * fs * bw * 0.75;
            float2 fdir = normalize(-dir * (0.9 - flap) + side * fs * (0.8 + flap));
            float2 fd = q - root - fdir * bw * 0.75;
            float2 fl = float2(dot(fd, fdir), dot(fd, float2(-fdir.y, fdir.x)));
            float fe = length(fl / float2(bw * 0.85, bw * 0.48)) - 1.0;
            float fin = smoothstep(0.08, -0.08, fe) * 0.45;
            float rays = 0.85 + 0.15 * sin(atan2(fl.y, fl.x + bw) * 30.0);
            colour = lerp(colour, float3(0.92, 0.90, 0.86) * rays, fin);
        }

        // Translucent Forked Tail Fin
        float v = (u - 0.72) / 0.28;
        float wf = bw * (0.2 + 1.25 * pow(saturate(v), 0.85));
        float fa = saturate(abs(sp.z) / max(wf, 1e-4));
        float fork = 0.7 + 0.45 * fa - 0.3 * fa * fa;
        float tailFin = step(0.0, v) * smoothstep(wf + px, wf - px, abs(sp.z)) * smoothstep(0.02, -0.02, v - fork);
        float tailRays = 0.8 + 0.2 * sin(sp.z / max(wf, 1e-4) * 14.0);

        // Body Surface SDF
        float sd = sp.x - bodyWidth(u, bw);
        float body = smoothstep(px, -px, sd) * step(u, 0.82);

        // Skin patterns (Kohaku / Tancho / Sanke varieties)
        float kind = hash11(fi * 8.1 + 0.3);
        float3 white = float3(0.96, 0.94, 0.90);
        float3 orange = float3(1.0000, 0.1441, 0.0137); // ittybitty #ff6a1f linear
        float2 bp = float2(u * 5.0, an * 0.7) + fi * 13.0;
        float hi = smoothstep(0.46, 0.54, noise(bp) * 0.9 + 0.3 * (1.0 - an * an) - 0.12);
        hi = max(hi, smoothstep(0.2, 0.14, u) * smoothstep(0.8, 0.5, abs(an)));

        float3 skin;
        float sheen = 0.20;
        if (kind < 0.38)
        {
            skin = lerp(white, orange, hi);
        }
        else if (kind < 0.65)
        {
            float sumi = smoothstep(0.64, 0.70, noise(bp * 1.9 + 7.0)) * smoothstep(0.1, 0.2, u);
            skin = lerp(lerp(white, orange, hi), float3(0.04, 0.04, 0.05), sumi);
        }
        else
        {
            skin = lerp(orange, float3(1.0, 0.82, 0.35), 0.55);
            sheen = 0.45;
        }

        // Diamond Scales Netting
        float s1 = abs(frac(u * 36.0 + an * 1.6) - 0.5);
        float s2 = abs(frac(u * 36.0 - an * 1.6) - 0.5);
        skin *= 1.0 - 0.1 * smoothstep(0.08, 0.0, min(s1, s2)) * smoothstep(0.15, 0.3, u) * smoothstep(0.8, 0.6, u);

        // 3D Dorsal Curvature & Black Eyes
        float round_ = saturate(1.0 - an * an);
        skin *= 0.55 + 0.5 * sqrt(round_);
        skin += float3(1.0, 0.97, 0.90) * exp(-an * an * 10.0) * sheen * smoothstep(0.75, 0.3, u);
        float eye = smoothstep(0.16, 0.10, length(float2((u - 0.07) * len / bw, abs(an) - 0.6)));
        skin = lerp(skin, float3(0.03, 0.03, 0.03), eye);
        skin = lerp(skin, waterCol, 0.12);

        // Composite Tail Fin & Body
        float3 finCol = lerp(float3(0.92, 0.90, 0.86), skin, 0.35) * tailRays;
        tailFin *= 0.55 * (1.0 - body);
        colour = lerp(colour, finCol, tailFin);
        colour = lerp(colour, skin, body);
    }

    // 3. FLOATING LILY PADS WITH NOTCHES & VEINS (#3f7a3a)
    float3 padCol = float3(0.0497, 0.1946, 0.0423); // ittybitty #3f7a3a linear
    for (int j = 0; j < 5; ++j)
    {
        float fj = float(j);
        float2 c = float2(aspect * hash11(fj * 3.3 + 1.0), hash11(fj * 6.1 + 2.0));
        c += float2(sin(t * 0.1 + fj), cos(t * 0.08 + fj)) * 0.015;
        float r = 0.06 + 0.04 * hash11(fj * 2.2);
        float2 d = p - c;
        float dist = length(d);

        // Soft pad shadow on the pond bottom
        float2 ds = d - float2(0.008, -0.012);
        float padShadow = 0.45 * smoothstep(0.008, -0.004, length(ds) - r);
        colour *= (1.0 - padShadow);

        if (dist <= r + px)
        {
            float ang = atan2(d.y, d.x) + fj;
            float notch = smoothstep(0.1, 0.0, abs(fmod(ang, 6.2831) - 0.3)) * step(dist, r);
            float on = smoothstep(px, -px, dist - r) * (1.0 - notch);
            float vein = smoothstep(0.06, 0.0, abs(frac(ang * 16.0 / 6.2831) - 0.5)) * smoothstep(0.0, r, dist);
            float3 leaf = padCol * (0.8 + 0.3 * noise(d * 60.0)) * (1.0 - vein * 0.25)
                        + padCol * smoothstep(r * 0.8, r, dist) * 0.3;
            colour = lerp(colour, leaf, on);
        }
    }

    // 4. WATER SURFACE SPARKLES (GLINTS)
    float2 gcell = floor(p * 45.0);
    float2 gf = frac(p * 45.0) - 0.5;
    float gh = hash12(gcell + floor(t * 0.7 + hash12(gcell) * 3.0) * 11.0);
    float glint = step(0.994, gh) * exp(-dot(gf, gf) * 80.0) * 0.5;
    colour += float3(0.7, 0.85, 0.85) * glint;

    // Convert linear pond color to sRGB with smooth dither
    float3 finalRgb = linearToSrgb(colour);
    finalRgb += (hash12(uv * 1080.0) - 0.5) / 255.0;

    return float4(saturate(finalRgb * brightness), 1.0);
}

