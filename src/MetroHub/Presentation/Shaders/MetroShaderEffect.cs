using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace MetroHub.Presentation.Shaders;

/// <summary>
/// Universal GPU Pixel Shader effect engine for Direct3D 9Ex HLSL shaders in MetroHub.
/// Loads precompiled bytecode from embedded assembly resources, caches frozen PixelShader instances,
/// and binds standardized registers:
/// - Sampler register s0: Input texture brush
/// - Constant register c0: FrameState (X=Time, Y=AspectRatio, Z=0, W=0)
/// - Constant register c1: EffectParams (custom effect constants)
/// </summary>
public class MetroShaderEffect : ShaderEffect
{
    private static readonly ConcurrentDictionary<string, PixelShader> ShaderCache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly Point4D DefaultWaterRippleParams = new(18.0, 0.006, 2.5, 2.2);

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(
            nameof(Input),
            typeof(MetroShaderEffect),
            0,
            SamplingMode.Bilinear);

    public static readonly DependencyProperty FrameStateProperty =
        DependencyProperty.Register(
            nameof(FrameState),
            typeof(Point4D),
            typeof(MetroShaderEffect),
            new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(0)));

    public static readonly DependencyProperty EffectParamsProperty =
        DependencyProperty.Register(
            nameof(EffectParams),
            typeof(Point4D),
            typeof(MetroShaderEffect),
            new UIPropertyMetadata(new Point4D(0, 0, 0, 0), PixelShaderConstantCallback(1)));

    /// <summary>
    /// Name of the compiled shader resource (e.g. "WaterRipple").
    /// </summary>
    public string ShaderName { get; }

    public Brush? Input
    {
        get => (Brush?)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public Point4D FrameState
    {
        get => (Point4D)GetValue(FrameStateProperty);
        set => SetValue(FrameStateProperty, value);
    }

    public Point4D EffectParams
    {
        get => (Point4D)GetValue(EffectParamsProperty);
        set => SetValue(EffectParamsProperty, value);
    }

    public new PixelShader? PixelShader
    {
        get => base.PixelShader;
        protected set => base.PixelShader = value;
    }

    public MetroShaderEffect(string shaderName, Point4D initialParams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderName);
        ShaderName = shaderName;
        PixelShader = GetOrCreatePixelShader(shaderName);
        EffectParams = initialParams;

        UpdateShaderValue(InputProperty);
        UpdateShaderValue(FrameStateProperty);
        UpdateShaderValue(EffectParamsProperty);
    }

    /// <summary>
    /// ARCHITECTURAL DIRECTIVE FOR AGENTS / AI / CONTRIBUTORS:
    /// DO NOT create separate *Effect.cs subclass files or *Registry.cs files for new backdrop shaders.
    /// All standard shaders using register s0 (sampler), c0 (FrameState), and c1 (EffectParams) are directly
    /// instantiated as MetroShaderEffect via this pattern-matching switch expression.
    /// To add a new shader effect:
    /// 1. Place YourEffect.hlsl in Presentation/Shaders/ and compile with compile_shaders.ps1.
    /// 2. Add ONE line to the switch expression below:
    ///    "YourEffect" => new MetroShaderEffect("YourEffect", new Point4D(...)),
    /// </summary>
    public static MetroShaderEffect? TryCreate(string? effectName) =>
        effectName switch
        {
            _ when string.Equals(effectName, "WaterRipple", StringComparison.OrdinalIgnoreCase) =>
                new MetroShaderEffect("WaterRipple", DefaultWaterRippleParams),
            _ => null
        };

    /// <summary>
    /// Checks if the named effect is a registered GPU shader effect.
    /// </summary>
    public static bool IsSupported(string? effectName) => TryCreate(effectName) != null;

    public static PixelShader GetOrCreatePixelShader(string shaderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderName);

        return ShaderCache.GetOrAdd(shaderName, static name =>
        {
            var assembly = typeof(MetroShaderEffect).Assembly;
            string resourceName = $"MetroHub.Presentation.Shaders.{name}.ps";
            using Stream? stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded shader bytecode '{resourceName}' was not found in assembly '{assembly.GetName().Name}'.");

            var shader = new PixelShader();
            shader.SetStreamSource(stream);
            shader.Freeze();
            return shader;
        });
    }
}

