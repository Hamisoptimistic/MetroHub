// Planet.hlsl - Universal Textured Planet Shader with Exact Website Default Controls & Shading
// Ported from jsulpis/realtime-planet-shader (planet.fragment.glsl & earth.fragment.glsl) for MetroHub Direct3D 9 ps_3_0
// Procedural High-Definition Starfield & Milky Way: 100% GPU, 0 MB System RAM

sampler2D ColorSampler    : register(s0);
sampler2D NightSampler    : register(s1);
sampler2D CloudsSampler   : register(s2);
sampler2D SpecularSampler : register(s3);

float4 FrameState      : register(c0); // X=Time, Y=AspectRatio, Z=TimeOfDaySeconds, W=Unused
float4 EffectParams    : register(c1); // X=TimeMode (0=Realtime, 1=Website 45deg), Y=AtmoDensity, Z=HasNight, W=CloudsDensity
float4 MouseState      : register(c2); // X=MouseX, Y=MouseY, Z=LeftDown, W=PlanetDragYaw
float4 AtmosphereColor : register(c3); // RGB=AtmoColor, W=SunIntensity

static const float PI = 3.14159265359;
static const float3 CAMERA_POSITION = float3(0.0, 0.0, 6.0);
static const float3 uPlanetPosition = float3(0.0, 0.0, -10.0);
static const float  uPlanetRadius   = 2.0;
static const float  FOCAL_LENGTH    = 0.375; // 6.0 / (6.0 - (-10.0))
static const float3 SUN_COLOR       = float3(1.0, 1.0, 1.0);
static const float3 DEEP_SPACE      = float3(0.0005, 0.0005, 0.001);

// Exact Scurve sigmoid contrast function from planet.fragment.glsl lines 145-151
float Sigmoid(float x)
{
    return 1.0 / (1.0 + exp(-(x - 0.7) * 6.5));
}

float3 Scurve(float3 c)
{
    return float3(Sigmoid(c.x), Sigmoid(c.y), Sigmoid(c.z));
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
    return float3x3(c, 0, -s, 0, 1, 0, s, 0, c);
}

float2 sphereProjection(float3 p, float3 origin)
{
    float3 dir = normalize(p - origin);
    float lon = atan2(dir.x, dir.z);
    float lat = asin(clamp(dir.y, -1.0, 1.0));
    // DirectX texture coords: V=0 is North Pole (top), V=1 is South Pole (bottom)
    return float2((lon + PI) / (2.0 * PI), (PI * 0.5 - lat) / PI);
}

// 100% Procedural High-Definition Space Background (0 MB System RAM)
float hash31(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yzx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float noise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);

    float n000 = hash31(i + float3(0, 0, 0));
    float n100 = hash31(i + float3(1, 0, 0));
    float n010 = hash31(i + float3(0, 1, 0));
    float n110 = hash31(i + float3(1, 1, 0));
    float n001 = hash31(i + float3(0, 0, 1));
    float n101 = hash31(i + float3(1, 0, 1));
    float n011 = hash31(i + float3(0, 1, 1));
    float n111 = hash31(i + float3(1, 1, 1));

    float nx00 = lerp(n000, n100, u.x);
    float nx10 = lerp(n010, n110, u.x);
    float nx01 = lerp(n001, n101, u.x);
    float nx11 = lerp(n011, n111, u.x);

    float nxy0 = lerp(nx00, nx10, u.y);
    float nxy1 = lerp(nx01, nx11, u.y);

    return lerp(nxy0, nxy1, u.z);
}

