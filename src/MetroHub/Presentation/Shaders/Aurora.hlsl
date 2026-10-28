// Aurora Borealis (Northern Lights over Reflective Water)
// Based on nimitz (GLSLSandbox 103181.2 / Shadertoy XtGGRt)
// Direct3D 9 ps_3_0 for MetroHub
// Balanced high-fidelity volumetric rendering with zero banding & faithful color grading
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Speed (1.0), Y=Brightness (1.0), Z=Unused, W=Unused

float2 rotate2D(float2 v, float a)
{
    float c = cos(a);
    float s = sin(a);
    return float2(v.x * c - v.y * s, v.x * s + v.y * c);
}

float tri(float x)
{
    return clamp(abs(frac(x) - 0.5), 0.01, 0.49);
}

float2 tri2(float2 p)
{
    return float2(tri(p.x) + tri(p.y), tri(p.y + tri(p.x)));
}

// 4-octave procedural triangle noise for crisp, flowing curtain ribbons
float triNoise2d(float2 p, float2 rotCS)
{
    float z = 1.8;
    float invZ2 = 0.4; // 1.0 / 2.5
    float rz = 0.0;
    p = rotate2D(p, p.x * 0.06);
    float2 bp = p;

    [unroll]
    for (int i = 0; i < 4; i++)
    {
        float2 dg = tri2(bp * 1.85) * 0.75;
        dg = float2(dg.x * rotCS.x - dg.y * rotCS.y, dg.x * rotCS.y + dg.y * rotCS.x);
        p -= dg * invZ2;

        bp *= 1.3;
        invZ2 *= 2.2222; // 1.0 / 0.45
        z *= 0.42;
        p *= 1.21 + (rz - 1.0) * 0.02;

        rz += tri(p.x + tri(p.y)) * z;
        p = -float2(p.x * 0.95534 - p.y * 0.29552, p.x * 0.29552 + p.y * 0.95534);
    }
    return clamp(1.0 / pow(max(rz * 29.0, 0.0001), 1.3), 0.0, 0.55);
}

float hash12(float2 p)
{
    float3 q = frac(p.xyx * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

float3 hash33(float3 p)
{
    p = frac(p * float3(443.8975, 397.2973, 491.1871));
    p += dot(p.zxy, p.yxz + 19.27);
    return frac(float3(p.x * p.y, p.z * p.x, p.y * p.z));
}

// 4-layer starry night sky
float3 stars(float3 p, float resX)
{
    float3 c = 0.0;
    [unroll]
    for (float i = 0.0; i < 4.0; i += 1.0)
    {
        float3 q = frac(p * (0.15 * resX)) - 0.5;
        float3 id = floor(p * (0.15 * resX));
        float2 rn = hash33(id).xy;
        float c2 = 1.0 - smoothstep(0.0, 0.6, length(q));
        c2 *= step(rn.x, 0.0005 + i * i * 0.001);
        c += c2 * (lerp(float3(1.0, 0.49, 0.1), float3(0.75, 0.9, 1.0), rn.y) * 0.1 + 0.9);
        p *= 1.3;
    }
    return c * c * 0.8;
}

// Faithful deep space background
float3 bg(float3 rd)
{
    float sd = dot(normalize(float3(-0.5, -0.6, 0.9)), rd) * 0.5 + 0.5;
    sd = pow(sd, 5.0);
    float3 col = lerp(float3(0.05, 0.1, 0.2), float3(0.1, 0.05, 0.2), sd);
    return col * 0.63;
}

// Volumetric ray integration with coherent per-ray dithering & full spectral palette
float4 aurora(float3 ro, float3 rd, float dither, float2 rotCS, int numSteps)
{
    float4 col = 0.0;
    float4 avgCol = 0.0;
    float stepScale = 50.0 / float(numSteps);

    [loop]
    for (int i = 0; i < 36; i++)
    {
        if (i >= numSteps) break;
        float fi = float(i) * stepScale;
        float of = 0.006 * dither * smoothstep(0.0, 15.0, fi);
        float pt = ((0.8 + pow(fi, 1.4) * 0.002) - ro.y) / (rd.y * 2.0 + 0.4);
        pt -= of;
        float3 bpos = ro + pt * rd;
        float2 p = bpos.zx;
        float rzt = triNoise2d(p, rotCS);
        float4 col2 = float4(0, 0, 0, rzt);

        // Exact original multi-spectral color gradient (emerald -> cyan -> blue -> violet/magenta)
        col2.rgb = (sin(1.0 - float3(2.15, -0.5, 1.2) + fi * 0.043) * 0.5 + 0.5) * rzt;
        avgCol = lerp(avgCol, col2, 0.5);
        col += avgCol * exp2(-fi * 0.065 - 2.5) * smoothstep(0.0, 5.0, fi) * stepScale;
    }

    col *= saturate(rd.y * 15.0 + 0.4);
    return col * 1.8;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 1.0;
    float brightness = EffectParams.y > 0.0 ? EffectParams.y : 1.0;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float timeVal = FrameState.x * speed;

    float2 p = float2((uv.x - 0.5) * aspect, 0.5 - uv.y);
    float3 ro = float3(0.0, 0.0, -6.7);
    float3 rd = normalize(float3(p, 1.3));

    // Hands-free camera: gentle sway with serene upward tilt towards the aurora
    float sway = sin(timeVal * 0.05) * 0.15;
    rd.xz = rotate2D(rd.xz, sway);
    rd.yz = rotate2D(rd.yz, -0.15);

    float3 col = 0.0;
    float3 brd = rd;
    float fade = smoothstep(0.0, 0.01, abs(brd.y)) * 0.1 + 0.9;
    col = bg(rd) * fade;

    float2 fragCoord = uv * float2(1920.0, 1080.0);
    float dither = hash12(fragCoord);

    // Precalculate noise rotation once per pixel
    float rotAngle = timeVal * 10.0 * 0.06;
    float2 rotCS = float2(cos(rotAngle), sin(rotAngle));

    if (rd.y > 0.0)
    {
        // 32 dense steps for silky smooth volumetric curtains with zero horizontal bands
        float4 aur = smoothstep(0.0, 1.5, aurora(ro, rd, dither, rotCS, 32)) * fade;
        col += stars(rd, 1920.0);
        col = col * (1.0 - aur.a) + aur.rgb;
    }
    else
    {
        // Water reflection: 14 steps (ripples blur water naturally)
        rd.y = abs(rd.y);
        col = bg(rd) * fade * 0.6;
        float4 aur = smoothstep(0.0, 2.5, aurora(ro, rd, dither, rotCS, 14));
        col += stars(rd, 1920.0) * 0.1;
        col = col * (1.0 - aur.a) + aur.rgb;
        float3 pos = ro + ((0.5 - ro.y) / rd.y) * rd;
        float2 waveCS = float2(1.0, 0.0);
        float nz2 = triNoise2d(pos.xz * float2(0.5, 0.7), waveCS);
        col += lerp(float3(0.2, 0.25, 0.5) * 0.08, float3(0.3, 0.3, 0.5) * 0.7, nz2 * 0.4);
    }

    return float4(saturate(col * brightness), 1.0);
}
