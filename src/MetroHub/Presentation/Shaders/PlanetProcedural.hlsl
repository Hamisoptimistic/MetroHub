// PlanetProcedural.hlsl - Procedural Planet with FBM Terrain, Dynamic Clouds, Orbiting Moon, and Atmosphere
// Ported from jsulpis/realtime-planet-shader (procedural.fragment.glsl) for MetroHub Direct3D 9 ps_3_0

sampler2D InputSampler : register(s0);
float4 FrameState      : register(c0); // X=Time, Y=AspectRatio, Z=TimeOfDaySeconds, W=Unused
float4 EffectParams    : register(c1); // X=CloudsDensity, Y=AtmosphereDensity, Z=SunIntensity, W=RotationSpeed
float4 MouseState      : register(c2); // X=MouseX, Y=MouseY, Z=LeftDown, W=PlanetDragYaw

static const float PI = 3.14159265359;
static const float3 CAMERA_POSITION = float3(0.0, 0.0, 6.0);
static const float3 SUN_COLOR = float3(1.0, 1.0, 0.95);
static const float3 DEEP_SPACE = float3(0.0005, 0.0005, 0.002);

// Planet Palette
static const float3 WATER_COLOR_DEEP    = float3(0.01, 0.05, 0.15);
static const float3 WATER_COLOR_SURFACE = float3(0.02, 0.12, 0.27);
static const float3 SAND_COLOR          = float3(0.95, 0.90, 0.75);
static const float3 TREE_COLOR          = float3(0.02, 0.14, 0.06);
static const float3 ROCK_COLOR          = float3(0.18, 0.14, 0.14);
static const float3 ICE_COLOR           = float3(0.85, 0.92, 0.95);
static const float3 CLOUD_COLOR         = float3(1.0, 1.0, 1.0);

static const float WATER_SURFACE_LEVEL = 0.0;
static const float SAND_LEVEL          = 0.028;
static const float TREE_LEVEL          = 0.035;
static const float ROCK_LEVEL          = 0.10;
static const float ICE_LEVEL           = 0.16;
static const float TRANSITION          = 0.02;

static const float3 uPlanetPosition = float3(0.0, 0.0, 0.0);
static const float  uPlanetRadius   = 1.85;

static const float  MOON_RADIUS     = 0.14;
static const float3 MOON_OFFSET     = float3(2.5, 0.5, 0.0);

float hash13(float3 p3)
{
    p3 = frac(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return frac((p3.x + p3.y) * p3.z);
}

float noise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);

    return lerp(
        lerp(lerp(hash13(i + float3(0,0,0)), hash13(i + float3(1,0,0)), u.x),
             lerp(hash13(i + float3(0,1,0)), hash13(i + float3(1,1,0)), u.x), u.y),
        lerp(lerp(hash13(i + float3(0,0,1)), hash13(i + float3(1,0,1)), u.x),
             lerp(hash13(i + float3(0,1,1)), hash13(i + float3(1,1,1)), u.x), u.y),
        u.z
    );
}

