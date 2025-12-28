using System.ComponentModel;
using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.Services.Input;
using OpenTK.Mathematics;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class Viewport3DDockViewModel : Tool, IDisposable
{
    private readonly Viewport3DSettings _settings;
    private readonly FreecamSettings _freecamSettings;
    private HlaeInputSender? _inputSender;
    private readonly HlaeWebSocketClient? _webSocketClient;
    private readonly VideoDisplayDockViewModel? _videoDisplay;
    private readonly GsiServer? _gsiServer;
    private long _lastHeartbeat;
    private bool _awaitFreecamRelease;

    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };

    public event Action<IReadOnlyList<ViewportPin>>? PinsUpdated;

    public Viewport3DDockViewModel(Viewport3DSettings settings, FreecamSettings freecamSettings, HlaeWebSocketClient? webSocketClient = null, VideoDisplayDockViewModel? videoDisplay = null, GsiServer? gsiServer = null)
    {
        _settings = settings;
        _freecamSettings = freecamSettings;
        _webSocketClient = webSocketClient;
        _videoDisplay = videoDisplay;
        _gsiServer = gsiServer;
        _settings.PropertyChanged += OnSettingsChanged;
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated += OnGameStateUpdated;

        Title = "3D Viewport";
        CanFloat = true;
        CanPin = true;
    }

    public string MapObjPath
    {
        get => _settings.MapObjPath;
        set
        {
            if (_settings.MapObjPath != value)
            {
                _settings.MapObjPath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    public FreecamSettings FreecamSettings => _freecamSettings;
    public HlaeInputSender? InputSender => _inputSender;

    public void SetInputSender(HlaeInputSender sender)
    {
        _inputSender = sender;
        OnPropertyChanged(nameof(InputSender));
    }

    public async void HandoffFreecam(ViewportFreecamState state)
    {
        if (_webSocketClient == null)
            return;

        if (state.RawForward.LengthSquared < 0.0001f)
            return;

        var pitch = state.RawPitch;
        var yaw = state.RawYaw;
        var roll = state.RawRoll;
        var smoothQuat = Quaternion.Normalize(state.SmoothedOrientation);

        var args = new
        {
            posX = state.RawPosition.X,
            posY = state.RawPosition.Y,
            posZ = state.RawPosition.Z,
            pitch,
            yaw,
            roll,
            fov = state.RawFov,
            smoothPosX = state.SmoothedPosition.X,
            smoothPosY = state.SmoothedPosition.Y,
            smoothPosZ = state.SmoothedPosition.Z,
            smoothQuatW = smoothQuat.W,
            smoothQuatX = smoothQuat.X,
            smoothQuatY = smoothQuat.Y,
            smoothQuatZ = smoothQuat.Z,
            smoothFov = state.SmoothedFov,
            speedScalar = state.SpeedScalar,
            mouseSensitivity = (float)_freecamSettings.MouseSensitivity,
            moveSpeed = (float)_freecamSettings.MoveSpeed,
            sprintMultiplier = (float)_freecamSettings.SprintMultiplier,
            verticalSpeed = (float)_freecamSettings.VerticalSpeed,
            speedAdjustRate = (float)_freecamSettings.SpeedAdjustRate,
            speedMinMultiplier = (float)_freecamSettings.SpeedMinMultiplier,
            speedMaxMultiplier = (float)_freecamSettings.SpeedMaxMultiplier,
            rollSpeed = (float)_freecamSettings.RollSpeed,
            rollSmoothing = (float)_freecamSettings.RollSmoothing,
            leanStrength = (float)_freecamSettings.LeanStrength,
            leanAccelScale = (float)_freecamSettings.LeanAccelScale,
            leanVelocityScale = (float)_freecamSettings.LeanVelocityScale,
            leanMaxAngle = (float)_freecamSettings.LeanMaxAngle,
            leanHalfTime = (float)_freecamSettings.LeanHalfTime,
            clampPitch = _freecamSettings.ClampPitch,
            fovMin = (float)_freecamSettings.FovMin,
            fovMax = (float)_freecamSettings.FovMax,
            fovStep = (float)_freecamSettings.FovStep,
            defaultFov = (float)_freecamSettings.DefaultFov,
            smoothEnabled = _freecamSettings.SmoothEnabled,
            halfVec = (float)_freecamSettings.HalfVec,
            halfRot = (float)_freecamSettings.HalfRot,
            lockHalfRot = (float)_freecamSettings.LockHalfRot,
            lockHalfRotTransition = (float)_freecamSettings.LockHalfRotTransition,
            halfFov = (float)_freecamSettings.HalfFov,
            rotCriticalDamping = _freecamSettings.RotCriticalDamping,
            rotDampingRatio = (float)_freecamSettings.RotDampingRatio
        };

        await _webSocketClient.SendCommandAsync("freecam_handoff", args);
        _videoDisplay?.RequestFreecamInputLock();
        _awaitFreecamRelease = true;
    }

    public void ReleaseHandoffFreecamInput()
    {
        if (!_awaitFreecamRelease)
            return;

        _awaitFreecamRelease = false;
        _videoDisplay?.RequestFreecamInputRelease();
    }

    public float PinScale
    {
        get => _settings.PinScale;
        set
        {
            if (Math.Abs(_settings.PinScale - value) > 0.0001f)
            {
                _settings.PinScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float PinOffsetZ
    {
        get => _settings.PinOffsetZ;
        set
        {
            if (Math.Abs(_settings.PinOffsetZ - value) > 0.0001f)
            {
                _settings.PinOffsetZ = value;
                OnPropertyChanged();
            }
        }
    }

    public float ViewportMouseScale
    {
        get => _settings.ViewportMouseScale;
        set
        {
            if (Math.Abs(_settings.ViewportMouseScale - value) > 0.0001f)
            {
                _settings.ViewportMouseScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapScale
    {
        get => _settings.MapScale;
        set
        {
            if (Math.Abs(_settings.MapScale - value) > 0.0001f)
            {
                _settings.MapScale = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapYaw
    {
        get => _settings.MapYaw;
        set
        {
            if (Math.Abs(_settings.MapYaw - value) > 0.0001f)
            {
                _settings.MapYaw = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapPitch
    {
        get => _settings.MapPitch;
        set
        {
            if (Math.Abs(_settings.MapPitch - value) > 0.0001f)
            {
                _settings.MapPitch = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapRoll
    {
        get => _settings.MapRoll;
        set
        {
            if (Math.Abs(_settings.MapRoll - value) > 0.0001f)
            {
                _settings.MapRoll = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetX
    {
        get => _settings.MapOffsetX;
        set
        {
            if (Math.Abs(_settings.MapOffsetX - value) > 0.0001f)
            {
                _settings.MapOffsetX = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetY
    {
        get => _settings.MapOffsetY;
        set
        {
            if (Math.Abs(_settings.MapOffsetY - value) > 0.0001f)
            {
                _settings.MapOffsetY = value;
                OnPropertyChanged();
            }
        }
    }

    public float MapOffsetZ
    {
        get => _settings.MapOffsetZ;
        set
        {
            if (Math.Abs(_settings.MapOffsetZ - value) > 0.0001f)
            {
                _settings.MapOffsetZ = value;
                OnPropertyChanged();
            }
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.MapObjPath))
            OnPropertyChanged(nameof(MapObjPath));
        else if (e.PropertyName == nameof(Viewport3DSettings.PinScale))
            OnPropertyChanged(nameof(PinScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.PinOffsetZ))
            OnPropertyChanged(nameof(PinOffsetZ));
        else if (e.PropertyName == nameof(Viewport3DSettings.ViewportMouseScale))
            OnPropertyChanged(nameof(ViewportMouseScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapScale))
            OnPropertyChanged(nameof(MapScale));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapYaw))
            OnPropertyChanged(nameof(MapYaw));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapPitch))
            OnPropertyChanged(nameof(MapPitch));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapRoll))
            OnPropertyChanged(nameof(MapRoll));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetX))
            OnPropertyChanged(nameof(MapOffsetX));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetY))
            OnPropertyChanged(nameof(MapOffsetY));
        else if (e.PropertyName == nameof(Viewport3DSettings.MapOffsetZ))
            OnPropertyChanged(nameof(MapOffsetZ));
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated -= OnGameStateUpdated;
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        if (state.Heartbeat == _lastHeartbeat)
            return;
        _lastHeartbeat = state.Heartbeat;

        var pins = new List<ViewportPin>();
        foreach (var p in state.Players)
        {
            if (p == null || !p.IsAlive)
                continue;

            var label = GetSlotLabel(p.Slot, _settings.UseAltPlayerBinds);
            pins.Add(new ViewportPin
            {
                Position = p.Position,
                Forward = p.Forward,
                Team = p.Team,
                Slot = p.Slot,
                Label = label,
                IsAlive = p.IsAlive
            });
        }

        Dispatcher.UIThread.Post(() => PinsUpdated?.Invoke(pins));
    }

    private static string GetSlotLabel(int slot, bool useAlt)
    {
        if (slot < 0 || slot > 9)
            return string.Empty;

        if (useAlt && slot >= 5)
            return AltBindLabels[slot - 5];

        return ((slot + 1) % 10).ToString();
    }

}
