using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using System.Numerics;
using HlaeObsTools.Services.Campaths;

namespace HlaeObsTools.ViewModels;

public sealed class CampathEditorViewModel : ViewModelBase
{
    private readonly CampathCurve _curve = new();
    private double _playheadTime;
    private double _duration = 20.0;
    private bool _useCubic = true;
    private CampathKeyframeViewModel? _selectedKeyframe;
    private bool _suppressCollectionEvents;
    private bool _isPlaying;
    private bool _isPreviewEnabled = true;
    private double _playbackRate = 1.0;
    private readonly DispatcherTimer _playTimer;
    private DateTime _lastPlayTick;
    private bool _useExternalPlaybackTicks;
    private bool _hold = true;
    private double _timeOffset;

    public CampathEditorViewModel()
    {
        Keyframes.CollectionChanged += OnKeyframesChanged;
        _playTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _playTimer.Tick += OnPlayTick;
        TogglePlayCommand = new RelayCommand(_ => TogglePlay());
        ClearCommand = new RelayCommand(_ => Clear());
    }

    public ObservableCollection<CampathKeyframeViewModel> Keyframes { get; } = new();

    public CampathCurve Curve => _curve;

    public double PlayheadTime
    {
        get => _playheadTime;
        set
        {
            if (SetProperty(ref _playheadTime, value))
            {
                if (ClampPlayhead())
                    OnPropertyChanged();
                OnPropertyChanged(nameof(PlayheadSample));
            }
        }
    }

    public double Duration
    {
        get => _duration;
        set
        {
            if (value <= 0)
                value = 0.01;
            if (SetProperty(ref _duration, value))
            {
                if (ClampPlayhead())
                    OnPropertyChanged(nameof(PlayheadTime));
            }
        }
    }

    public bool UseCubic
    {
        get => _useCubic;
        set
        {
            if (SetProperty(ref _useCubic, value))
            {
                _curve.PositionInterp = value ? CampathDoubleInterp.Cubic : CampathDoubleInterp.Linear;
                _curve.RotationInterp = value ? CampathQuaternionInterp.SCubic : CampathQuaternionInterp.SLinear;
                _curve.FovInterp = value ? CampathDoubleInterp.Cubic : CampathDoubleInterp.Linear;
                RebuildCurve();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public bool IsPreviewEnabled
    {
        get => _isPreviewEnabled;
        set => SetProperty(ref _isPreviewEnabled, value);
    }

    public double PlaybackRate
    {
        get => _playbackRate;
        set
        {
            if (value <= 0.0)
                value = 0.01;
            SetProperty(ref _playbackRate, value);
        }
    }

    public bool UseExternalPlaybackTicks
    {
        get => _useExternalPlaybackTicks;
        set => SetProperty(ref _useExternalPlaybackTicks, value);
    }

    public bool Hold
    {
        get => _hold;
        set => SetProperty(ref _hold, value);
    }

    public double TimeOffset
    {
        get => _timeOffset;
        set => SetProperty(ref _timeOffset, value);
    }

    public CampathKeyframeViewModel? SelectedKeyframe
    {
        get => _selectedKeyframe;
        set
        {
            if (SetProperty(ref _selectedKeyframe, value))
            {
                foreach (var key in Keyframes)
                    key.Selected = key == _selectedKeyframe;
            }
        }
    }

    public CampathSample? PlayheadSample
    {
        get
        {
            if (!_curve.CanEvaluate())
                return null;
            return _curve.Evaluate(PlayheadTime);
        }
    }

    public ICommand TogglePlayCommand { get; }
    public ICommand ClearCommand { get; }

    public void AddKeyframe(double time, Vector3 position, Quaternion rotation, double fov)
    {
        const double timeEpsilon = 0.0001;
        var existing = Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) <= timeEpsilon);
        if (existing != null)
        {
            existing.Position = position;
            existing.Rotation = rotation;
            existing.Fov = fov;
            SelectedKeyframe = existing;
            RebuildCurve();
            return;
        }

        var vm = new CampathKeyframeViewModel
        {
            Time = time,
            Position = position,
            Rotation = rotation,
            Fov = fov
        };
        InsertKeyframeSorted(vm);
        SelectedKeyframe = vm;
    }

    public void RemoveSelectedKeyframe()
    {
        if (SelectedKeyframe == null)
            return;
        Keyframes.Remove(SelectedKeyframe);
        SelectedKeyframe = Keyframes.FirstOrDefault();
    }

    public void Clear()
    {
        Keyframes.Clear();
        SelectedKeyframe = null;
        RebuildCurve();
    }

    public void LoadFromData(CampathFileIo.CampathFileData data)
    {
        _suppressCollectionEvents = true;
        Keyframes.Clear();
        foreach (var key in data.Keyframes.OrderBy(k => k.Time))
        {
            Keyframes.Add(new CampathKeyframeViewModel
            {
                Time = key.Time,
                Position = key.Position,
                Rotation = key.Rotation,
                Fov = key.Fov,
                Selected = key.Selected
            });
        }
        _suppressCollectionEvents = false;

        UseCubic = data.UseCubic;
        Hold = data.Hold;
        TimeOffset = data.TimeOffset;

        SelectedKeyframe = Keyframes.FirstOrDefault(k => k.Selected) ?? Keyframes.FirstOrDefault();
        Duration = GetKeyframeDuration();
        PlayheadTime = SelectedKeyframe?.Time ?? 0.0;
        RebuildCurve();
    }

    public void ShiftAllTimes(double delta)
    {
        if (Keyframes.Count == 0)
            return;

        _suppressCollectionEvents = true;
        foreach (var key in Keyframes)
            key.Time += delta;
        _suppressCollectionEvents = false;
        SortByTimeDeferred();
        RebuildCurve();
    }

    public void SetDuration(double newDuration)
    {
        if (newDuration <= 0)
            newDuration = 0.01;

        var currentDuration = GetKeyframeDuration();
        if (currentDuration <= 0.0)
        {
            Duration = newDuration;
            return;
        }

        var scale = newDuration / currentDuration;
        _suppressCollectionEvents = true;
        foreach (var key in Keyframes)
            key.Time *= scale;
        _suppressCollectionEvents = false;

        Duration = newDuration;
        SortByTimeDeferred();
        RebuildCurve();
    }

    public void SnapPlayheadToKeyframe()
    {
        if (SelectedKeyframe == null)
            return;
        PlayheadTime = SelectedKeyframe.Time;
    }

    public double GetKeyframeDuration()
    {
        if (Keyframes.Count == 0)
            return 0.0;
        var min = Keyframes.Min(k => k.Time);
        var max = Keyframes.Max(k => k.Time);
        return Math.Max(0.0, max - min);
    }

    public void StopPlayback()
    {
        if (!IsPlaying)
            return;

        _playTimer.Stop();
        IsPlaying = false;
    }

    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        if (Duration <= 0.0)
            return;

        if (PlayheadTime >= Duration)
            PlayheadTime = 0.0;

        _lastPlayTick = DateTime.UtcNow;
        IsPlaying = true;
        if (!UseExternalPlaybackTicks)
            _playTimer.Start();
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        AdvancePlaybackInternal((DateTime.UtcNow - _lastPlayTick).TotalSeconds, updateTimestamp: true);
    }

