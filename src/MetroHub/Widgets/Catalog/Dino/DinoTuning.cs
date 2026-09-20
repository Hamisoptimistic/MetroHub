using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Dino;

/// <summary>
/// Centralized tuning constants for the Chrome Dino Game widget (Implementation Plan v2).
/// Isolates all physics, layout, spawner parameters, timing, and palettes so they can be
/// adjusted after play-testing without altering core game loop logic.
/// </summary>
public static class DinoTuning
{
    // --- Layout & Canvas Dimensions ---
    public const double CanvasWidth = 504.0;
    public const double CanvasHeight = 184.0;
    public const double GroundY = 145.0; // Feet of Dino and bottom of cacti rest here

    // --- Dino Geometry & Hitboxes ---
    public const double DinoX = 40.0;
    
    // Standing Dino: Sprite 44x46, Hitbox 36x40 (inset 4px left/right, 6px top, flush bottom)
    public const double DinoStandingWidth = 44.0;
    public const double DinoStandingHeight = 46.0;
    public const double DinoStandingHitboxWidth = 36.0;
    public const double DinoStandingHitboxHeight = 40.0;
    public const double DinoStandingHitboxOffsetX = 4.0;
    public const double DinoStandingHitboxOffsetY = 6.0;

    // Ducking Dino: Sprite 58x26, Hitbox 50x20 (anchored at X=40, extends forward)
    public const double DinoDuckingWidth = 58.0;
    public const double DinoDuckingHeight = 26.0;
    public const double DinoDuckingHitboxWidth = 50.0;
    public const double DinoDuckingHitboxHeight = 20.0;
    public const double DinoDuckingHitboxOffsetX = 4.0;
    public const double DinoDuckingHitboxOffsetY = 6.0;

    // --- Obstacle Hitboxes (Sprite inset 1px left/right/top) ---
    // Small Cactus: Sprite 17x34, Hitbox 15x32
    public const double SmallCactusWidth = 17.0;
    public const double SmallCactusHeight = 34.0;
    public const double SmallCactusHitboxWidth = 15.0;
    public const double SmallCactusHitboxHeight = 32.0;

    // Large Cactus: Sprite 25x50, Hitbox 23x48 (single only)
    public const double LargeCactusWidth = 25.0;
    public const double LargeCactusHeight = 50.0;
    public const double LargeCactusHitboxWidth = 23.0;
    public const double LargeCactusHitboxHeight = 48.0;

    // Double Small Cactus: Sprite 34x34, Hitbox 32x32
    public const double DoubleSmallCactusWidth = 34.0;
    public const double DoubleSmallCactusHeight = 34.0;
    public const double DoubleSmallCactusHitboxWidth = 32.0;
    public const double DoubleSmallCactusHitboxHeight = 32.0;

    // Triple Small Cactus: Sprite 51x34, Hitbox 49x32
    public const double TripleSmallCactusWidth = 51.0;
    public const double TripleSmallCactusHeight = 34.0;
    public const double TripleSmallCactusHitboxWidth = 49.0;
    public const double TripleSmallCactusHitboxHeight = 32.0;

    // Pterodactyl: Sprite 44x28, Hitbox 42x14 (centred vertically)
    public const double BirdWidth = 44.0;
    public const double BirdHeight = 28.0;
    public const double BirdHitboxWidth = 42.0;
    public const double BirdHitboxHeight = 14.0;

    // --- Player Physics ---
    public const double Gravity = 2000.0;       // px/s²
    public const double JumpVelocity = 560.0;    // px/s (upward v0)
    public const double JumpApexHeight = 78.4;   // px above ground (v0² / 2g)
    public const double AirTime = 0.56;          // seconds (2 * v0 / g)
    public const double JumpBufferSeconds = 0.080; // 80ms jump buffer before landing
    public const double LegSwapInterval = 0.083;   // 83ms run-cycle leg swap (~12 Hz)
    public const double BirdFlapInterval = 0.160;  // 160ms wing flap toggle (~6 Hz)

    // --- Difficulty & Progression ---
    public const double ScoreRate = 0.02;             // points per px travelled
    public const double InitialSpeed = 380.0;         // px/s (snappy, brisk start)
    public const double SpeedScoreFactor = 0.70;      // speed += score * 0.70 (fast progression)
    public const double MaxSpeed = 820.0;             // high-adrenaline terminal speed cap
    public const double BirdSpeedMultiplier = 1.22;   // birds fly 22% faster than ground hazards

    // --- Spawner Spacing (Edge-to-Edge) ---
    public const double MinGapBase = 140.0;           // px
    public const double MinGapSpeedFactor = 0.38;     // minGap = 140 + 0.38 * vObj
    public const int RandomGapTightMin = 15;          // px (challenging rapid double combo)
    public const int RandomGapTightMax = 50;          // px
    public const int RandomGapMediumMin = 65;         // px (focused tempo)
    public const int RandomGapMediumMax = 150;        // px
    public const int RandomGapWideMin = 180;          // px (brief breather)
    public const int RandomGapWideMax = 300;          // px
    public const double BirdClosureGapBonus = 56.0;   // extra gap when bird follows ground obstacle

    // --- Score Tiers & Mix Thresholds ---
    public const int BirdScoreThreshold = 500;
    public const int Tier1MaxScore = 150;
    public const int Tier2MaxScore = 500;

    // --- Pterodactyl Altitudes (Height 'h' above ground line Y=145) ---
    // Low: h = 8..22, Hitbox Top Y = 123, Sprite Top Y = 116 (Jump-only)
    public const double BirdLowHitboxTopY = 123.0;
    public const double BirdLowSpriteTopY = 116.0;

    // Mid: h = 30..44, Hitbox Top Y = 101, Sprite Top Y = 94 (Duck or Jump)
    public const double BirdMidHitboxTopY = 101.0;
    public const double BirdMidSpriteTopY = 94.0;

    // High: h = 60..74, Hitbox Top Y = 71, Sprite Top Y = 64 (Stand or Duck, Jump hits)
    public const double BirdHighHitboxTopY = 71.0;
    public const double BirdHighSpriteTopY = 64.0;

    // --- Day / Night Cycle ---
    public const int NightTriggerInterval = 700;    // Every 700 points (700, 1400, 2100...)
    public const int NightDurationPoints = 250;     // Lasts 250 points (~18-21 sec)
    public const double NightFadeSpeed = 1.0 / 1.2;  // 1.2s crossfade

    // Day Palette Colors
    public static readonly Color DayBackgroundColor = Color.FromRgb(0xF7, 0xF7, 0xF7);
    public static readonly Color DayForegroundColor = Color.FromRgb(0x53, 0x53, 0x53);
    public static readonly Color DayCloudColor = Color.FromRgb(0xC8, 0xC8, 0xC8);

    // Night Palette Colors
    public static readonly Color NightBackgroundColor = Color.FromRgb(0x20, 0x21, 0x24);
    public static readonly Color NightForegroundColor = Color.FromRgb(0xE8, 0xEA, 0xED);
    public static readonly Color NightMoonColor = Color.FromRgb(0xF1, 0xF3, 0xF4);

    // --- Timings & Locks ---
    public const double DeathShakeDuration = 0.300; // 300ms damped canvas shake
    public const double RestartLockoutSeconds = 0.400; // 400ms lockout before Space restarts game
}
