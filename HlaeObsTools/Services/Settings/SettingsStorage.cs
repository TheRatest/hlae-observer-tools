using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HlaeObsTools.Services.Settings;

public class SettingsStorage
{
    private readonly string _storagePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SettingsStorage()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = Path.Combine(appData, "HlaeObsTools");
        Directory.CreateDirectory(baseDir);
        _storagePath = Path.Combine(baseDir, "settings.json");
    }

    public AppSettingsData Load()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath);
                var data = JsonSerializer.Deserialize<AppSettingsData>(json, _jsonOptions);
                if (data != null)
                    return data;
            }
        }
        catch
        {
            // ignore load errors, return defaults
        }

        return new AppSettingsData();
    }

    public void Save(AppSettingsData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            File.WriteAllText(_storagePath, json);
        }
        catch
        {
            // ignore save errors
        }
    }
}

public class AppSettingsData
{
    public List<AttachmentPresetData> AttachPresets { get; set; } = new();
    public double MarkerScale { get; set; } = 1.0;
    public double HeightScaleMultiplier { get; set; } = 1.0;
    public bool UseAltPlayerBinds { get; set; } = false;
    public string WebSocketHost { get; set; } = "127.0.0.1";
    public int WebSocketPort { get; set; } = 31338;
    public int UdpPort { get; set; } = 31339;
    public int RtpPort { get; set; } = 5000;
    public int GsiPort { get; set; } = 31337;
    public string MapObjPath { get; set; } = string.Empty;
    public double PinScale { get; set; } = 1.0;
    public double PinOffsetZ { get; set; }
    public double ViewportMouseScale { get; set; } = 1.0;
    public double MapScale { get; set; } = 1.0;
    public double MapYaw { get; set; }
    public double MapPitch { get; set; }
    public double MapRoll { get; set; }
    public double MapOffsetX { get; set; }
    public double MapOffsetY { get; set; }
    public double MapOffsetZ { get; set; }
    public FreecamSettingsData FreecamSettings { get; set; } = new();
}

public class AttachmentPresetData
{
    public string AttachmentName { get; set; } = string.Empty;
    public double OffsetPosX { get; set; }
    public double OffsetPosY { get; set; }
    public double OffsetPosZ { get; set; }
    public double OffsetPitch { get; set; }
    public double OffsetYaw { get; set; }
    public double OffsetRoll { get; set; }
    public double Fov { get; set; } = 90.0;
    public AttachmentPresetAnimationData? Animation { get; set; }
}

public class AttachmentPresetAnimationData
{
    public bool Enabled { get; set; }
    public List<AttachmentPresetAnimationEventData> Events { get; set; } = new();
}

public class AttachmentPresetAnimationEventData
{
    public string Type { get; set; } = "keyframe"; // "keyframe" | "transition"
    public double Time { get; set; }
    public int Order { get; set; }

    public double? DeltaPosX { get; set; }
    public double? DeltaPosY { get; set; }
    public double? DeltaPosZ { get; set; }

    public double? DeltaPitch { get; set; }
    public double? DeltaYaw { get; set; }
    public double? DeltaRoll { get; set; }

    public double? Fov { get; set; }
}

public class FreecamSettingsData
{
    public double MouseSensitivity { get; set; } = 0.12;
    public double MoveSpeed { get; set; } = 200.0;
    public double SprintMultiplier { get; set; } = 2.5;
    public double VerticalSpeed { get; set; } = 200.0;
    public double SpeedAdjustRate { get; set; } = 1.1;
    public double SpeedMinMultiplier { get; set; } = 0.05;
    public double SpeedMaxMultiplier { get; set; } = 5.0;
    public double RollSpeed { get; set; } = 45.0;
    public double RollSmoothing { get; set; } = 0.8;
    public double LeanStrength { get; set; } = 1.0;
    public double LeanAccelScale { get; set; } = 0.250;
    public double LeanVelocityScale { get; set; } = 0.01;
    public double LeanMaxAngle { get; set; } = 20.0;
    public double LeanHalfTime { get; set; } = 0.30;
    public double FovMin { get; set; } = 10.0;
    public double FovMax { get; set; } = 150.0;
    public double FovStep { get; set; } = 2.0;
    public double DefaultFov { get; set; } = 90.0;
    public bool SmoothEnabled { get; set; } = true;
    public double HalfVec { get; set; } = 0.5;
    public double HalfRot { get; set; } = 0.5;
    public double LockHalfRot { get; set; } = 0.1;
    public double LockHalfRotTransition { get; set; } = 1.0;
    public double HalfFov { get; set; } = 0.8;
    public bool RotCriticalDamping { get; set; } = false;
    public double RotDampingRatio { get; set; } = 1.0;
    public bool HoldMovementFollowsCamera { get; set; } = true;
    public bool AnalogKeyboardEnabled { get; set; }
    public double AnalogLeftDeadzone { get; set; }
    public double AnalogRightDeadzone { get; set; }
    public double AnalogCurve { get; set; }
    public bool ClampPitch { get; set; }
}
