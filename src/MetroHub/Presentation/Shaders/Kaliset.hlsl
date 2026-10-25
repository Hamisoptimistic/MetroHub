// Kaliset by Piers Haken (http://www.fractalforums.com/new-theories-and-research/very-simple-formula-for-fractal-patterns/)
// Ported to Direct3D 9 ps_3_0 for MetroHub with Hybrid Mouse Steering
sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams : register(c1); // X=Scale (3.0), Y=Speed (1.0), Z=Brightness (1.0), W=MouseInfluence (0.75)
float4 MouseState : register(c2);   // X=MouseX (0..1), Y=MouseY (0..1), Z=LeftDown, W=RightDown

static const float PI = 3.141592653589793;
static const int max_iteration = 20;

float cabs(float2 c)
{
    return dot(c, c);
}

float2 cmul(float2 c1, float2 c2)
{
    return float2(
        c1.x * c2.x - c1.y * c2.y,
        c1.x * c2.y + c1.y * c2.x
    );
}

float3 Hue(float H)
{
    H *= 6.0;
    return saturate(float3(
        abs(H - 3.0) - 1.0,
        2.0 - abs(H - 2.0),
        2.0 - abs(H - 4.0)
    ));
}

float3 HSVtoRGB(float h, float s, float v)
{
    return ((Hue(h) - 1.0) * s + 1.0) * v;
}

float wave(float t)
{
    return 0.5 - 0.5 * cos(t);
}

float2 circle(float a)
{
    return float2(cos(a), sin(a));
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float scale = EffectParams.x > 0.0 ? EffectParams.x : 3.0;
    float speed = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float brightness = EffectParams.z > 0.0 ? EffectParams.z : 1.0;
    float mouseInf = EffectParams.w > 0.0 ? EffectParams.w : 0.75;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float t = FrameState.x * 0.45 * speed;

    // Active Folding Sweet Spot: continuously blooms without flat dead zones
    float2 c = float2(-0.55, -0.55) + float2(sin(t * 0.7), cos(t * 0.5)) * 0.35;

    // Centered screen coordinate with aspect ratio compensation
    float2 z = float2((uv.x - 0.5) * aspect, uv.y - 0.5) * scale;
    float2 R = circle(t / 5.123);

    float color = 0.0;

    [unroll]
    for (int i = 0; i < max_iteration; ++i)
    {
        float2 zold = z;
        float m = max(cabs(z), 1e-5);
        z = abs(cmul(z, R)) / m + c;

        color += exp(-cabs(z));
        color += exp(-1.0 / max(cabs(zold - z), 1e-5));
    }

    color /= (float)max_iteration;

    // Rich color tuning: saturation clamped >= 0.70 to eliminate washed-out white phases
    float hue = wave(color + t / 10.0);
    float sat = 0.70 + 0.30 * wave(color * 3.7891);
    float val = 0.35 + 0.65 * wave(color * 13.3234);

    float3 rgb = HSVtoRGB(hue, sat, val);

    return float4(saturate(rgb * brightness), 1.0);
}

