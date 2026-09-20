using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MetroHub.Widgets.Catalog.Dino;

public enum DinoGameState
{
    Idle,
    Running,
    Dying,
    GameOver,
    Paused
}

public enum ObstacleType
{
    SmallCactus,
    LargeCactus,
    DoubleSmallCactus,
    TripleSmallCactus,
    BirdLow,
    BirdMid,
    BirdHigh
}

public sealed class ObstacleInstance
{
    public ObstacleType Type { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double HitboxX => X + HitboxOffsetX;
    public double HitboxY => Y + HitboxOffsetY;
    public double HitboxWidth { get; set; }
    public double HitboxHeight { get; set; }
    public double HitboxOffsetX { get; set; }
    public double HitboxOffsetY { get; set; }
    public bool IsBird { get; set; }
    public bool IsActive { get; set; }
}

public readonly record struct Aabb(double X, double Y, double Width, double Height)
{
    public bool Intersects(Aabb other)
    {
        return X < other.X + other.Width &&
               X + Width > other.X &&
               Y < other.Y + other.Height &&
               Y + Height > other.Y;
    }
}

/// <summary>
/// Pure C# game engine logic for Chrome Dino (Implementation Plan v2).
/// Contains zero WPF dependency types; takes an injectable Random instance for deterministic testing.
/// </summary>
public sealed class DinoGameEngine
{
    private readonly Random _random;

    public DinoGameState State { get; private set; } = DinoGameState.Idle;

    // Player State
    public double DinoY { get; private set; } = DinoTuning.GroundY; // Y coordinate of feet
    public double DinoHeightAboveGround { get; private set; } = 0.0;
    public double VerticalVelocity { get; private set; } = 0.0;
    public bool IsGrounded => DinoHeightAboveGround <= 0.001;
    public bool IsDucking { get; private set; } = false;
    public bool DownKeyHeld { get; private set; } = false;

    // Input Buffer & Timing
    private double _jumpBufferTimer = 0.0;
    private double _legSwapTimer = 0.0;
    public bool RunLegState { get; private set; } = false; // toggles every 83ms

    private double _birdFlapTimer = 0.0;
    public bool BirdFlapState { get; private set; } = false; // toggles every 160ms

    private double _dyingTimer = 0.0;
    private long _restartAllowedTimestamp = 0;

    // Progression
    public double Score { get; private set; } = 0.0;
    public int DisplayScore => (int)Math.Floor(Score);
    public int HighScore { get; private set; } = 0;
    public double Speed { get; private set; } = DinoTuning.InitialSpeed;
    public double BirdSpeed => Speed * DinoTuning.BirdSpeedMultiplier;

    // Day/Night Cycle
    public double NightBlend { get; private set; } = 0.0; // 0 = Day, 1 = Night

    // Obstacles
    private readonly List<ObstacleInstance> _obstacles = new(8);
    public IReadOnlyList<ObstacleInstance> ActiveObstacles => _obstacles;

    private double _distanceSinceLastSpawn = 0.0;
    private double _nextSpawnGap = 0.0;
    private ObstacleType _nextObstacleType = ObstacleType.SmallCactus;

    // Events for Audio & Milestones
    public event Action? OnJump;
    public event Action? OnMilestone;
    public event Action? OnGameOver;

    public DinoGameEngine(int highScore = 0, Random? random = null)
    {
        HighScore = highScore;
        _random = random ?? new Random();
        ResetToIdle();
    }

    public void UpdateHighScore(int highScore)
    {
        if (highScore > HighScore)
        {
            HighScore = highScore;
        }
    }

    public void ResetHighScore()
    {
        HighScore = 0;
    }

