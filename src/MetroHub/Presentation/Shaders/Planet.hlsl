// Planet.hlsl - Universal Textured Planet Shader with Exact Website Default Controls & Shading
// Ported from jsulpis/realtime-planet-shader (planet.fragment.glsl & earth.fragment.glsl) for MetroHub Direct3D 9 ps_3_0

sampler2D ColorSampler  : register(s0);
sampler2D NightSampler  : register(s1);
sampler2D CloudsSampler : register(s2);

float4 FrameState      : register(c0); // X=Time, Y=AspectRatio, Z=TimeOfDaySeconds, W=Unused
float4 EffectParams    : register(c1); // X=TimeMode (0=Realtime, 1=Website 45deg), Y=AtmoDensity, Z=HasNight, W=CloudsDensity
float4 MouseState      : register(c2); // X=MouseX, Y=MouseY, Z=LeftDown, W=PlanetDragYaw
float4 AtmosphereColor : register(c3); // RGB=AtmoColor, W=SunIntensity

static const float PI = 3.14159265359;
static const float3 CAMERA_POSITION = float3(0.0, 0.0, 6.0);
static const float3 uPlanetPosition = float3(0.0, 0.0, -10.0);
static const float  uPlanetRadius   = 2.0;
static const float  FOCAL_LENGTH    = 0.375; // 6.0 / (6.0 - (-10.0))
static const float3 SUN_COLOR       = float3(1.0, 1.0, 0.95);
static const float3 DEEP_SPACE      = float3(0.0, 0.0, 0.0005);

// Exact Scurve sigmoid contrast function from planet.fragment.glsl lines 145-151
float Sigmoid(float x)
{
    return 1.0 / (1.0 + exp(-(x - 0.7) * 6.5));
}

float3 Scurve(float3 c)
{
    return float3(Sigmoid(c.x), Sigmoid(c.y), Sigmoid(c.z));
}

