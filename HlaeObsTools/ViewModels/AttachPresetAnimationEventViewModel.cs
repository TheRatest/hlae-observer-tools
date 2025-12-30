using System;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

public enum AttachPresetAnimationEventType
{
    Keyframe,
    Transition
}

public sealed class AttachPresetAnimationEventViewModel : ViewModelBase
{
    private AttachPresetAnimationEventType _type = AttachPresetAnimationEventType.Keyframe;
    private double _time;
    private int _order;

    private double? _deltaPosX;
    private double? _deltaPosY;
    private double? _deltaPosZ;

    private double? _deltaPitch;
    private double? _deltaYaw;
    private double? _deltaRoll;

    private double? _fov;

    public AttachPresetAnimationEventViewModel()
        : this(isBaseKeyframe: false)
    {
    }

    public AttachPresetAnimationEventViewModel(bool isBaseKeyframe)
    {
        IsBaseKeyframe = isBaseKeyframe;
        if (isBaseKeyframe)
        {
            Type = AttachPresetAnimationEventType.Keyframe;
            Time = 0.0;
            Order = 0;
        }
    }

    public bool IsBaseKeyframe { get; }

    public AttachPresetAnimationEventType Type
    {
        get => _type;
        set
        {
            if (IsBaseKeyframe)
            {
                value = AttachPresetAnimationEventType.Keyframe;
            }

            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(IsKeyframe));
                OnPropertyChanged(nameof(IsTransition));
            }
        }
    }

    public bool IsKeyframe => Type == AttachPresetAnimationEventType.Keyframe;
    public bool IsTransition => Type == AttachPresetAnimationEventType.Transition;

    public double Time
    {
        get => _time;
        set
        {
            if (IsBaseKeyframe) value = 0.0;
            if (SetProperty(ref _time, Math.Max(0.0, value)))
            {
                OnPropertyChanged(nameof(DisplayTime));
            }
        }
    }

    public int Order
    {
        get => _order;
        set => SetProperty(ref _order, value);
    }

    public string DisplayTime => $"{Time:0.###}s";

    public double? DeltaPosX { get => _deltaPosX; set => SetProperty(ref _deltaPosX, value); }
    public double? DeltaPosY { get => _deltaPosY; set => SetProperty(ref _deltaPosY, value); }
    public double? DeltaPosZ { get => _deltaPosZ; set => SetProperty(ref _deltaPosZ, value); }

    public double? DeltaPitch { get => _deltaPitch; set => SetProperty(ref _deltaPitch, value); }
    public double? DeltaYaw { get => _deltaYaw; set => SetProperty(ref _deltaYaw, value); }
    public double? DeltaRoll { get => _deltaRoll; set => SetProperty(ref _deltaRoll, value); }

    public double? Fov { get => _fov; set => SetProperty(ref _fov, value); }
}
