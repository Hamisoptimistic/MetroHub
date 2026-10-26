// CyberTunnel (Ether Rainbow Mod) by Shadertoy DsVSRy
// Ported to Direct3D 9 ps_3_0 for MetroHub
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Speed (0.8), Y=Zoom (1.0), Z=Brightness (1.0), W=Unused

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
    float s = sin(t * 0.7);
    return length(p + float3(s, s, s)) * log(length(p) + 1.0)
         + sin(q.x + sin(q.z + sin(q.y))) * 0.5 - 1.0;
}

float3 hslToRgb(float3 hsl)
{
    float3 rgb = saturate(abs(fmod(hsl.x * 6.0 + float3(0.0, 4.0, 2.0), 6.0) - 3.0) - 1.0);
    return hsl.z + hsl.y * (rgb - 0.5) * (1.0 - abs(2.0 * hsl.z - 1.0));
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float speed = EffectParams.x > 0.0 ? EffectParams.x : 0.8;
    float zoom = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float t = FrameState.x * speed;

    // Centered coordinates with aspect ratio compensation (1x native resolution)
    float2 p = float2((uv.x - 0.5) * aspect, 0.5 - uv.y) * zoom;
    float3 rayDir = normalize(float3(p, -1.0));

    float3 cl = float3(0.0, 0.0, 0.0);
    float d = 4.0;

    [unroll]
    for (int i = 0; i <= 5; ++i)
    {
        float3 pos = float3(0.0, 0.0, 5.0) + rayDir * d;
        float rz = map(pos, t);
        float f = clamp((rz - map(pos + 0.1, t)) * 0.5, -0.1, 1.0);

        // Dynamic rainbow hue wave with green suppression (synthwave cyberpunk palette)
        float hue = frac(t * 0.125 + float(i) / 5.0);
        hue = smoothstep(0.0, 0.5, hue) + 0.3;
        float3 col = hslToRgb(float3(hue, 1.0, 0.5));

        float3 l = col + float3(5.0, 2.5, 3.0) * f;
        cl = cl * l + smoothstep(2.5, 0.0, rz) * 0.7 * l;
        d += min(rz, 1.0);
    }

    // Suppress green channel by 8x (0.125) for the signature deep violet/magenta/cyan tone
    float3 outColor = float3(cl.r, cl.g * 0.125, cl.b) * brightness;
    return float4(saturate(outColor), 1.0);
}

