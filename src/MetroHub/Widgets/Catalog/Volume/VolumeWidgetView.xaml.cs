using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace MetroHub.Widgets.Catalog.Volume;

public partial class VolumeWidgetView : UserControl
{
    private VolumeWidgetViewModel? _vm;
    private Storyboard? _soundwaveStoryboard;
    private bool _isStoryboardActive;

    public VolumeWidgetView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is VolumeWidgetViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = e.NewValue as VolumeWidgetViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateSoundwaveAnimation();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(VolumeWidgetViewModel.IsAudioStreaming) or nameof(VolumeWidgetViewModel.IsMasterMuted))
        {
            UpdateSoundwaveAnimation();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
        UpdateSoundwaveAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopSoundwaveAnimation();
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateSoundwaveAnimation();
    }

    private void UpdateSoundwaveAnimation()
    {
        if (_vm == null)
        {
            StopSoundwaveAnimation();
            return;
        }

        bool shouldAnimate = _vm.IsAudioStreaming && !_vm.IsMasterMuted && IsLoaded && IsVisible;
        if (shouldAnimate)
        {
            StartSoundwaveAnimation();
        }
        else
        {
            StopSoundwaveAnimation();
        }
    }

    private void StartSoundwaveAnimation()
    {
        if (_isStoryboardActive) return;

        _soundwaveStoryboard ??= TryFindResource("SoundwavePulseStoryboard") as Storyboard;
        if (_soundwaveStoryboard != null)
        {
            _soundwaveStoryboard.Begin(this, isControllable: true);
            _isStoryboardActive = true;
        }
    }

    private void StopSoundwaveAnimation()
    {
        if (!_isStoryboardActive) return;

        _soundwaveStoryboard?.Stop(this);
        _isStoryboardActive = false;
    }
}
