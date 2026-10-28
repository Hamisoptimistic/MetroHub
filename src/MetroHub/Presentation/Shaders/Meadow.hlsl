// Meadow (Swaying Grass & Dandelions with Film Still Grading) for MetroHub
// Based on ittybitty.dev Meadow by Blake Cyze
// Optimized for Direct3D 9 ps_3_0 with multi-pass register isolation and 24-hour diurnal cycle

sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=TimeOfDaySeconds, W=0
float4 EffectParams : register(c1); // X=Speed, Y=Gusts, Z=Zoom, W=Variant (0=Dynamic 24h, 1=Lonely, 2=Summer, 3=Cottage, 4=PaleBlue, 5=Night, 6=Mono)
float4 MouseState : register(c2);   // X=MouseX, Y=MouseY, Z=LeftDown, W=0

static const float CAM = 0.6;
static const float RATIO = 1.07;

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

float gust(float2 w, float t)
{
    float g = noise(w * float2(0.35, 0.25) - float2(t * 0.5, t * 0.22)) * 0.7;
    g += noise(w * float2(1.1, 0.8) - float2(t * 1.1, t * 0.4)) * 0.3;
    return smoothstep(0.2, 0.85, g);
}

float brush(float2 at, float2 hand)
{
    float2 away = at - hand;
    float near = exp(-dot(away, away) / 0.03);
    return away.x * 11.0 * near;
}

float detailOf(float size, float pixel)
{
    return smoothstep(1.0, 3.0, size / pixel);
}

float grainAt(float2 p, float frame)
{
    return hash12(p + frame * 17.31) + hash12(p * 1.37 + frame * 3.1) - 1.0;
}

float3 chromaAt(float2 p, float frame)
{
    return float3(hash12(p + frame * 5.7), hash12(p + frame * 9.1 + 3.0), hash12(p + frame * 2.3 + 7.0)) - 0.5;
}

