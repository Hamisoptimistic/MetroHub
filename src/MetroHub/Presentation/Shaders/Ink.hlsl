// Ink (Live Flowing Ink Droplets) for MetroHub
// Based on ittybitty.dev Ink by Blake Cyze
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time, Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Speed (0.6), Y=Style (0=ink, 1=smoke, 2=paint), Z=Brightness (1.0), W=0
float4 MouseState : register(c2);   // X=MouseX, Y=MouseY, Z=LeftDown, W=0
float4 Drops[24] : register(c4);    // 24 procedural ink drops: (X, Y, Age, Alive)

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

float3 oklabToLinear(float3 lab)
{
    float3x3 m1 = float3x3(
        1.0, 0.3963377774, 0.2158037573,
        1.0, -0.1055613458, -0.0638541728,
        1.0, -0.0894841775, -1.2914855480
    );
    float3 lms = mul(m1, lab);
    float3x3 m2 = float3x3(
        4.0767416621, -3.3077115913, 0.2309699292,
        -1.2684380046, 2.6097574011, -0.3413193965,
        -0.0041960863, -0.7034186147, 1.7076147010
    );
    return mul(m2, lms * lms * lms);
}

float3 linearToSrgb(float3 c)
{
    c = max(c, 0.0);
    return lerp(c * 12.92, 1.055 * pow(c, (1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

// 4-stop gradient palette (Gold -> Rose Pink -> Violet -> Midnight Purple)
float3 inkPalette(float x)
{
    // Stops: 0.0: #ffe3a3, 0.35: #ff5c8a, 0.70: #6a3bff, 1.0: #1b1840 (in Oklab)
    float4 s0 = float4(0.924202, 0.004541, 0.086545, 0.00);
    float4 s1 = float4(0.700722, 0.198634, 0.023117, 0.35);
    float4 s2 = float4(0.541895, 0.065021, -0.257370, 0.70);
    float4 s3 = float4(0.238396, 0.016126, -0.071816, 1.00);

    float3 c = s0.rgb;
    c = lerp(c, s1.rgb, saturate((x - s0.a) / max(s1.a - s0.a, 1e-4)));
    c = lerp(c, s2.rgb, saturate((x - s1.a) / max(s2.a - s1.a, 1e-4)));
    c = lerp(c, s3.rgb, saturate((x - s2.a) / max(s3.a - s2.a, 1e-4)));
    return c;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 0.6;
    float style = EffectParams.y; // 0=ink, 1=smoke, 2=paint
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float t = FrameState.x * speed;

    float2 p = float2(uv.x * aspect, 1.0 - uv.y);

    // Curl flow field
    float2 flow = float2(noise(p * 3.0 + t * 0.2), noise(p * 3.0 + 7.3 - t * 0.2)) - 0.5;

    float density = 0.0;
    float ageAcc = 0.0;

    float size = 0.07;
    float spread = 1.2;
    float fade = 0.8;
    float curl = 0.7;

    for (int i = 0; i < 24; ++i)
    {
        float4 d = Drops[i];
        if (d.w <= 0.001) continue;

        float dropAge = d.z;
        float a = 1.0 + dropAge * spread;
        float radius = size * a;
        float curlAmt = curl * 0.15 * min(dropAge, 3.0);
        float2 q = p + flow * curlAmt - d.xy;

        float w = d.w * exp(-dropAge * fade) / a * exp2(-dot(q, q) * (1.442695 / (radius * radius)));
        density += w;
        ageAcc += w * dropAge;
    }

    float normAge = ageAcc / max(density, 1e-4);
    float3 inkLab = inkPalette(saturate(normAge * 0.45));
    float3 inkLinear = max(oklabToLinear(inkLab), 0.0);

    float cover = 1.0 - exp(-density * 1.6);
    if (style > 1.5)
    {
        cover = smoothstep(0.42, 0.5, cover); // Paint mode: crisp edges
    }

    float3 bgLinear = float3(0.003, 0.003, 0.006); // #0b0b12 linear
    float3 colour = (style > 0.5 && style < 1.5)
        ? bgLinear + inkLinear * cover * 1.4      // Smoke mode: glowing additive
        : lerp(bgLinear, inkLinear, cover);       // Ink mode: fluid absorption

    float3 finalRgb = linearToSrgb(colour);
    finalRgb += (hash12(uv * 1080.0) - 0.5) / 255.0;

    return float4(saturate(finalRgb * brightness), 1.0);
}
