// Pebbles (Refractive Floating Glass Pebbles) for MetroHub
// Based on ittybitty.dev Pebbles by Blake Cyze
// Optimized with Taubin distance normalization, fwidth subpixel AA, and fluid Lissajous kinematics
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Speed (0.5), Y=Clarity (1.0), Z=Refraction (1.0), W=Variant (0=Rose, 1=Amber, 2=Ice, 3=Obsidian, 4=Rerank, 5=Translate)
float4 MouseState : register(c2);   // X=MouseX, Y=MouseY, Z=LeftDown, W=0

struct PebbleVariant
{
    float3 stop0;
    float3 stop1;
    float3 stop2;
    float stop1Pos;
    float3 glass;
    float3 glow;
    float roundness;
    float clarity;
    float frost;
    float refraction;
    float rim;
    int count;
};

PebbleVariant GetVariant(int index)
{
    int v = index % 6;
    if (v < 0) v += 6;

    PebbleVariant pv;

    if (v == 0) // 0: Rose (Plum & Hot Pink)
    {
        pv.stop0 = float3(0.227, 0.039, 0.165); // #3a0a2a
        pv.stop1 = float3(0.690, 0.165, 0.416); // #b02a6a
        pv.stop2 = float3(1.000, 0.690, 0.784); // #ffb0c8
        pv.stop1Pos = 0.50;
        pv.glass = float3(1.000, 0.478, 0.690); // #ff7ab0
        pv.glow  = float3(1.000, 0.227, 0.478); // #ff3a7a
        pv.roundness = 0.55;
        pv.clarity = 0.60;
        pv.frost = 0.55;
        pv.refraction = 0.70;
        pv.rim = 0.70;
        pv.count = 5;
    }
    else if (v == 1) // 1: Amber (Dark Mocha & Honey Gold)
    {
        pv.stop0 = float3(0.165, 0.071, 0.016); // #2a1204
        pv.stop1 = float3(0.478, 0.212, 0.031); // #7a3608
        pv.stop2 = float3(0.941, 0.627, 0.125); // #f0a020
        pv.stop1Pos = 0.50;
        pv.glass = float3(0.878, 0.541, 0.125); // #e08a20
        pv.glow  = float3(1.000, 0.478, 0.063); // #ff7a10
        pv.roundness = 0.60;
        pv.clarity = 0.65;
        pv.frost = 0.45;
        pv.refraction = 0.70;
        pv.rim = 0.60;
        pv.count = 5;
    }
    else if (v == 2) // 2: Ice (Arctic Navy & Sky Cyan)
    {
        pv.stop0 = float3(0.039, 0.165, 0.353); // #0a2a5a
        pv.stop1 = float3(0.165, 0.478, 0.816); // #2a7ad0
        pv.stop2 = float3(0.749, 0.918, 1.000); // #bfeaff
        pv.stop1Pos = 0.50;
        pv.glass = float3(0.604, 0.847, 1.000); // #9ad8ff
        pv.glow  = float3(0.251, 0.659, 1.000); // #40a8ff
        pv.roundness = 0.75;
        pv.clarity = 0.85;
        pv.frost = 0.15;
        pv.refraction = 1.00;
        pv.rim = 1.00;
        pv.count = 5;
    }
    else if (v == 3) // 3: Obsidian (Dark Slate & Charcoal)
    {
        pv.stop0 = float3(0.290, 0.310, 0.345); // #4a4f58
        pv.stop1 = float3(0.082, 0.090, 0.106); // #15171b
        pv.stop2 = float3(0.416, 0.455, 0.518); // #6a7484
        pv.stop1Pos = 0.55;
        pv.glass = float3(0.290, 0.302, 0.322); // #4a4d52
        pv.glow  = float3(0.227, 0.290, 0.416); // #3a4a6a
        pv.roundness = 0.50;
        pv.clarity = 0.45;
        pv.frost = 0.35;
        pv.refraction = 0.90;
        pv.rim = 1.10;
        pv.count = 5;
    }
    else if (v == 4) // 4: Rerank (Deep Violet & Neon Purple)
    {
        pv.stop0 = float3(0.353, 0.016, 0.847); // #5a04d8
        pv.stop1 = float3(0.486, 0.063, 0.941); // #7c10f0
        pv.stop2 = float3(0.627, 0.188, 1.000); // #a030ff
        pv.stop1Pos = 0.50;
        pv.glass = float3(0.941, 0.251, 1.000); // #f040ff
        pv.glow  = float3(0.604, 0.125, 1.000); // #9a20ff
        pv.roundness = 0.85;
        pv.clarity = 0.40;
        pv.frost = 0.20;
        pv.refraction = 0.90;
        pv.rim = 1.00;
        pv.count = 5;
    }
    else // 5: Translate (Teal & Coral Red)
    {
        pv.stop0 = float3(0.004, 0.396, 0.357); // #01655b
        pv.stop1 = float3(0.353, 0.337, 0.275); // #5a5646
        pv.stop2 = float3(0.769, 0.141, 0.078); // #c42414
        pv.stop1Pos = 0.50;
        pv.glass = float3(0.314, 0.290, 0.267); // #504a44
        pv.glow  = float3(0.627, 0.047, 0.024); // #a00c06
        pv.roundness = 0.35;
        pv.clarity = 0.60;
        pv.frost = 0.75;
        pv.refraction = 0.70;
        pv.rim = 0.30;
        pv.count = 5;
    }

    return pv;
}