    public void ResetToIdle()
    {
        State = DinoGameState.Idle;
        DinoHeightAboveGround = 0.0;
        DinoY = DinoTuning.GroundY;
        VerticalVelocity = 0.0;
        IsDucking = false;
        DownKeyHeld = false;
        Score = 0.0;
        Speed = DinoTuning.InitialSpeed;
        NightBlend = 0.0;
        _dyingTimer = 0.0;
        _restartAllowedTimestamp = 0;
        _jumpBufferTimer = 0.0;
        _distanceSinceLastSpawn = 0.0;
        _obstacles.Clear();
        RollNextObstacle(null);
        _nextSpawnGap = 240.0 + _random.Next(20, 80);
    }

    public void StartGame()
    {
        ResetToIdle();
        State = DinoGameState.Running;
        TriggerJump();
    }

    public void RestartGame()
    {
        if (State is DinoGameState.GameOver or DinoGameState.Idle)
        {
            StartGame();
        }
    }

    public void HandleJumpInput()
    {
        if (State == DinoGameState.Idle)
        {
            StartGame();
            return;
        }

        if (State == DinoGameState.GameOver)
        {
            if (Stopwatch.GetTimestamp() >= _restartAllowedTimestamp)
            {
                StartGame();
            }
            return;
        }

        if (State == DinoGameState.Running)
        {
            if (IsGrounded)
            {
                TriggerJump();
            }
            else
            {
                // Buffer jump if within window
                _jumpBufferTimer = DinoTuning.JumpBufferSeconds;
            }
        }
    }

    public void SetDownInput(bool isDown)
    {
        DownKeyHeld = isDown;
        if (State == DinoGameState.Running && IsGrounded)
        {
            IsDucking = isDown;
        }
    }

    private void TriggerJump()
    {
        VerticalVelocity = DinoTuning.JumpVelocity;
        IsDucking = false; // Jumping cancels duck
        _jumpBufferTimer = 0.0;
        OnJump?.Invoke();
    }

    public void Pause()
    {
        if (State == DinoGameState.Running)
        {
            State = DinoGameState.Paused;
        }
    }

    public void Resume()
    {
        if (State == DinoGameState.Paused)
        {
            State = DinoGameState.Running;
        }
    }

    public void Update(double dt) => Step(dt);

    public void Step(double dt)
    {
        if (dt <= 0.0) return;

        if (State == DinoGameState.Dying)
        {
            _dyingTimer -= dt;
            if (_dyingTimer <= 0.0)
            {
                State = DinoGameState.GameOver;
                _restartAllowedTimestamp = Stopwatch.GetTimestamp() + (long)(DinoTuning.RestartLockoutSeconds * Stopwatch.Frequency);
            }
            return;
        }

        if (State != DinoGameState.Running) return;

        // 1. Jump Physics & Euler Integration
        if (!IsGrounded || VerticalVelocity > 0.0)
        {
            VerticalVelocity -= DinoTuning.Gravity * dt;
            DinoHeightAboveGround += VerticalVelocity * dt;

            if (DinoHeightAboveGround <= 0.0)
            {
                DinoHeightAboveGround = 0.0;
                VerticalVelocity = 0.0;
                // Landed: check buffered jump or resume held duck
                if (_jumpBufferTimer > 0.0)
                {
                    TriggerJump();
                }
                else if (DownKeyHeld)
                {
                    IsDucking = true;
                }
            }
        }
        else
        {
            if (_jumpBufferTimer > 0.0)
            {
                _jumpBufferTimer -= dt;
                if (_jumpBufferTimer > 0.0)
                {
                    TriggerJump();
                }
            }
            IsDucking = DownKeyHeld;
        }

        DinoY = DinoTuning.GroundY - DinoHeightAboveGround;

        // 2. Leg Swap & Bird Flap Animation Timers
        if (IsGrounded)
        {
            _legSwapTimer += dt;
            if (_legSwapTimer >= DinoTuning.LegSwapInterval)
            {
                _legSwapTimer -= DinoTuning.LegSwapInterval;
                RunLegState = !RunLegState;
            }
        }

        _birdFlapTimer += dt;
        if (_birdFlapTimer >= DinoTuning.BirdFlapInterval)
        {
            _birdFlapTimer -= DinoTuning.BirdFlapInterval;
            BirdFlapState = !BirdFlapState;
        }

        // 3. Score & Speed Progression
        int prevMilestone = (int)(Score / 100.0);
        double distanceTravelled = Speed * dt;
        Score += distanceTravelled * DinoTuning.ScoreRate;
        int newMilestone = (int)(Score / 100.0);

        if (newMilestone > prevMilestone)
        {
            OnMilestone?.Invoke();
        }

        Speed = Math.Min(DinoTuning.InitialSpeed + (Score * DinoTuning.SpeedScoreFactor), DinoTuning.MaxSpeed);

        // 4. Day / Night Cycle Progression
        UpdateDayNightCycle(dt);

        // 5. Obstacle Movement & Recycling
        for (int i = _obstacles.Count - 1; i >= 0; i--)
        {
            var obs = _obstacles[i];
            double obsSpeed = obs.IsBird ? BirdSpeed : Speed;
            obs.X -= obsSpeed * dt;

            if (obs.X + obs.Width < -50.0)
            {
                _obstacles.RemoveAt(i);
            }
        }

        // 6. Obstacle Spawner (§3.2)
        _distanceSinceLastSpawn += distanceTravelled;

        if (_distanceSinceLastSpawn >= _nextSpawnGap)
        {
            SpawnObstacle(_nextObstacleType);
            _distanceSinceLastSpawn = 0.0;
            RollNextObstacle(_obstacles[^1]);
        }

        // 7. Collision Detection (AABB)
        CheckCollisions();
    }