float fbm(float3 p, int octaves)
{
    float total = 0.0;
    float amp = 0.5;
    float norm = 0.0;
    [loop]
    for (int i = 0; i < octaves; ++i)
    {
        total += noise3D(p) * amp;
        norm += amp;
        amp *= 0.5;
        p *= 2.02;
    }
    total /= norm;
    return pow(total * 0.8 + 0.1, 3.5);
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

float planetNoise(float3 p)
{
    float n = fbm(p * 1.5, 4);
    return lerp(n * 0.3, n, smoothstep(SAND_LEVEL, SAND_LEVEL + TRANSITION * 0.5, n * 5.0));
}

float planetDist(float3 ro, float3 rd, float rotAngle)
{
    float smoothDist = sphIntersect(ro, rd, uPlanetPosition, uPlanetRadius);
    if (smoothDist < 0.0) return -1.0;
    float3 hit = ro + smoothDist * rd;
    float3 rotP = mul(rotateY(rotAngle), hit - uPlanetPosition) + uPlanetPosition;
    return sphIntersect(ro, rd, uPlanetPosition, uPlanetRadius + planetNoise(rotP) * 0.15);
}

float3 planetNormal(float3 p, float rotAngle)
{
    float3 rd = normalize(uPlanetPosition - p);
    float d = planetDist(p, rd, rotAngle);
    float e = 0.015;
    float3 n = d - float3(
        planetDist(p - float3(e, 0, 0), rd, rotAngle),
        planetDist(p - float3(0, e, 0), rd, rotAngle),
        planetDist(p + float3(0, 0, e), rd, rotAngle)
    );
    return normalize(n);
}

float3 spaceStars(float3 dir)
{
    float3 p = dir * 140.0;
    float3 id = floor(p);
    float3 q = frac(p) - 0.5;
    float starHash = hash13(id);
    float star = smoothstep(0.985, 1.0, starHash);
    star *= 1.0 - smoothstep(0.0, 0.45, length(q));
    return DEEP_SPACE + star * float3(1.0, 0.95, 0.85);
}

float4 main(float2 texCoord : TEXCOORD) : COLOR
{
    float aspect = FrameState.y > 0.0 ? FrameState.y : 1.7777778;
    float time = FrameState.x;

    float2 uv = float2((texCoord.x - 0.5) * aspect, -(texCoord.y - 0.5));

    // Fixed camera - completely removes unwanted cursor parallax
    float3 ro = CAMERA_POSITION;
    float3 rd = normalize(float3(uv, -1.0));

    float rotSpeed = EffectParams.w > 0.0 ? EffectParams.w : 0.08;
    float dragOffset = MouseState.w;
    float planetRot = time * rotSpeed + dragOffset;

    float3 sunDir = normalize(float3(1.2, 0.35, 0.8));

    // Moon calculation
    float moonSpeed = -rotSpeed * 2.5;
    float3 moonPos = mul(rotateY(time * moonSpeed + dragOffset * 0.5), MOON_OFFSET) + uPlanetPosition;
    float moonHit = sphIntersect(ro, rd, moonPos, MOON_RADIUS);

    // Planet intersection
    float planetHit = sphIntersect(ro, rd, uPlanetPosition, uPlanetRadius);

    float3 col = float3(0.0, 0.0, 0.0);

    if (moonHit > 0.0 && (planetHit < 0.0 || moonHit < planetHit))
    {
        // Shading the Moon
        float3 hitP = ro + moonHit * rd;
        float3 norm = normalize(hitP - moonPos);
        float moonNoise = fbm(hitP * 12.0, 3);
        float3 moonCol = float3(moonNoise, moonNoise, moonNoise) * 0.8;
        float diff = saturate(dot(norm, sunDir));
        col = moonCol * diff * SUN_COLOR + moonCol * 0.02;
    }
    else if (planetHit > 0.0)
    {
        float3 hitP = ro + planetHit * rd;
        float3 rotP = mul(rotateY(planetRot), hitP - uPlanetPosition) + uPlanetPosition;
        float altitude = 5.0 * planetNoise(rotP);

        float3 norm = planetNormal(hitP, planetRot);

        // Biome gradient
        float3 surfaceCol = lerp(WATER_COLOR_DEEP, WATER_COLOR_SURFACE, smoothstep(WATER_SURFACE_LEVEL, WATER_SURFACE_LEVEL + TRANSITION, altitude));
        surfaceCol = lerp(surfaceCol, SAND_COLOR, smoothstep(SAND_LEVEL, SAND_LEVEL + TRANSITION * 0.5, altitude));
        surfaceCol = lerp(surfaceCol, TREE_COLOR, smoothstep(TREE_LEVEL, TREE_LEVEL + TRANSITION, altitude));
        surfaceCol = lerp(surfaceCol, ROCK_COLOR, smoothstep(ROCK_LEVEL, ROCK_LEVEL + TRANSITION, altitude));
        surfaceCol = lerp(surfaceCol, ICE_COLOR, smoothstep(ICE_LEVEL, ICE_LEVEL + TRANSITION, altitude));

        // Procedural Clouds
        float3 cloudCoord = (rotP + float3(time * 0.015, 0.0, 0.0)) * 2.2;
        float cloudDensity = fbm(cloudCoord, 3);
        float cloudMask = smoothstep(0.42, 0.65, cloudDensity);
        surfaceCol = lerp(surfaceCol, CLOUD_COLOR, cloudMask * 0.85);

        // Diffuse & Ambient Lighting
        float directLight = pow(saturate(dot(norm, sunDir)), 1.8) * 2.5;
        float3 diffuse = surfaceCol * (0.015 + directLight * SUN_COLOR);

        // Ocean Specular glint
        float3 ref = reflect(-sunDir, norm);
        float spec = pow(saturate(dot(rd, ref)), 12.0) * (1.0 - smoothstep(SAND_LEVEL, SAND_LEVEL + TRANSITION, altitude));
        diffuse += spec * float3(1.0, 0.98, 0.9) * 0.8;

        col = diffuse;
    }
    else
    {
        col = spaceStars(rd);
    }

    // Atmosphere Rim Glow
    float distOrigin = length(uPlanetPosition - ro);
    float distEdge = sqrt(max(distOrigin * distOrigin - uPlanetRadius * uPlanetRadius, 0.001));
    float3 coordFromCenter = (ro + rd * distEdge) - uPlanetPosition;
    float distFromEdge = abs(length(coordFromCenter) - uPlanetRadius);
    float planetEdge = saturate((uPlanetRadius - distFromEdge) / uPlanetRadius);

    float atmoDot = dot(sunDir, coordFromCenter);
    float atmoMask = pow(saturate((atmoDot + uPlanetRadius) / (uPlanetRadius * 1.5)), 4.0);
    float3 atmoColor = float3(0.12, 0.45, 1.0);
    float rim = pow(planetEdge, 100.0) * 0.5 + pow(planetEdge, 35.0) * 0.3 + pow(planetEdge, 12.0) * 0.03;
    col += atmoColor * rim * atmoMask * 0.8;

    // Reinhard Tone Mapping
    col *= 1.5 / (1.0 + col / 1.5);
    col = pow(saturate(col), 1.0 / 2.2);

    // Subtle Vignette
    col *= 1.0 - 0.4 * pow(length(uv), 3.0);

    return float4(saturate(col), 1.0);
}
