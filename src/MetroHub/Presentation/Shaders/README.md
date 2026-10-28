# MetroHub Shader Architecture

This directory contains the Direct3D 9 GPU Pixel Shader engine used for animated window backdrops and interactive effects.

---

## 1. System Components

```
┌────────────────────────────────────────────────────────────────────────┐
│                        MetroHub Shader Pipeline                        │
├────────────────────────────────────────────────────────────────────────┤
│ 1. INPUT SOURCES                                                       │
│    • Display Render Timer  ──► Time, Aspect Ratio                      │
│    • Window Mouse Events   ──► Normalized Mouse (X, Y, Click)          │
│    • Audio Service (BASS)  ──► Bass / Beat Energy                      │
│                                │                                       │
│                                ▼                                       │
│ 2. DRIVER: BackdropShaderDriver.cs                                     │
│    • Synchronized to display refresh via CompositionTarget.Rendering   │
│    • Stops rendering completely when window is hidden or minimized     │
│    • Passes live frame and mouse state into MetroShaderEffect          │
│                                │                                       │
│                                ▼                                       │
│ 3. ENGINE: MetroShaderEffect.cs                                        │
│    • Universal WPF ShaderEffect container                              │
│    • Loads embedded bytecode (.ps) and caches frozen PixelShader       │
│    • Binds Direct3D 9 constant registers (s0, c0, c1)                  │
│    • Single switch factory to create any effect with default params    │
│                                │                                       │
│                                ▼                                       │
│ 4. GPU EXECUTION (Direct3D 9 Pixel Shader 3.0)                         │
│    • WaterRipple.ps, TinyClouds.ps, etc.                               │
│    • Executes directly on the graphics hardware                        │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Direct3D 9 Register Standard

All `.hlsl` shaders must declare these standard registers:

| Register | HLSL Variable | Type | Description |
|---|---|---|---|
| `s0` | `InputSampler` | `sampler2D` | Target background texture (wallpaper image or custom brush) |
| `c0` | `FrameState` | `float4` | `X` = Time (modulo), `Y` = Aspect Ratio (`W/H`), `Z` = DeltaTime, `W` = Unused |
| `c1` | `EffectParams` | `float4` | `X, Y, Z, W` = Custom static constants for this effect |

---

## 3. Adding a New Effect in 3 Steps

When you create a new shader, **do not create a separate `*Effect.cs` subclass file**.

```
┌────────────────────────────────────────────────────────────────────────┐
│                        3-Step Developer Recipe                         │
├────────────────────────────────────────────────────────────────────────┤
│ Step 1: Write the HLSL Shader                                          │
│   Create src/MetroHub/Presentation/Shaders/YourEffect.hlsl             │
│   Use registers s0, c0, and c1.                                        │
│                                                                        │
│ Step 2: Compile Bytecode                                               │
│   Run tools/compile_shaders.ps1                                        │
│   (Compiles HLSL into embedded resource YourEffect.ps via D3DCompiler) │
│                                                                        │
│ Step 3: Register Defaults in MetroShaderEffect.cs                      │
│   Add one line to the TryCreate switch expression:                     │
│   "YourEffect" => new MetroShaderEffect("YourEffect", DefaultParams),  │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Architectural Rules

1. **Shaders Are Data, Not C# Classes:**  
   Never create individual `*Effect.cs` subclasses. All shaders run through the universal `MetroShaderEffect.cs` runner.
2. **Encapsulation:**  
   `MetroShaderEffect.cs` encapsulates GPU bytecode loading, hardware registers, and any required input brush (e.g. procedural noise).
3. **No External JSON Files:**  
   Shader defaults stay in a compile-time C# table. This prevents file I/O latency, JSON parsing errors, and startup crashes.
4. **Native Display Refresh:**  
   `BackdropShaderDriver.cs` synchronizes render ticks directly to the display refresh via `CompositionTarget.Rendering` for maximum fluid motion on high-refresh displays.
5. **Zero Background Overhead:**  
   When the app is minimized or hidden, the render loop unhooks immediately. Background CPU and GPU load remains at 0%.
6. **Bounded Modulo Time:**  
   Time is bounded modulo (`400 * PI`) to prevent floating-point precision loss when the app runs for hours or days.