float3 linearToSrgb(float3 c)
{
    c = max(c, 0.0);
    return lerp(c * 12.92, 1.055 * pow(c, (1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

// Subroutine for flower & seed-clock rendering to keep register count strictly under ps_3_0 limit
void RenderFlower(
    float fid, float cell, float fw, float d, float s, float sz, float px, float L,
    float ground, float rowGap, float bendRow, float xw, float yy, float t,
    float3 lit, float3 grass, float3 skyLow, float haze, float clocks, float zoom,
    float2 hand, float touch,
    inout float3 acc, inout float through)
{
    float f2 = hash11(fid * 5.71 + 1.3);
    float fx = (cell + 0.15 + 0.7 * f2) * fw;
    float aw = 1.2 * px / sz;
    float swing = abs(bendRow) * 0.45;
    if (abs(xw - fx) > swing * 0.625 * L + 1.42 * max(0.0308, 0.55 * aw) + aw * 1.01) return;

    float f3 = hash11(fid * 2.33 + 7.1);
    float f4 = hash11(fid * 9.13 + 3.7);
    bool clock = hash11(fid * 6.29 + 4.4) < clocks;
    float fg = ground - f3 * rowGap;
    float fh = L * (0.5 + 0.75 * f3 * f4);
    float bend = bendRow * 0.45;
    float zw = d / zoom;
    float handNear = touch * exp(-(zw - hand.y) * (zw - hand.y) / 0.03);
    if (handNear > 0.01) bend += touch * brush(float2(fx, zw), hand) * 0.8;

    float hx = fx + bend * 0.5 * fh;
    float hy = fh * (1.0 - 0.15 * bend * bend);
    float yw = (yy - fg) / sz;
    float r = (clock ? 0.018 : 0.013) * (0.6 + 0.8 * frac(f2 * 9.1));
    float rr = max(r, 0.55 * aw);
    float energy = (r * r) / (rr * rr);
    float detail = smoothstep(2.0, 6.0, r / aw);
    float eu = aw / rr;
    float2 q = float2(xw - hx, yw - hy);
    float a = 0.0;
    float3 c = float3(0.0, 0.0, 0.0);

    if (dot(q, q) < rr * rr * 2.0)
    {
        if (clock)
        {
            float u = length(q) / rr;
            float fib = lerp(0.5, 0.5 + 0.5 * sin(atan2(q.y, q.x) * 37.0 + f2 * 60.0), detail);
            float rim = 1.0 - 0.06 * fib;
            float body = smoothstep(rim + eu, rim - 0.45 - eu, u);
            float strands = pow(abs(fib), 6.0) * smoothstep(rim + 0.08, 0.5, u) * detail;
            a = saturate(body * 0.65 + strands * 0.2) * lerp(0.5, 1.0, f4);
            float shade = 0.55 + 0.3 * q.y / rr + 0.25 * (1.0 - sqrt(max(1.0 - u * u, 0.0)));
            c = float3(0.86, 0.86, 0.82) * lit * shade;
            c = lerp(c, grass * 0.4 * lit, smoothstep(0.25, 0.08, u) * 0.6 * detail);
        }
        else
        {
            float squash = clamp(CAM * s * 1.3 + 0.45, 0.5, 0.9);
            float tilt = (f4 - 0.5) * 0.6;
            float2 b = float2(q.x + q.y * tilt, q.y / (squash * (0.7 + 0.6 * f3)));
            float u = length(b) / rr;
            float petals = lerp(0.5, 0.5 + 0.5 * cos(atan2(b.y, b.x) * 18.0 + f2 * 20.0), detail);
            float rim = 0.92 + 0.08 * petals;
            float disc = smoothstep(rim + eu, rim - eu, u);
            float sepal = smoothstep(0.92 + eu, 0.92 - eu, length(b + float2(0.0, 0.2 * rr)) / rr) * detail;
            float3 bloom = float3(0.957, 0.761, 0.122);
            float3 petal = bloom * (0.8 + 0.3 * f3) * lit * (0.75 + 0.25 * petals * u) * (0.7 + 0.35 * saturate(b.y / rr + 0.3));
            petal = lerp(petal, bloom * float3(1.0, 0.55, 0.2) * lit * 0.7, smoothstep(0.4, 0.1, u));
            a = max(disc, sepal);
            c = lerp(grass * 0.55 * lit, petal, disc);
        }
        a *= energy;
    }

    float stemY = saturate(yw / hy);
    float sx = fx + (hx - fx) * stemY * stemY;
    float ws = 0.0012 * (1.2 - 0.5 * stemY);
    float wsd = max(ws, 0.5 * aw);
    float stem = smoothstep(wsd + aw, wsd - aw, abs(xw - sx)) * step(yw, hy) * ws / wsd;
    stem *= smoothstep(0.0, 0.4, stemY);
    float3 sc = grass * lit * (0.25 + 0.35 * stemY);
    float sa = stem * (1.0 - a);
    c = c * a + sc * sa;
    a += sa;
    if (a > 0.0)
    {
        c = lerp(c / a, skyLow, haze) * a;
        acc += through * c;
        through *= 1.0 - min(a, 1.0);
    }
}

float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    float aspect = FrameState.y;
    float time = FrameState.x;
    int variant = (int)EffectParams.w;

    // Palette values
    float3 skyTop = float3(0.109, 0.113, 0.129); // #5d5e64 (Lonely default)
    float3 skyLow = float3(0.237, 0.243, 0.266); // #85868b
    float3 light  = float3(0.102, 0.102, 0.109); // #5a5a5c
    float3 ambient= float3(0.274, 0.282, 0.310); // #8d8e94
    float3 grass  = float3(0.076, 0.145, 0.027); // #4f6b2c
    float3 tint   = float3(0.549, 0.604, 0.651); // #8c9aa6
    float fog = 0.60;
    float exposure = -0.30;
    float saturation = 0.25;
    float toning = 0.20;
    float contrast = 1.05;
    float fade = 0.20;
    float grain = 0.50;
    float noiseAmt = 0.10;
    float stars = 0.0;
    float flowers = 0.55;
    float clocks = 0.70;
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 0.35;
    float gusts = EffectParams.y > 0.0 ? EffectParams.y : 0.70;
    float zoom  = EffectParams.z > 0.0 ? EffectParams.z : 0.75;
    float sway  = 0.45;

    // Fixed Styles
    if (variant == 1) // 1: Lonely
    {
        // defaults already match Lonely
    }
    else if (variant == 2) // 2: Summer
    {
        skyTop = float3(0.098, 0.274, 0.686); // #5b8fd9
        skyLow = float3(0.667, 0.761, 0.827); // #d6e3ec
        light  = float3(1.000, 0.871, 0.675); // #fff1d6
        ambient= float3(0.212, 0.337, 0.549); // #7f9cc4
        grass  = float3(0.102, 0.278, 0.024); // #5b8f2a
        tint   = float3(0.500, 0.500, 0.500);
        fog = 0.35;
        exposure = 0.0;
        saturation = 1.35;
        toning = 0.0;
        contrast = 1.08;
        fade = 0.05;
        grain = 0.25;
        noiseAmt = 0.0;
        clocks = 0.20;
    }
    else if (variant == 3) // 3: Cottage (Golden Hour)
    {
        skyTop = float3(0.157, 0.392, 0.835); // #6fa8ee
        skyLow = float3(1.000, 0.690, 0.404); // #ffd9ab
        light  = float3(1.000, 0.627, 0.271); // #ffd08f
        ambient= float3(0.482, 0.455, 0.380); // #b9b4a6
        grass  = float3(0.114, 0.459, 0.024); // #5fb52a
        tint   = float3(1.000, 0.702, 0.769); // #ffb3c4
        fog = 0.25;
        exposure = 0.10;
        saturation = 1.90;
        toning = 0.20;
        contrast = 1.08;
        fade = 0.08;
        grain = 0.25;
        noiseAmt = 0.0;
        flowers = 1.00;
        clocks = 0.30;
        zoom = 1.00;
        speed = 0.25;
        gusts = 0.50;
    }
    else if (variant == 4) // 4: Pale Blue
    {
        skyTop = float3(0.251, 0.478, 0.804); // #8ab8e8
        skyLow = float3(0.765, 0.859, 0.925); // #e3eef7
        light  = float3(0.576, 0.678, 0.784); // #c9d8e6
        ambient= float3(0.396, 0.549, 0.725); // #a9c4de
        grass  = float3(0.076, 0.145, 0.027);
        tint   = float3(0.639, 0.706, 0.769); // #a3b4c4
        fog = 0.60;
        exposure = 0.10;
        saturation = 0.80;
        toning = 0.20;
        contrast = 1.02;
        fade = 0.30;
        grain = 0.40;
        clocks = 0.60;
    }
    else if (variant == 5) // 5: Night
    {
        skyTop = float3(0.004, 0.008, 0.035); // #03050c
        skyLow = float3(0.016, 0.035, 0.090); // #121a2c
        light  = float3(0.012, 0.027, 0.078); // #0d1426
        ambient= float3(0.024, 0.043, 0.118); // #141c30
        grass  = float3(0.015, 0.035, 0.010);
        tint   = float3(0.361, 0.478, 0.659); // #5c7aa8
        stars = 0.60;
        fog = 0.40;
        exposure = 0.20;
        saturation = 0.50;
        toning = 0.20;
        contrast = 1.07;
        fade = 0.12;
        grain = 0.70;
        noiseAmt = 0.35;
        clocks = 0.90;
    }
    else if (variant == 6) // 6: Black & White
    {
        saturation = 0.0;
        tint = float3(0.5, 0.5, 0.5);
        fade = 0.30;
        grain = 0.80;
    }
    else // Variant 0: 24-Hour Diurnal Time-of-Day Automatic Interpolation
    {
        float tod = FrameState.z / 3600.0; // Hour (0.0 to 24.0)
        if (tod < 5.0) // Deep Night (00:00 - 05:00)
        {
            skyTop = float3(0.004, 0.008, 0.035);
            skyLow = float3(0.016, 0.035, 0.090);
            light  = float3(0.012, 0.027, 0.078);
            ambient= float3(0.024, 0.043, 0.118);
            grass  = float3(0.015, 0.035, 0.010);
            tint   = float3(0.361, 0.478, 0.659);
            stars = 0.60;
            fog = 0.40;
            exposure = 0.20;
            saturation = 0.50;
            grain = 0.70;
            clocks = 0.90;
        }
        else if (tod < 7.5) // Dawn / Sunrise (05:00 - 07:30)
        {
            float f = (tod - 5.0) / 2.5;
            skyTop = lerp(float3(0.004, 0.008, 0.035), float3(0.157, 0.392, 0.835), f);
            skyLow = lerp(float3(0.016, 0.035, 0.090), float3(1.000, 0.690, 0.404), f);
            light  = lerp(float3(0.012, 0.027, 0.078), float3(1.000, 0.627, 0.271), f);
            ambient= lerp(float3(0.024, 0.043, 0.118), float3(0.482, 0.455, 0.380), f);
            grass  = lerp(float3(0.015, 0.035, 0.010), float3(0.114, 0.459, 0.024), f);
            tint   = lerp(float3(0.361, 0.478, 0.659), float3(1.000, 0.702, 0.769), f);
            stars = lerp(0.60, 0.0, f);
            fog = lerp(0.40, 0.25, f);
            saturation = lerp(0.50, 1.80, f);
            grain = lerp(0.70, 0.25, f);
            clocks = lerp(0.90, 0.30, f);
            flowers = lerp(0.30, 0.90, f);
        }
        else if (tod < 11.5) // Morning (07:30 - 11:30)
        {
            float f = (tod - 7.5) / 4.0;
            skyTop = lerp(float3(0.157, 0.392, 0.835), float3(0.251, 0.478, 0.804), f);
            skyLow = lerp(float3(1.000, 0.690, 0.404), float3(0.765, 0.859, 0.925), f);
            light  = lerp(float3(1.000, 0.627, 0.271), float3(0.576, 0.678, 0.784), f);
            ambient= lerp(float3(0.482, 0.455, 0.380), float3(0.396, 0.549, 0.725), f);
            grass  = lerp(float3(0.114, 0.459, 0.024), float3(0.090, 0.220, 0.025), f);
            tint   = lerp(float3(1.000, 0.702, 0.769), float3(0.639, 0.706, 0.769), f);
            fog = lerp(0.25, 0.55, f);
            saturation = lerp(1.80, 0.90, f);
            clocks = lerp(0.30, 0.50, f);
        }
        else if (tod < 16.5) // Midday / Afternoon (11:30 - 16:30)
        {
            skyTop = float3(0.098, 0.274, 0.686);
            skyLow = float3(0.667, 0.761, 0.827);
            light  = float3(1.000, 0.871, 0.675);
            ambient= float3(0.212, 0.337, 0.549);
            grass  = float3(0.102, 0.278, 0.024);
            tint   = float3(0.500, 0.500, 0.500);
            fog = 0.35;
            exposure = 0.0;
            saturation = 1.35;
            toning = 0.0;
            clocks = 0.25;
            flowers = 0.85;
        }
        else if (tod < 19.5) // Golden Sunset (16:30 - 19:30)
        {
            float f = (tod - 16.5) / 3.0;
            skyTop = lerp(float3(0.098, 0.274, 0.686), float3(0.157, 0.392, 0.835), f);
            skyLow = lerp(float3(0.667, 0.761, 0.827), float3(1.000, 0.690, 0.404), f);
            light  = lerp(float3(1.000, 0.871, 0.675), float3(1.000, 0.627, 0.271), f);
            ambient= lerp(float3(0.212, 0.337, 0.549), float3(0.482, 0.455, 0.380), f);
            grass  = lerp(float3(0.102, 0.278, 0.024), float3(0.114, 0.459, 0.024), f);
            tint   = lerp(float3(0.500, 0.500, 0.500), float3(1.000, 0.702, 0.769), f);
            fog = lerp(0.35, 0.25, f);
            saturation = lerp(1.35, 1.90, f);
            clocks = lerp(0.25, 0.40, f);
        }
        else if (tod < 22.0) // Twilight / Dusk (19:30 - 22:00)
        {
            float f = (tod - 19.5) / 2.5;
            skyTop = lerp(float3(0.157, 0.392, 0.835), float3(0.109, 0.113, 0.129), f);
            skyLow = lerp(float3(1.000, 0.690, 0.404), float3(0.237, 0.243, 0.266), f);
            light  = lerp(float3(1.000, 0.627, 0.271), float3(0.102, 0.102, 0.109), f);
            ambient= lerp(float3(0.482, 0.455, 0.380), float3(0.274, 0.282, 0.310), f);
            grass  = lerp(float3(0.114, 0.459, 0.024), float3(0.076, 0.145, 0.027), f);
            tint   = lerp(float3(1.000, 0.702, 0.769), float3(0.549, 0.604, 0.651), f);
            fog = lerp(0.25, 0.60, f);
            saturation = lerp(1.90, 0.35, f);
            clocks = lerp(0.40, 0.70, f);
        }
        else // Nightfall (22:00 - 24:00)
        {
            float f = (tod - 22.0) / 2.0;
            skyTop = lerp(float3(0.109, 0.113, 0.129), float3(0.004, 0.008, 0.035), f);
            skyLow = lerp(float3(0.237, 0.243, 0.266), float3(0.016, 0.035, 0.090), f);
            light  = lerp(float3(0.102, 0.102, 0.109), float3(0.012, 0.027, 0.078), f);
            ambient= lerp(float3(0.274, 0.282, 0.310), float3(0.024, 0.043, 0.118), f);
            grass  = lerp(float3(0.076, 0.145, 0.027), float3(0.015, 0.035, 0.010), f);
            tint   = lerp(float3(0.549, 0.604, 0.651), float3(0.361, 0.478, 0.659), f);
            stars = lerp(0.0, 0.60, f);
            fog = lerp(0.60, 0.40, f);
            saturation = lerp(0.35, 0.50, f);
            grain = lerp(0.50, 0.70, f);
            clocks = lerp(0.70, 0.90, f);
        }
    }

    float screenY = 1.0 - uv.y;
    float2 p = float2((uv.x - 0.5) * aspect, screenY);
    float px = 1.0 / 1080.0;
    float t = time * speed;
    float uSlope = -0.08;
    float uHorizon = 0.60;
    float yy = p.y - uSlope * p.x;
    float h = uHorizon - yy;

    // Sky gradient & stars
    float3 skyCol = float3(0.0, 0.0, 0.0);
    if (h < 0.012)
    {
        skyCol = lerp(skyLow, skyTop, smoothstep(uHorizon - 0.05, 1.05, yy));
        skyCol *= 0.92 + 0.16 * noise(float2(p.x * 1.5 + time * 0.01, yy * 4.0));
        float2 star = floor(float2(uv.x, screenY) * 540.0);
        skyCol += stars * step(0.997, hash12(star)) * hash12(star + 7.0) * smoothstep(uHorizon, uHorizon + 0.3, yy);
        skyCol = lerp(skyCol, skyLow, fog * exp(-max(-h, 0.0) * 9.0));
    }

    float3 colour = skyCol;
    if (h > 0.0)
    {
        float L = CAM * 0.25;
        float LF = L * 1.5;
        float lnR = log(RATIO);
        float iHi = floor(log(CAM / h) / lnR);
        float iLo = ceil(log(max(CAM - LF * zoom, CAM * 0.2) / h) / lnR);
        float spacingW = 0.008;
        float own = 0.6 * spacingW;
        float fw = 0.22;
        float3 lit = ambient + light;

        // Pointer influence in world space
        float mouseY = 1.0 - MouseState.y;
        float2 hp = float2((MouseState.x - 0.5) * aspect, mouseY);
        float hh = uHorizon - (hp.y - uSlope * hp.x);
        float hd = (CAM - 0.5 * L * zoom) / max(hh, 0.02);
        float2 hand = float2(hp.x * hd, hd) / zoom;
        float touch = 1.0 * step(0.0, hh);

        float3 acc = float3(0.0, 0.0, 0.0);
        float through = 1.0;

        [loop]
        for (int n = 0; n < 12; n++)
        {
            float i = iLo + float(n);
            if (i > iHi + 1.0 || through < 0.02) break;
            float d = pow(RATIO, i);
            float s = 1.0 / d;
            float sz = s * zoom;
            float ground = uHorizon - CAM * s;
            float lod = smoothstep(1.0, 3.0, spacingW * sz / px);
            bool bladesHere = (lod > 0.0) && (yy < ground + L * 1.1 * sz);
            bool flowersHere = (flowers > 0.0) && (yy < ground + LF * 1.3 * sz);
            if (!bladesHere && !flowersHere) continue;

            float xw = p.x / sz;
            float zw = d / zoom;
            float aw = 1.2 * px / sz;
            float haze = 1.0 - exp(-d * fog * 0.09);
            float g = gust(float2(xw, zw), t);
            float bendRow = sway * (lerp(0.5, 0.1 + 0.8 * g, gusts) + 0.12 * sin(t * 0.7 + xw * 0.8 + zw * 0.5));

            if (flowersHere)
            {
                float fid0 = floor(xw / fw);
                float prob = flowers * saturate(0.12 * d * 1.6);
                [unroll]
                for (int k = -1; k <= 1; k++)
                {
                    float cell = fid0 + float(k);
                    float fid = cell + i * 71.3;
                    if (hash11(fid * 3.17 + 0.5) <= prob)
                    {
                        RenderFlower(fid, cell, fw, d, s, sz, px, L, ground, CAM * s * (1.0 - 1.0 / RATIO),
                                     bendRow, xw, yy, t, lit, grass, skyLow, haze, clocks, zoom, hand, touch, acc, through);
                    }
                }
            }

            if (bladesHere && through > 0.02)
            {
                float tone = noise(float2(xw * 0.6, zw * 0.6)) * 0.6 + noise(float2(xw * 2.1, zw * 2.1)) * 0.4;
                float hRow = max(yy - ground, 0.0) / sz;
                float handNear = touch * exp(-(zw - hand.y) * (zw - hand.y) / 0.03);
                float bendEst = bendRow + sin(t * 2.4 + xw * 5.0) * 0.12 * (0.4 + g);
                if (handNear > 0.01) bendEst += touch * brush(float2(xw, zw), hand);
                bendEst = clamp(bendEst, -1.2, 1.2);

                float lean = 0.6 * hRow * hRow / L;
                float id = floor((xw - bendEst * lean) / spacingW);

                [loop]
                for (int k = -1; k <= 1; k++)
                {
                    float cell = id + float(k);
                    float bid = cell + i * 131.7;
                    float hA = hash11(bid * 7.13 + 1.7);
                    float hB = frac(hA * 13.17 + 0.31);
                    float hC = frac(hA * 71.31 + 0.67);
                    float x0 = (cell + hA) * spacingW;
                    float gr = ground - hC * (CAM * s * (1.0 - 1.0 / RATIO));
                    float bl = L * (0.4 + 0.75 * hB * hB);
                    float above = (yy - gr) / sz;
                    if (above < 0.0 || above > bl) continue;

                    float bend = bendRow + sin(t * 2.4 + x0 * 5.0) * 0.12 * (0.4 + g);
                    if (handNear > 0.01) bend += touch * brush(float2(x0, zw), hand);
                    bend = clamp(bend, -1.2, 1.2);
                    float tall = bl * (1.0 - 0.3 * bend * bend);
                    float y = above / tall;
                    if (y > 1.0) continue;

                    float stray = ((hA - 0.5) + (hC - 0.5) * abs(bendRow)) * own;
                    float cx = x0 + bend * lean + stray * y * y;
                    float sl = 1.2 * bend * hRow / L + 2.0 * stray * y / tall;
                    float side = max(0.00308, 0.5 * aw) + aw;
                    if ((xw - cx) * (xw - cx) > side * side * (1.0 + sl * sl)) continue;

                    float w = 0.0022 * (0.6 + 0.8 * hB) * pow(abs(1.0 - y), 0.6);
                    float wd = max(w, 0.5 * aw);
                    float dx = (xw - cx) / sqrt(1.0 + sl * sl);
                    float a = smoothstep(wd + aw, wd - aw, abs(dx)) * lod * w / wd;
                    if (a <= 0.0) continue;

                    float3 base = grass * lerp(float3(0.8, 1.0, 0.75), float3(1.15, 1.05, 0.7), hA) * (0.55 + 0.7 * frac(hC * 7.3));
                    float3 c = base * lerp(0.1, 1.0, pow(abs(y), 0.8)) * (ambient + light * (0.6 + 0.4 * hB));
                    c *= 0.75 + 0.3 * clamp(dx / wd, -1.0, 1.0) * detailOf(w, aw);
                    c *= (0.65 + 0.7 * tone) * (1.0 + (g - 0.4) * 0.6 * sway * y);
                    c += skyLow * ambient.g * 0.3 * y * y * hB * hB;
                    c = lerp(c, skyLow, haze);

                    acc += through * a * c;
                    through *= 1.0 - a;
                    if (through < 0.02) break;
                }
            }
        }

        // Ground under the blades
        float dm = CAM / h;
        float lodM = smoothstep(1.0, 3.0, spacingW * zoom / dm / px);
        float xm = p.x * dm / zoom;
        float zm = dm / zoom;
        float tex = noise(float2(xm * 30.0, zm * 6.0)) * 0.5 + noise(float2(xm * 160.0, zm * 8.0)) * 0.5;
        float toneM = noise(float2(xm * 0.6, zm * 0.6)) * 0.6 + noise(float2(xm * 2.1, zm * 2.1)) * 0.4;
        float3 under = grass * lit * lerp(0.4, 0.1, lodM) * (0.6 + 0.8 * tex) * (0.65 + 0.7 * toneM);
        under *= 1.0 + (gust(float2(xm, zm), t) - 0.4) * 0.4 * sway * (1.0 - lodM);
        under = lerp(under, skyLow, 1.0 - exp(-dm * fog * 0.09));
        colour = acc + through * under;
        colour = lerp(colour, skyCol, smoothstep(0.012, 0.0, h) * fog);
    }

    // Color grading & film stock simulation
    colour *= exp2(exposure * 2.0);
    colour = 1.0 - exp(-colour * 1.4);
    float3 c = linearToSrgb(colour);
    float l = dot(c, float3(0.2126, 0.7152, 0.0722));
    c = lerp(float3(l, l, l), c, saturation);
    float3 toned = lerp(2.0 * l * tint, 1.0 - 2.0 * (1.0 - l) * (1.0 - tint), step(0.5, l));
    c = lerp(c, toned, toning);
    c = (c - 0.5) * contrast + 0.5;
    c = lerp(float3(fade * 0.2, fade * 0.2, fade * 0.2),
             float3(1.0 - fade * 0.06, 1.0 - fade * 0.06, 1.0 - fade * 0.06),
             saturate(c));
    float2 vq = uv - 0.5;
    c *= 1.0 - (0.15 + 0.5 * grain) * smoothstep(0.2, 0.9, dot(vq, vq) * 2.2);

    // Dynamic animated grain & chromatic noise
    float2 gp = uv * 1080.0;
    float ft = time * 12.0;
    float f0 = floor(ft);
    float fr = smoothstep(0.0, 1.0, frac(ft));
    l = dot(c, float3(0.2126, 0.7152, 0.0722));
    if (grain > 0.0)
    {
        float gSample = lerp(grainAt(gp, f0), grainAt(gp, f0 + 1.0), fr) * 0.55 + grainAt(gp, -1.0) * 0.45;
        c += gSample * grain * 0.13 * (0.35 + l * (1.0 - l) * 2.6);
    }
    if (noiseAmt > 0.0)
    {
        float3 cn = lerp(chromaAt(gp, f0), chromaAt(gp, f0 + 1.0), fr);
        c += (cn - dot(cn, float3(0.2126, 0.7152, 0.0722))) * noiseAmt * 0.35 * (1.0 - l * 0.6);
    }

    return float4(saturate(c), 1.0);
}

