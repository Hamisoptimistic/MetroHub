// Jellyfish (Medusae) for MetroHub
// Based on Ash Weeks (milcktoast) particulate-medusae
// Ported & Enhanced for Direct3D 9 ps_3_0 HLSL

sampler2D InputSampler : register(s0);
float4 FrameState      : register(c0); // X=Time, Y=AspectRatio, Z=DeltaTime, W=0
float4 EffectParams    : register(c1); // X=Speed (1.0), Y=Glow (1.0), Z=Scale (1.0), W=Palette (0..3)
float4 MouseState      : register(c2); // X=MouseX, Y=MouseY, Z=LeftDown, W=0

static const float PI = 3.14159265;

// Ash Weeks' exact hood texture vein formula from bulb-frag.glsl (Fixed operators)
float accumulateHood(float2 uv, float saturation, float scale)
{
    saturation -= sin(uv.x * 60.0) * 0.25 + sin(uv.x * 50.0 * scale) * 0.25 + 0.75;
    saturation -= sin(uv.y * sin(uv.x * 5.0) * 5.0 * scale) * 0.05;
    saturation -= sin(uv.y * sin((1.0 - uv.x) * 5.0) * 5.0 * scale) * 0.05;
    saturation -= sin(uv.y * sin(uv.y + cos(uv.x) * 2.0) * 3.0 * scale) * 0.15;
    saturation -= sin(uv.y * sin(uv.y + cos(1.0 - uv.x) * 2.0) * 3.0 * scale) * 0.15;
    saturation -= sin((uv.y - 1.5) * sin(uv.y + cos(uv.x - 1.0) * 2.0) * 4.0 * scale) * 0.15;
    saturation -= sin((uv.y - 1.5) * sin(uv.y + cos(uv.x) * 2.0) * 4.0 * scale) * 0.15;
    saturation -= sin(uv.y * 5.0) * 0.15 + sin(uv.y * 2.5) * 1.25;
    return saturation;
}

// Ash Weeks' oral arms / belly texture formula from tail-frag.glsl (Fixed operators)
float accumulateTail(float2 uv, float saturation, float scale)
{
    saturation -= sin(uv.y * 12.0 * scale) * 0.8 + uv.y * 1.5 + sin(uv.x * 20.0 * scale) * 0.1 + 0.85;
    saturation -= sin(uv.y * sin(uv.x * 5.0) * 5.0 * scale) * 0.05;
    saturation -= sin(uv.y * sin((1.0 - uv.x) * 5.0) * 5.0 * scale) * 0.05;
    saturation -= sin(uv.y * sin(uv.y + cos(uv.x) * 2.0) * 10.0 * scale) * 0.15;
    saturation -= sin(uv.y * sin(uv.y + cos(1.0 - uv.x) * 2.0) * 10.0 * scale) * 0.15;
    return saturation;
}

// Asymmetric swim contraction curve (fast propulsion squeeze, slow recovery glide)
float timePhase(float t)
{
    float s = (sin(t * PI - 1.5707963) + 1.0) * 0.5;
    return pow(s, 1.5);
}

// Hash helper for ambient ocean dust
float hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