    public void AdvancePlayback(double deltaSeconds)
    {
        AdvancePlaybackInternal(deltaSeconds, updateTimestamp: false);
    }

    private void AdvancePlaybackInternal(double deltaSeconds, bool updateTimestamp)
    {
        if (!IsPlaying)
            return;

        if (deltaSeconds <= 0.0)
            return;

        if (updateTimestamp)
            _lastPlayTick = DateTime.UtcNow;

        PlayheadTime += deltaSeconds * PlaybackRate;

        if (PlayheadTime >= Duration)
        {
            PlayheadTime = Duration;
            StopPlayback();
        }
    }

    private void OnKeyframesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressCollectionEvents)
            return;

        if (e.OldItems != null)
        {
            foreach (CampathKeyframeViewModel key in e.OldItems)
                key.PropertyChanged -= OnKeyframePropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (CampathKeyframeViewModel key in e.NewItems)
                key.PropertyChanged += OnKeyframePropertyChanged;
        }

        RebuildCurve();
    }

    private void OnKeyframePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressCollectionEvents)
            return;

        if (e.PropertyName == nameof(CampathKeyframeViewModel.Time))
            Dispatcher.UIThread.Post(SortByTimeDeferred);

        RebuildCurve();
    }

    private void SortByTimeDeferred()
    {
        if (_suppressCollectionEvents)
            return;

        if (Keyframes.Count < 2)
            return;

        _suppressCollectionEvents = true;
        var ordered = Keyframes.OrderBy(k => k.Time).ToList();
        Keyframes.Clear();
        foreach (var key in ordered)
            Keyframes.Add(key);
        _suppressCollectionEvents = false;
    }

    private void InsertKeyframeSorted(CampathKeyframeViewModel vm)
    {
        if (Keyframes.Count == 0)
        {
            Keyframes.Add(vm);
            return;
        }

        var index = 0;
        while (index < Keyframes.Count && Keyframes[index].Time <= vm.Time)
            index++;

        Keyframes.Insert(index, vm);
    }

    private void RebuildCurve()
    {
        _curve.SetKeyframes(Keyframes.Select(k => k.ToModel()));
        OnPropertyChanged(nameof(PlayheadSample));
    }

    private bool ClampPlayhead()
    {
        var changed = false;
        if (_playheadTime < 0)
        {
            _playheadTime = 0;
            changed = true;
        }
        if (_playheadTime > Duration)
        {
            _playheadTime = Duration;
            changed = true;
        }
        return changed;
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class CampathKeyframeViewModel : ViewModelBase
{
    private double _time;
    private Vector3 _position;
    private Quaternion _rotation = Quaternion.Identity;
    private double _fov = 90.0;
    private bool _selected;

    public double Time
    {
        get => _time;
        set => SetProperty(ref _time, value);
    }

    public Vector3 Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    public Quaternion Rotation
    {
        get => _rotation;
        set => SetProperty(ref _rotation, value);
    }

    public double Fov
    {
        get => _fov;
        set => SetProperty(ref _fov, value);
    }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public CampathKeyframe ToModel()
    {
        return new CampathKeyframe
        {
            Time = Time,
            Position = Position,
            Rotation = Rotation,
            Fov = Fov,
            Selected = Selected
        };
    }
}
