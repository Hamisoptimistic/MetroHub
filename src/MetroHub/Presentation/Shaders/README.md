# MetroHub Backdrop Shader Pipeline

Direct3D 9 (Pixel Shader 3.0) pipeline driving MetroHub desktop backdrops and interactive effects.

---

## 1. System Pipeline

```
┌────────────────────────────────────────────────────────────────────────┐
│                        MetroHub Shader Pipeline                        │
├────────────────────────────────────────────────────────────────────────┤
│ 1. INPUT SOURCES                                                       │
│    • Render Clock (Stopwatch) ──► c0.x (Modulo Time)                   │
│    • Display / Target Bounds  ──► c0.y (Aspect Ratio: W / H)           │
│    • System Clock (DateTime)  ──► c0.z (TimeOfDaySeconds: 0..86400)   │
│    • Window Mouse Input       ──► c2   (MouseState: X, Y, Click, Yaw)  │
│                                                                        │
│                                │                                       │
│                                ▼                                       │
│ 2. DRIVER: BackdropShaderDriver.cs                                     │
│    • Synchronized to display refresh via CompositionTarget.Rendering   │
│    • Stops rendering completely (0% CPU/GPU) when hidden or minimized  │
│    • Smooths mouse movement with liquid inertia (0.035 lerp)           │
│    • Cycles palette themes on desktop click (CyclePalette)             │
│                                │                                       │
│                                ▼                                       │
│ 3. HOST: MetroShaderEffect.cs                                          │
│    • Universal WPF ShaderEffect container                              │
│    • Loads embedded bytecode (.ps) from assembly resources             │
│    • Caches frozen PixelShader instances                               │
│    • Binds registers (s0..s3, c0..c3, and c4..c27 for drop arrays)     │
│    • Single switch factory (TryCreate) with default parameters         │
│                                │                                       │
│                                ▼                                       │
│ 4. GPU EXECUTION (Direct3D 9Ex ps_3_0)                                 │
│    • Compiles HLSL to Bytecode/*.ps via tools/compile_shaders.ps1       │
│    • Embedded in MetroHub.dll automatically by MetroHub.csproj         │
│    • Executes directly on graphics hardware                            │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Pipeline Capabilities

What you can build with this pipeline:

- **Procedural Backdrops**: Math-driven generative visuals running entirely on the GPU with zero image RAM (`Aurora`, `Plasma`, `Starfield`, `CyberTunnel`, `Ether`, `Circuit`, `FluidWaves`, `SmokeFog`).
- **Interactive Mouse Effects**: Liquid cursor trails, water disturbances, and steering (`Ink`, `KoiPond`, `Pebbles`, `Kaliset`). Smooth cursor coordinates arrive through register `c2`.
- **24-Hour Diurnal Lighting**: Sunlight and sky colors synced to local wall-clock time (`Meadow`, `Planet`, `NeonClock`) via `c0.z` (seconds since midnight).
- **Multi-Texture 3D Shaders**: Spherical projections with diffuse, night city lights, clouds, and specular maps (`Planet`) using samplers `s0..s3` and mouse drag yaw (`c2.w`).
- **One-Click Theme Cycling**: Instant palette swaps on background click (`WaterCaustics`, `NeonClock`) via `#include "ShaderPalettes.hlsl"`.
- **Wallpaper Post-Processing**: Refracting or distorting desktop wallpaper pixels (`WaterRipple`) by sampling `s0`.

---

## 3. Direct3D 9 Register Standard

Standard registers bound by `BackdropShaderDriver` and `MetroShaderEffect`:

| Register | HLSL Variable | Type | Description |
|---|---|---|---|
| `s0` | `InputSampler` | `sampler2D` | Target background texture (wallpaper image or dummy brush; required by WPF) |
| `c0` | `FrameState` | `float4` | `X` = Modulo Time (`400 * PI`), `Y` = Aspect Ratio (`W/H`), `Z` = `TimeOfDaySeconds` (`0..86400`), `W` = Unused |
| `c1` | `EffectParams` | `float4` | Effect constants (e.g. speed, scale, intensity, palette index) |
| `c2` | `MouseState` | `float4` | `X` = Smoothed Mouse X (`0..1`), `Y` = Smoothed Mouse Y (`0..1`), `Z` = Left Click (`1` or `0`), `W` = Drag Yaw |

