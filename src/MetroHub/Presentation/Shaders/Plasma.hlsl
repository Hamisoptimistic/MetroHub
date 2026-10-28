// Plasma (Domain Warping procedural fluid)
// Ported to Direct3D 9 ps_3_0 for MetroHub
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Scale (3.0), Y=Speed (1.0), Z=Brightness (1.0), W=Unused

#define NUM_OCTAVES 5

float2 rotate2D(float2 v, float a)
{
    float c = cos(a);
    float s = sin(a);
    return float2(v.x * c - v.y * s, v.x * s + v.y * c);
}

// Hash without Sine (Dave Hoskins) - prevents precision aliasing and line artifacts on GPUs
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

float fbm(float2 pos)
{
    float v = 0.0;
    float a = 0.5;
    float2 shift = float2(100.0, 100.0);
    [unroll]
    for (int i = 0; i < NUM_OCTAVES; i++)
    {
        v += a * noise(pos);
        pos = rotate2D(pos, 0.5) * 2.0 + shift;
        a *= 0.5;
    }
    return v;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float scale = EffectParams.x > 0.0 ? EffectParams.x : 3.0;
    float speed = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float2 p = float2((uv.x - 0.5) * aspect, uv.y - 0.5) * scale;
    float time2 = 0.3 * FrameState.x * speed;

    float2 q;
    q.x = fbm(p + 0.30 * time2);
    q.y = fbm(p + float2(1.0, 1.0));

    float2 r;
    r.x = fbm(p + q + float2(1.2, 3.2) + 0.135 * time2);
    r.y = fbm(p + q + float2(8.8, 2.8) + 0.126 * time2);

    float f = fbm(p + r);

    float3 color = lerp(
        float3(0.0, 0.0, 0.0),
        float3(1.0, 0.0, 1.0),
        clamp((f * f) * 8.0, 0.0, 5.0)
    );

    color = lerp(
        color,
        float3(0.0, 0.0, 1.0),
        clamp(length(q), 0.0, 1.0)
    );

    color = lerp(
        color,
        float3(0.3, 1.0, 1.0),
        clamp(length(r.x), 0.0, 1.0)
    );

    color = (f * f * f + 0.6 * f * f + 0.9 * f) * color;

    return float4(saturate(color * brightness), 1.0);
}