float3 spaceColor(float3 dir, float time)
{
    // Slowly rotate background skybox
    float3 rDir = mul(rotateY(time * 0.005), dir);

    // Galactic plane tilted across screen matching the website panorama
    static const float3 GALACTIC_NORMAL = float3(0.22, 0.94, -0.26);
    static const float3 CORE_DIR = float3(-0.35, -0.08, -0.93);

    float distGal = abs(dot(rDir, normalize(GALACTIC_NORMAL)));
    // Milky Way dense band profile
    float band = exp(-(distGal * distGal) / (2.0 * 0.0256));

    // Galactic core bulge
    float core = pow(saturate((dot(rDir, normalize(CORE_DIR)) - 0.7) / 0.3), 3.0);

    // Multi-scale cosmic dust lanes and star clouds
    float fbm = noise3(rDir * 10.0) * 0.65 + noise3(rDir * 22.0) * 0.35;
    // Dark absorption rift slicing through the galactic core
    float rift = 1.0 - 0.85 * exp(-pow(distGal - 0.02 * fbm, 2.0) / 0.004) * saturate(fbm * 1.5);

    // Rich nebula coloration: warm peach/amber core + cool violet/blue interstellar arms
    float3 coreCol = float3(0.70, 0.45, 0.20) * core;
    float3 armCol  = float3(0.18, 0.14, 0.22);
    float3 nebula  = band * (armCol + coreCol) * pow(fbm, 1.5) * rift;

    // Layer 1: Dense background stars (concentrated inside the Milky Way)
    float3 p1 = rDir * 320.0;
    float3 id1 = floor(p1);
    float3 q1 = p1 - id1 - 0.5;
    float h1 = hash31(id1);
    float star1 = saturate((h1 - 0.965) / 0.035);
    star1 *= saturate(1.0 - length(q1) / 0.32);
    star1 *= (0.25 + 0.75 * band);

    // Layer 2: Bright distinct foreground stars (sparkling diamonds)
    float3 p2 = rDir * 110.0;
    float3 id2 = floor(p2);
    float3 q2 = p2 - id2 - 0.5;
    float h2 = hash31(id2 + 100.0);
    float star2 = pow(saturate((h2 - 0.988) / 0.012), 2.0);
    star2 *= saturate(1.0 - length(q2) / 0.42);

    float3 stars = star1 * float3(0.85, 0.92, 1.0) + star2 * float3(1.2, 1.1, 0.95);

    return DEEP_SPACE + nebula + stars;
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

    // Exact website lighting parameters (Slider 0.50 = 0.5^5 = 0.03125 for all bodies)
    float sunIntensity = AtmosphereColor.w > 0.0 ? AtmosphereColor.w : 4.0;
    float ambientLight = 0.06;

    if (hit > 0.0)
    {
        float3 hitP = ro + hit * rd;
        float3 norm = normalize(hitP - uPlanetPosition);

        // Spherical projection with rotation matching website
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

            // Specular glint on oceans using NASA specular map (2k_earth_specular)
            float3 reflected = normalize(reflect(-sunDir, norm));
            float phongValue = pow(saturate(dot(-rd, reflected)), 8.0) * 0.3 * sunIntensity;
            float oceanMask = tex2D(SpecularSampler, surfaceUv).r;
            float3 specCol = oceanMask * phongValue * SUN_COLOR;

            col = diffuseLight + nightCol + specCol;
        }
        else
        {
            // Other planets: Scurve contrast enhancement (matching planet.fragment.glsl)
            surfaceCol = Scurve(rawColor.rgb);

            float directLight = pow(saturate(dot(norm, sunDir)), 2.0) * sunIntensity;
            float3 diffuseLight = surfaceCol * (ambientLight + directLight * SUN_COLOR);

            // Rocky/gas planets have no ocean specular shine
            col = diffuseLight;
        }
    }
    else
    {
        // Background stars & Milky Way: 100% GPU procedural, 0 MB System RAM
        float3 backgroundRd = normalize(float3(uv * 0.75, -1.0));
        col = spaceColor(backgroundRd, time);
    }

    // Atmosphere Rim Glow matching planet.fragment.glsl lines 190-205
    float atmoDensity = EffectParams.y;
    if (atmoDensity > 0.001)
    {
        float distOrigin = 16.0;
        float distEdge = 15.87450787; // sqrt(16^2 - 2^2)
        float3 coordFromCenter = (ro + rd * distEdge) - uPlanetPosition;
        float distFromEdge = abs(length(coordFromCenter) - uPlanetRadius);
        float planetEdge = max(uPlanetRadius - distFromEdge, 0.0) / uPlanetRadius;

        float atmoDot = dot(sunDir, coordFromCenter);
        float atmoRemap = max(0.0, (atmoDot - (-uPlanetRadius)) / (uPlanetRadius * 0.5 - (-uPlanetRadius)));
        float atmoMask = pow(atmoRemap, 5.0) * atmoDensity * uPlanetRadius * sunIntensity;

        float planetMask = hit > 0.0 ? 1.0 : 0.0;
        float rim = pow(planetEdge, 120.0) * 0.5
                  + pow(planetEdge, 50.0) * 0.3 * (1.5 - planetMask)
                  + pow(planetEdge, 15.0) * 0.015
                  + pow(planetEdge, 5.0) * 0.04 * planetMask;

        col += AtmosphereColor.rgb * rim * atmoMask;
    }

    // Color grading: simpleReinhardToneMapping matching website
    float exposure = 1.5;
    col *= exposure / (1.0 + col / exposure);
    col = pow(saturate(col), 1.0 / 2.4);

    // Vignette matching website
    col *= 1.0 - 0.5 * pow(length(uv), 3.0);

    return float4(saturate(col), 1.0);
}