// Planet.hlsl - Universal Textured Planet Shader with Exact Website Default Controls & Shading
// Ported from jsulpis/realtime-planet-shader (planet.fragment.glsl & earth.fragment.glsl) for MetroHub Direct3D 9 ps_3_0
// High-Definition Starfield & Galactic Nebula: 100% GPU, Deep Space Black, 0 MB System RAM

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
static const float3 SUN_COLOR       = float3(1.0, 1.0, 0.9);

// Orthogonal 3D rotation matrix to break cubic lattice alignment between FBM octaves
static const float3x3 NEBULA_ROT = float3x3(
     0.00,  0.80,  0.60,
    -0.80,  0.36, -0.48,
    -0.60, -0.48,  0.64
);

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

// ============================================================================
// High-Definition Procedural Cosmos: Galactic Band, Cyan/Royal-Blue/Violet
// Billowing Nebula, Dark Dust Rifts & 4-Tier Circular Gaussian Starfield
// ============================================================================

float hash13(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.zyx + 33.33);
    return frac((p.x + p.y) * p.z);
}

float3 hash33(float3 p)
{
    p = frac(p * float3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return frac((p.xxy + p.yxx) * p.zyx);
}

// C2-continuous quintic 3D value noise (eliminates cubic Mach banding)
float noise3(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);

    float n000 = hash13(i + float3(0.0, 0.0, 0.0));
    float n100 = hash13(i + float3(1.0, 0.0, 0.0));
    float n010 = hash13(i + float3(0.0, 1.0, 0.0));
    float n110 = hash13(i + float3(1.0, 1.0, 0.0));
    float n001 = hash13(i + float3(0.0, 0.0, 1.0));
    float n101 = hash13(i + float3(1.0, 0.0, 1.0));
    float n011 = hash13(i + float3(0.0, 1.0, 1.0));
    float n111 = hash13(i + float3(1.0, 1.0, 1.0));

    float nx00 = lerp(n000, n100, u.x);
    float nx10 = lerp(n010, n110, u.x);
    float nx01 = lerp(n001, n101, u.x);
    float nx11 = lerp(n011, n111, u.x);

    float nxy0 = lerp(nx00, nx10, u.y);
    float nxy1 = lerp(nx01, nx11, u.y);

    return lerp(nxy0, nxy1, u.z);
}

// Stellar color temperature lookup (blue-white O/B, white A/F, golden G/K, warm rose M)
float3 starTemperatureColor(float t)
{
    float3 coolGold = float3(1.35, 0.92, 0.62);
    float3 pureWhite = float3(1.05, 1.08, 1.15);
    float3 hotBlue  = float3(0.68, 0.88, 1.40);
    return (t < 0.5)
        ? lerp(coolGold, pureWhite, t * 2.0)
        : lerp(pureWhite, hotBlue, (t - 0.5) * 2.0);
}

float3 spaceNebula(float3 gDir, float time)
{
    // Slow, natural celestial drift
    float3 drift = float3(time * 0.003, time * 0.001, -time * 0.002);

    // Broad, smooth 3-octave FBM for natural cosmic gas clouds
    float3 p1 = gDir * 1.8 + drift;
    float n1 = noise3(p1);

    // Rotated octaves to break any grid alignment
    float3 p2 = mul(NEBULA_ROT, p1 * 1.8) + float3(n1 * 0.40, -n1 * 0.30, n1 * 0.20);
    float n2 = noise3(p2);

    float3 p3 = mul(NEBULA_ROT, p2 * 1.9) + float3(-n2 * 0.30, n2 * 0.30, -n2 * 0.20);
    float n3 = noise3(p3);

    // Smooth, spread-out cloud density across space (no sharp edges or narrow ribbons)
    float cloud = n1 * 0.52 + n2 * 0.32 + n3 * 0.16;

    // Gentle contrast curve: spreads out smoothly with no hard borders
    float density = smoothstep(0.22, 0.78, cloud);
    density = pow(density, 1.5);

    // Realistic deep space palette: midnight navy into soft, diffuse celestial blue
    float3 deepNavy  = float3(0.0010, 0.0035, 0.0120);
    float3 softBlue  = float3(0.0050, 0.0200, 0.0600);
    float3 cyanGlow  = float3(0.0140, 0.0550, 0.1250);

    float3 col = lerp(deepNavy, softBlue, saturate(density * 1.6));
    col = lerp(col, cyanGlow, saturate((density - 0.40) * 1.8));

    return col * density * 1.6;
}

// Single 2D spherical star layer helper: crisp pinpricks strictly bounded inside cell
float3 evalStarLayer(float2 skyPos, float cosLat, float scale, float seed, float threshold, float coreRad, float haloRad, float time, float twinkleAmt)
{
    float2 p = skyPos * scale;
    float2 id = floor(p);
    float2 f = frac(p) - 0.5;

    float3 h = hash33(float3(id, seed));
    float presence = saturate((h.z - threshold) / (1.0 - threshold));
    if (presence <= 0.0) return float3(0.0, 0.0, 0.0);

    // Keep jitter + haloRad strictly inside cell so stars never clip
    float2 jitter = (h.xy - 0.5) * 0.32;
    float2 delta = f - jitter;
    delta.x *= cosLat; // Metric correction -> 100% round circular stars on screen

    float dist = length(delta);

    // Crisp pinpoint core + very subtle, tight halo (no giant blurry blobs)
    float core = pow(saturate(1.0 - dist / coreRad), 6.0);
    float halo = pow(saturate(1.0 - dist / haloRad), 3.0) * 0.15;

    float twinkle = 1.0 - twinkleAmt + twinkleAmt * (0.5 + 0.5 * sin(time * (1.8 + h.x * 2.5) + h.y * 62.83));
    float3 col = starTemperatureColor(frac(h.x * 7.13 + h.y * 3.71));

    float mag = presence * presence;
    return (core + halo) * mag * twinkle * col;
}

