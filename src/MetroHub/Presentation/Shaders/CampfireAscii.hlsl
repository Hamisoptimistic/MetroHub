// ============================================================================
// MetroHub Backdrop Shader: CampfireAscii.hlsl
// Target: Direct3D 9Ex Pixel Shader 3.0 (ps_3_0)
// Description: Procedural ASCII-art "Campfire Under Stars" with interactive 
//              mouse wind, flickering character flames, smoke, pine silhouettes,
//              and twinkling star glyphs. Zero texture RAM.
// ============================================================================

sampler2D InputSampler : register(s0);

// c0: X = Modulo Time (400 * PI), Y = Aspect Ratio (W/H), Z = TimeOfDaySeconds, W = Unused
float4 FrameState   : register(c0);

// c1: X = Speed (1.0), Y = GridRows (48.0), Z = GlowIntensity (1.15), W = PaletteIndex (0..5)
float4 EffectParams : register(c1);

// c2: X = Mouse X (0..1), Y = Mouse Y (0..1), Z = Click (0/1), W = Drag Yaw
float4 MouseState   : register(c2);

// ----------------------------------------------------------------------------
// Deterministic Hash Helpers
// ----------------------------------------------------------------------------
float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float Noise2D(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = Hash21(i);
    float b = Hash21(i + float2(1.0, 0.0));
    float c = Hash21(i + float2(0.0, 1.0));
    float d = Hash21(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

// ----------------------------------------------------------------------------
// Procedural ASCII Glyph Renderer (Sub-cell coordinates p in [0..1, 0..1])
// Glyphs:
//  0: Space       1: Dot (.)      2: Plus (+)     3: Star (*)
//  4: Caret (^)   5: Slash (/)    6: BSlash (\)   7: FlameParens ( / )
//  8: Log (#/=)   9: Tilde (~)
// ----------------------------------------------------------------------------
float SampleGlyph(int id, float2 p)
{
    // Add crisp terminal padding inside each character cell
    if (p.x < 0.12 || p.x > 0.88 || p.y < 0.10 || p.y > 0.90)
        return 0.0;

    float2 c = p - 0.5;
    float mask = 0.0;

    if (id == 1) // '.' (Small ember / dim star / ground pebble)
    {
        float2 d = p - float2(0.5, 0.72);
        mask = step(length(d), 0.11);
    }
    else if (id == 2) // '+' (Twinkling star)
    {
        float h = step(abs(c.y), 0.065) * step(abs(c.x), 0.28);
        float v = step(abs(c.x), 0.065) * step(abs(c.y), 0.24);
        mask = max(h, v);
    }
    else if (id == 3) // '*' (Bright star / hot flame core)
    {
        float h  = step(abs(c.y), 0.06) * step(abs(c.x), 0.30);
        float v  = step(abs(c.x), 0.06) * step(abs(c.y), 0.28);
        float d1 = step(abs(c.x - c.y * 0.85), 0.065) * step(length(c), 0.30);
        float d2 = step(abs(c.x + c.y * 0.85), 0.065) * step(length(c), 0.30);
        mask = max(max(h, v), max(d1, d2));
    }
    else if (id == 4) // '^' (Pine needles / flickering flame tip)
    {
        float2 q = float2(abs(c.x), p.y - 0.28);
        float lineDist = abs(q.x * 1.35 - q.y * 0.75);
        mask = step(lineDist, 0.075) * step(p.y, 0.70) * step(0.24, p.y);
    }
    else if (id == 5) // '/' (Left log / left tree bough / flame edge)
    {
        float d = abs((p.x - 0.5) + (p.y - 0.5) * 0.65);
        mask = step(d, 0.075);
    }
    else if (id == 6) // '\' (Right log / right tree bough / flame edge)
    {
        float d = abs((p.x - 0.5) - (p.y - 0.5) * 0.65);
        mask = step(d, 0.075);
    }
    else if (id == 7) // '(' or ')' (Curved licking flame tongue)
    {
        float side = (c.x >= 0.0) ? 1.0 : -1.0;
        float curve = (c.y * c.y) * 0.95 - 0.18;
        float d = abs(abs(c.x) + curve);
        mask = step(d, 0.075) * step(abs(c.y), 0.36);
    }
    else if (id == 8) // '#' / '=' (Charred campfire log grating)
    {
        float barsH = step(abs(abs(c.y) - 0.14), 0.055) * step(abs(c.x), 0.34);
        float barsV = step(abs(abs(c.x) - 0.16), 0.055) * step(abs(c.y), 0.32);
        mask = max(barsH, barsV);
    }
    else if (id == 9) // '~' (Drifting smoke wisp / ground grass ripple)
    {
        float wave = sin(c.x * 9.0) * 0.09;
        mask = step(abs(c.y - wave), 0.065) * step(abs(c.x), 0.32);
    }

    return mask;
}

// ----------------------------------------------------------------------------
// Main Entry Point
// ----------------------------------------------------------------------------
float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    float time   = FrameState.x * max(EffectParams.x, 0.1);
    float aspect = max(FrameState.y, 0.5);
    float glow   = max(EffectParams.z, 0.1);

    // 1. Construct Terminal Character Grid (Monospace 1 : 1.75 cell ratio)
    float rows = clamp(EffectParams.y, 28.0, 90.0);
    float cols = floor(rows * aspect * 1.75);
    float2 gridSize = float2(cols, rows);

    float2 cellId = floor(uv * gridSize);
    float2 cellUV = frac(uv * gridSize);

    // Normalized cell center in aspect-corrected space: X in [-aspect..aspect], Y in [0..1] (0=top, 1=bottom)
    float2 cellNorm = (cellId + 0.5) / gridSize;
    float2 p = float2((cellNorm.x - 0.5) * aspect, cellNorm.y);

    // Smooth mouse wind influence (-1 to +1 sway based on cursor X distance from center)
    float mouseWind = (MouseState.x - 0.5) * 0.38;

    // 2. Base Night Sky & Ambient Campfire Radial Warmth
    float2 fireOrigin = float2(0.0, 0.80);
    float2 fireDelta  = float2(p.x - fireOrigin.x, (p.y - fireOrigin.y) * 1.35);
    float  fireDist   = length(fireDelta);
    float  flicker    = sin(time * 7.3) * 0.04 + cos(time * 13.1) * 0.025;

    float3 bgCol = lerp(float3(0.020, 0.024, 0.055), float3(0.055, 0.035, 0.075), saturate(p.y));
    float  warmAura = exp(-fireDist * (3.2 + flicker)) * 0.32 * glow;
    bgCol += float3(1.0, 0.42, 0.12) * warmAura;

    int    glyphId  = 0;
    float3 glyphCol = float3(0.0, 0.0, 0.0);

    // 3. Horizon & Pine Forest Silhouette (Y: 0.48 .. 0.83)
    float horizonY = 0.82 + sin(p.x * 2.8) * 0.015;

    // Pine tree clusters flanking the clearing (leaves center open for campfire & stars)
    float clearingMask = smoothstep(0.16, 0.38, abs(p.x));
    float treeIndex    = floor((p.x + 4.0) * 7.5);
    float treeHash     = Hash21(float2(treeIndex, 11.7));
    float treeCenter   = (treeIndex + 0.5) / 7.5 - 4.0;
    float treeLocalX   = abs(p.x - treeCenter) * 7.5;
    float treeHeight   = (0.18 + treeHash * 0.16) * clearingMask;
    float treeTopY     = horizonY - treeHeight;

    // Tiered conical pine branches
    float branchTier   = frac((p.y - treeTopY) * 18.0);
    float coneWidth    = saturate((p.y - treeTopY) / max(treeHeight, 0.001)) * (0.85 + 0.25 * branchTier);
    bool  inPineTree   = (clearingMask > 0.05) && (p.y > treeTopY) && (p.y < horizonY) && (treeLocalX < coneWidth);

    // 4. Campfire Flame, Smoke, Embers & Crossed Logs
    float2 fp = p - fireOrigin;

    // Apply height-dependent wind sway + organic turbulence to the rising column
    float heightFactor = saturate(-fp.y / 0.42);
    float sway = sin(time * 4.2 - fp.y * 14.0) * 0.045 * heightFactor
               + cos(time * 6.7 - fp.y * 21.0) * 0.022 * heightFactor
               + mouseWind * heightFactor * 0.65;

    float2 flameCoord = float2(fp.x - sway, fp.y);

    // Flame teardrop envelope (above fireOrigin: fp.y in [-0.30 .. 0.02])
    float flameH = saturate(-flameCoord.y / 0.27);
    float flameW = (1.0 - flameH * 0.88) * 0.145;
    float nTurb  = Noise2D(float2(flameCoord.x * 16.0, flameCoord.y * 12.0 + time * 4.8));
    float flameDensity = (1.0 - abs(flameCoord.x) / max(flameW, 0.001)) + (nTurb - 0.5) * 0.55;
    bool  inFlame = (flameCoord.y <= 0.025) && (flameCoord.y >= -0.28) && (flameDensity > 0.15);

    // Crossed logs at base (fp.y in [-0.01 .. 0.065], abs(fp.x) < 0.16)
    bool inLogs = (abs(fp.x) < 0.155) && (fp.y >= -0.005) && (fp.y <= 0.060);

    // Rising smoke plume above the flame (fp.y in [-0.62 .. -0.24])
    float smokeH = saturate((-fp.y - 0.22) / 0.40);
    float smokeSway = sin(time * 2.1 - fp.y * 9.0) * 0.075 + mouseWind * (0.4 + smokeH * 0.8);
    float smokeDist = abs(fp.x - smokeSway);
    float smokeNoise = Noise2D(float2(cellId.x * 0.35, cellId.y * 0.25 + time * 1.6));
    bool  inSmoke = (fp.y < -0.23) && (fp.y > -0.64) && (smokeDist < 0.05 + smokeH * 0.11) && (smokeNoise > 0.56);

    // Rising sparks / embers
    float2 emberGrid = float2(cellId.x, floor(cellId.y + time * 5.5));
    float  emberHash = Hash21(emberGrid);
    bool   inEmber   = (fp.y < -0.10) && (fp.y > -0.52) && (abs(fp.x - sway * 1.1) < 0.16) && (emberHash > 0.965);

    // ------------------------------------------------------------------------
    // 5. Priority Layer Assignment (Foreground to Background)
    // ------------------------------------------------------------------------
    if (inLogs)
    {
        // Charred logs with glowing inner embers
        float emberPulse = 0.5 + 0.5 * sin(time * 5.5 + fp.x * 25.0);
        if (abs(fp.x) < 0.045)
        {
            glyphId  = 8; // '#' central grate
            glyphCol = lerp(float3(0.85, 0.25, 0.08), float3(1.0, 0.65, 0.15), emberPulse);
        }
        else if (fp.x < 0.0)
        {
            glyphId  = 5; // '/' left angled log
            glyphCol = float3(0.58, 0.28, 0.12);
        }
        else
        {
            glyphId  = 6; // '\' right angled log
            glyphCol = float3(0.58, 0.28, 0.12);
        }
    }
    else if (inFlame)
    {
        // Multi-zone ASCII fire: core ('*'), mid ('(' / ')'), tips ('^' / '.')
        if (flameDensity > 0.78 && flameH < 0.55)
        {
            glyphId  = 3; // '*' White-yellow hot core
            glyphCol = float3(1.00, 0.96, 0.78);
        }
        else if (flameDensity > 0.46)
        {
            glyphId  = 7; // '(' or ')' Golden-orange licking body
            glyphCol = lerp(float3(1.00, 0.78, 0.20), float3(1.00, 0.45, 0.08), flameH);
        }
        else
        {
            glyphId  = (frac(nTurb * 7.3) > 0.35) ? 4 : 1; // '^' or '.' Crimson-orange outer tips
            glyphCol = float3(0.95, 0.26, 0.06);
        }
    }
    else if (inEmber)
    {
        glyphId  = (emberHash > 0.985) ? 3 : 1; // '*' or '.'
        glyphCol = float3(1.00, 0.62, 0.18);
    }
    else if (inSmoke)
    {
        glyphId  = (smokeNoise > 0.72) ? 9 : 1; // '~' or '.'
        float fade = 1.0 - smokeH * 0.75;
        glyphCol = float3(0.42, 0.44, 0.52) * fade;
    }
    else if (p.y >= horizonY)
    {
        // Ground clearing: textured grass ripples ('~', '^', '.') lit by the fire
        float gHash = Hash21(cellId);
        if (gHash > 0.68)
        {
            glyphId = (gHash > 0.88) ? 9 : 1; // '~' or '.'
            float groundLight = exp(-abs(p.x) * 3.5) * exp(-(p.y - horizonY) * 8.0);
            glyphCol = lerp(float3(0.10, 0.15, 0.14), float3(0.65, 0.36, 0.14), saturate(groundLight * 1.3));
        }
    }
    else if (inPineTree)
    {
        // Pine silhouettes with subtle fire rim-lighting on inner clearing edges
        if (treeLocalX < 0.12 && p.y > horizonY - 0.04)
        {
            glyphId  = 8; // '#' tree trunk
            glyphCol = float3(0.18, 0.12, 0.09);
        }
        else
        {
            glyphId = (p.x < treeCenter) ? 5 : 6; // '/' or '\' boughs, or '^' canopy
            if (branchTier < 0.35) glyphId = 4;

            float rimLight = exp(-abs(p.x) * 3.8) * saturate(1.0 - (horizonY - p.y) * 4.0);
            glyphCol = lerp(float3(0.07, 0.16, 0.15), float3(0.45, 0.32, 0.16), rimLight * 0.85);
        }
    }
    else
    {
        // Starry Night Sky (Upper canopy above horizon & trees)
        float starSeed = Hash21(cellId);

        // Crescent moon in the upper-left sky (around X = -0.48, Y = 0.20)
        float2 moonCenter = float2(-0.48 * aspect, 0.20);
        float  moonDist   = length(p - moonCenter);
        float  caterDist  = length(p - (moonCenter + float2(0.035, -0.015)));

        if (moonDist < 0.075 && caterDist > 0.062)
        {
            glyphId  = 7; // '(' crescent arc
            glyphCol = float3(0.94, 0.92, 0.76);
        }
        else if (starSeed > 0.935)
        {
            // Twinkling stars with individual phase offsets
            float twinkle = 0.5 + 0.5 * sin(time * (1.8 + frac(starSeed * 19.0) * 3.5) + starSeed * 60.0);

            if (starSeed > 0.986 && twinkle > 0.65)
            {
                glyphId  = 3; // '*' Bright beacon star
                glyphCol = float3(0.95, 0.97, 1.00) * (0.75 + 0.25 * twinkle);
            }
            else if (starSeed > 0.965 && twinkle > 0.45)
            {
                glyphId  = 2; // '+' Medium star
                glyphCol = float3(0.72, 0.82, 0.98) * (0.60 + 0.40 * twinkle);
            }
            else
            {
                glyphId  = 1; // '.' Faint background star
                glyphCol = float3(0.50, 0.58, 0.78) * (0.45 + 0.55 * twinkle);
            }
        }
    }

    // ------------------------------------------------------------------------
    // 6. Rasterize Selected ASCII Glyph & Composite
    // ------------------------------------------------------------------------
    float charMask = (glyphId > 0) ? SampleGlyph(glyphId, cellUV) : 0.0;

    // Subtle CRT/terminal phosphor bloom around lit characters
    float3 finalColor = bgCol + glyphCol * charMask * glow;

    // Gentle vignette at screen edges
    float2 vigUV = uv * (1.0 - uv.yx);
    float  vig   = saturate(pow(vigUV.x * vigUV.y * 15.0, 0.18));
    finalColor  *= vig;

    return float4(saturate(finalColor), 1.0);
}
