// Smoke or Fog by Ultipuk (https://godotshaders.com/shader/smoke-or-fog)
// Based on "Wind of change" by FatumR (https://www.shadertoy.com/view/ldBSDd)
// Ported to Direct3D 9 ps_3_0 for MetroHub
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Scale (2.0), Y=Speed (1.0), Z=Brightness (1.0), W=Unused

static const float smoke_persistance = 2.0;
static const float smoke_amplitude = 0.55;
static const float smoke_starting_offset = 0.02;

float rand2(float2 co)
{
    return frac(cos(dot(co, float2(12.9898, 78.233))) * 43758.5453);
}

// 2D Value Noise
float value_noise_simple(float2 vl)
{
    float2 grid = floor(vl);
    float2 f = frac(vl);
    f = f * f * (3.0 - 2.0 * f);

    float s = rand2(grid);
    float t = rand2(grid + float2(1.0, 0.0));
    float u = rand2(grid + float2(0.0, 1.0));
    float v = rand2(grid + float2(1.0, 1.0));

    return lerp(lerp(s, t, f.x), lerp(u, v, f.x), f.y);
}

// Multi-octave fractal noise
float fractal_noise(float2 vl)
{
    float persistance = smoke_persistance;
    float amplitude = smoke_amplitude;
    float result = smoke_starting_offset;
    float2 p = vl;

    [unroll]
    for (int i = 0; i < 5; ++i)
    {
        result += amplitude * value_noise_simple(p);
        amplitude /= persistance;
        p *= persistance;
    }

    return result;
}

// 4-level nested domain warping for realistic fluid smoke curl
float complex_fbm(float2 p, float time, float speed)
{
    float slow = time * 0.35 * speed;
    float fast = time * 0.15 * speed;
    float2 offset_1 = float2(slow, 0.0);
    float2 offset_2 = float2(sin(fast) * 0.1, 0.0);

    return fractal_noise(
        p + offset_1 + fractal_noise(
            p + fractal_noise(
                p + 2.0 * fractal_noise(
                    p - offset_2
                )
            )
        )
    );
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float scale = EffectParams.x > 0.0 ? EffectParams.x : 2.0;
    float speed = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;
    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);

    // Normalized coordinates preserving circular smoke curls across aspect ratios
    float2 p = float2(uv.x * aspect, uv.y) * scale;
    float fbm = complex_fbm(p, FrameState.x, speed);

    // Procedural atmospheric gradient (Deep Midnight Navy -> Misty Slate Blue -> Cyan Mist)
    float3 colDeep = float3(0.02, 0.04, 0.09);
    float3 colMid  = float3(0.10, 0.22, 0.40);
    float3 colHigh = float3(0.65, 0.82, 0.95);

    float3 color = lerp(colDeep, colMid, saturate(fbm * 1.15));
    color = lerp(color, colHigh, saturate((fbm - 0.48) * 1.75));

    return float4(saturate(color * brightness), 1.0);
}