    private void UpdateDayNightCycle(double dt)
    {
        // First 0..699 points are always Day
        if (Score < DinoTuning.NightTriggerInterval)
        {
            NightBlend = Math.Max(0.0, NightBlend - (DinoTuning.NightFadeSpeed * dt));
            return;
        }

        // Every 700 points (700, 1400, 2100...), night lasts for 250 points
        int pointsIntoCycle = (int)Score % DinoTuning.NightTriggerInterval;
        bool shouldBeNight = pointsIntoCycle < DinoTuning.NightDurationPoints;

        if (shouldBeNight)
        {
            NightBlend = Math.Min(1.0, NightBlend + (DinoTuning.NightFadeSpeed * dt));
        }
        else
        {
            NightBlend = Math.Max(0.0, NightBlend - (DinoTuning.NightFadeSpeed * dt));
        }
    }

    private void RollNextObstacle(ObstacleInstance? prev)
    {
        // Select next obstacle type based on score tier (§3.3)
        if (Score < DinoTuning.Tier1MaxScore)
        {
            // Early game (0..150): Large (40%), Double (30%), Small (30%)
            int roll = _random.Next(100);
            if (roll < 30) _nextObstacleType = ObstacleType.SmallCactus;
            else if (roll < 60) _nextObstacleType = ObstacleType.DoubleSmallCactus;
            else _nextObstacleType = ObstacleType.LargeCactus;
        }
        else if (Score < DinoTuning.BirdScoreThreshold)
        {
            // Mid game (150..500): Large (30%), Triple (25%), Double (25%), Small (20%)
            int roll = _random.Next(100);
            if (roll < 20) _nextObstacleType = ObstacleType.SmallCactus;
            else if (roll < 45) _nextObstacleType = ObstacleType.DoubleSmallCactus;
            else if (roll < 75) _nextObstacleType = ObstacleType.LargeCactus;
            else _nextObstacleType = ObstacleType.TripleSmallCactus;
        }
        else
        {
            // Late game (500+): Pterodactyls (35%) + Challenging Cacti (65%)
            int roll = _random.Next(100);
            if (roll < 35)
            {
                // Pterodactyl altitude roll (§3.4)
                int birdRoll = _random.Next(100);
                if (birdRoll < 40) _nextObstacleType = ObstacleType.BirdLow;
                else if (birdRoll < 75) _nextObstacleType = ObstacleType.BirdMid;
                else _nextObstacleType = ObstacleType.BirdHigh;
            }
            else if (roll < 60) _nextObstacleType = ObstacleType.LargeCactus;
            else if (roll < 80) _nextObstacleType = ObstacleType.TripleSmallCactus;
            else if (roll < 95) _nextObstacleType = ObstacleType.DoubleSmallCactus;
            else _nextObstacleType = ObstacleType.SmallCactus;
        }

        // Calculate spawn gap (§3.2)
        bool nextIsBird = _nextObstacleType is ObstacleType.BirdLow or ObstacleType.BirdMid or ObstacleType.BirdHigh;
        double vObj = nextIsBird ? BirdSpeed : Speed;
        double minGap = DinoTuning.MinGapBase + (DinoTuning.MinGapSpeedFactor * vObj);

        // Weighted gap distribution for Medium-to-Hard:
        // 45% Tight rhythm (quick 1-2 jump/duck combos)
        // 45% Medium spacing (focused tempo)
        // 10% Wide stretch (brief breather)
        int gapRoll = _random.Next(100);
        double randomGap = gapRoll switch
        {
            < 45 => _random.Next(DinoTuning.RandomGapTightMin, DinoTuning.RandomGapTightMax),
            < 90 => _random.Next(DinoTuning.RandomGapMediumMin, DinoTuning.RandomGapMediumMax),
            _ => _random.Next(DinoTuning.RandomGapWideMin, DinoTuning.RandomGapWideMax)
        };

        double prevWidth = prev?.Width ?? 0.0;
        double spawnGap = prevWidth + minGap + randomGap;

        if (nextIsBird && prev != null && !prev.IsBird)
        {
            spawnGap += DinoTuning.BirdClosureGapBonus;
        }

        _nextSpawnGap = spawnGap;
    }

