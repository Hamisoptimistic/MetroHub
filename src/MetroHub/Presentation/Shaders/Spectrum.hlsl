// =============================================================================
// Spectrum.hlsl - MetroHub Dispersion & Prism Spectrum Backdrop Shader
// Ported from ittybitty.dev WebGL Spectrum shader by Blake Cyze (MIT License).
// Direct3D 9Ex HLSL (Shader Model 3.0).
// Simulates light split by a prism into 16 wavelengths, refracted through
// rough glass, and captured on film stock with OKLab tone mapping.
// Standard Registers:
//   s0: Input texture brush (unused for procedural backdrop, required by WPF)
//   c0: FrameState (X=Time, Y=AspectRatio, Z=TimeOfDay, W=Unused)
//   c1: EffectParams (X=Preset: 0=North Small, 1=Prism, 2=Sunset; Y=Intensity; Z=Speed; W=Dispersion)
//   c2: MouseState (X=MouseX, Y=MouseY, Z=LeftDown, W=Unused)
// =============================================================================

sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);
float4 EffectParams : register(c1);
float4 MouseState : register(c2);

#define N 16

float hash12(float2 p)
{
    float3 q = frac(p.xyx * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

float hashFrom(float x, float y)
{
    float3 q = float3(x, y, x);
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float2 x = frac(float2(i.x, i.x + 1.0) * 0.1031);
    float2 y = frac(float2(i.y, i.y + 1.0) * 0.1031);
    float a = hashFrom(x.x, y.x);
    float b = hashFrom(x.y, y.x);
    float c = hashFrom(x.x, y.y);
    float d = hashFrom(x.y, y.y);
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float2 turn_mul(float2 p)
{
    return float2(1.624 * p.x + 1.218 * p.y, -1.218 * p.x + 1.624 * p.y);
}

float fbm2(float2 p)
{
    float s = -0.375;
    s += 0.5 * noise(p);
    p = turn_mul(p) + 17.1;
    s += 0.25 * noise(p);
    return s;
}

float fbm3(float2 p)
{
    float s = -0.4375;
    s += 0.5 * noise(p);
    p = turn_mul(p) + 17.1;
    s += 0.25 * noise(p);
    p = turn_mul(p) + 17.1;
    s += 0.125 * noise(p);
    return s;
}

float fbm4(float2 p)
{
    float s = -0.46875;
    s += 0.5 * noise(p);
    p = turn_mul(p) + 17.1;
    s += 0.25 * noise(p);
    p = turn_mul(p) + 17.1;
    s += 0.125 * noise(p);
    p = turn_mul(p) + 17.1;
    s += 0.0625 * noise(p);
    return s;
}

float3 chroma(float3 c, float k)
{
    float3x3 m1 = float3x3(
        0.4122214708, 0.5363325363, 0.0514459929,
        0.2119034982, 0.6806995451, 0.1073969566,
        0.0883024619, 0.2817188376, 0.6299787005
    );
    float3 lms = mul(m1, c);
    lms = exp2(log2(max(lms, 1e-4)) * (1.0 / 3.0));
    float lum = dot(lms, float3(0.2104542553, 0.7936177850, -0.0040720468));
    lms = lerp(float3(lum, lum, lum), lms, k);
    float3x3 m2 = float3x3(
        4.0767416621, -3.3077115913,  0.2309699292,
       -1.2684380046,  2.6097574011, -0.3413193965,
       -0.0041960863, -0.7034186147,  1.7076147010
    );
    return mul(m2, lms * lms * lms);
}

float3 linearToSrgb(float3 c)
{
    c = max(c, 0.0);
    return lerp(c * 12.92, 1.055 * exp2(log2(max(c, 1e-4)) * (1.0 / 2.4)) - 0.055, step(0.0031308, c));
}

void GetSpectrumKnots(int preset, out float4 sp0, out float4 sp1, out float4 sp2, out float4 sp3,
                                  out float4 sp4, out float4 sp5, out float4 sp6, out float4 sp7,
                                  out float4 sp8, out float4 sp9, out float4 sp10, out float4 sp11)
{
    if (preset == 0) // North Small (Film Palette)
    {
        sp0  = float4(0.0385, 0.0000, 0.0000, 0.0000);
        sp1  = float4(0.0000, 0.0000, 0.0000, 0.0000);
        sp2  = float4(0.0057, 0.0279, 0.0998, 0.1733);
        sp3  = float4(0.1936, 0.1489, 0.0828, 0.0296);

        sp4  = float4(0.0117, 0.0387, 0.0778, 0.0997);
        sp5  = float4(0.1045, 0.1056, 0.1063, 0.1067);
        sp6  = float4(0.1067, 0.1078, 0.1113, 0.1023);
        sp7  = float4(0.0726, 0.0348, 0.0115, 0.0019);

        sp8  = float4(0.1177, 0.1408, 0.1100, 0.0635);
        sp9  = float4(0.0236, 0.0184, 0.0134, 0.0085);
        sp10 = float4(0.0039, 0.0000, 0.0000, 0.0000);
        sp11 = float4(0.0000, 0.0000, 0.0000, 0.0001);
    }
    else if (preset == 1) // Prism (Vivid Palette)
    {
        sp0  = float4(0.0233, 0.0051, 0.0037, 0.0023);
        sp1  = float4(0.0002, 0.0000, 0.0000, 0.0000);
        sp2  = float4(0.0039, 0.0117, 0.0200, 0.1176);
        sp3  = float4(0.2519, 0.2372, 0.2174, 0.1056);

        sp4  = float4(0.0039, 0.0103, 0.0142, 0.0185);
        sp5  = float4(0.0239, 0.0627, 0.1268, 0.1416);
        sp6  = float4(0.1453, 0.1490, 0.1527, 0.0868);
        sp7  = float4(0.0321, 0.0195, 0.0102, 0.0024);

        sp8  = float4(0.1092, 0.1681, 0.1719, 0.1754);
        sp9  = float4(0.1775, 0.1070, 0.0429, 0.0120);
        sp10 = float4(0.0101, 0.0083, 0.0065, 0.0000);
        sp11 = float4(0.0000, 0.0000, 0.0048, 0.0063);
    }
    else // Sunset (Warm Palette)
    {
        sp0  = float4(0.0108, 0.0000, 0.0380, 0.0732);
        sp1  = float4(0.0801, 0.0863, 0.0916, 0.0962);
        sp2  = float4(0.0997, 0.1019, 0.1030, 0.1024);
        sp3  = float4(0.1001, 0.0873, 0.0718, 0.0576);

        sp4  = float4(0.0082, 0.0418, 0.0218, 0.0049);
        sp5  = float4(0.0092, 0.0148, 0.0217, 0.0308);
        sp6  = float4(0.0420, 0.0551, 0.0692, 0.0848);
        sp7  = float4(0.1024, 0.1051, 0.0987, 0.0895);

        sp8  = float4(0.0682, 0.0278, 0.0000, 0.0110);
        sp9  = float4(0.0083, 0.0059, 0.0039, 0.0023);
        sp10 = float4(0.0012, 0.0011, 0.0007, 0.0004);
        sp11 = float4(0.0015, 0.0052, 0.0091, 0.0122);
    }
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float2 p = (uv - 0.5) * float2(aspect, 1.0);
    float t = FrameState.x * 0.3;

    int preset = (int)(EffectParams.x + 0.5);

    // Preset parameters
    float angleDeg = -50.0;
    int beams = 1;
    float width = 0.18;
    float dispersion = 1.6;
    float mottle = 0.15;
    float textureScale = 1.5;
    float smear = 0.5;
    float intensity = 1.4;
    float3 backdrop = float3(0.10, 0.23, 0.27); // #1a3c44
    float grain = 0.06;

    if (preset == 1) // Prism
    {
        angleDeg = -30.0;
        beams = 1;
        width = 0.08;
        dispersion = 1.6;
        mottle = 0.0;
        intensity = 1.4;
        backdrop = float3(0.02, 0.02, 0.03); // #050508
        grain = 0.03;
    }
    else if (preset == 2) // Sunset
    {
        angleDeg = -15.0;
        beams = 2;
        width = 0.20;
        dispersion = 1.6;
        mottle = 0.25;
        intensity = 2.0;
        backdrop = float3(0.07, 0.02, 0.05); // #12060c
        grain = 0.06;
    }

    if (EffectParams.y > 0.0) intensity *= EffectParams.y;
    if (EffectParams.z > 0.0) t = FrameState.x * (0.3 * EffectParams.z);
    if (EffectParams.w > 0.0) dispersion *= EffectParams.w;

    float rad = angleDeg * 0.0174532925;
    float2 flow = float2(cos(rad), sin(rad));
    float2 nrm = float2(-flow.y, flow.x);

    // Pointer influence
    float2 pointer = MouseState.xy - 0.5;
    float moveX = (pointer.x * -flow.y + pointer.y * flow.x) * 0.4;
    float moveY = (pointer.x * flow.x + pointer.y * flow.y) * 0.3;
    float moveZ = (0.25 + 0.08 * sin(t * 0.05)) * max(1.0 - mottle * 0.8, 0.0);

    float u = dot(p, nrm) - moveX;
    float v = dot(p, flow) - moveY;

    u += moveZ * v * v;
    u += 0.45 * fbm3(float2(v * 0.6 - t * 0.03, u * 0.4 + 3.0));

    float2 fold = float2(
        fbm3(float2(u * 0.8, v * 1.0 - t * 0.04) + 11.0),
        fbm3(float2(u * 0.9 + 4.0, v * 1.0 + t * 0.03) + 23.0)
    );
    u += mottle * 1.2 * fold.x;
    v += mottle * 0.6 * fold.y;

    float stretch = 1.0 + smear * 6.0;
    float2 q = float2(u * textureScale * 3.0, (v + t * 0.06) * textureScale * 3.0 / stretch);
    float rough = fbm4(q + float2(0.0, t * 0.05));
    float rough2 = fbm2(q * 0.25 + float2(5.2, 1.3 - t * 0.04));
    u += mottle * 0.24 * rough;
    float spread = dispersion * 0.55 * (1.0 + mottle * 3.2 * rough2);

    float source = exp(0.8 * v);
    float catchLight = max(0.0, 1.0 + mottle * (1.2 * fbm2(q * 1.4 + 9.0) + 1.6 * fold.y));

    float jitter = hash12(uv * 1000.0 + frac(t) * 91.0) - 0.5;
    float w = max(width * 0.5 * (1.0 + 0.6 * max(0.3 - v, 0.0)), abs(spread) * 0.75 / float(N));
    const float K = 1.2011224;
    float iw = K / w;
    float first = (0.5 + jitter * 0.15) / float(N) - 0.5;
    float aRow = v * (1.3 + mottle * 1.5) - t * 0.05;

    float4 sp0, sp1, sp2, sp3, sp4, sp5, sp6, sp7, sp8, sp9, sp10, sp11;
    GetSpectrumKnots(preset, sp0, sp1, sp2, sp3, sp4, sp5, sp6, sp7, sp8, sp9, sp10, sp11);

    // Quarter sums for film scatter
    float4 qR = float4(dot(sp0, 1.0), dot(sp1, 1.0), dot(sp2, 1.0), dot(sp3, 1.0));
    float4 qG = float4(dot(sp4, 1.0), dot(sp5, 1.0), dot(sp6, 1.0), dot(sp7, 1.0));
    float4 qB = float4(dot(sp8, 1.0), dot(sp9, 1.0), dot(sp10, 1.0), dot(sp11, 1.0));

    float3 light = 0.0;

    [unroll]
    for (int k = 0; k < 2; ++k)
    {
        if (k >= beams) break;

        // Multi-beam positioning and reflection
        float beamOffset = (float)k * 0.18;
        float beamBright = (k == 0) ? 1.0 : 0.65;
        float a = max((0.3 + 1.4 * noise(float2(aRow, float(k) * 7.3))) * beamBright, 0.0);
        float fan = spread;
        float du = u - beamOffset;

        if (abs(du) <= abs(fan) * 0.6 + 7.0 * w && a > 0.0)
        {
            float3 sum = 0.0;
            float x = (du - fan * first) * iw;
            float dx = fan / float(N) * iw;

            float r = exp2(dx * (2.0 * x - dx));
            float rr = exp2(-2.0 * dx * dx);
            float4 e;

            e.x = exp2(-x * x);
            e.y = e.x * r; r *= rr;
            e.z = e.y * r; r *= rr;
            e.w = e.z * r; r *= rr;
            sum += float3(dot(e, sp0), dot(e, sp4), dot(e, sp8));

            x -= 4.0 * dx; r *= rr;
            e.x = exp2(-x * x);
            e.y = e.x * r; r *= rr;
            e.z = e.y * r; r *= rr;
            e.w = e.z * r; r *= rr;
            sum += float3(dot(e, sp1), dot(e, sp5), dot(e, sp9));

            x -= 4.0 * dx; r *= rr;
            e.x = exp2(-x * x);
            e.y = e.x * r; r *= rr;
            e.z = e.y * r; r *= rr;
            e.w = e.z * r; r *= rr;
            sum += float3(dot(e, sp2), dot(e, sp6), dot(e, sp10));

            x -= 4.0 * dx; r *= rr;
            e.x = exp2(-x * x);
            e.y = e.x * r; r *= rr;
            e.z = e.y * r; r *= rr;
            e.w = e.z * r;
            sum += float3(dot(e, sp3), dot(e, sp7), dot(e, sp11));

            // Film scatter
            const float H = 0.387298;
            x = (du + fan * 0.375) * iw * H;
            dx = fan * 0.25 * iw * H;

            r = exp2(dx * (2.0 * x - dx));
            rr = exp2(-2.0 * dx * dx);
            e.x = exp2(-x * x);
            e.y = e.x * r; r *= rr;
            e.z = e.y * r; r *= rr;
            e.w = e.z * r;
            float3 h = float3(dot(e, qR), dot(e, qG), dot(e, qB));
            sum += 0.1 * h + 0.05 * (h.r + h.g + h.b);

            // Violet spill glow
            float gx = (du + fan * 0.58) / (w * 0.9 + 0.05);
            sum += float3(sp0.x, sp4.x, sp8.x) * (3.0 * exp(-gx * gx));

            light += a * sum;
        }
    }

    float vignette = 1.0 - 0.45 * smoothstep(0.3, 1.2, length(p * float2(0.8, 1.2)));
    light *= catchLight * source * vignette * intensity;

    float pool = smoothstep(-0.35, 0.45, fbm2(p * 0.9 + float2(t * 0.02, 4.0)) + p.y * 0.35 + p.x * 0.2);
    float3 bg = backdrop * (0.25 + 1.5 * pool);

    float cover = exp(-dot(light, float3(2.0, 2.0, 2.0)));
    float3 color = 1.0 - exp(-(bg * cover + light) * 1.15);

    color = linearToSrgb(chroma(color, lerp(0.65, 1.35, smoothstep(0.03, 0.3, max(color.r, max(color.g, color.b))))));

    // Film grain
    float n = hash12(uv * 1080.0 + frac(t * 7.0) * 113.0) + hash12(uv * 1480.0 + 41.0) - 1.0;
    float lum = dot(color, float3(0.3, 0.55, 0.15));
    color += n * (grain * (0.35 + 0.65 * 4.0 * lum * (1.0 - lum)) + 1.0 / 255.0);

    return float4(saturate(color), 1.0);
}

