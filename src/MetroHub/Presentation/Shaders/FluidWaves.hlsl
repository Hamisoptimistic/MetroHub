sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=0, W=0
float4 EffectParams : register(c1); // X=Speed (0.6), Y=Scale (1.0), Z=Brightness (1.0), W=Unused

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 0.6;
    float scale = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;
    float time = FrameState.x * speed;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float2 p = (uv - 0.5) * float2(aspect, 1.0) * (8.0 * scale);

    float i0 = 1.0;
    float i1 = 1.0;
    float i2 = 1.0;
    float i4 = 0.0;

    for (int s = 0; s < 7; ++s)
    {
        float2 r;
        r = float2(cos(p.y * i0 - i4 + time / i1), sin(p.x * i0 - i4 + time / i1)) / i2;
        r += float2(-r.y, r.x) * 0.3;
        p += r;

        i0 *= 1.93;
        i1 *= 1.15;
        i2 *= 1.7;
        i4 += 0.05 + 0.1 * time * i1;
    }

    float r = sin(p.x - time) * 0.5 + 0.5;
    float b = sin(p.y + time) * 0.5 + 0.5;
    float g = sin((p.x + p.y + sin(time * 0.5)) * 0.5) * 0.5 + 0.5;

    float3 color = float3(r, g, b) * brightness;
    return float4(saturate(color), 1.0);
}

