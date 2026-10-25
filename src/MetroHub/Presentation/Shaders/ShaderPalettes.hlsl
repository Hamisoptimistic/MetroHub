// =============================================================================
// ShaderPalettes.hlsl - MetroHub Shared Luxury Pastel Shader Palettes
// Single source of truth for backdrop shader color palettes across MetroHub.
// GPU-native 3-tier color model:
//   1. Ambient:   Deep atmospheric base tone (eliminates harsh pitch-black void)
//   2. Ribbon:    Soft, elegant pastel midtone (high luminosity, delicate saturation)
//   3. Highlight: Silky luminous crest / pearl glint
// =============================================================================

struct PastelPalette
{
    float3 ambient;
    float3 ribbon;
    float3 highlight;
    float3 tint;
};

PastelPalette GetPastelPalette(int index)
{
    int theme = index % 6;
    if (theme < 0) theme += 6;

    PastelPalette pal;

    if (theme == 0) // 0: Nordic Glacier (Pastel Ice Blue & Arctic Navy)
    {
        pal.ambient   = float3(0.02, 0.04, 0.07);
        pal.ribbon    = float3(0.52, 0.78, 0.95);
        pal.highlight = float3(0.92, 0.97, 1.00);
        pal.tint      = float3(0.55, 0.82, 0.98);
    }
    else if (theme == 1) // 1: Lagoon Mint (Pastel Seafoam & Ocean Slate)
    {
        pal.ambient   = float3(0.015, 0.05, 0.05);
        pal.ribbon    = float3(0.48, 0.88, 0.76);
        pal.highlight = float3(0.88, 1.00, 0.95);
        pal.tint      = float3(0.45, 0.92, 0.78);
    }
    else if (theme == 2) // 2: Dusk Lilac (Pastel Lavender & Midnight Plum)
    {
        pal.ambient   = float3(0.05, 0.03, 0.07);
        pal.ribbon    = float3(0.78, 0.65, 0.95);
        pal.highlight = float3(0.96, 0.92, 1.00);
        pal.tint      = float3(0.80, 0.68, 0.98);
    }
    else if (theme == 3) // 3: Sunset Peach (Pastel Apricot & Dark Mocha)
    {
        pal.ambient   = float3(0.06, 0.03, 0.02);
        pal.ribbon    = float3(0.96, 0.68, 0.55);
        pal.highlight = float3(1.00, 0.94, 0.88);
        pal.tint      = float3(0.98, 0.70, 0.58);
    }
    else if (theme == 4) // 4: Blush Rose (Pastel Rose Quartz & Smoky Charcoal)
    {
        pal.ambient   = float3(0.06, 0.03, 0.04);
        pal.ribbon    = float3(0.95, 0.62, 0.75);
        pal.highlight = float3(1.00, 0.92, 0.96);
        pal.tint      = float3(0.96, 0.65, 0.78);
    }
    else // 5: Pearl Champagne (Pastel Ivory & Warm Obsidian)
    {
        pal.ambient   = float3(0.04, 0.04, 0.04);
        pal.ribbon    = float3(0.92, 0.85, 0.70);
        pal.highlight = float3(1.00, 0.98, 0.92);
        pal.tint      = float3(0.94, 0.88, 0.75);
    }

    return pal;
}