    private void SpawnObstacle(ObstacleType type)
    {
        var obs = new ObstacleInstance
        {
            Type = type,
            X = DinoTuning.CanvasWidth,
            IsActive = true
        };

        switch (type)
        {
            case ObstacleType.SmallCactus:
                obs.Width = DinoTuning.SmallCactusWidth;
                obs.Height = DinoTuning.SmallCactusHeight;
                obs.Y = DinoTuning.GroundY - obs.Height;
                obs.HitboxWidth = DinoTuning.SmallCactusHitboxWidth;
                obs.HitboxHeight = DinoTuning.SmallCactusHitboxHeight;
                obs.HitboxOffsetX = 1.0;
                obs.HitboxOffsetY = 1.0;
                break;

            case ObstacleType.LargeCactus:
                obs.Width = DinoTuning.LargeCactusWidth;
                obs.Height = DinoTuning.LargeCactusHeight;
                obs.Y = DinoTuning.GroundY - obs.Height;
                obs.HitboxWidth = DinoTuning.LargeCactusHitboxWidth;
                obs.HitboxHeight = DinoTuning.LargeCactusHitboxHeight;
                obs.HitboxOffsetX = 1.0;
                obs.HitboxOffsetY = 1.0;
                break;

            case ObstacleType.DoubleSmallCactus:
                obs.Width = DinoTuning.DoubleSmallCactusWidth;
                obs.Height = DinoTuning.DoubleSmallCactusHeight;
                obs.Y = DinoTuning.GroundY - obs.Height;
                obs.HitboxWidth = DinoTuning.DoubleSmallCactusHitboxWidth;
                obs.HitboxHeight = DinoTuning.DoubleSmallCactusHitboxHeight;
                obs.HitboxOffsetX = 1.0;
                obs.HitboxOffsetY = 1.0;
                break;

            case ObstacleType.TripleSmallCactus:
                obs.Width = DinoTuning.TripleSmallCactusWidth;
                obs.Height = DinoTuning.TripleSmallCactusHeight;
                obs.Y = DinoTuning.GroundY - obs.Height;
                obs.HitboxWidth = DinoTuning.TripleSmallCactusHitboxWidth;
                obs.HitboxHeight = DinoTuning.TripleSmallCactusHitboxHeight;
                obs.HitboxOffsetX = 1.0;
                obs.HitboxOffsetY = 1.0;
                break;

            case ObstacleType.BirdLow:
                obs.IsBird = true;
                obs.Width = DinoTuning.BirdWidth;
                obs.Height = DinoTuning.BirdHeight;
                obs.Y = DinoTuning.BirdLowSpriteTopY;
                obs.HitboxWidth = DinoTuning.BirdHitboxWidth;
                obs.HitboxHeight = DinoTuning.BirdHitboxHeight;
                obs.HitboxOffsetX = 1.0;
                obs.HitboxOffsetY = 7.0;
                break;

            case ObstacleType.BirdMid:
                obs.IsBird = true;
                obs.Width = DinoTuning.BirdWidth;
                obs.Height = DinoTuning.BirdHeight;
                obs.Y = DinoTuning.BirdMidSpriteTopY;
                obs.HitboxWidth = DinoTuning.BirdHitboxWidth;
                obs.HitboxHeight = DinoTuning.BirdHitboxHeight;
                obs.HitboxOffsetX = 1.0;
                obs.HitboxOffsetY = 7.0;
                break;

            case ObstacleType.BirdHigh:
                obs.IsBird = true;
                obs.Width = DinoTuning.BirdWidth;
                obs.Height = DinoTuning.BirdHeight;
                obs.Y = DinoTuning.BirdHighSpriteTopY;
                obs.HitboxWidth = DinoTuning.BirdHitboxWidth;
                obs.HitboxHeight = DinoTuning.BirdHitboxHeight;
                obs.HitboxOffsetX = 1.0;
                obs.HitboxOffsetY = 7.0;
                break;
        }

        _obstacles.Add(obs);
    }

