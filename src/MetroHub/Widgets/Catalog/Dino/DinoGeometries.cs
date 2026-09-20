using System;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Dino;

/// <summary>
/// Pre-compiled and frozen vector geometries for Chrome Dino sprites and scenery (Implementation Plan v2).
/// All shapes are frozen on initialization to maximize rendering performance and eliminate per-frame allocations.
/// </summary>
public static class DinoGeometries
{
    public static readonly Geometry DinoStandingIdle;
    public static readonly Geometry DinoRunLeft;
    public static readonly Geometry DinoRunRight;
    public static readonly Geometry DinoDuckingLeft;
    public static readonly Geometry DinoDuckingRight;
    public static readonly Geometry DinoDead;

    public static readonly Geometry SmallCactus;
    public static readonly Geometry LargeCactus;
    public static readonly Geometry DoubleSmallCactus;
    public static readonly Geometry TripleSmallCactus;

    public static readonly Geometry BirdWingUp;
    public static readonly Geometry BirdWingDown;

    public static readonly Geometry Cloud;
    public static readonly Geometry Star;
    public static readonly Geometry Moon;
    public static readonly Geometry RestartIcon;

    static DinoGeometries()
    {
        // 1. Standing Idle Dino (44x46)
        DinoStandingIdle = CreateDinoStanding(legState: 0);
        DinoStandingIdle.Freeze();

        // 2. Running Dino - Leg State 1 (Left leg forward/planted, Right leg back)
        DinoRunLeft = CreateDinoStanding(legState: 1);
        DinoRunLeft.Freeze();

        // 3. Running Dino - Leg State 2 (Right leg forward/planted, Left leg back)
        DinoRunRight = CreateDinoStanding(legState: 2);
        DinoRunRight.Freeze();

        // 4. Dead / Crash Dino (Startled dilated eye)
        DinoDead = CreateDinoStanding(legState: 0, isDead: true);
        DinoDead.Freeze();

        // 5. Ducking Dino (58x26) - Leg State 1
        DinoDuckingLeft = CreateDinoDucking(legState: 1);
        DinoDuckingLeft.Freeze();

        // 6. Ducking Dino (58x26) - Leg State 2
        DinoDuckingRight = CreateDinoDucking(legState: 2);
        DinoDuckingRight.Freeze();

        // 7. Small Cactus (17x34)
        SmallCactus = Geometry.Parse(
            "F1 M 6,0 H 11 V 34 H 6 V 22 H 4 V 16 H 1 V 10 H 4 V 14 H 6 V 0 Z " +
            "M 11,16 H 13 V 12 H 16 V 18 H 13 V 24 H 11 Z");
        SmallCactus.Freeze();

        // 8. Large Cactus (25x50)
        LargeCactus = Geometry.Parse(
            "F1 M 9,0 H 16 V 50 H 9 V 28 H 6 V 20 H 2 V 12 H 6 V 18 H 9 V 0 Z " +
            "M 16,22 H 19 V 16 H 23 V 24 H 19 V 32 H 16 Z");
        LargeCactus.Freeze();

        // 9. Double Small Cactus (34x34)
        var doubleGroup = new GeometryGroup { FillRule = FillRule.Nonzero };
        doubleGroup.Children.Add(SmallCactus);
        var c2 = SmallCactus.Clone();
        c2.Transform = new TranslateTransform(17, 0);
        doubleGroup.Children.Add(c2);
        DoubleSmallCactus = doubleGroup;
        DoubleSmallCactus.Freeze();

        // 10. Triple Small Cactus (51x34)
        var tripleGroup = new GeometryGroup { FillRule = FillRule.Nonzero };
        tripleGroup.Children.Add(SmallCactus);
        var t2 = SmallCactus.Clone();
        t2.Transform = new TranslateTransform(17, 0);
        tripleGroup.Children.Add(t2);
        var t3 = SmallCactus.Clone();
        t3.Transform = new TranslateTransform(34, 0);
        tripleGroup.Children.Add(t3);
        TripleSmallCactus = tripleGroup;
        TripleSmallCactus.Freeze();

        // 11. Pterodactyl Wing UP (44x28)
        BirdWingUp = Geometry.Parse(
            "F1 M 30,11 H 42 V 13 H 44 V 15 H 34 V 17 H 30 V 19 H 14 V 17 H 8 V 14 H 14 V 11 Z " +
            "M 28,11 V 2 H 24 V 0 H 18 V 2 H 14 V 5 H 18 V 11 Z " +
            "M 34,9 H 38 V 11 H 34 Z");
        BirdWingUp.Freeze();

        // 12. Pterodactyl Wing DOWN (44x28)
        BirdWingDown = Geometry.Parse(
            "F1 M 30,11 H 42 V 13 H 44 V 15 H 34 V 17 H 30 V 19 H 14 V 17 H 8 V 14 H 14 V 11 Z " +
            "M 28,17 V 26 H 24 V 28 H 18 V 26 H 14 V 23 H 18 V 17 Z " +
            "M 34,9 H 38 V 11 H 34 Z");
        BirdWingDown.Freeze();

        // 13. Cloud (46x14)
        Cloud = Geometry.Parse(
            "F1 M 16,0 H 30 V 4 H 38 V 7 H 46 V 13 H 0 V 9 H 4 V 5 H 10 V 2 H 16 Z");
        Cloud.Freeze();

        // 14. Star (7x7)
        Star = Geometry.Parse(
            "F1 M 3,0 H 4 V 7 H 3 Z M 0,3 H 7 V 4 H 0 Z");
        Star.Freeze();

        // 15. Moon (20x20)
        Moon = Geometry.Parse(
            "F1 M 10,0 A 10,10 0 1 0 20,10 A 8,8 0 1 1 10,0 Z");
        Moon.Freeze();

        // 16. Restart Icon (36x32)
        RestartIcon = Geometry.Parse(
            "F1 M 18,6 A 12,12 0 1 1 8,22 L 4,24 A 16,16 0 1 0 18,2 V -2 L 26,5 L 18,12 Z");
        RestartIcon.Freeze();
    }

