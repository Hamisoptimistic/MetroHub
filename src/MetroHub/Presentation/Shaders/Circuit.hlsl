sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=0, W=0
float4 EffectParams : register(c1); // X=GridScale (7.0), Y=Speed (1.0), Z=Brightness (1.0), W=Unused

static const float2 Directions[4] = {
    float2(1.0, 0.0),
    float2(0.0, 1.0),
    float2(-1.0, 0.0),
    float2(0.0, -1.0)
};

float capsule(float2 p, float2 a, float2 b)
{
    float2 pa = p - a;
    float2 ab = b - a;
    float h = saturate(dot(pa, ab) / dot(ab, ab));
    float2 q = abs(pa - h * ab);

    // Octagon distance metric
    return max(max(q.x, q.y), q.x + q.y);
}

float hash2d(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 4000.0);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float gridScale = EffectParams.x > 0.0 ? EffectParams.x : 14.0;
    float speed = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float time = FrameState.x * speed;

    // Centered coordinates matching Shadertoy (2*I - R) / R.y
    float2 p = float2((uv.x - 0.5) * 2.0 * aspect, (uv.y - 0.5) * 2.0);

    float2 gridCoord = p * gridScale;
    float2 v = frac(gridCoord) - 0.5;
    float2 cell = floor(gridCoord);

    // Signed distance field: union of center-to-neighbor segments
    float d = 9.0;
    float n = 0.0;

    for (int i = 0; i < 4; ++i)
    {
        float2 s = Directions[i];
        float wave = 0.5 + 0.05 * sin(time - length(cell + s * 0.5 - 1.0) * 2.0);

        if (hash2d(cell + s * 0.5) < wave)
        {
            d = min(d, capsule(v, float2(0.0, 0.0), s));
            n += 1.0;
        }
    }

    // Isolines of the distance field
    float k = floor(d * 10.0);
    float d_iso = smoothstep(0.1, 0.0, abs(frac(d * 4.0) - 0.5)) * step(k, 3.0);

    // Pad at a dead end
    if (n == 1.0)
    {
        d_iso = max(d_iso, smoothstep(0.1, 0.0, length(v) - 0.1));
    }

    // Color: cyan-blue to neon magenta
    float3 col = d_iso * lerp(float3(0.133, 0.596, 0.886), float3(0.898, 0.122, 0.831), step(2.5, k));
    col *= brightness;

    return float4(saturate(col), 1.0);
}

