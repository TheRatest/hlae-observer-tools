using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.Services.Input;
using System.Numerics;

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
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated += OnGameStateUpdated;

        Title = "3D Viewport";
        CanFloat = true;
        CanPin = true;
    }

    public Viewport3DSettings Viewport3DSettings => _settings;
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

        if (state.RawForward.LengthSquared() < 0.0001f)
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

    public void Dispose()
    {
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
