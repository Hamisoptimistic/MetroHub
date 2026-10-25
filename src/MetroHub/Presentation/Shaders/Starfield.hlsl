sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=0, W=0
float4 EffectParams : register(c1); // X=Threshold (0.97), Y=VirtualResolution (1080.0), Z=Speed (1.0), W=Brightness (1.0)

// Return random noise in the range [0.0, 1.0], as a function of x.
float Noise2d(float2 x)
{
    float xhash = cos(x.x * 37.0);
    float yhash = cos(x.y * 57.0);
    return frac(415.92653 * (xhash + yhash));
}

// Convert Noise2d() into a "star field" by stomping everything below fThreshhold to zero.
float NoisyStarField(float2 vSamplePos, float fThreshhold)
{
    float starVal = Noise2d(vSamplePos);
    if (starVal >= fThreshhold)
    {
        starVal = pow(saturate((starVal - fThreshhold) / (1.0 - fThreshhold)), 6.0);
    }
    else
    {
        starVal = 0.0;
    }
    return starVal;
}

// Stabilize NoisyStarField() by only sampling at integer values.
float StableStarField(float2 vSamplePos, float fThreshhold)
{
    float fractX = frac(vSamplePos.x);
    float fractY = frac(vSamplePos.y);
    float2 floorSample = floor(vSamplePos);

    float v1 = NoisyStarField(floorSample, fThreshhold);
    float v2 = NoisyStarField(floorSample + float2(0.0, 1.0), fThreshhold);
    float v3 = NoisyStarField(floorSample + float2(1.0, 0.0), fThreshhold);
    float v4 = NoisyStarField(floorSample + float2(1.0, 1.0), fThreshhold);

    return v1 * (1.0 - fractX) * (1.0 - fractY)
         + v2 * (1.0 - fractX) * fractY
         + v3 * fractX * (1.0 - fractY)
         + v4 * fractX * fractY;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float threshold = EffectParams.x > 0.0 ? EffectParams.x : 0.97;
    float resolution = EffectParams.y > 0.0 ? EffectParams.y : 1080.0;
    float speed = EffectParams.z > 0.0 ? EffectParams.z : 1.0;
    float brightness = EffectParams.w > 0.0 ? EffectParams.w : 1.0;

    float aspect = FrameState.y > 0.0 ? FrameState.y : (16.0 / 9.0);

    // Sky Background Color (cosmic blue gradient - slightly boosted)
    float3 vColor = float3(0.13, 0.24, 0.48) * (1.0 - uv.y);

    // Virtual pixel grid for consistent star size across resolutions
    float2 fragCoord = float2(uv.x * aspect, uv.y) * resolution;

    // Time-based crawl: ~60 ticks per second
    float frame = FrameState.x * 60.0 * speed;
    float xRate = 0.2;
    float yRate = -0.06;
    float2 vSamplePos = fragCoord + float2(xRate * frame, yRate * frame);

    float starVal = StableStarField(vSamplePos, threshold) * brightness;
    vColor += float3(starVal, starVal, starVal);

    return float4(saturate(vColor), 1.0);
}

