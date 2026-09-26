using System;
using System.Security.Cryptography;
using System.Windows.Threading;

namespace MetroHub.Widgets.Catalog.Rover;

/// <summary>
/// High-efficiency, zero-garbage animation driver for Rover.
/// Executes discrete frame steps at Rover's authentic 100 ms cadence using a dynamic DispatcherTimer.
/// Crucial: When Rover is at rest or sleeping, the timer is strictly STOPPED, yielding 0.000% CPU.
/// </summary>
public sealed class RoverAnimationEngine
{
    private readonly DispatcherTimer _timer;
    private RoverAnimation? _currentAnimation;
    private int _currentFrameIndex;
    private RoverFrame? _currentFrame;
    private bool _loop;
    private bool _exiting;
    private Action? _onCompletedCallback;

    public event Action<int, int>? FrameChanged;
    public event Action<string>? SoundTriggered;
    public event Action<string>? AnimationCompleted;

    public bool IsRunning => _timer.IsEnabled;
    public string CurrentAnimationName { get; private set; } = "RestPose";
    public int CurrentFrameIndex => _currentFrameIndex;

    public RoverAnimationEngine()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// Starts playing a named animation sequence.
    /// </summary>
    public bool Play(string animationName, bool loop = false, Action? onComplete = null)
    {
        var animation = RoverManifest.GetAnimation(animationName);
        if (animation == null || animation.Frames.Length == 0)
        {
            return false;
        }

        _timer.Stop();
        _currentAnimation = animation;
        CurrentAnimationName = animationName;
        _currentFrameIndex = 0;
        _currentFrame = animation.Frames[0];
        _loop = loop;
        _exiting = false;
        _onCompletedCallback = onComplete;

        RenderCurrentFrame();

        if (animation.Frames.Length > 1 || loop)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(_currentFrame.DurationMs);
            _timer.Start();
        }
        else
        {
            // Single-frame static pose: Keep timer stopped for 0% CPU
            _onCompletedCallback?.Invoke();
            _onCompletedCallback = null;
        }

        return true;
    }

    /// <summary>
    /// Gracefully exits the current animation by following Rover's exitBranch if available.
    /// </summary>
    public void ExitGracefully()
    {
        _exiting = true;
    }

    /// <summary>
    /// Immediately halts the animation timer, putting the engine into complete CPU quiescence.
    /// </summary>
    public void Stop()
    {
        _timer.Stop();
    }

    /// <summary>
    /// Displays a single static frame (e.g. "RestPose") with the timer stopped.
    /// </summary>
    public void SetStaticPose(string animationName = "RestPose", int frameIndex = 0)
    {
        _timer.Stop();
        var animation = RoverManifest.GetAnimation(animationName);
        if (animation != null && animation.Frames.Length > 0)
        {
            _currentAnimation = animation;
            CurrentAnimationName = animationName;
            _currentFrameIndex = Math.Clamp(frameIndex, 0, animation.Frames.Length - 1);
            _currentFrame = animation.Frames[_currentFrameIndex];
            RenderCurrentFrame();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_currentAnimation == null || _currentFrame == null)
        {
            _timer.Stop();
            return;
        }

        int nextIndex = CalculateNextFrameIndex();

        // Terminal frame reached
        if (nextIndex >= _currentAnimation.Frames.Length)
        {
            if (_loop)
            {
                nextIndex = 0;
            }
            else
            {
                _timer.Stop();
                string completedName = CurrentAnimationName;
                var callback = _onCompletedCallback;
                _onCompletedCallback = null;

                callback?.Invoke();
                AnimationCompleted?.Invoke(completedName);
                return;
            }
        }

        _currentFrameIndex = nextIndex;
        _currentFrame = _currentAnimation.Frames[_currentFrameIndex];

        RenderCurrentFrame();

        // Adjust interval dynamically for the next frame
        _timer.Interval = TimeSpan.FromMilliseconds(_currentFrame.DurationMs);
    }

    private int CalculateNextFrameIndex()
    {
        if (_currentAnimation == null || _currentFrame == null) return 0;

        // If exiting and an exitBranch is defined, jump directly to the exit branch
        if (_exiting && _currentFrame.ExitBranch.HasValue)
        {
            _exiting = false;
            return _currentFrame.ExitBranch.Value;
        }

        // Markov weighted branching evaluation
        var branching = _currentFrame.Branching;
        if (branching?.Branches is { Length: > 0 } branches)
        {
            int totalWeight = 0;
            for (int i = 0; i < branches.Length; i++)
            {
                totalWeight += branches[i].Weight;
            }

            int roll = RandomNumberGenerator.GetInt32(0, Math.Max(100, totalWeight));
            for (int i = 0; i < branches.Length; i++)
            {
                if (roll < branches[i].Weight)
                {
                    return branches[i].FrameIndex;
                }
                roll -= branches[i].Weight;
            }
        }

        return _currentFrameIndex + 1;
    }

    private void RenderCurrentFrame()
    {
        if (_currentFrame == null) return;

        FrameChanged?.Invoke(_currentFrame.X, _currentFrame.Y);

        if (!string.IsNullOrEmpty(_currentFrame.Sound))
        {
            SoundTriggered?.Invoke(_currentFrame.Sound);
        }
    }
}