float hash12(float2 p)
{
    float3 q = frac(p.xyx * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

float3 linearToSrgb(float3 c)
{
    c = saturate(c);
    return lerp(c * 12.92, 1.055 * pow(c, (1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

// Evaluates the diagonal background gradient with silky smooth hermite interpolation
float3 evalBackdrop(float2 p, PebbleVariant pv, float2 gBack)
{
    float x = saturate(0.5 + dot(p, gBack));
    float3 col;
    if (x < pv.stop1Pos)
    {
        float t = smoothstep(0.0, 1.0, x / max(pv.stop1Pos, 1e-4));
        col = lerp(pv.stop0, pv.stop1, t);
    }
    else
    {
        float t = smoothstep(0.0, 1.0, (x - pv.stop1Pos) / max(1.0 - pv.stop1Pos, 1e-4));
        col = lerp(pv.stop1, pv.stop2, t);
    }
    return col;
}

// Evaluates a pebble using Taubin-corrected superellipse distance and smooth sinusoidal dome
float evalPebble(
    int i,
    float2 p,
    float time,
    float aspect,
    float roundness,
    float2 mousePos,
    out float2 outNormal2D,
    out float outThickness,
    out float outM,
    out float2 outCenter,
    out float2 outDir)
{
    // Well-balanced pebble anchors spread organically across the canvas
    float baseT[5] = {  0.08, -0.26,  0.32,  0.20, -0.38 };
    float baseN[5] = { -0.10, -0.22,  0.05,  0.38,  0.28 };
    float baseR[5] = {  0.32,  0.42,  0.36,  0.48,  0.26 };
    float baseB[5] = {  0.40,  0.48,  0.58,  0.38,  0.24 };
    float baseA[5] = {  0.45,  0.15, -0.35,  0.20, -0.40 };
    float baseS[5] = {  1.00,  1.05,  0.95,  0.85,  1.00 };

    float ph = float(i) * 2.15;

    // Organic multi-octave harmonic drift (zero jerky accelerations)
    float w1 = 0.16 + 0.02 * sin(ph * 1.3);
    float w2 = 0.13 + 0.02 * cos(ph * 0.9);
    float cx = (baseT[i] + 0.045 * sin(time * w1 + ph) + 0.015 * sin(time * 0.27 + ph * 1.8)) * aspect;
    float cy = baseN[i] + 0.035 * cos(time * w2 + ph * 1.3) + 0.012 * cos(time * 0.21 + ph * 0.7);

    float2 center = float2(cx, cy);

    // Continuous Gaussian hydrodynamic deflection (no jump/singularity when mouse enters center)
    float2 toMouse = center - mousePos;
    float d2 = dot(toMouse, toMouse);
    float push = exp(-d2 * 5.0) * 0.04;
    center += toMouse * push;
    outCenter = center;

    // Ultra-smooth breathing and rotation
    float breathe = 1.0 + 0.025 * sin(time * 0.22 + ph);
    float rot = baseA[i] + 0.07 * sin(time * 0.14 + ph * 0.5);

    float2 halfSize = float2(baseR[i], baseB[i]) * breathe;
    outM = min(halfSize.x, halfSize.y);

    float2 d = p - center;
    float cosR = cos(rot);
    float sinR = sin(rot);
    float2 q = float2(cosR * d.x + sinR * d.y, cosR * d.y - sinR * d.x);

    // Superellipse exponent: 2.0 (round ellipse) to 4.2 (smooth soft squircle)
    float exponent = max(2.0, (2.0 + (1.0 - roundness) * 2.2) * baseS[i]);

    float2 u = abs(q) / halfSize;
    float2 un = pow(max(u, 1e-4), exponent);
    float sum = un.x + un.y;
    float r = pow(sum, 1.0 / exponent);

    // Analytic Taubin distance: divides by true gradient length so SDF is perfectly linear everywhere
    float2 grad = (pow(max(u, 1e-4), exponent - 1.0) * sign(q) / halfSize) * pow(sum, (1.0 / exponent) - 1.0);
    float gradLen = max(length(grad), 1e-4);
    float sd = (r - 1.0) / gradLen;

    float2 localDir = grad / gradLen;
    outDir = float2(cosR * localDir.x - sinR * localDir.y, sinR * localDir.x + cosR * localDir.y);

    // C-infinity sinusoidal dome profile: prevents edge derivative spikes and aliasing
    float depth = saturate(1.0 - r);
    outThickness = sin(depth * 1.5707963);
    float tilt = cos(depth * 1.5707963);
    outNormal2D = -outDir * tilt;

    return sd;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 0.5;
    // Calibrated gentle drift time base
    float time = FrameState.x * speed * 0.4;
    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);

    int variantIdx = (int)round(EffectParams.w);
    PebbleVariant pv = GetVariant(variantIdx);

    float2 p = float2((uv.x - 0.5) * aspect, 0.5 - uv.y);
    float2 gBack = float2(0.42 * (1.0 / aspect), -0.9);

    float2 mousePos = float2((MouseState.x - 0.5) * aspect, 0.5 - MouseState.y);

    // Light source subtly tracks mouse and top-left ambient
    float2 lightPos = float2(0.18 * aspect, -1.0) + mousePos * 0.20;

    // Atmospheric diagonal gradient backdrop
    float3 finalCol = evalBackdrop(p, pv, gBack);

    // Pre-calculate glass absorption factor
    float clarityMod = (1.0 - pv.clarity) * (1.0 - pv.clarity) * 4.5;
    float3 absorb = -log(max(pv.glass, 0.01)) + clarityMod;

    int numPebbles = min(pv.count, 5);

    for (int i = numPebbles - 1; i >= 0; --i)
    {
        float2 normal2D;
        float thickness;
        float m;
        float2 center;
        float2 dir;
        float sd = evalPebble(i, p, time, aspect, pv.roundness, mousePos, normal2D, thickness, m, center, dir);

        // Hardware-derivative subpixel anti-aliasing (buttery smooth on 1080p, 1440p, and 4K)
        float aa = max(fwidth(sd), 0.001);
        float alpha = saturate(0.5 - sd / aa);

        if (alpha > 0.001)
        {
            // Smooth optical refraction without hard clipping
            float2 bend = normal2D * (m * 0.25 * pv.refraction);
            float3 behind = evalBackdrop(p + bend, pv, gBack);

            // True glass absorption across dome depth
            float3 body = behind * exp(-absorb * (0.25 + 0.75 * thickness));

            // Inner light pool glowing organically through the glass volume
            float2 dp = p - (center + float2(0.035, 0.035));
            float poolDist = dot(dp, dp);
            float poolLight = 1.5 * exp(-2.2 * poolDist / max(m * m, 1e-4)) * (0.35 + 0.65 * (1.0 - thickness));
            body += pv.glow * poolLight;

            // Smooth 3D surface normal
            float3 N = normalize(float3(normal2D * 0.75, max(thickness, 0.18)));

            // Fresnel surface reflections
            float fresnel = pow(1.0 - saturate(N.z), 2.5);
            float3 env = evalBackdrop(p + N.xy * 0.35, pv, gBack) * 0.14;
            float3 pebbleCol = lerp(body, env, fresnel * 0.80);

            // Specular sheen facing light
            float2 lp = lightPos - p;
            float3 L = normalize(float3(lp, 0.9));
            float3 H = normalize(L + float3(0.0, 0.0, 1.0));
            float spec = pow(saturate(dot(N, H)), 22.0);
            pebbleCol += pv.glow * spec * 0.28 * pv.rim;

            // Hairline rim graze highlight
            float rimGrazing = saturate(dot(dir, normalize(lp)));
            float rimLine = exp(-max(-sd, 0.0) / (aa * 2.5 + 0.002)) * (0.15 + 0.85 * rimGrazing);
            pebbleCol += (pv.glow + 0.25) * rimLine * pv.rim * 1.3;

            // Smooth composite
            finalCol = lerp(finalCol, pebbleCol, alpha);
        }
    }

    // Micro film grain to eliminate 8-bit banding on dark gradients
    float grain = (hash12(uv * 1080.0 + frac(FrameState.x)) - 0.5) * 0.025;
    finalCol += grain;

    return float4(linearToSrgb(finalCol), 1.0);
}