### Extended Registers (Specialized Shaders)

| Register | HLSL Variable | Type | Used By | Description |
|---|---|---|---|---|
| `s1..s3` | `NightSampler`, `CloudsSampler`, `SpecularSampler` | `sampler2D` | `Planet` | Additional texture layers bound in `MetroShaderEffect` |
| `c3` | `AtmosphereColor` | `float4` | `Planet` | Atmosphere tint (`RGB`) and sun intensity (`W`) |
| `c4..c27`| `Drops[24]` | `float4[24]` | `Ink` | 24 procedural drop coordinates: `(X, Y, Age, Alive)` |

---

## 4. Shared Palettes and Helper Files

MetroHub provides 6 shared luxury pastel palettes in `ShaderPalettes.hlsl`:
- `0`: Nordic Glacier (Ice Blue / Navy)
- `1`: Lagoon Mint (Seafoam / Slate)
- `2`: Dusk Lilac (Lavender / Plum)
- `3`: Sunset Peach (Apricot / Mocha)
- `4`: Blush Rose (Rose Quartz / Charcoal)
- `5`: Pearl Champagne (Ivory / Warm Obsidian)

Include palettes in any shader with:
```hlsl
#include "ShaderPalettes.hlsl"
```

> **Compiler Rule:** `tools/compile_shaders.ps1` compiles all `*.hlsl` files in this directory except files matching `*Palette*` or `*.inc.hlsl`. Always name helper or include files with `*.inc.hlsl` or include `Palette` in the filename so the compiler does not treat them as standalone shaders.

---

## 5. Adding a New Effect in 3 Steps

Never create a separate C# `ShaderEffect` subclass. All shaders run through the universal `MetroShaderEffect` container.

```
┌────────────────────────────────────────────────────────────────────────┐
│                        3-Step Developer Recipe                         │
├────────────────────────────────────────────────────────────────────────┤
│ Step 1: Write the HLSL Shader                                          │
│   Create src/MetroHub/Presentation/Shaders/YourEffect.hlsl             │
│   Declare registers s0, c0, and c1 (and c2 if interactive).            │
│                                                                        │
│ Step 2: Compile Bytecode                                               │
│   Run tools/compile_shaders.ps1 in PowerShell.                         │
│   Compiles into Bytecode/YourEffect.ps.                                │
│   MetroHub.csproj automatically embeds all Bytecode/*.ps files.        │
│                                                                        │
│ Step 3: Register Defaults in MetroShaderEffect.cs                      │
│   Add one case to TryCreate:                                           │
│   "YourEffect" => new MetroShaderEffect("YourEffect", DefaultParams),  │
└────────────────────────────────────────────────────────────────────────┘
```

---

## 6. Runtime Invariants

1. **Shaders Are Data, Not C# Classes:**  
   `MetroShaderEffect.cs` handles bytecode loading, register binding, and caching. New effects only require an HLSL file, compiled `.ps` bytecode, and one line in `TryCreate`.
2. **Zero Idle Overhead:**  
   When MetroHub is hidden, minimized, or obscured, `BackdropShaderDriver.cs` detaches from `CompositionTarget.Rendering`. Background CPU and GPU load remains at 0%.
3. **Hardware Acceleration Guard:**  
   Shaders only run if hardware Tier 2 is available (`(RenderCapability.Tier >> 16) >= 2`). The system falls back cleanly on virtualized or legacy displays.
4. **Bounded Modulo Time:**  
   Time in `c0.x` wraps modulo `400 * PI`. This prevents 32-bit floating point precision loss during long application uptime.
5. **Subsystem Containment:**  
   Shader logic stays strictly inside `Presentation/Shaders`. Shell controls, tile rendering, and audio services do not bleed into the shader pipeline.
