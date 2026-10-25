// Ether by nimitz 2014 (twitter: @stormoid)
// Ported to Direct3D 9 ps_3_0 for MetroHub
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Zoom (1.1), Y=Speed (0.8), Z=Brightness (1.0), W=Unused

float2 rotate2D(float2 v, float a)
{
    float c = cos(a);
    float s = sin(a);
    return float2(v.x * c - v.y * s, v.x * s + v.y * c);
}

float map(float3 p, float t)
{
    p.xz = rotate2D(p.xz, t * 0.4);
    p.xy = rotate2D(p.xy, t * 0.3);
    float3 q = p * 2.0 + t;
    return length(p + float3(sin(t * 0.7), sin(t * 0.7), sin(t * 0.7))) * log(length(p) + 1.0)
         + sin(q.x + sin(q.z + sin(q.y))) * 0.5 - 1.0;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float zoom = EffectParams.x > 0.0 ? EffectParams.x : 1.1;
    float speed = EffectParams.y > 0.0 ? EffectParams.y : 0.8;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float t = FrameState.x * speed;

    // Centered coordinates with aspect ratio compensation and wide FOV zoom
    float2 p = float2((uv.x - 0.5) * aspect, 0.5 - uv.y) * zoom;
    float3 rayDir = normalize(float3(p, -1.0));

    float3 cl = float3(0.0, 0.0, 0.0);
    float d = 2.5;

    [unroll]
    for (int i = 0; i <= 5; ++i)
    {
        float3 pos = float3(0.0, 0.0, 5.0) + rayDir * d;
        float rz = map(pos, t);
        float f = clamp((rz - map(pos + 0.1, t)) * 0.5, -0.1, 1.0);
        float3 l = float3(0.1, 0.3, 0.4) + float3(5.0, 2.5, 3.0) * f;
        cl = cl * l + smoothstep(2.5, 0.0, rz) * 0.7 * l;
        d += min(rz, 1.0);
    }

    return float4(saturate(cl * brightness), 1.0);
}