    public Aabb GetDinoHitbox()
    {
        if (IsDucking && IsGrounded)
        {
            return new Aabb(
                DinoTuning.DinoX + DinoTuning.DinoDuckingHitboxOffsetX,
                DinoY - DinoTuning.DinoDuckingHeight + DinoTuning.DinoDuckingHitboxOffsetY,
                DinoTuning.DinoDuckingHitboxWidth,
                DinoTuning.DinoDuckingHitboxHeight);
        }

        return new Aabb(
            DinoTuning.DinoX + DinoTuning.DinoStandingHitboxOffsetX,
            DinoY - DinoTuning.DinoStandingHeight + DinoTuning.DinoStandingHitboxOffsetY,
            DinoTuning.DinoStandingHitboxWidth,
            DinoTuning.DinoStandingHitboxHeight);
    }

    private void CheckCollisions()
    {
        Aabb dinoBox = GetDinoHitbox();

        foreach (var obs in _obstacles)
        {
            var obsBox = new Aabb(obs.HitboxX, obs.HitboxY, obs.HitboxWidth, obs.HitboxHeight);
            if (dinoBox.Intersects(obsBox))
            {
                TriggerDeath();
                break;
            }
        }
    }

    public void TriggerDeath(bool skipShake = false)
    {
        if (State != DinoGameState.Running) return;

        if ((int)Score > HighScore)
        {
            HighScore = (int)Score;
        }

        OnGameOver?.Invoke();

        if (skipShake)
        {
            State = DinoGameState.GameOver;
            _restartAllowedTimestamp = Stopwatch.GetTimestamp() + (long)(DinoTuning.RestartLockoutSeconds * Stopwatch.Frequency);
        }
        else
        {
            State = DinoGameState.Dying;
            _dyingTimer = DinoTuning.DeathShakeDuration;
        }
    }
}