float3 spaceStars(float2 skyPos, float cosLat, float time)
{
    float3 stars = float3(0.0, 0.0, 0.0);

    // Tier 1: Dense microscopic cosmic stardust (fine pinpricks across the sky)
    stars += evalStarLayer(skyPos, cosLat, 260.0, 11.3, 0.82, 0.080, 0.110, time, 0.05) * 0.35;

    // Tier 2: Crisp distinct stars with realistic spectral color temperature
    stars += evalStarLayer(skyPos, cosLat, 150.0, 37.9, 0.88, 0.070, 0.100, time, 0.10) * 0.85;

    // Tier 3: Occasional brighter pinprick stars with small subtle glow (strictly no giant fuzzy blobs)
    stars += evalStarLayer(skyPos, cosLat,  85.0, 73.1, 0.94, 0.060, 0.090, time, 0.16) * 1.60;

    return stars;
}

float3 spaceBackground(float3 dir, float time)
{
    // Slow, majestic celestial rotation
    float rotAngle = fmod(time * 0.0015, 628.31853);
    float3 gDir = mul(rotateY(rotAngle), dir);

    // Continuous 2D spherical coordinates for stars
    float lon = atan2(dir.x, -dir.z) + rotAngle;
    float lat = asin(clamp(dir.y, -0.99, 0.99));
    float cosLat = sqrt(max(1.0 - dir.y * dir.y, 0.05));
    float2 skyPos = float2(lon, lat);

    // Deep space baseline (rich, clean dark cosmos)
    static const float3 DEEP_COSMOS = float3(0.0001, 0.0002, 0.0005);

    float3 nebula = spaceNebula(gDir, time);
    float3 stars  = spaceStars(skyPos, cosLat, time);

    return DEEP_COSMOS + nebula + stars;
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

    // Balanced ambient lighting: Earth = 0.008, Other planets = 0.020
    float sunIntensity = AtmosphereColor.w > 0.0 ? AtmosphereColor.w : 4.0;
    float ambientLight = (EffectParams.z > 0.5) ? 0.008 : 0.020;

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
            float sunFacing = saturate(dot(norm, sunDir));
            float directLight = pow(sunFacing, 2.0) * sunIntensity;
            float3 diffuseLight = surfaceCol * (ambientLight + directLight * SUN_COLOR);

            // Night lights on shadow side: rich golden city lights on dark continents
            float nightLightIntensity = saturate(dot(-norm, sunDir) + 0.1);
            float nightSample = tex2D(NightSampler, surfaceUv).r;
            float3 nightCol = pow(nightSample, 2.8) * float3(1.2, 0.95, 0.65) * nightLightIntensity * 2.2;

            // Specular glint on oceans: strictly gated by sunlight (NEVER shines on night side)
            float3 reflected = normalize(reflect(-sunDir, norm));
            float phongRd = normalize(float3(uv / FOCAL_LENGTH, -1.0));
            float phongValue = pow(saturate(dot(phongRd, reflected)), 12.0) * 0.35 * sunIntensity * sunFacing;
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
        // Background: Galactic Nebula & 4-Tier Circular Gaussian Starfield
        float3 backgroundRd = normalize(float3(uv * 0.75, -1.0));
        col = spaceBackground(backgroundRd, time);
    }

    // Atmosphere Rim Glow: Radiant High-Luminance Electric Cyan-Blue Scattering Halo
    float atmoDensity = EffectParams.y;
    if (atmoDensity > 0.001)
    {
        float distOrigin = 16.0;
        float distEdge = 15.87450787; // sqrt(16^2 - 2^2)
        float3 coordFromCenter = (ro + rd * distEdge) - uPlanetPosition;
        float distFromEdge = abs(length(coordFromCenter) - uPlanetRadius);
        float planetEdge = max(uPlanetRadius - distFromEdge, 0.0) / uPlanetRadius;

        float atmoDot = dot(sunDir, coordFromCenter);
        float atmoRemap = saturate((atmoDot - (-uPlanetRadius * 0.3)) / (uPlanetRadius * 0.6 - (-uPlanetRadius * 0.3)));
        float atmoMask = pow(atmoRemap, 3.5) * atmoDensity * uPlanetRadius * sunIntensity;

        float planetMask = hit > 0.0 ? 1.0 : 0.0;
        float rim = pow(planetEdge, 120.0) * 0.85
                  + pow(planetEdge, 45.0)  * 0.45 * (1.5 - planetMask)
                  + pow(planetEdge, 18.0)  * 0.12 * (1.5 - planetMask)
                  + pow(planetEdge, 6.0)   * 0.03 * (1.5 - planetMask)
                  + pow(planetEdge, 15.0)  * 0.02
                  + pow(planetEdge, 4.0)   * 0.06 * planetMask;

        float3 atmoColor = (EffectParams.z > 0.5) ? float3(0.10, 0.55, 1.35) : AtmosphereColor.rgb;
        col += atmoColor * rim * atmoMask;
    }

    // Color grading: simpleReinhardToneMapping matching website
    float exposure = 1.5;
    col *= exposure / (1.0 + col / exposure);
    col = pow(saturate(col), 1.0 / 2.4);

    // Vignette matching website
    col *= 1.0 - 0.5 * pow(length(uv), 3.0);

    return float4(saturate(col), 1.0);
}
