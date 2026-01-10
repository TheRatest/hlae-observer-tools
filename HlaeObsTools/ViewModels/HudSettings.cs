using System.Linq;
using System.Collections.Generic;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Settings;

namespace HlaeObsTools.ViewModels;

/// <summary>
/// Settings for HUD.
/// </summary>
public sealed class HudSettings : ViewModelBase
{
    public record AttachmentPreset
    {
        public string AttachmentName { get; init; } = string.Empty;
        public double OffsetPosX { get; init; }
        public double OffsetPosY { get; init; }
        public double OffsetPosZ { get; init; }
        public double OffsetPitch { get; init; }
        public double OffsetYaw { get; init; }
        public double OffsetRoll { get; init; }
        public double Fov { get; init; } = 90.0;
        public AttachmentPresetAnimation Animation { get; init; } = new();
    }

    public record AttachmentPresetAnimation
    {
        public bool Enabled { get; init; }
        public List<AttachmentPresetAnimationEvent> Events { get; init; } = new();
    }

    public enum AttachmentPresetAnimationEventType
    {
        Keyframe,
        Transition
    }

    public enum AttachmentPresetAnimationTransitionEasing
    {
        Linear,
        Smoothstep,
        EaseInOutCubic
    }

    public record AttachmentPresetAnimationEvent
    {
        public AttachmentPresetAnimationEventType Type { get; init; } = AttachmentPresetAnimationEventType.Keyframe;
        public double Time { get; init; }
        public int Order { get; init; }

        public double? DeltaPosX { get; init; }
        public double? DeltaPosY { get; init; }
        public double? DeltaPosZ { get; init; }

        public double? DeltaPitch { get; init; }
        public double? DeltaYaw { get; init; }
        public double? DeltaRoll { get; init; }

        public double? Fov { get; init; }

        public double? TransitionDuration { get; init; }
        public AttachmentPresetAnimationTransitionEasing? TransitionEasing { get; init; }
    }

    private bool _isHudEnabled = true;
    private bool _useAltPlayerBinds;
    private bool _showKillfeed = true;
    private bool _showKillfeedAttackerSlot = true;

    /// <summary>
    /// Whether the native HUD overlay in the video display is shown.
    /// </summary>
    public bool IsHudEnabled
    {
        get => _isHudEnabled;
        set => SetProperty(ref _isHudEnabled, value);
    }

    /// <summary>
    /// Whether to use alternative player bind labels for slots 6-0.
    /// </summary>
    public bool UseAltPlayerBinds
    {
        get => _useAltPlayerBinds;
        set => SetProperty(ref _useAltPlayerBinds, value);
    }

    /// <summary>
    /// Whether the killfeed overlay is shown.
    /// </summary>
    public bool ShowKillfeed
    {
        get => _showKillfeed;
        set => SetProperty(ref _showKillfeed, value);
    }

    /// <summary>
    /// Whether to show attacker bind labels in the killfeed.
    /// </summary>
    public bool ShowKillfeedAttackerSlot
    {
        get => _showKillfeedAttackerSlot;
        set => SetProperty(ref _showKillfeedAttackerSlot, value);
    }

    /// <summary>
    /// Attach action presets for the radial menu (5 slots).
    /// </summary>
    public List<AttachmentPreset> AttachPresets { get; } = Enumerable.Range(0, 5).Select(_ => new AttachmentPreset()).ToList();

    public void ApplyAttachPresets(IEnumerable<AttachmentPresetData> presets)
    {
        var items = presets?.ToList() ?? new List<AttachmentPresetData>();
        AttachPresets.Clear();

        foreach (var preset in items)
        {
            AttachPresets.Add(new AttachmentPreset
            {
                AttachmentName = preset.AttachmentName,
                OffsetPosX = preset.OffsetPosX,
                OffsetPosY = preset.OffsetPosY,
                OffsetPosZ = preset.OffsetPosZ,
                OffsetPitch = preset.OffsetPitch,
                OffsetYaw = preset.OffsetYaw,
                OffsetRoll = preset.OffsetRoll,
                Fov = preset.Fov,
                Animation = FromData(preset.Animation)
            });
        }

        while (AttachPresets.Count < 5)
        {
            AttachPresets.Add(new AttachmentPreset());
        }
    }

