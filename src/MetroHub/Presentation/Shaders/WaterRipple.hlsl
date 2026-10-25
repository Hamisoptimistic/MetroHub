sampler2D InputSampler : register(s0);
float4 FrameState : register(c0);   // X=Time (Bounded Modulo), Y=AspectRatio, Z=0 (Unused), W=0 (Unused)
float4 EffectParams : register(c1); // X=Freq (18.0), Y=Amp (0.006), Z=Speed (2.5), W=Damping (2.2)

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 delta = uv - float2(0.5, 0.5);
    float2 metricDelta = float2(delta.x * FrameState.y, delta.y);
    float dist = length(metricDelta);
    float2 dir = delta / max(dist, 1e-5);

    // 1. Center envelope: Eliminates pinhole center tear
    float centerFade = smoothstep(0.0, 0.04, dist);

    // 2. Primary wave
    float primaryWave = sin(dist * EffectParams.x - FrameState.x * EffectParams.z);

    // 3. Radial damping
    float amp = EffectParams.y;
    float damping = exp(-dist * EffectParams.w);

    // 4. Border envelope: Prevents edge-clamp sampling streaks (strictly min < max)
    float edgeFade = smoothstep(0.0, 0.04, uv.x) * smoothstep(0.0, 0.04, 1.0 - uv.x) *
                     smoothstep(0.0, 0.04, uv.y) * smoothstep(0.0, 0.04, 1.0 - uv.y);

    float totalDisplacement = primaryWave * amp * damping * centerFade * edgeFade;
    float2 offset = dir * totalDisplacement;

    return tex2D(InputSampler, saturate(uv + offset));
}