    private static Geometry CreateDinoStanding(int legState, bool isDead = false)
    {
        // Body path (44x46)
        // Main torso, head, snout, tail, arms
        string bodyPath =
            "M 22,0 H 44 V 12 H 34 V 14 H 42 V 17 H 27 V 24 " +
            "H 33 V 28 H 30 V 26 H 27 V 34 H 22 V 36 H 15 V 34 " +
            "H 12 V 30 H 4 V 26 H 0 V 20 H 4 V 22 H 8 V 24 H 12 V 26 H 15 V 20 H 22 Z ";

        // Eye cutout: at (26, 3), 3x3 square (or dead eye)
        string eyePath = isDead
            ? "M 25,2 H 29 V 6 H 25 Z " // Dilated black eye block
            : "M 26,3 H 28 V 5 H 26 Z "; // Standard 2x2 eye

        // Legs:
        string legsPath;
        if (legState == 1) // Left leg planted down, Right leg raised
        {
            legsPath =
                "M 15,36 H 19 V 44 H 23 V 46 H 15 Z " + // Left leg down
                "M 23,36 H 27 V 41 H 29 V 43 H 23 Z "; // Right leg bent up
        }
        else if (legState == 2) // Right leg planted down, Left leg raised
        {
            legsPath =
                "M 15,36 H 19 V 41 H 17 V 43 H 13 Z " + // Left leg bent back
                "M 23,36 H 27 V 44 H 31 V 46 H 23 Z "; // Right leg down
        }
        else // Idle: both legs down
        {
            legsPath =
                "M 15,36 H 19 V 44 H 23 V 46 H 15 Z " +
                "M 23,36 H 27 V 44 H 31 V 46 H 23 Z ";
        }

        // Use EvenOdd fill rule so eye cutout works automatically
        var geom = Geometry.Parse("F0 " + bodyPath + eyePath + legsPath);
        return geom;
    }

    private static Geometry CreateDinoDucking(int legState)
    {
        // Ducking Dino (58x26)
        string bodyPath =
            "M 36,0 H 58 V 10 H 48 V 12 H 56 V 15 H 44 V 18 H 38 V 20 " +
            "H 15 V 18 H 8 V 14 H 0 V 8 H 8 V 10 H 15 V 12 H 22 V 14 H 36 Z " +
            "M 40,2 H 42 V 4 H 40 Z "; // Eye cutout at (40, 2)

        string legsPath;
        if (legState == 1)
        {
            legsPath =
                "M 16,20 H 20 V 24 H 24 V 26 H 16 Z " +
                "M 28,20 H 32 V 23 H 34 V 25 H 28 Z ";
        }
        else
        {
            legsPath =
                "M 16,20 H 20 V 23 H 18 V 25 H 14 Z " +
                "M 28,20 H 32 V 24 H 36 V 26 H 28 Z ";
        }

        var geom = Geometry.Parse("F0 " + bodyPath + legsPath);
        return geom;
    }
}