    public IEnumerable<AttachmentPresetData> ToAttachPresetData()
    {
        return AttachPresets.Select(p => new AttachmentPresetData
        {
            AttachmentName = p.AttachmentName,
            OffsetPosX = p.OffsetPosX,
            OffsetPosY = p.OffsetPosY,
            OffsetPosZ = p.OffsetPosZ,
            OffsetPitch = p.OffsetPitch,
            OffsetYaw = p.OffsetYaw,
            OffsetRoll = p.OffsetRoll,
            Fov = p.Fov,
            Animation = ToData(p.Animation)
        });
    }

    private static AttachmentPresetAnimation FromData(AttachmentPresetAnimationData? data)
    {
        if (data == null)
        {
            return new AttachmentPresetAnimation();
        }

        var events = (data.Events ?? new List<AttachmentPresetAnimationEventData>())
            .Select(e => new AttachmentPresetAnimationEvent
            {
                Type = string.Equals(e.Type, "transition", System.StringComparison.OrdinalIgnoreCase)
                    ? AttachmentPresetAnimationEventType.Transition
                    : AttachmentPresetAnimationEventType.Keyframe,
                Time = e.Time,
                Order = e.Order,
                DeltaPosX = e.DeltaPosX,
                DeltaPosY = e.DeltaPosY,
                DeltaPosZ = e.DeltaPosZ,
                DeltaPitch = e.DeltaPitch,
                DeltaYaw = e.DeltaYaw,
                DeltaRoll = e.DeltaRoll,
                Fov = e.Fov,
                TransitionDuration = e.TransitionDuration,
                TransitionEasing = ParseTransitionEasing(e.TransitionEasing)
            })
            .ToList();

        return new AttachmentPresetAnimation
        {
            Enabled = data.Enabled,
            Events = events
        };
    }

    private static AttachmentPresetAnimationData? ToData(AttachmentPresetAnimation animation)
    {
        if (!animation.Enabled && (animation.Events == null || animation.Events.Count == 0))
        {
            return null;
        }

        return new AttachmentPresetAnimationData
        {
            Enabled = animation.Enabled,
            Events = (animation.Events ?? new List<AttachmentPresetAnimationEvent>())
                .Select(e => new AttachmentPresetAnimationEventData
                {
                    Type = e.Type == AttachmentPresetAnimationEventType.Transition ? "transition" : "keyframe",
                    Time = e.Time,
                    Order = e.Order,
                    DeltaPosX = e.DeltaPosX,
                    DeltaPosY = e.DeltaPosY,
                    DeltaPosZ = e.DeltaPosZ,
                    DeltaPitch = e.DeltaPitch,
                    DeltaYaw = e.DeltaYaw,
                    DeltaRoll = e.DeltaRoll,
                    Fov = e.Fov,
                    TransitionDuration = e.TransitionDuration,
                    TransitionEasing = ToTransitionEasing(e.TransitionEasing)
                })
                .ToList()
        };
    }

    private static AttachmentPresetAnimationTransitionEasing? ParseTransitionEasing(string? easing)
    {
        if (string.IsNullOrWhiteSpace(easing)) return null;

        return easing.Trim().ToLowerInvariant() switch
        {
            "linear" => AttachmentPresetAnimationTransitionEasing.Linear,
            "smoothstep" => AttachmentPresetAnimationTransitionEasing.Smoothstep,
            "easeinoutcubic" => AttachmentPresetAnimationTransitionEasing.EaseInOutCubic,
            _ => AttachmentPresetAnimationTransitionEasing.Smoothstep
        };
    }

    private static string? ToTransitionEasing(AttachmentPresetAnimationTransitionEasing? easing)
    {
        if (easing == null) return null;

        return easing.Value switch
        {
            AttachmentPresetAnimationTransitionEasing.Linear => "linear",
            AttachmentPresetAnimationTransitionEasing.Smoothstep => "smoothstep",
            AttachmentPresetAnimationTransitionEasing.EaseInOutCubic => "easeinoutcubic",
            _ => "smoothstep"
        };
    }
}