float hash13(float3 p3)
{
    p3 = frac(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return frac((p3.x + p3.y) * p3.z);
}

float3 spaceStars(float3 dir)
{
    float3 p = dir * 180.0;
    float3 id = floor(p);
    float3 q = frac(p) - 0.5;
    float starHash = hash13(id);
    float star = smoothstep(0.983, 1.0, starHash);
    star *= 1.0 - smoothstep(0.0, 0.45, length(q));
    return DEEP_SPACE + star * float3(1.0, 0.95, 0.88);
}

float sphIntersect(float3 ro, float3 rd, float3 ce, float ra)
{
    float3 oc = ro - ce;
    float b = dot(oc, rd);
    float c = dot(oc, oc) - ra * ra;
    float h = b * b - c;
    if (h < 0.0) return -1.0;
    return -b - sqrt(h);
}

float3x3 rotateY(float angle)
{
    float c = cos(angle);
    float s = sin(angle);
    return float3x3(c, 0, s, 0, 1, 0, -s, 0, c);
}

float2 sphereProjection(float3 p, float3 origin)
{
    float3 dir = normalize(p - origin);
    float lon = atan2(dir.x, dir.z);
    float lat = asin(clamp(dir.y, -1.0, 1.0));
    // DirectX texture coords: V=0 is North Pole (top), V=1 is South Pole (bottom)
    return float2((lon + PI) / (2.0 * PI), (PI * 0.5 - lat) / PI);
}

float4 main(float2 texCoord : TEXCOORD) : COLOR
{
    float aspect = FrameState.y > 0.0 ? FrameState.y : 1.7777778;
    float time = FrameState.x;

    float2 uv = float2((texCoord.x - 0.5) * aspect, -(texCoord.y - 0.5));

    // Fixed camera position - completely removes cursor drift
    float3 ro = CAMERA_POSITION;
    float3 rd = normalize(float3(uv * FOCAL_LENGTH, -1.0));

    // Website default Sun position: [1.00, 1.00] in top-right
    float3 sunDir;
    float planetRot;
    float cloudsRot;
    float dragOffset = MouseState.w; // Free drag rotation from BackdropShaderDriver

    if (EffectParams.x < 0.5)
    {
        // MODE 0: REALTIME 24-HOUR SYNC
        float dayFraction = frac(FrameState.z / 86400.0);
        float sunAngle = dayFraction * 2.0 * PI - PI * 0.5;
        sunDir = normalize(float3(cos(sunAngle), 0.28, sin(sunAngle)));

        planetRot = dragOffset;
        cloudsRot = time * 0.003 + dragOffset * 1.05;
    }
    else
    {
        // MODE 1: EXACT WEBSITE SUN & ROTATION (sunDirectionXY: [1, 1], z: 0)
        sunDir = normalize(float3(1.0, 1.0, 0.0));
        float spinSpeed = 0.05;
        planetRot = time * spinSpeed + dragOffset;
        cloudsRot = time * (spinSpeed * 1.15) + dragOffset * 1.05;
    }

    float hit = sphIntersect(ro, rd, uPlanetPosition, uPlanetRadius);
    float3 col = float3(0.0, 0.0, 0.0);

    // Default website controls: Sun intensity = 3.0, Ambient light = 0.002
    float sunIntensity = AtmosphereColor.w > 0.0 ? AtmosphereColor.w : 3.0;
    float ambientLight = 0.002;

    if (hit > 0.0)
    {
        float3 hitP = ro + hit * rd;
        float3 norm = normalize(hitP - uPlanetPosition);

        // Spherical projection with rotation
        float3 rotP = mul(rotateY(planetRot), hitP - uPlanetPosition) + uPlanetPosition;
        float2 surfaceUv = sphereProjection(rotP, uPlanetPosition);

        float4 rawColor = tex2D(ColorSampler, surfaceUv);
        float3 surfaceCol;

        if (EffectParams.z > 0.5)
        {
            // Earth: use original texture colors directly (matching earth.fragment.glsl)
            surfaceCol = rawColor.rgb;

            // Clouds blending matching earth.fragment.glsl lines 239-243
            float cloudsDensity = EffectParams.w > 0.0 ? EffectParams.w : 0.50;
            float3 rotCloudsP = mul(rotateY(cloudsRot), hitP - uPlanetPosition) + uPlanetPosition;
            float2 cloudsUv = sphereProjection(rotCloudsP, uPlanetPosition);
            float cloudVal = tex2D(CloudsSampler, cloudsUv).r;
            float cloudsThreshold = 1.0 - cloudsDensity;
            float smoothness = cloudsDensity * (1.0 - cloudsDensity);
            cloudVal *= smoothstep(cloudsThreshold - smoothness, cloudsThreshold, cloudVal);
            surfaceCol = lerp(surfaceCol, float3(1.0, 1.0, 1.0), cloudVal);

            // Diffuse lighting from earth.fragment.glsl line 262
            float directLight = pow(saturate(dot(norm, sunDir)), 2.0) * sunIntensity;
            float3 diffuseLight = surfaceCol * (ambientLight + directLight * SUN_COLOR);

            // Night lights on shadow side from earth.fragment.glsl line 235
            float nightLightIntensity = saturate(dot(-norm, sunDir) + 0.1);
            float nightSample = tex2D(NightSampler, surfaceUv).r;
            float3 nightCol = pow(nightSample, 3.0) * float3(1.0, 0.8, 0.6) * nightLightIntensity * 1.5;

            // Specular glint on oceans from earth.fragment.glsl lines 266-270
            float3 reflected = normalize(reflect(-sunDir, norm));
            float3 phongRd = normalize(float3(uv / FOCAL_LENGTH, -1.0));
            float phongValue = pow(max(0.0, dot(phongRd, reflected)), 8.0) * 0.2 * sunIntensity;
            float lum = dot(rawColor.rgb, float3(0.299, 0.587, 0.114));
            float isOcean = 1.0 - smoothstep(0.18, 0.45, lum);
            float3 specCol = isOcean * float3(phongValue, phongValue, phongValue) * float3(1.0, 1.0, 0.95);

            col = diffuseLight + nightCol + specCol;
        }
        else
        {
            // Other planets: Scurve contrast enhancement (matching planet.fragment.glsl)
            surfaceCol = Scurve(rawColor.rgb);

            float directLight = pow(saturate(dot(norm, sunDir)), 2.0) * sunIntensity;
            float3 diffuseLight = surfaceCol * (0.01 + directLight * SUN_COLOR);

            float3 reflected = normalize(reflect(-sunDir, norm));
            float phongValue = pow(max(0.0, dot(rd, reflected)), 10.0) * 0.05 * sunIntensity;
            float3 specCol = float3(phongValue, phongValue, phongValue);

            col = diffuseLight + specCol;
        }
    }
    else
    {
        col = spaceStars(rd);
    }

    // Atmosphere Rim Glow matching earth.fragment.glsl lines 201-213
    float atmoDensity = EffectParams.y;
    if (atmoDensity > 0.001)
    {
        float distOrigin = 16.0;
        float distEdge = 15.87450787; // sqrt(16^2 - 2^2)
        float3 coordFromCenter = (ro + rd * distEdge) - uPlanetPosition;
        float distFromEdge = abs(length(coordFromCenter) - uPlanetRadius);
        float planetEdge = max(uPlanetRadius - distFromEdge, 0.0) / uPlanetRadius;

        float atmoDot = dot(sunDir, coordFromCenter);
        float atmoRemap = saturate((atmoDot - (-uPlanetRadius)) / (uPlanetRadius * 0.5 - (-uPlanetRadius)));
        float atmoMask = pow(atmoRemap, 5.0) * atmoDensity * uPlanetRadius * sunIntensity;

        float planetMask = hit > 0.0 ? 1.0 : 0.0;
        float rim = pow(planetEdge, 120.0) * 0.5
                  + pow(planetEdge, 50.0) * 0.3 * (1.5 - planetMask)
                  + pow(planetEdge, 15.0) * 0.015
                  + pow(planetEdge, 5.0) * 0.04 * planetMask;

        col += AtmosphereColor.rgb * rim * atmoMask;
    }

    // Color grading: simpleReinhardToneMapping
    float exposure = 1.5;
    col *= exposure / (1.0 + col / exposure);
    col = pow(saturate(col), 1.0 / 2.2);

    // Vignette from repo
    col *= 1.0 - 0.5 * pow(length(uv), 3.0);

    return float4(saturate(col), 1.0);
}