// Ambient drifting marine plankton
float renderMarineDust(float2 p, float time)
{
    float dust = 0.0;
    float2 grid = floor(p * 32.0);
    float2 local = frac(p * 32.0) - 0.5;

    [unroll]
    for (int y = -1; y <= 1; y += 2)
    {
        [unroll]
        for (int x = -1; x <= 1; x += 2)
        {
            float2 cell = grid + float2(x, y);
            float h = hash21(cell);
            if (h > 0.60)
            {
                float offsetY = frac(cell.y * 0.13 - time * 0.035) - 0.5;
                float offsetX = sin(cos(offsetY * 8.0) + sin(offsetY * 8.0 + cell.x * 0.5) * 2.0) * 0.25;
                float2 dotPos = float2(offsetX, offsetY) + float2(x, y) * 0.5;
                float d = length(local - dotPos);
                dust += (0.0008 / (d * d + 0.0005)) * (h - 0.60) * 2.5;
            }
        }
    }
    return dust;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float speed      = EffectParams.x > 0.0 ? EffectParams.x : 1.0;
    float brightness = EffectParams.y > 0.0 ? EffectParams.y : 1.0;
    float scale      = EffectParams.z > 0.0 ? EffectParams.z : 1.0;
    int paletteIdx   = (int)round(EffectParams.w) % 4;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);
    float time   = FrameState.x * speed * 0.85;

    // Screen-centered coordinates (Y+ is up)
    float2 p = float2((uv.x - 0.5) * aspect, 0.5 - uv.y) / scale;

    // Interactive mouse water displacement
    float2 mouseP    = float2((MouseState.x - 0.5) * aspect, 0.5 - MouseState.y) / scale;
    float2 toMouse   = p - mouseP;
    float mouseDist  = length(toMouse);
    float mouseNudge = smoothstep(0.45, 0.0, mouseDist);
    p += normalize(toMouse + 0.0001) * mouseNudge * 0.045;

    // Palette Configuration (0 = Original Pink/Violet Medusae)
    float3 colHoodPrim  = float3(1.000, 0.663, 0.824); // #FFA9D2
    float3 colHoodSec   = float3(0.439, 0.145, 0.424); // #70256C
    float3 colHoodGel   = float3(0.255, 0.353, 0.710); // #415AB5
    float3 colTentFore  = float3(1.000, 0.867, 0.914); // #FFDDE9
    float3 colTentMid   = float3(0.976, 0.620, 0.741); // #F99EBD
    float3 colTentTrail = float3(0.600, 0.447, 0.600); // #997299
    float3 colArmsPrim  = float3(0.894, 0.733, 0.933); // #E4BBEE
    float3 colArmsCore  = float3(0.937, 0.651, 0.941); // #EFA6F0

    if (paletteIdx == 1) // Cyan Bioluminescence
    {
        colHoodPrim  = float3(0.35, 0.95, 0.92);
        colHoodSec   = float3(0.08, 0.22, 0.45);
        colHoodGel   = float3(0.12, 0.45, 0.98);
        colTentFore  = float3(0.82, 0.98, 1.00);
        colTentMid   = float3(0.35, 0.85, 0.95);
        colTentTrail = float3(0.15, 0.45, 0.65);
        colArmsPrim  = float3(0.40, 0.90, 0.95);
        colArmsCore  = float3(0.20, 0.75, 1.00);
    }
    else if (paletteIdx == 2) // Abyssal Gold & Violet
    {
        colHoodPrim  = float3(1.00, 0.78, 0.38);
        colHoodSec   = float3(0.45, 0.12, 0.32);
        colHoodGel   = float3(0.85, 0.38, 0.15);
        colTentFore  = float3(1.00, 0.92, 0.80);
        colTentMid   = float3(0.95, 0.68, 0.35);
        colTentTrail = float3(0.55, 0.32, 0.45);
        colArmsPrim  = float3(1.00, 0.72, 0.40);
        colArmsCore  = float3(0.95, 0.45, 0.25);
    }
    else if (paletteIdx == 3) // Ghost Ice
    {
        colHoodPrim  = float3(0.88, 0.95, 1.00);
        colHoodSec   = float3(0.25, 0.32, 0.52);
        colHoodGel   = float3(0.45, 0.72, 0.98);
        colTentFore  = float3(0.95, 0.98, 1.00);
        colTentMid   = float3(0.70, 0.85, 0.95);
        colTentTrail = float3(0.40, 0.50, 0.65);
        colArmsPrim  = float3(0.80, 0.90, 1.00);
        colArmsCore  = float3(0.60, 0.80, 1.00);
    }

    // 1. Deep Oceanic Abyss Background
    float vignette = length(uv - 0.5);
    float3 finalColor = lerp(float3(0.045, 0.024, 0.065), float3(0.018, 0.018, 0.038), smoothstep(0.05, 0.85, vignette));

    float4 bgSample = tex2D(InputSampler, uv);
    finalColor = lerp(finalColor, finalColor * bgSample.rgb * 1.4 + bgSample.rgb * 0.12, 0.45);

    // 2. Ambient Marine Dust
    float dust = renderMarineDust(p, time);
    finalColor += lerp(colHoodGel, colTentFore, 0.5) * dust * 0.45;

    // 3. Jellyfish Kinematic Swim Motion
    float phase    = timePhase(time * 0.95);
    float phaseLag = timePhase(time * 0.95 - 0.35); // Delayed secondary wave for bell rim

    // Vertical propulsion surge + gentle lateral drift
    float swimY = sin(time * 0.35) * 0.05 + phase * 0.055 + 0.10;
    float swimX = sin(time * 0.22) * 0.10;
    float2 jPos = p - float2(swimX, swimY);

    // Natural body tilt aligned with swimming drift
    float tiltAngle = -cos(time * 0.22) * 0.14 + (mouseNudge * 0.12);
    float sinA = sin(tiltAngle);
    float cosA = cos(tiltAngle);
    jPos = float2(jPos.x * cosA - jPos.y * sinA, jPos.x * sinA + jPos.y * cosA);

    // 4. 3D Bell / Umbrella Geometry (Dome + Contracting Skirt)
    float bellTopY = 0.21 * (1.0 + 0.12 * phase);
    float bellBotY = -0.05 * (1.0 + 0.20 * phaseLag);
    float bellHeight = bellTopY - bellBotY;

    // Normalized vertical coordinate on the bell: 0.0 = top apex, 1.0 = bottom margin
    float vNorm = (bellTopY - jPos.y) / bellHeight;

    // Width profile of a real jellyfish bell (hemispherical top + flaring/contracting skirt)
    float baseWidth = 0.25 * (1.0 - 0.24 * phase);
    float skirtFlare = sin(saturate(vNorm) * PI * 0.78);
    float marginPinch = (1.0 - 0.22 * phaseLag * smoothstep(0.45, 1.0, vNorm));
    float profileRadiusX = max(0.001, baseWidth * skirtFlare * marginPinch);

    // Margin rim radius (used to anchor tentacles & oral arms)
    float rimRadiusX = 0.25 * (1.0 - 0.24 * phase) * sin(0.78 * PI) * (1.0 - 0.22 * phaseLag);

    // Ambient bioluminescent water glow around the jellyfish
    float2 glowSpace = float2(jPos.x, (jPos.y - 0.02) * 0.8);
    float waterGlow = exp(-length(glowSpace) * 5.5);
    finalColor += colHoodGel * waterGlow * 0.28;

    // 5. Trailing Tentacles (Rendered first so the translucent bell layers over their roots)
    if (jPos.y < bellBotY + 0.04)
    {
        float3 tentColorSum = float3(0.0, 0.0, 0.0);

        [unroll]
        for (int i = 0; i < 16; ++i)
        {
            float fi = (float)i;
            float theta = (fi / 16.0) * 6.2831853;

            // 3D elliptical ring anchor on the bell margin
            float xAnchor = sin(theta) * rimRadiusX * 0.92;
            float zDepth  = cos(theta); // -1 = back rim, +1 = front rim
            float yAnchor = bellBotY + zDepth * 0.018;

            float yDist = yAnchor - jPos.y;
            float maxLen = 0.62 + 0.18 * sin(fi * 2.1 + 0.5);

            if (yDist > 0.0 && yDist < maxLen)
            {
                // Travelling wave down the tentacle with phase delay
                float delay = yDist * 7.5;
                float waveArg1 = time * 2.6 - delay + theta * 1.5;
                float waveArg2 = time * 1.5 - delay * 0.6 + fi;

                // Bell contraction pulls roots inward and sends a whip wave down
                float rootPull = sin(theta) * rimRadiusX * 0.18 * sin(phaseLag * PI - yDist * 4.5) * exp(-yDist * 2.5);

                float sway = sin(waveArg1) * (0.012 + yDist * 0.065)
                           + cos(waveArg2) * (0.008 + yDist * 0.040)
                           + rootPull;

                // Derivative of sway for slope-corrected distance (prevents thinning on curves)
                float dSwayDy = -7.5 * cos(waveArg1) * (0.012 + yDist * 0.065)
                              - 0.9 * sin(waveArg2) * (0.008 + yDist * 0.040);

                float tX = xAnchor + sway;
                float dTent = abs(jPos.x - tX) / sqrt(1.0 + dSwayDy * dSwayDy);

                // Silky inverse-square filament glow
                float fade = saturate(1.0 - yDist / maxLen);
                float depthFade = lerp(0.55, 1.15, zDepth * 0.5 + 0.5);
                float ill = (0.00022 / (dTent * dTent * 420.0 + 0.00025)) * fade * fade * depthFade;

                float3 tCol = (i % 3 == 0) ? colTentFore : ((i % 3 == 1) ? colTentMid : colTentTrail);
                tentColorSum += tCol * ill;
            }
        }
        finalColor += tentColorSum * 0.72;
    }

    // 6. Frilly 3D Oral Arms (4 Ruffled Ribbon Arms Under the Bell)
    if (jPos.y < bellBotY + 0.05 && jPos.y > -0.56)
    {
        float armY = (bellBotY + 0.04) - jPos.y;
        float normY = saturate(armY / 0.58);

        [unroll]
        for (int a = 0; a < 4; ++a)
        {
            float fa = (float)a;
            float spread = (fa - 1.5) * 0.028 * (1.0 - 0.2 * phase);

            // Organic undulating path + 3D ribbon twist
            float centerWave = spread * (1.0 + armY * 1.4)
                             + sin(armY * 14.0 - time * 2.4 + fa * 1.7) * (0.015 + armY * 0.045)
                             + cos(armY * 8.0  - time * 1.6 + fa * 2.3) * (0.010 + armY * 0.025);

            // Twist modulates width to create 3D folded ruffle appearance
            float twist = 0.45 + 0.55 * abs(sin(armY * 19.0 - time * 2.1 + fa * 1.3));
            float taper = sin(pow(normY, 0.45) * PI);
            float armW  = 0.046 * taper * twist;

            float dArm = abs(jPos.x - centerWave);
            if (dArm < armW && armW > 0.001)
            {
                float uArm = (jPos.x - centerWave) / armW * 0.5 + 0.5;
                float2 armUv = float2(uArm, armY * 2.2 + fa * 0.35);

                float armSat = accumulateTail(armUv, 2.0, 12.0);
                float3 armCol = lerp(colArmsCore, colArmsPrim, saturate(armSat));

                // Bright ruffled edge highlight + translucent core
                float edgeRim = smoothstep(0.35, 0.95, dArm / armW);
                float edgeSoft = smoothstep(1.0, 0.72, dArm / armW);
                float alpha = (0.35 + 0.65 * edgeRim) * edgeSoft * (1.0 - normY * 0.85);

                finalColor += (armCol + colTentFore * edgeRim * 0.45) * alpha * 0.52;
            }
        }
    }

    // 7. Translucent 3D Umbrella / Hood (Rendered over inner roots)
    float uNormSigned = jPos.x / profileRadiusX;

    // 20 scalloped lappets along the bottom bell margin
    float scallopAngle = clamp(uNormSigned, -1.0, 1.0) * PI;
    float scallopWave  = cos(scallopAngle * 10.0) * 0.035 * smoothstep(0.6, 1.0, vNorm);

    // 3D perspective curve for the front & back bottom rim opening
    float zRimCurve = sqrt(saturate(1.0 - uNormSigned * uNormSigned));
    float frontRimLimit = 1.0 + zRimCurve * 0.10 + scallopWave;
    float backRimLimit  = 1.0 - zRimCurve * 0.08 - scallopWave * 0.5;

    if (vNorm >= 0.0 && vNorm <= frontRimLimit && abs(uNormSigned) <= 1.02)
    {
        // 3D Spherical/Dome Surface Normal
        float zDome = sqrt(max(0.0001, 1.0 - saturate(uNormSigned * uNormSigned)));
        float yNorm = (0.45 - vNorm) * 1.35;
        float3 norm = normalize(float3(uNormSigned, yNorm, zDome));

        // Fresnel rim factor (strong glow at grazing angles)
        float rim = 1.0 - max(norm.z, 0.0);

        // True 3D Dome UVs: u wraps around the azimuth via asin, v runs from apex to margin
        float uDome = (asin(clamp(uNormSigned, -0.999, 0.999)) / PI) + 0.5;
        float vDome = saturate(vNorm / frontRimLimit);
        float2 hoodUv = float2(uDome, vDome);

        // Ash Weeks' dual scale harmonics
        float scale0 = lerp(8.0, 14.0, (sin(time * 0.25 + 0.5) + 1.0) * 0.5);
        float scale1 = lerp(12.0, 18.0, (sin(time * 0.125) + 1.0) * 0.5);

        float saturation = 0.0;
        saturation += max(accumulateHood(hoodUv, 2.0, scale0), -0.5);
        saturation += max(accumulateHood(hoodUv + rim * 0.18, 2.0, scale1), 0.25);
        saturation += max(accumulateHood(float2(-rim * 0.25, -rim * 0.25), 1.0, 1.0), -0.25);

        // Base translucent membrane & glowing radial veins
        float veinMask = 1.0 - smoothstep(-0.35, 1.85, saturation);
        float3 hoodSurface = lerp(colHoodSec, colHoodPrim, smoothstep(-0.5, 0.6, saturation));

        // Internal bioluminescent gonads / manubrium core glow inside upper dome
        float2 corePos = float2(uNormSigned * 0.85, (vDome - 0.42) * 1.7);
        float internalCore = exp(-dot(corePos, corePos) * 4.5) * zDome;
        hoodSurface += colArmsCore * internalCore * 0.85;

        // Outer Gel Fresnel glow (#415AB5) + Apex Specular highlight
        float gelGlow = pow(rim, 2.0) * 1.55;
        float apexGlow = pow(saturate(1.0 - vDome), 2.5) * zDome * 0.6;
        hoodSurface += colHoodGel * gelGlow + colTentFore * apexGlow;

        // Highlight the scalloped bottom margin ring (front and back sub-umbrella rim)
        float frontMarginGlow = exp(-abs(vNorm - frontRimLimit) * 38.0);
        float backMarginGlow  = exp(-abs(vNorm - backRimLimit) * 32.0) * 0.45;
        hoodSurface += colTentFore * (frontMarginGlow * 0.9 + backMarginGlow * 0.6);

        // Soft silhouette anti-aliasing at the dome edges
        float silhouetteAlpha = smoothstep(1.02, 0.92, abs(uNormSigned))
                              * smoothstep(0.0, 0.05, vNorm)
                              * smoothstep(frontRimLimit + 0.02, frontRimLimit - 0.03, vNorm);

        float hoodOpacity = saturate(0.28 + veinMask * 0.55 + pow(rim, 1.8) * 0.55 + frontMarginGlow * 0.5);

        // Additive subsurface scattering + translucent alpha blend
        finalColor = lerp(finalColor, finalColor * 0.55 + hoodSurface * 1.15, silhouetteAlpha * hoodOpacity);
    }

    return float4(saturate(finalColor * brightness), 1.0);
}
