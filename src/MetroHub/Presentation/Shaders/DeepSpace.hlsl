sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=TimeOfDaySeconds, W=0
float4 EffectParams : register(c1); // X=VirtualResolution (1080.0), Y=Speed (1.0), Z=Brightness (1.0), W=0

// Cold and hot star spectrum anchors
static const float3 cold = float3(255.0, 244.0, 189.0) / 255.0;
static const float3 hot  = float3(181.0, 236.0, 255.0) / 255.0;

// Dave Hoskins hash functions
float4 hash42(float2 p)
{
    float4 p4 = frac(p.xyxy * float4(0.1031, 0.1030, 0.0973, 0.1099));
    p4 += dot(p4, p4.wzxy + 19.19);
    return frac((p4.xxyz + p4.yzzw) * p4.zywx) - 0.5;
}

float2 hash22(float2 p)
{
    float3 p3 = frac(p.xyx * float3(443.897, 441.423, 437.195));
    p3 += dot(p3, p3.yzx + 19.19);
    return -1.0 + 2.0 * frac((p3.xx + p3.yz) * p3.zy);
}

// IQ's Gradient Noise
float Gradient2D(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);

    return lerp(lerp(dot(hash22(i + float2(0.0, 0.0)), f - float2(0.0, 0.0)),
                     dot(hash22(i + float2(1.0, 0.0)), f - float2(1.0, 0.0)), u.x),
                lerp(dot(hash22(i + float2(0.0, 1.0)), f - float2(0.0, 1.0)),
                     dot(hash22(i + float2(1.0, 1.0)), f - float2(1.0, 1.0)), u.x), u.y);
}

float3 StarFieldLayer(float2 p, float du, float count, float brightness, float size)
{
    // Tiling:
    du *= count;
    p *= count;
    float2 pi = floor(p);
    p = frac(p) - 0.5;

    // Randomize position, brightness and spectrum:
    float4 h = hash42(pi);

    // Resolution independent radius:
    float s = brightness * (0.7 + 0.6 * h.z) * smoothstep(0.8 * du, -0.2 * du, length(p + 0.9 * h.xy) - size * du);

    return s * lerp(lerp(float3(1.0, 1.0, 1.0), cold, min(1.0, -2.0 * h.w)), hot, max(0.0, 2.0 * h.w));
}

float3 StarField(float2 p, float du)
{
    float3 c = StarFieldLayer(p, du, 25.0, 0.18, 0.5);
    c += StarFieldLayer(p, du, 15.0, 0.25, 0.5);
    c += StarFieldLayer(p, du, 12.0, 0.50, 0.5);
    c += StarFieldLayer(p, du,  5.0, 1.00, 0.5);
    c += StarFieldLayer(p, du,  3.0, 1.00, 0.9);

    // Cluster:
    float s = 3.5 * (max(0.2, Gradient2D(2.0 * p * float2(1.2, 1.9))) - 0.2) / (1.0 - 0.2);
    c += s * StarFieldLayer(p, du, 160.0, 0.10, 0.5);
    c += s * StarFieldLayer(p, du,  80.0, 0.15, 0.5);
    c += s * StarFieldLayer(p, du,  40.0, 0.25, 0.5);
    c += s * StarFieldLayer(p, du,  30.0, 0.50, 0.5);
    c += s * StarFieldLayer(p, du,  20.0, 1.00, 0.5);
    c += s * StarFieldLayer(p, du,  10.0, 1.00, 0.9);

    c *= 1.3;

    // Resolution independent brightness:
    float f = 1.0 / sqrt(660.0 * du);

    return f * min(c, 1.0);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float virtualResolution = EffectParams.x > 0.0 ? EffectParams.x : 1080.0;
    float speed = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;
    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);

    float du = 1.0 / virtualResolution;
    float2 centered = float2((uv.x - 0.5) * aspect, 0.5 - uv.y);
    float2 p = centered + 1.33 + (FrameState.x * speed) / 30.0;

    float3 color = StarField(p, du) * brightness;
    return float4(saturate(color), 1.0);
}

