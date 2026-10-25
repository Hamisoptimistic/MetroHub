#include "ShaderPalettes.hlsl"

sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=TimeOfDaySeconds, W=0 (Unused)
float4 EffectParams : register(c1); // X=Brightness (1.0), Y=Scale (1.0), Z=GlowPulse (1.0), W=ColorTheme (0.0 to 5.0)

static const float PI = 3.14159265359;
static const float TAU = 6.28318530718;
static const float SCALE = 1.0 / 6.0;

float mod_glsl(float x, float y)
{
    return x - y * floor(x / y);
}

float hash12(float2 p)
{
    float3 p3 = frac(p.xyx * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float noise(float2 pos)
{
    float2 i = floor(pos);
    float2 f = frac(pos);
    float a = hash12(i);
    float b = hash12(i + float2(1.0, 0.0));
    float c = hash12(i + float2(0.0, 1.0));
    float d = hash12(i + float2(1.0, 1.0));
    float2 u = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float dfLine(float2 pStart, float2 pEnd, float2 uv)
{
    pStart *= SCALE;
    pEnd *= SCALE;
    float2 lineSeg = pEnd - pStart;
    float fracVal = dot(uv - pStart, lineSeg) / dot(lineSeg, lineSeg);
    return distance(pStart + lineSeg * saturate(fracVal), uv);
}

float dfCircle(float2 origin, float radius, float2 uv)
{
    origin *= SCALE;
    radius *= SCALE;
    return abs(length(uv - origin) - radius);
}

float dfArc(float2 origin, float pStart, float sweep, float radius, float2 uv)
{
    origin *= SCALE;
    radius *= SCALE;
    uv -= origin;
    
    float c = cos(pStart);
    float s = sin(pStart);
    float2 rotUv = float2(uv.x * c + uv.y * s, -uv.x * s + uv.y * c);
    
    float offs = sweep * 0.5 - PI;
    float rawAng = atan2(rotUv.y, rotUv.x) - offs;
    float ang = mod_glsl(rawAng, TAU) + offs;
    ang = clamp(ang, min(0.0, sweep), max(0.0, sweep));
    
    return distance(radius * float2(cos(ang), sin(ang)), rotUv);
}

float dfDigit(float d, float2 uv)
{
    d = floor(d + 0.5);
    float dist = 1e6;

    if (d < 0.5) // 0
    {
        dist = min(dist, dfLine(float2(1.0, 1.0), float2(1.0, 0.5), uv));
        dist = min(dist, dfLine(float2(0.0, 1.0), float2(0.0, 0.5), uv));
        dist = min(dist, dfArc(float2(0.5, 1.0), 0.0, 3.142, 0.5, uv));
        dist = min(dist, dfArc(float2(0.5, 0.5), 3.142, 3.142, 0.5, uv));
    }
    else if (d < 1.5) // 1
    {
        dist = min(dist, dfLine(float2(0.5, 1.5), float2(0.5, 0.0), uv));
    }
    else if (d < 2.5) // 2
    {
        dist = min(dist, dfLine(float2(1.0, 0.0), float2(0.0, 0.0), uv));
        dist = min(dist, dfLine(float2(0.388, 0.561), float2(0.806, 0.719), uv));
        dist = min(dist, dfArc(float2(0.5, 1.0), 0.0, 3.142, 0.5, uv));
        dist = min(dist, dfArc(float2(0.7, 1.0), 5.074, 1.209, 0.3, uv));
        dist = min(dist, dfArc(float2(0.6, 0.0), 1.932, 1.209, 0.6, uv));
    }
    else if (d < 3.5) // 3
    {
        dist = min(dist, dfLine(float2(0.0, 1.5), float2(1.0, 1.5), uv));
        dist = min(dist, dfLine(float2(1.0, 1.5), float2(0.5, 1.0), uv));
        dist = min(dist, dfArc(float2(0.5, 0.5), 3.142, 4.712, 0.5, uv));
    }
    else if (d < 4.5) // 4
    {
        dist = min(dist, dfLine(float2(0.7, 1.5), float2(0.0, 0.5), uv));
        dist = min(dist, dfLine(float2(0.0, 0.5), float2(1.0, 0.5), uv));
        dist = min(dist, dfLine(float2(0.7, 1.2), float2(0.7, 0.0), uv));
    }
    else if (d < 5.5) // 5
    {
        dist = min(dist, dfLine(float2(1.0, 1.5), float2(0.3, 1.5), uv));
        dist = min(dist, dfLine(float2(0.3, 1.5), float2(0.2, 0.9), uv));
        dist = min(dist, dfArc(float2(0.5, 0.5), 3.142, 5.356, 0.5, uv));
    }
    else if (d < 6.5) // 6
    {
        dist = min(dist, dfLine(float2(0.067, 0.75), float2(0.5, 1.5), uv));
        dist = min(dist, dfCircle(float2(0.5, 0.5), 0.5, uv));
    }
    else if (d < 7.5) // 7
    {
        dist = min(dist, dfLine(float2(0.0, 1.5), float2(1.0, 1.5), uv));
        dist = min(dist, dfLine(float2(1.0, 1.5), float2(0.5, 0.0), uv));
    }
    else if (d < 8.5) // 8
    {
        dist = min(dist, dfCircle(float2(0.5, 0.4), 0.4, uv));
        dist = min(dist, dfCircle(float2(0.5, 1.15), 0.35, uv));
    }
    else // 9
    {
        dist = min(dist, dfLine(float2(0.933, 0.75), float2(0.5, 0.0), uv));
        dist = min(dist, dfCircle(float2(0.5, 1.0), 0.5, uv));
    }
    return dist;
}

float4 main(float2 uvCoords : TEXCOORD) : COLOR
{
    float brightness = EffectParams.x > 0.0 ? EffectParams.x : 1.0;
    float scaleMod = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float glowMod = EffectParams.z > 0.0 ? EffectParams.z : 1.0;

    // Centered coordinates with aspect ratio compensation
    // Invert Y so OpenGL/Shadertoy geometry matches Direct3D/WPF
    float2 uv = float2((uvCoords.x - 0.5) * FrameState.y, -(uvCoords.y - 0.5)) * (1.1 / scaleMod);

    float sec = FrameState.z > 0.0 ? FrameState.z : FrameState.x;
    int totalMinutes = (int)(sec / 60.0);
    int totalHours = totalMinutes / 60;
    int hour = totalHours % 24;
    // 12-hour clock
    if (hour > 12) hour -= 12;
    if (hour == 0) hour = 12;
    int minute = totalMinutes % 60;

    float d0 = floor(hour / 10.0);
    float d1 = floor(fmod(hour, 10.0));
    float d2 = floor(minute / 10.0);
    float d3 = floor(fmod(minute, 10.0));

    static const float digitW = 1.0 * SCALE;
    static const float digitGap = 0.25 * SCALE;
    static const float colonW = 0.4 * SCALE;

    static const float totalWidth = 4.0 * digitW + 3.0 * digitGap + colonW;
    static const float startX = -totalWidth * 0.5;
    static const float startY = -(1.5 * SCALE) * 0.5;

    float x0 = startX;
    float x1 = x0 + digitW + digitGap;
    float xColon = x1 + digitW + digitGap;
    float x2 = xColon + colonW + digitGap;
    float x3 = x2 + digitW + digitGap;

    float colonCenterX = xColon + colonW * 0.5;

    float dist = 1e6;

    // Digits
    dist = min(dist, dfDigit(d0, float2(uv.x - x0, uv.y - startY)));
    dist = min(dist, dfDigit(d1, float2(uv.x - x1, uv.y - startY)));
    
    // Colon dots
    dist = min(dist, abs(distance(uv, float2(colonCenterX, 0.045)) - 0.007));
    dist = min(dist, abs(distance(uv, float2(colonCenterX, -0.045)) - 0.007));
    
    dist = min(dist, dfDigit(d2, float2(uv.x - x2, uv.y - startY)));
    dist = min(dist, dfDigit(d3, float2(uv.x - x3, uv.y - startY)));

    float shade = (0.004 * brightness) / max(dist, 1e-4);

    // Color theme from shared luxury palettes
    PastelPalette pal = GetPastelPalette((int)(EffectParams.w + 0.5));
    float3 tint = pal.tint;

    float3 neonColor = tint * shade;

    // Animated glow pulse
    neonColor += tint * shade * glowMod * noise((uv + float2(FrameState.x * 0.5, FrameState.x * 0.5)) * 2.5 + 0.5);

    // Subtle background grid (pure black between grid lines)
    float2 gridVal = abs(frac(uv * 48.0) - 0.5);
    float grid = 0.5 - max(gridVal.x, gridVal.y);
    neonColor *= 0.25 + smoothstep(0.0, 0.06, grid) * 0.75;

    // STANDALONE: Zero wallpaper sampling! Pure dark background.
    return float4(saturate(neonColor), 1.0);
}
