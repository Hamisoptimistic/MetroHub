#include "ShaderPalettes.hlsl"

sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=0 (Unused), W=0 (Unused)
float4 EffectParams : register(c1); // X=Speed (1.0), Y=Scale (1.0), Z=Intensity (1.0), W=ColorPalette (0.0 to 5.0)

float length2(float2 p)
{
    return dot(p, p);
}

float noise(float2 p)
{
    return frac(sin(frac(sin(p.x) * 43.13311) + p.y) * 31.0011);
}

float worley(float2 p)
{
    float d = 1e30;
    for (int xo = -1; xo <= 1; ++xo)
    {
        for (int yo = -1; yo <= 1; ++yo)
        {
            float2 tp = floor(p) + float2(xo, yo);
            d = min(d, length2(p - tp - noise(tp)));
        }
    }
    return 3.0 * exp(-4.0 * abs(2.5 * d - 1.0));
}

float fworley(float2 p, float time)
{
    return sqrt(sqrt(sqrt(
        worley(p * 5.0 + 0.05 * time) *
        sqrt(worley(p * 50.0 + 0.12 - 0.1 * time)) *
        sqrt(sqrt(worley(p * -10.0 + 0.03 * time))))));
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 1.0;
    float scale = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float intensity = EffectParams.z > 0.0 ? EffectParams.z : 1.0;

    float time = FrameState.x * speed;
    float2 p = float2(uv.x * FrameState.y, uv.y) * (scale * 1.3);

    float t = fworley(p, time) * intensity;
    
    // Original corner gradient softened so the dark corner spreads significantly less
    t *= exp(-0.25 * length2(abs(0.7 * uv - 1.0)));

    PastelPalette pal = GetPastelPalette((int)(EffectParams.w + 0.5));

    // Caustic contrast curves:
    // c1 (ribbon body): power curve creates deep contrast between dark water valleys and pastel wave ribbons
    // c2 (crest glint): steep power curve creates sharp, sparkling luminous photon highlights
    float c1 = pow(saturate(t), 2.8);
    float c2 = pow(saturate(t), 6.0);
    float ribbonAlpha = saturate(c1 * 1.5);

    float3 col = lerp(pal.ambient, pal.ribbon, ribbonAlpha) + pal.highlight * (c2 * 1.1);

    return float4(saturate(col), 1.0);
}
