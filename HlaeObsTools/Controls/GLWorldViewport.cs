using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTK.Graphics.OpenGL;
using Box2i = OpenTK.Mathematics.Box2i;
using Vector2i = OpenTK.Mathematics.Vector2i;
using GLPixelFormat = OpenTK.Graphics.OpenGL.PixelFormat;
using GLWindowState = OpenTK.Windowing.Common.WindowState;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using HlaeObsTools.Services.Input;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.ViewModels;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes;

namespace HlaeObsTools.Controls;

public sealed class GLWorldViewport : NativeControlHost
{
    private const float MaxUncappedFps = 1000f;
    private static readonly string LogPath = GetLogPath();
    private static bool _logPathAnnounced;
    private static bool _logWriteFailedLogged;
    private static readonly string WndClassName = $"HLAE_GLViewportHost_{Guid.NewGuid():N}";
    private static readonly Dictionary<IntPtr, WeakReference<GLWorldViewport>> HostMap = new();
    private static bool _classRegistered;
    private static readonly object ClassLock = new();
    private static WndProcDelegate? _wndProc;
    private static IntPtr _wndProcPtr = IntPtr.Zero;

    public static readonly StyledProperty<string?> MapPathProperty =
        AvaloniaProperty.Register<GLWorldViewport, string?>(nameof(MapPath));
    public static readonly StyledProperty<float> ViewportMouseScaleProperty =
        AvaloniaProperty.Register<GLWorldViewport, float>(nameof(ViewportMouseScale), 0.75f);
    public static readonly StyledProperty<float> ViewportFpsCapProperty =
        AvaloniaProperty.Register<GLWorldViewport, float>(nameof(ViewportFpsCap), 60.0f);
    public static readonly StyledProperty<FreecamSettings?> FreecamSettingsProperty =
        AvaloniaProperty.Register<GLWorldViewport, FreecamSettings?>(nameof(FreecamSettings));
    public static readonly StyledProperty<HlaeInputSender?> InputSenderProperty =
        AvaloniaProperty.Register<GLWorldViewport, HlaeInputSender?>(nameof(InputSender));

    private IntPtr _hwnd;
    private NativeWindow? _nativeWindow;
    private readonly object _nativeWindowLock = new();
    private bool _nativeInitDone;
    private int _renderWidth;
    private int _renderHeight;

    private RendererContext? _rendererContext;
    private Renderer? _renderer;
    private TextRenderer? _textRenderer;
    private Framebuffer? _mainFramebuffer;
    private Framebuffer? _defaultFramebuffer;
    private GameFileLoader? _fileLoader;
    private Package? _mapPackage;
    private bool _rendererReady;
    private bool _mapLoadPending;
    private string? _pendingMapPath;
    private bool _showEntityModels;
    private bool _renderLogged;
    private bool _mapHasExternalReferences;

    private Vector3 _target = Vector3.Zero;
    private float _distance = 10f;
    private float _yaw = DegToRad(45f);
    private float _pitch = DegToRad(30f);
    private float _minDistance = 0.5f;
    private float _maxDistance = 1000f;
    private Vector3 _orbitTargetBeforeFreecam;
    private float _orbitYawBeforeFreecam;
    private float _orbitPitchBeforeFreecam;
    private float _orbitDistanceBeforeFreecam;
    private bool _orbitStateSaved;

    private bool _dragging;
    private bool _panning;
    private readonly HashSet<Key> _keysDown = new();
    private Point _lastPointer;
    private bool _mouseButton4Down;
    private bool _mouseButton5Down;

    private Point _freecamCenterLocal;
    private PixelPoint _freecamCenterScreen;
    private bool _freecamCursorHidden;
    private bool _freecamActive;
    private bool _freecamInputEnabled;
    private bool _freecamInitialized;
    private bool _freecamIgnoreNextDelta;
    private float _freecamSpeedScalar = 1.0f;
    private bool _lastMouseButton4;
    private bool _lastMouseButton5;
    private float _mouseButton4Hold;
    private float _mouseButton5Hold;
    private float _freecamMouseVelocityX;
    private float _freecamMouseVelocityY;
    private float _freecamTargetRoll;
    private float _freecamCurrentRoll;
    private float _freecamRollVelocity;
    private float _freecamLastLateralVelocity;
    private Quaternion _freecamRawQuat = Quaternion.Identity;
    private Quaternion _freecamSmoothedQuat = Quaternion.Identity;
    private Vector3 _freecamRotVelocity = Vector3.Zero;
    private Vector3 _freecamLastSmoothedPosition;
    private Vector2 _freecamMouseDelta;
    private float _freecamWheelDelta;
    private DateTime _freecamLastUpdate;
    private FreecamTransform _freecamTransform;
    private FreecamTransform _freecamSmoothed;
    private FreecamConfig _freecamConfig = FreecamConfig.Default;
    private FreecamSettings? _freecamSettings;
    private HlaeInputSender? _inputSender;

    private float _viewportFpsCapCached;
    private readonly Stopwatch _frameLimiter = Stopwatch.StartNew();
    private long _lastLimiterTicks;
    private long _lastFrameTimestamp;
    private DispatcherTimer? _frameLimiterTimer;
    private bool _frameLimiterPending;
    private CancellationTokenSource? _renderCts;
    private Task? _renderLoop;
    private readonly ManualResetEventSlim _renderSignal = new(false);

    public GLWorldViewport()
    {
        Focusable = true;
        IsHitTestVisible = true;
    }

    static GLWorldViewport()
    {
        MapPathProperty.Changed.AddClassHandler<GLWorldViewport>((sender, args) => sender.OnMapPathChanged(args));
        ViewportFpsCapProperty.Changed.AddClassHandler<GLWorldViewport>((sender, _) => sender.OnViewportFpsCapChanged());
        FreecamSettingsProperty.Changed.AddClassHandler<GLWorldViewport>((sender, args) => sender.OnFreecamSettingsChanged(args));
        InputSenderProperty.Changed.AddClassHandler<GLWorldViewport>((sender, args) => sender.OnInputSenderChanged(args));
    }

    public string? MapPath
    {
        get => GetValue(MapPathProperty);
        set => SetValue(MapPathProperty, value);
    }

    public float ViewportMouseScale
    {
        get => GetValue(ViewportMouseScaleProperty);
        set => SetValue(ViewportMouseScaleProperty, value);
    }

    public float ViewportFpsCap
    {
        get => GetValue(ViewportFpsCapProperty);
        set => SetValue(ViewportFpsCapProperty, value);
    }

    public FreecamSettings? FreecamSettings
    {
        get => GetValue(FreecamSettingsProperty);
        set => SetValue(FreecamSettingsProperty, value);
    }

    public HlaeInputSender? InputSender
    {
        get => GetValue(InputSenderProperty);
        set => SetValue(InputSenderProperty, value);
    }

    public bool IsFreecamActive => _freecamActive;
    public bool IsFreecamInputEnabled => _freecamInputEnabled;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CreateNativeControlCore(parent);
        }

        _hwnd = CreateChildWindow(parent.Handle);
        if (_hwnd == IntPtr.Zero)
        {
            return base.CreateNativeControlCore(parent);
        }

        RegisterHostWindow(_hwnd, this);
        UpdateChildWindowSize();
        InitializeAfterNativeCreated();
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        StopRenderLoop();
        DisposeRenderer();
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHostWindow(_hwnd);
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        base.DestroyNativeControlCore(control);
    }

    protected override void OnMeasureInvalidated()
    {
        base.OnMeasureInvalidated();
        UpdateChildWindowSize();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewportFpsCapCached = ViewportFpsCap;
        if (_hwnd != IntPtr.Zero)
        {
            InitializeAfterNativeCreated();
        }
        else
        {
            Dispatcher.UIThread.Post(InitializeAfterNativeCreated, DispatcherPriority.Loaded);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _keysDown.Clear();
        DisableFreecam();
        _frameLimiterTimer?.Stop();
        _frameLimiterPending = false;
        StopRenderLoop();
        DisposeRenderer();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == BoundsProperty)
        {
            UpdateChildWindowSize();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _keysDown.Add(e.Key);
        RequestNextFrame();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keysDown.Remove(e.Key);
        RequestNextFrame();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        HandlePointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        HandlePointerReleased(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HandlePointerMoved(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        HandlePointerWheel(e);
    }

    private void HandlePointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed || updateKind == PointerUpdateKind.MiddleButtonPressed;
        var rightPressed = point.Properties.IsRightButtonPressed || updateKind == PointerUpdateKind.RightButtonPressed;
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        if (rightPressed)
        {
            BeginFreecam(point.Position);
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
            return;
        }

        if (!middlePressed)
            return;

        if (_freecamActive)
            DisableFreecam();

        _dragging = true;
        _panning = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    private void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed || updateKind == PointerUpdateKind.MiddleButtonPressed;
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        var rightReleased = updateKind == PointerUpdateKind.RightButtonReleased;
        if (_freecamActive && rightReleased)
        {
            EndFreecamInput();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        if (!_dragging)
            return;

        var released = updateKind == PointerUpdateKind.MiddleButtonReleased || !middlePressed;
        if (released)
        {
            _dragging = false;
            _panning = false;
            e.Pointer.Capture(null);
        }
    }

    private void HandlePointerMoved(PointerEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        _mouseButton4Down = point.Properties.IsXButton1Pressed;
        _mouseButton5Down = point.Properties.IsXButton2Pressed;

        if (_freecamActive && _freecamInputEnabled)
        {
            if (_freecamIgnoreNextDelta)
            {
                _freecamIgnoreNextDelta = false;
                CenterFreecamCursor();
                RequestNextFrame();
                e.Handled = true;
                return;
            }

            var scale = MathF.Max(0.01f, ViewportMouseScale);
            var dx = (float)(point.Position.X - _freecamCenterLocal.X) * scale;
            var dy = (float)(point.Position.Y - _freecamCenterLocal.Y) * scale;
            if (dx != 0 || dy != 0)
                _freecamMouseDelta += new Vector2(dx, dy);
            CenterFreecamCursor();
            RequestNextFrame();
            e.Handled = true;
            return;
        }

        if (!_dragging)
            return;

        var pos = point.Position;
        var delta = pos - _lastPointer;
        _lastPointer = pos;

        if (_panning)
        {
            Pan((float)delta.X, (float)delta.Y);
        }
        else
        {
            Orbit((float)delta.X, (float)delta.Y);
        }

        RequestNextFrame();
        e.Handled = true;
    }

    private void HandlePointerWheel(PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < double.Epsilon)
            return;

        if (_freecamActive && _freecamInputEnabled)
        {
            _freecamWheelDelta += (float)e.Delta.Y;
            RequestNextFrame();
            e.Handled = true;
            return;
        }
        if (_freecamActive)
            return;

        var zoomFactor = MathF.Pow(1.1f, (float)-e.Delta.Y);
        _distance = Math.Min(_distance * zoomFactor, _maxDistance);
        if (_distance < 0.0001f)
            _distance = 0.0001f;
        RequestNextFrame();
        e.Handled = true;
    }

    private void HandleNativeMouse(uint msg, int x, int y, int xButton)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => HandleNativeMouse(msg, x, y, xButton));
            return;
        }

        const uint WM_MOUSEMOVE = 0x0200;
        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_MBUTTONDOWN = 0x0207;
        const uint WM_MBUTTONUP = 0x0208;
        const uint WM_XBUTTONDOWN = 0x020B;
        const uint WM_XBUTTONUP = 0x020C;

        var position = ClientToLocalPoint(x, y);
        var updateKind = msg switch
        {
            WM_LBUTTONDOWN => PointerUpdateKind.LeftButtonPressed,
            WM_LBUTTONUP => PointerUpdateKind.LeftButtonReleased,
            WM_RBUTTONDOWN => PointerUpdateKind.RightButtonPressed,
            WM_RBUTTONUP => PointerUpdateKind.RightButtonReleased,
            WM_MBUTTONDOWN => PointerUpdateKind.MiddleButtonPressed,
            WM_MBUTTONUP => PointerUpdateKind.MiddleButtonReleased,
            WM_XBUTTONDOWN => xButton == 1 ? PointerUpdateKind.XButton1Pressed : PointerUpdateKind.XButton2Pressed,
            WM_XBUTTONUP => xButton == 1 ? PointerUpdateKind.XButton1Released : PointerUpdateKind.XButton2Released,
            _ => PointerUpdateKind.Other
        };

        if (msg == WM_MOUSEMOVE)
        {
            HandleNativePointerMoved(position);
            return;
        }

        if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN)
        {
            HandleNativePointerPressed(position, updateKind, IsShiftKeyDown());
        }
        else if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP || msg == WM_MBUTTONUP || msg == WM_XBUTTONUP)
        {
            HandleNativePointerReleased(position, updateKind);
        }
    }

    private Point ClientToLocalPoint(int x, int y)
    {
        double scale = (VisualRoot as Avalonia.Rendering.IRenderRoot)?.RenderScaling ?? 1.0;
        return new Point(x / scale, y / scale);
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private static bool IsShiftKeyDown()
    {
        const int VK_SHIFT = 0x10;
        return (GetKeyState(VK_SHIFT) & 0x8000) != 0;
    }

    private void HandleNativePointerPressed(Point position, PointerUpdateKind updateKind, bool shiftDown)
    {
        var middlePressed = updateKind == PointerUpdateKind.MiddleButtonPressed;
        var rightPressed = updateKind == PointerUpdateKind.RightButtonPressed;

        if (updateKind == PointerUpdateKind.XButton1Pressed)
            _mouseButton4Down = true;
        if (updateKind == PointerUpdateKind.XButton2Pressed)
            _mouseButton5Down = true;

        if (rightPressed)
        {
            BeginFreecam(position);
            Focus();
            return;
        }

        if (!middlePressed)
            return;

        if (_freecamActive)
            DisableFreecam();

        _dragging = true;
        _panning = shiftDown;
        _lastPointer = position;
        Focus();
    }

    private void HandleNativePointerReleased(Point position, PointerUpdateKind updateKind)
    {
        var middlePressed = updateKind == PointerUpdateKind.MiddleButtonPressed;
        if (updateKind == PointerUpdateKind.XButton1Released)
            _mouseButton4Down = false;
        if (updateKind == PointerUpdateKind.XButton2Released)
            _mouseButton5Down = false;

        var rightReleased = updateKind == PointerUpdateKind.RightButtonReleased;
        if (_freecamActive && rightReleased)
        {
            EndFreecamInput();
            return;
        }

        if (!_dragging)
            return;

        var released = updateKind == PointerUpdateKind.MiddleButtonReleased || !middlePressed;
        if (released)
        {
            _dragging = false;
            _panning = false;
        }
    }

    private void HandleNativePointerMoved(Point position)
    {
        if (_freecamActive && _freecamInputEnabled)
        {
            if (_freecamIgnoreNextDelta)
            {
                _freecamIgnoreNextDelta = false;
                CenterFreecamCursor();
                RequestNextFrame();
                return;
            }

            var scale = MathF.Max(0.01f, ViewportMouseScale);
            var dx = (float)(position.X - _freecamCenterLocal.X) * scale;
            var dy = (float)(position.Y - _freecamCenterLocal.Y) * scale;
            if (dx != 0 || dy != 0)
                _freecamMouseDelta += new Vector2(dx, dy);
            CenterFreecamCursor();
            RequestNextFrame();
            return;
        }

        if (!_dragging)
            return;

        var delta = position - _lastPointer;
        _lastPointer = position;

        if (_panning)
        {
            Pan((float)delta.X, (float)delta.Y);
        }
        else
        {
            Orbit((float)delta.X, (float)delta.Y);
        }

        RequestNextFrame();
    }

    public void ForwardPointerPressed(PointerPressedEventArgs e)
    {
        OnPointerPressed(e);
    }

    public void ForwardPointerReleased(PointerReleasedEventArgs e)
    {
        OnPointerReleased(e);
    }

    public void ForwardPointerMoved(PointerEventArgs e)
    {
        OnPointerMoved(e);
    }

    public void ForwardPointerWheel(PointerWheelEventArgs e)
    {
        OnPointerWheelChanged(e);
    }

    public void ForwardKeyDown(KeyEventArgs e)
    {
        OnKeyDown(e);
    }

    public void ForwardKeyUp(KeyEventArgs e)
    {
        OnKeyUp(e);
    }

    public bool TryGetFreecamState(out ViewportFreecamState state)
    {
        if (!_freecamActive)
        {
            state = default;
            return false;
        }

        GetFreecamBasis(_freecamTransform, out var rawForward, out var rawUp);
        GetFreecamBasis(_freecamSmoothed, out var smoothForward, out var smoothUp);
        state = new ViewportFreecamState
        {
            RawPosition = _freecamTransform.Position,
            RawForward = Vector3.Normalize(rawForward),
            RawUp = Vector3.Normalize(rawUp),
            RawOrientation = _freecamTransform.Orientation,
            RawPitch = _freecamTransform.Pitch,
            RawYaw = _freecamTransform.Yaw,
            RawRoll = _freecamTransform.Roll,
            RawFov = _freecamTransform.Fov,
            SmoothedPosition = _freecamSmoothed.Position,
            SmoothedForward = Vector3.Normalize(smoothForward),
            SmoothedUp = Vector3.Normalize(smoothUp),
            SmoothedOrientation = _freecamSmoothed.Orientation,
            SmoothedFov = _freecamSmoothed.Fov,
            SpeedScalar = _freecamSpeedScalar
        };
        return true;
    }

    public void DisableFreecamInput()
    {
        EndFreecamInput();
    }

    private void Orbit(float deltaX, float deltaY)
    {
        const float rotateSpeed = 0.01f;
        _yaw -= deltaX * rotateSpeed;
        _pitch += deltaY * rotateSpeed;
        _pitch = Math.Clamp(_pitch, -1.55f, 1.55f);
    }

    private Vector3 GetCameraPosition()
    {
        var cosPitch = MathF.Cos(_pitch);
        var sinPitch = MathF.Sin(_pitch);
        var cosYaw = MathF.Cos(_yaw);
        var sinYaw = MathF.Sin(_yaw);

        var direction = new Vector3(cosPitch * cosYaw, cosPitch * sinYaw, sinPitch);
        return _target + direction * _distance;
    }

    private void Pan(float deltaX, float deltaY)
    {
        var cameraPos = GetCameraPosition();
        var forward = Vector3.Normalize(_target - cameraPos);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        var panScale = _distance * 0.001f;
        _target += (-right * deltaX + up * deltaY) * panScale;
    }

    private void BeginFreecam(Point start)
    {
        if (!_freecamActive)
        {
            _orbitTargetBeforeFreecam = _target;
            _orbitYawBeforeFreecam = _yaw;
            _orbitPitchBeforeFreecam = _pitch;
            _orbitDistanceBeforeFreecam = _distance;
            _orbitStateSaved = true;

            if (!_freecamInitialized)
                InitializeFreecamFromOrbit();
            else
                ResetFreecamFromOrbit();
            _freecamActive = true;
        }

        _freecamInputEnabled = true;
        _freecamIgnoreNextDelta = true;
        _freecamMouseDelta = Vector2.Zero;
        _freecamWheelDelta = 0f;
        _freecamLastUpdate = DateTime.UtcNow;
        LockFreecamCursor();
        RequestNextFrame();
    }

    private void EndFreecamInput()
    {
        _freecamInputEnabled = false;
        ClearFreecamInputState();
        UnlockFreecamCursor();
        RequestNextFrame();
    }

    private void DisableFreecam()
    {
        _freecamInputEnabled = false;
        _freecamActive = false;
        ClearFreecamInputState();
        UnlockFreecamCursor();
        RestoreOrbitState();
        RequestNextFrame();
    }

    private void InitializeFreecamFromOrbit()
    {
        var cameraPos = GetCameraPosition();
        var forward = Vector3.Normalize(_target - cameraPos);
        GetYawPitchFromForward(forward, out var yaw, out var pitch);

        var forwardFromAngles = GetForwardVector(pitch, yaw);
        var worldUp = Vector3.UnitZ;
        var right = Vector3.Cross(forwardFromAngles, worldUp);
        if (right.LengthSquared() < 1e-6f)
            right = Vector3.Cross(forwardFromAngles, Vector3.UnitX);
        right = Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, forwardFromAngles));
        var roll = ComputeRollForUp(pitch, yaw, up);

        _freecamTransform = new FreecamTransform
        {
            Position = cameraPos,
            Yaw = yaw,
            Pitch = pitch,
            Roll = roll,
            Fov = _freecamConfig.DefaultFov,
            Orientation = BuildQuat(pitch, yaw, roll)
        };
        _freecamSmoothed = _freecamTransform;
        ResetFreecamState();
        _freecamInitialized = true;
    }

    private void ResetFreecamFromOrbit()
    {
        InitializeFreecamFromOrbit();
    }

    private void ResetFreecamState()
    {
        _freecamSpeedScalar = Clamp(1.0f, _freecamConfig.SpeedMinMultiplier, _freecamConfig.SpeedMaxMultiplier);
        _lastMouseButton4 = false;
        _lastMouseButton5 = false;
        _mouseButton4Hold = 0.0f;
        _mouseButton5Hold = 0.0f;
        _freecamMouseVelocityX = 0.0f;
        _freecamMouseVelocityY = 0.0f;
        _freecamTargetRoll = 0.0f;
        _freecamCurrentRoll = 0.0f;
        _freecamRollVelocity = 0.0f;
        _freecamLastLateralVelocity = 0.0f;
        _freecamLastSmoothedPosition = _freecamSmoothed.Position;
        _freecamTransform.Orientation = BuildQuat(_freecamTransform);
        _freecamSmoothed.Orientation = BuildQuat(_freecamSmoothed);
        _freecamRawQuat = _freecamTransform.Orientation;
        _freecamSmoothedQuat = _freecamSmoothed.Orientation;
        _freecamRotVelocity = Vector3.Zero;
    }

    private void ClearFreecamInputState()
    {
        _keysDown.Clear();
        _mouseButton4Down = false;
        _mouseButton5Down = false;
        _freecamMouseDelta = Vector2.Zero;
        _freecamWheelDelta = 0f;
    }

    private void RestoreOrbitState()
    {
        if (!_orbitStateSaved)
            return;

        _target = _orbitTargetBeforeFreecam;
        _yaw = _orbitYawBeforeFreecam;
        _pitch = _orbitPitchBeforeFreecam;
        _distance = _orbitDistanceBeforeFreecam;
        _orbitStateSaved = false;
    }

    private void UpdateFreecamForFrame()
    {
        if (!_freecamActive)
            return;

        var now = DateTime.UtcNow;
        if (_freecamLastUpdate == default)
            _freecamLastUpdate = now;
        var deltaTime = (float)(now - _freecamLastUpdate).TotalSeconds;
        _freecamLastUpdate = now;
        UpdateFreecam(deltaTime);
    }

    private void ApplyCameraForFrame(int width, int height)
    {
        if (_renderer == null || _rendererContext == null)
        {
            return;
        }

        if (_freecamActive)
        {
            var fovRad = GetSourceVerticalFovRadians(_freecamSmoothed.Fov);
            _rendererContext.FieldOfView = RadToDeg(fovRad);
            _renderer.Camera.SetViewportSize(width, height);
            var forward = GetForwardFromQuat(_freecamSmoothed.Orientation);
            var up = GetUpFromQuat(_freecamSmoothed.Orientation);
            _renderer.Camera.SetLocationForwardUp(_freecamSmoothed.Position, forward, up);
        }
        else
        {
            _rendererContext.FieldOfView = 60f;
            _renderer.Camera.SetViewportSize(width, height);
            var cameraPos = GetCameraPosition();
            var forward = Vector3.Normalize(_target - cameraPos);
            GetYawPitchFromForward(forward, out var yawDeg, out var pitchDeg);
            var rollDeg = ComputeRollForUp(pitchDeg, yawDeg, Vector3.UnitZ);
            _renderer.Camera.SetLocationPitchYawRoll(
                cameraPos,
                DegToRad(pitchDeg),
                DegToRad(yawDeg),
                DegToRad(rollDeg));
        }
    }

    private void UpdateFreecam(float deltaTime)
    {
        if (!_freecamActive)
            return;

        deltaTime = MathF.Min(deltaTime, 0.1f);
        var wheel = _freecamWheelDelta;
        _freecamWheelDelta = 0f;

        if (_freecamInputEnabled)
        {
            UpdateFreecamSpeed(deltaTime, wheel);
            UpdateFreecamMouseLook(deltaTime);
        }

        if (_freecamInputEnabled)
        {
            UpdateFreecamMovement(deltaTime);
            UpdateFreecamFov(wheel);
        }

        UpdateFreecamRoll(deltaTime);
        _freecamTransform.Orientation = BuildQuat(_freecamTransform);
        _freecamRawQuat = _freecamTransform.Orientation;

        if (_freecamConfig.SmoothEnabled)
        {
            ApplyFreecamSmoothing(deltaTime);
        }
        else
        {
            _freecamSmoothed = _freecamTransform;
            _freecamSmoothed.Orientation = _freecamTransform.Orientation;
            _freecamSmoothedQuat = _freecamSmoothed.Orientation;
            _freecamRotVelocity = Vector3.Zero;
        }
    }

    private void UpdateFreecamMouseLook(float deltaTime)
    {
        if (deltaTime <= 0f)
            return;

        var deltaYaw = -_freecamMouseDelta.X * _freecamConfig.MouseSensitivity;
        var deltaPitch = _freecamMouseDelta.Y * _freecamConfig.MouseSensitivity;
        _freecamMouseDelta = Vector2.Zero;

        _freecamTransform.Yaw += deltaYaw;
        _freecamTransform.Pitch += deltaPitch;

        _freecamMouseVelocityX = deltaYaw / deltaTime;
        _freecamMouseVelocityY = deltaPitch / deltaTime;

        if (_freecamConfig.ClampPitch)
        {
            _freecamTransform.Pitch = Clamp(_freecamTransform.Pitch, -89.0f, 89.0f);
        }
    }

    private void UpdateFreecamMovement(float deltaTime)
    {
        var moveSpeed = _freecamConfig.MoveSpeed * _freecamSpeedScalar;
        var verticalSpeed = _freecamConfig.VerticalSpeed * _freecamSpeedScalar;
        var analogEnabled = _freecamSettings?.AnalogKeyboardEnabled == true;
        var useAnalog = false;
        var analogLX = 0f;
        var analogLY = 0f;
        var analogRY = 0f;
        var analogRX = 0f;

        if (analogEnabled && _inputSender != null)
        {
            useAnalog = _inputSender.TryGetAnalogState(out var enabled, out analogLX, out analogLY, out analogRY, out analogRX) && enabled;
        }

        if (useAnalog)
        {
            var sprintInput = MathF.Max(0.0f, analogRX);
            if (sprintInput <= 0.0f && IsShiftDown())
            {
                sprintInput = 1.0f;
            }
            var sprintFactor = 1.0f + sprintInput * (_freecamConfig.SprintMultiplier - 1.0f);
            moveSpeed *= sprintFactor;
            verticalSpeed *= sprintFactor;
        }
        else if (IsShiftDown())
        {
            moveSpeed *= _freecamConfig.SprintMultiplier;
            verticalSpeed *= _freecamConfig.SprintMultiplier;
        }

        var moveQuat = BuildQuat(_freecamTransform.Pitch, _freecamTransform.Yaw, 0f);
        var forward = GetForwardFromQuat(moveQuat);
        var right = GetRightFromQuat(moveQuat);
        var up = GetUpFromQuat(moveQuat);

        var desiredVel = Vector3.Zero;

        if (useAnalog)
        {
            analogLX = Clamp(analogLX, -1.0f, 1.0f);
            analogLY = Clamp(analogLY, -1.0f, 1.0f);
            analogRY = Clamp(analogRY, -1.0f, 1.0f);

            desiredVel += forward * (moveSpeed * analogLY);
            desiredVel += right * (moveSpeed * analogLX);
            desiredVel += up * (verticalSpeed * analogRY);
        }
        else
        {
            if (IsKeyDown(Key.W))
                desiredVel += forward * moveSpeed;
            if (IsKeyDown(Key.S))
                desiredVel -= forward * moveSpeed;
            if (IsKeyDown(Key.A))
                desiredVel -= right * moveSpeed;
            if (IsKeyDown(Key.D))
                desiredVel += right * moveSpeed;
            if (IsKeyDown(Key.Space))
                desiredVel += up * verticalSpeed;
            if (IsCtrlDown())
                desiredVel -= up * verticalSpeed;
        }

        var desiredSpeed = desiredVel.Length();
        var maxSpeed = moveSpeed;
        if ((useAnalog && Math.Abs(analogRY) > 0.0001f) || (!useAnalog && (IsKeyDown(Key.Space) || IsCtrlDown())))
            maxSpeed = MathF.Max(verticalSpeed, moveSpeed);

        if (desiredSpeed > maxSpeed && desiredSpeed > 0.001f)
        {
            var scale = maxSpeed / desiredSpeed;
            desiredVel *= scale;
        }

        _freecamTransform.Position += desiredVel * deltaTime;
        _freecamTransform.Velocity = desiredVel;
    }

    private void UpdateFreecamRoll(float deltaTime)
    {
        if (!_freecamConfig.SmoothEnabled)
        {
            if (IsKeyDown(Key.Q))
                _freecamTargetRoll += _freecamConfig.RollSpeed * deltaTime;
            if (IsKeyDown(Key.E))
                _freecamTargetRoll -= _freecamConfig.RollSpeed * deltaTime;
        }
        else
        {
            _freecamTargetRoll = 0;
        }

        var dynamicRoll = 0f;
        if (_freecamConfig.SmoothEnabled)
        {
            var view = _freecamConfig.SmoothEnabled ? _freecamSmoothed : _freecamTransform;
            // Match game freecam: use yaw-only right vector for lean to avoid roll feedback.
            var right = GetRightVector(view.Yaw);

            var posBlend = _freecamConfig.HalfVec > 0f
                ? 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / _freecamConfig.HalfVec)
                : 1.0f;

            var smoothedPos = Vector3.Lerp(_freecamSmoothed.Position, _freecamTransform.Position, posBlend);
            var smoothedVel = deltaTime > 0f
                ? (smoothedPos - _freecamLastSmoothedPosition) / deltaTime
                : Vector3.Zero;
            _freecamLastSmoothedPosition = smoothedPos;

            var lateralVelocity = Vector3.Dot(smoothedVel, right);
            var lateralAccel = 0f;
            if (deltaTime > 0f)
                lateralAccel = (lateralVelocity - _freecamLastLateralVelocity) / deltaTime;
            _freecamLastLateralVelocity = lateralVelocity;

            var rawLean = (lateralAccel * _freecamConfig.LeanAccelScale)
                          + (lateralVelocity * _freecamConfig.LeanVelocityScale);
            rawLean *= _freecamConfig.LeanStrength;

            if (_freecamConfig.LeanMaxAngle > 0f)
            {
                var curved = MathF.Tanh(rawLean / _freecamConfig.LeanMaxAngle);
                dynamicRoll = curved * _freecamConfig.LeanMaxAngle;
            }
        }
        else
        {
            _freecamLastLateralVelocity = 0f;
            _freecamLastSmoothedPosition = _freecamTransform.Position;
        }

        var combinedRoll = _freecamTargetRoll + dynamicRoll;
        if (_freecamConfig.SmoothEnabled && _freecamConfig.LeanHalfTime > 0f)
        {
            _freecamCurrentRoll = SmoothDamp(_freecamCurrentRoll, combinedRoll, ref _freecamRollVelocity, _freecamConfig.LeanHalfTime, deltaTime);
        }
        else if (_freecamConfig.SmoothEnabled)
        {
            _freecamCurrentRoll = combinedRoll;
            _freecamRollVelocity = 0f;
        }
        else
        {
            _freecamCurrentRoll = Lerp(_freecamCurrentRoll, combinedRoll, 1.0f - _freecamConfig.RollSmoothing);
            _freecamRollVelocity = 0f;
        }
        _freecamTransform.Roll = _freecamCurrentRoll;
    }

    private void UpdateFreecamFov(float wheelDelta)
    {
        if (Math.Abs(wheelDelta) < float.Epsilon || IsAltDown())
            return;

        _freecamTransform.Fov += wheelDelta * _freecamConfig.FovStep;
        _freecamTransform.Fov = Clamp(_freecamTransform.Fov, _freecamConfig.FovMin, _freecamConfig.FovMax);
    }

    private void UpdateFreecamSpeed(float deltaTime, float wheelDelta)
    {
        if (deltaTime <= 0.0f)
            return;

        const float clickWindow = 0.12f;
        var held4 = _mouseButton4Down;
        var held5 = _mouseButton5Down;

        if (held4 && held5)
        {
            _mouseButton4Hold = 0.0f;
            _mouseButton5Hold = 0.0f;
            _lastMouseButton4 = held4;
            _lastMouseButton5 = held5;
            return;
        }

        var prevHold4 = _mouseButton4Hold;
        var prevHold5 = _mouseButton5Hold;
        _mouseButton4Hold = held4 ? _mouseButton4Hold + deltaTime : 0.0f;
        _mouseButton5Hold = held5 ? _mouseButton5Hold + deltaTime : 0.0f;

        static float ExtraTime(float prevHold, float curHold)
        {
            const float window = 0.12f;
            var prevOver = prevHold > window ? prevHold - window : 0.0f;
            var curOver = curHold > window ? curHold - window : 0.0f;
            var deltaOver = curOver - prevOver;
            return deltaOver > 0.0f ? deltaOver : 0.0f;
        }

        var adjustment = 0.0f;
        if (held5)
        {
            if (!_lastMouseButton5)
                adjustment += _freecamConfig.SpeedAdjustRate * clickWindow;
            adjustment += _freecamConfig.SpeedAdjustRate * ExtraTime(prevHold5, _mouseButton5Hold);
        }
        else if (held4)
        {
            if (!_lastMouseButton4)
                adjustment -= _freecamConfig.SpeedAdjustRate * clickWindow;
            adjustment -= _freecamConfig.SpeedAdjustRate * ExtraTime(prevHold4, _mouseButton4Hold);
        }

        if (IsAltDown() && Math.Abs(wheelDelta) > float.Epsilon)
            adjustment += wheelDelta * 0.05f;

        _lastMouseButton4 = held4;
        _lastMouseButton5 = held5;

        if (Math.Abs(adjustment) > float.Epsilon)
        {
            var newScalar = _freecamSpeedScalar + adjustment;
            newScalar = Clamp(newScalar, _freecamConfig.SpeedMinMultiplier, _freecamConfig.SpeedMaxMultiplier);
            _freecamSpeedScalar = newScalar;
        }
    }

    private void ApplyFreecamSmoothing(float deltaTime)
    {
        var posBlend = _freecamConfig.HalfVec > 0f
            ? 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / _freecamConfig.HalfVec)
            : 1.0f;

        var fovBlend = _freecamConfig.HalfFov > 0f
            ? 1.0f - MathF.Exp((-MathF.Log(2.0f) * deltaTime) / _freecamConfig.HalfFov)
            : 1.0f;

        _freecamSmoothed.Position = Vector3.Lerp(_freecamSmoothed.Position, _freecamTransform.Position, posBlend);
        _freecamSmoothed.Fov = Lerp(_freecamSmoothed.Fov, _freecamTransform.Fov, fovBlend);

        if (_freecamConfig.HalfRot > 0f)
        {
            if (_freecamConfig.RotCriticalDamping)
            {
                var omega = MathF.Log(2.0f) / _freecamConfig.HalfRot;
                var damping = MathF.Max(1.0f, _freecamConfig.RotDampingRatio);
                var target = _freecamRawQuat;
                var qErr = target * Quaternion.Inverse(_freecamSmoothedQuat);

                var clampedW = Math.Clamp(qErr.W, -1f, 1f);
                var angle = 2f * MathF.Acos(clampedW);
                var sinHalf = MathF.Sqrt(MathF.Max(0f, 1f - clampedW * clampedW));
                var axis = sinHalf < 1e-6f
                    ? Vector3.UnitX
                    : new Vector3(qErr.X / sinHalf, qErr.Y / sinHalf, qErr.Z / sinHalf);

                var error = axis * angle;
                var wdot = (omega * omega) * error - (2f * damping * omega) * _freecamRotVelocity;
                _freecamRotVelocity += wdot * deltaTime;
                _freecamSmoothedQuat = IntegrateQuat(_freecamSmoothedQuat, _freecamRotVelocity, deltaTime);
            }
            else
            {
                var t = deltaTime / _freecamConfig.HalfRot;
                var target = _freecamRawQuat;
                var qErr = Quaternion.Normalize(Quaternion.Inverse(_freecamSmoothedQuat) * target);
                var w = Math.Clamp(qErr.W, -1f, 1f);
                var targetAngle = 2f * MathF.Acos(w);
                var sinHalf = MathF.Sqrt(MathF.Max(0f, 1f - w * w));

                if (targetAngle > 1.0e-6f && sinHalf > 1.0e-6f)
                {
                    var axis = new Vector3(qErr.X / sinHalf, qErr.Y / sinHalf, qErr.Z / sinHalf);
                    var stepAngle = CalcDeltaExpSmooth(t, targetAngle);
                    if (Math.Abs(stepAngle) > 1.0e-6f)
                    {
                        var half = 0.5f * stepAngle;
                        var sinStep = MathF.Sin(half);
                        var dq = new Quaternion(axis.X * sinStep, axis.Y * sinStep, axis.Z * sinStep, MathF.Cos(half));
                        _freecamSmoothedQuat = Quaternion.Normalize(_freecamSmoothedQuat * dq);
                        _freecamRotVelocity = axis * (stepAngle / deltaTime);
                    }
                    else
                    {
                        _freecamRotVelocity = Vector3.Zero;
                    }
                }
                else
                {
                    _freecamRotVelocity = Vector3.Zero;
                }
            }
        }
        else
        {
            _freecamSmoothedQuat = _freecamRawQuat;
            _freecamRotVelocity = Vector3.Zero;
        }

        _freecamSmoothed.Orientation = _freecamSmoothedQuat;
        UpdateAnglesFromQuat(_freecamSmoothedQuat, ref _freecamSmoothed);
    }

    private static Quaternion BuildQuat(float pitchDeg, float yawDeg, float rollDeg)
    {
        var pitchRad = DegToRad(pitchDeg);
        var yawRad = DegToRad(yawDeg);
        var rollRad = DegToRad(rollDeg);
        var qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitY, pitchRad);
        var qYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawRad);
        var qRoll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollRad);
        return Quaternion.Normalize(qYaw * qPitch * qRoll);
    }

    private static Quaternion BuildQuat(FreecamTransform transform) =>
        BuildQuat(transform.Pitch, transform.Yaw, transform.Roll);

    private static Vector3 GetForwardFromQuat(Quaternion q)
    {
        return Vector3.Normalize(Vector3.Transform(Vector3.UnitX, q));
    }

    private static Vector3 GetUpFromQuat(Quaternion q)
    {
        return Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q));
    }

    private static Vector3 GetRightFromQuat(Quaternion q)
    {
        return Vector3.Normalize(Vector3.Transform(-Vector3.UnitY, q));
    }

    private static Quaternion IntegrateQuat(Quaternion q, Vector3 angularVelocity, float deltaTime)
    {
        var speed = angularVelocity.Length();
        if (speed <= 1e-8f || deltaTime <= 0f)
            return q;

        var angle = speed * deltaTime;
        var axis = angularVelocity / speed;
        var dq = Quaternion.CreateFromAxisAngle(axis, angle);
        return Quaternion.Normalize(dq * q);
    }

    private static float CalcDeltaExpSmooth(float deltaT, float deltaVal)
    {
        const float limitTime = 19.931568f;
        if (deltaT < 0f)
            return 0f;
        if (deltaT > limitTime)
            return deltaVal;

        const float halfTime = 0.69314718f;
        var x = 1.0f / MathF.Exp(deltaT * halfTime);
        return (1.0f - x) * deltaVal;
    }

    private static void UpdateAnglesFromQuat(Quaternion q, ref FreecamTransform transform)
    {
        var forward = GetForwardFromQuat(q);
        var up = GetUpFromQuat(q);
        GetYawPitchFromForward(forward, out var yaw, out var pitch);
        var roll = ComputeRollForUp(pitch, yaw, up);
        transform.Yaw = NormalizeNear(yaw, transform.Yaw);
        transform.Pitch = NormalizeNear(pitch, transform.Pitch);
        transform.Roll = NormalizeNear(roll, transform.Roll);
    }

    private static float NormalizeNear(float value, float target)
    {
        var delta = target - value;
        var turns = MathF.Round(delta / 360f);
        return value + turns * 360f;
    }

    private static float ComputeRollForUp(float pitchDeg, float yawDeg, Vector3 desiredUp)
    {
        var forward = GetForwardVector(pitchDeg, yawDeg);
        var right = GetRightVector(yawDeg);
        var baseUp = Vector3.Normalize(Vector3.Cross(right, forward));
        var fwd = Vector3.Normalize(forward);
        var cross = Vector3.Cross(baseUp, desiredUp);
        var sin = Vector3.Dot(cross, fwd);
        var cos = Vector3.Dot(baseUp, desiredUp);
        var rollRad = MathF.Atan2(sin, cos);
        return (float)RadToDeg(rollRad);
    }

    private static void GetYawPitchFromForward(Vector3 forward, out float yawDeg, out float pitchDeg)
    {
        forward = Vector3.Normalize(forward);
        var yaw = MathF.Atan2(forward.Y, forward.X);
        var pitch = -MathF.Asin(Math.Clamp(forward.Z, -1f, 1f));
        yawDeg = (float)RadToDeg(yaw);
        pitchDeg = (float)RadToDeg(pitch);
    }

    private void GetFreecamBasis(FreecamTransform transform, out Vector3 forward, out Vector3 up)
    {
        forward = GetForwardFromQuat(transform.Orientation);
        up = GetUpFromQuat(transform.Orientation);
    }

    private bool IsKeyDown(Key key) => _keysDown.Contains(key);

    private bool IsShiftDown()
    {
        return _keysDown.Contains(Key.LeftShift)
            || _keysDown.Contains(Key.RightShift);
    }

    private bool IsCtrlDown()
    {
        return _keysDown.Contains(Key.LeftCtrl)
            || _keysDown.Contains(Key.RightCtrl);
    }

    private bool IsAltDown()
    {
        return _keysDown.Contains(Key.LeftAlt)
            || _keysDown.Contains(Key.RightAlt);
    }

    private void LockFreecamCursor()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        if (!TryGetFreecamCenter(out var centerLocal, out var centerScreen))
            return;

        _freecamCenterLocal = centerLocal;
        _freecamCenterScreen = centerScreen;
        SetCursorPosition(centerScreen.X, centerScreen.Y);
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.None);
        if (!_freecamCursorHidden)
        {
            ShowCursor(false);
            _freecamCursorHidden = true;
        }
    }

    private void UnlockFreecamCursor()
    {
        if (_freecamCursorHidden)
        {
            ShowCursor(true);
            Cursor = Avalonia.Input.Cursor.Default;
            _freecamCursorHidden = false;
        }
    }

    private void CenterFreecamCursor()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        if (!TryGetFreecamCenter(out var centerLocal, out var centerScreen))
            return;

        _freecamCenterLocal = centerLocal;
        _freecamCenterScreen = centerScreen;
        SetCursorPosition(centerScreen.X, centerScreen.Y);
    }

    private bool TryGetScreenPoint(Point localPoint, out PixelPoint screenPoint)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            screenPoint = default;
            return false;
        }

        var translated = this.TranslatePoint(localPoint, topLevel);
        if (!translated.HasValue)
        {
            screenPoint = default;
            return false;
        }

        screenPoint = topLevel.PointToScreen(translated.Value);
        return true;
    }

    private bool TryGetLocalPointFromScreen(PixelPoint screenPoint, out Point localPoint)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            localPoint = default;
            return false;
        }

        var clientPoint = topLevel.PointToClient(screenPoint);
        var translated = topLevel.TranslatePoint(clientPoint, this);
        if (!translated.HasValue)
        {
            localPoint = default;
            return false;
        }

        localPoint = translated.Value;
        return true;
    }

    private bool TryGetFreecamCenter(out Point centerLocal, out PixelPoint centerScreen)
    {
        var localCenter = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);
        if (!TryGetScreenPoint(localCenter, out centerScreen))
        {
            centerLocal = default;
            return false;
        }

        if (!TryGetLocalPointFromScreen(centerScreen, out centerLocal))
            centerLocal = localCenter;

        return true;
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    private static void SetCursorPosition(int x, int y)
    {
        SetCursorPos(x, y);
    }

    private static Vector3 GetForwardVector(float pitchDeg, float yawDeg)
    {
        var pitch = DegToRad(pitchDeg);
        var yaw = DegToRad(yawDeg);
        var cosPitch = MathF.Cos(pitch);
        return new Vector3(
            cosPitch * MathF.Cos(yaw),
            cosPitch * MathF.Sin(yaw),
            -MathF.Sin(pitch));
    }

    private static Vector3 GetRightVector(float yawDeg)
    {
        var yaw = DegToRad(yawDeg);
        return new Vector3(MathF.Sin(yaw), -MathF.Cos(yaw), 0f);
    }

    private static float GetSourceVerticalFovRadians(float sourceFovDeg)
    {
        var hRad = DegToRad(Math.Clamp(sourceFovDeg, 1.0f, 179.0f));
        var vRad = 2f * MathF.Atan(MathF.Tan(hRad * 0.5f) * (3f / 4f));
        return Math.Clamp(vRad, DegToRad(1.0f), DegToRad(179.0f));
    }

    private static float DegToRad(float degrees) => degrees * (MathF.PI / 180f);

    private static float RadToDeg(float radians) => radians * (180f / MathF.PI);

    private static float Clamp(float value, float min, float max)
    {
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SmoothDamp(float current, float target, ref float currentVelocity, float smoothTime, float deltaTime)
    {
        if (smoothTime <= 0f || deltaTime <= 0f)
        {
            currentVelocity = 0f;
            return target;
        }

        // Match C++ FreecamController and D3D11Viewport implementations.
        var omega = 2f / smoothTime;
        var x = omega * deltaTime;
        var exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);

        var change = current - target;
        var temp = (currentVelocity + omega * change) * deltaTime;
        currentVelocity = (currentVelocity - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    private void InitializeAfterNativeCreated()
    {
        if (_nativeInitDone || !OperatingSystem.IsWindows())
        {
            return;
        }

        _nativeInitDone = true;
        InitializeNativeWindow();
        StartRenderLoop();
        RequestNextFrame();
    }

    private void InitializeNativeWindow()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.InvokeAsync(InitializeNativeWindow).GetAwaiter().GetResult();
            return;
        }

        lock (_nativeWindowLock)
        {
        if (_nativeWindow != null)
        {
            return;
        }

        GLFWProvider.CheckForMainThread = false;
        GLFWProvider.EnsureInitialized();

        var settings = new NativeWindowSettings
        {
            APIVersion = GLEnvironment.RequiredVersion,
            Flags = ContextFlags.ForwardCompatible,
            StartFocused = false,
            StartVisible = false,
            WindowBorder = WindowBorder.Hidden,
            WindowState = GLWindowState.Normal,
            Title = "HLAE GL Viewport",
            ClientSize = new Vector2i(32, 32),
        };

        _nativeWindow = new NativeWindow(settings);
        IntPtr hwnd;
        unsafe
        {
            hwnd = GLFW.GetWin32Window(_nativeWindow.WindowPtr);
        }
        SetWindowAsChild(hwnd, _hwnd);
        _nativeWindow.IsVisible = true;
        _nativeWindow.Context.MakeNoneCurrent();
        LogMessage($"NativeWindow created hwnd=0x{hwnd.ToInt64():X}");

        _renderWidth = Math.Max(1, (int)Bounds.Width);
        _renderHeight = Math.Max(1, (int)Bounds.Height);
        _nativeWindow.ClientRectangle = new Box2i(0, 0, _renderWidth, _renderHeight);
        }
    }

    private void DisposeRenderer(bool disposeWindow = true)
    {
        LogMessage("DisposeRenderer");
        _rendererReady = false;
        _textRenderer = null;
        _renderer?.Dispose();
        _renderer = null;
        _rendererContext?.Dispose();
        _rendererContext = null;
        _fileLoader?.Dispose();
        _fileLoader = null;
        _mapPackage?.Dispose();
        _mapPackage = null;
        if (_mainFramebuffer != null && _mainFramebuffer != _defaultFramebuffer)
        {
            _mainFramebuffer.Delete();
        }
        _mainFramebuffer = null;
        _defaultFramebuffer = null;
        DisableFreecam();
        _mapHasExternalReferences = false;

        lock (_nativeWindowLock)
        {
            if (_nativeWindow != null)
            {
                _nativeWindow.Context.MakeNoneCurrent();
                if (disposeWindow)
                {
                    _nativeWindow.Dispose();
                    _nativeWindow = null;
                }
            }
        }
    }

    private void OnViewportFpsCapChanged()
    {
        _viewportFpsCapCached = ViewportFpsCap;
        _lastLimiterTicks = _frameLimiter.ElapsedTicks;
        if (_frameLimiterTimer != null)
        {
            _frameLimiterTimer.Stop();
        }
        _frameLimiterPending = false;
        RequestNextFrame();
    }

    private void OnFreecamSettingsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_freecamSettings != null)
        {
            _freecamSettings.PropertyChanged -= OnFreecamSettingsPropertyChanged;
        }

        _freecamSettings = e.NewValue as FreecamSettings;
        if (_freecamSettings != null)
        {
            _freecamSettings.PropertyChanged += OnFreecamSettingsPropertyChanged;
        }

        ApplyFreecamSettings();
    }

    private void OnInputSenderChanged(AvaloniaPropertyChangedEventArgs e)
    {
        _inputSender = e.NewValue as HlaeInputSender;
    }

    private void OnFreecamSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplyFreecamSettings();
    }

    private void ApplyFreecamSettings()
    {
        if (_freecamSettings == null)
        {
            _freecamConfig = FreecamConfig.Default;
            return;
        }

        _freecamConfig = new FreecamConfig
        {
            MouseSensitivity = (float)_freecamSettings.MouseSensitivity,
            MoveSpeed = (float)_freecamSettings.MoveSpeed,
            SprintMultiplier = (float)_freecamSettings.SprintMultiplier,
            VerticalSpeed = (float)_freecamSettings.VerticalSpeed,
            SpeedAdjustRate = (float)_freecamSettings.SpeedAdjustRate,
            SpeedMinMultiplier = (float)_freecamSettings.SpeedMinMultiplier,
            SpeedMaxMultiplier = (float)_freecamSettings.SpeedMaxMultiplier,
            RollSpeed = (float)_freecamSettings.RollSpeed,
            RollSmoothing = (float)_freecamSettings.RollSmoothing,
            LeanStrength = (float)_freecamSettings.LeanStrength,
            LeanAccelScale = (float)_freecamSettings.LeanAccelScale,
            LeanVelocityScale = (float)_freecamSettings.LeanVelocityScale,
            LeanMaxAngle = (float)_freecamSettings.LeanMaxAngle,
            LeanHalfTime = (float)_freecamSettings.LeanHalfTime,
            ClampPitch = _freecamSettings.ClampPitch,
            FovMin = (float)_freecamSettings.FovMin,
            FovMax = (float)_freecamSettings.FovMax,
            FovStep = (float)_freecamSettings.FovStep,
            DefaultFov = (float)_freecamSettings.DefaultFov,
            SmoothEnabled = _freecamSettings.SmoothEnabled,
            HalfVec = (float)_freecamSettings.HalfVec,
            HalfRot = (float)_freecamSettings.HalfRot,
            HalfFov = (float)_freecamSettings.HalfFov,
            RotCriticalDamping = _freecamSettings.RotCriticalDamping,
            RotDampingRatio = (float)_freecamSettings.RotDampingRatio
        };
    }

    private void OnMapPathChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var path = e.NewValue as string;
        LogMessage($"OnMapPathChanged: {path ?? "<null>"}");
        _pendingMapPath = string.IsNullOrWhiteSpace(path) ? null : path;
        _mapLoadPending = true;
        RequestNextFrame();
    }

    private void StartRenderLoop()
    {
        if (_renderLoop != null)
        {
            return;
        }

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        _lastLimiterTicks = _frameLimiter.ElapsedTicks;
        _renderCts = new CancellationTokenSource();
        _renderLoop = Task.Run(() => RenderLoop(_renderCts.Token));
        LogMessage("StartRenderLoop");
        _renderSignal.Set();
    }

    private void StopRenderLoop()
    {
        _renderCts?.Cancel();
        try
        {
            _renderLoop?.Wait(500);
        }
        catch
        {
            // ignore shutdown issues
        }
        _renderCts?.Dispose();
        _renderCts = null;
        _renderLoop = null;
        _renderSignal.Reset();
        LogMessage("StopRenderLoop");
    }

    private void RenderLoop(CancellationToken token)
    {
        LogMessage("RenderLoop started");
        while (!token.IsCancellationRequested)
        {
            try
            {
                bool continuous = _freecamActive;
                if (!continuous)
                {
                    _renderSignal.Wait(token);
                    _renderSignal.Reset();
                    RenderFrame();
                    continue;
                }

                float cap = GetEffectiveFpsCap(_viewportFpsCapCached);
                double targetMs = 1000.0 / cap;
                long nowTicks = _frameLimiter.ElapsedTicks;
                double elapsedMs = (nowTicks - _lastLimiterTicks) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs < targetMs)
                {
                    int wait = (int)Math.Max(1.0, targetMs - elapsedMs);
                    _renderSignal.Wait(wait, token);
                }
                _renderSignal.Reset();
                _lastLimiterTicks = _frameLimiter.ElapsedTicks;

                RenderFrame();
            }
            catch (Exception ex)
            {
                LogMessage($"RenderLoop error: {ex}");
                Thread.Sleep(20);
            }
        }
        LogMessage("RenderLoop stopped");
    }

    private void RenderFrame()
    {
        NativeWindow? nativeWindow;
        lock (_nativeWindowLock)
        {
            nativeWindow = _nativeWindow;
        }
        if (nativeWindow == null || !nativeWindow.Exists)
        {
            LogMessage("RenderFrame skipped: no native window");
            return;
        }

        try
        {
            nativeWindow.Context.MakeCurrent();
        }
        catch (Exception ex)
        {
            LogMessage($"RenderFrame MakeCurrent failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        try
        {
            if (_mapLoadPending)
            {
                _mapLoadPending = false;
                if (!string.IsNullOrWhiteSpace(_pendingMapPath))
                {
                    LoadMap(_pendingMapPath);
                    return;
                }
                else
                {
                    DisposeRenderer(disposeWindow: false);
                    return;
                }
            }

            if (!_rendererReady || _renderer == null || _mainFramebuffer == null || _defaultFramebuffer == null)
            {
                if (_renderWidth > 0 && _renderHeight > 0)
                {
                    GL.Viewport(0, 0, _renderWidth, _renderHeight);
                    GL.ClearColor(0.08f, 0.08f, 0.08f, 1f);
                    GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                }
                return;
            }

            if (!_renderLogged)
            {
                _renderLogged = true;
                LogMessage($"RenderFrame active size={_renderWidth}x{_renderHeight}");
            }

            var now = Stopwatch.GetTimestamp();
            if (_lastFrameTimestamp == 0)
            {
                _lastFrameTimestamp = now;
            }
            var delta = (float)Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds;
            _lastFrameTimestamp = now;

            var width = Math.Max(1, _renderWidth);
            var height = Math.Max(1, _renderHeight);
            if (_mainFramebuffer.Width != width || _mainFramebuffer.Height != height)
            {
                _mainFramebuffer.Resize(width, height);
            }

            var updateContext = new Scene.UpdateContext
            {
                Camera = _renderer.Camera,
                TextRenderer = _textRenderer!,
                Timestep = delta,
            };

            UpdateFreecamForFrame();
            ApplyCameraForFrame(width, height);

            _renderer.Update(updateContext);

            var renderContext = new Scene.RenderContext
            {
                Camera = _renderer.Camera,
                Framebuffer = _mainFramebuffer,
                Scene = _renderer.Scene,
                Textures = _renderer.Textures,
            };

            _renderer.Render(renderContext);
            if (_mainFramebuffer != _defaultFramebuffer)
            {
                _renderer.PostprocessRender(_mainFramebuffer, _defaultFramebuffer);
            }
            _textRenderer?.Render(_renderer.Camera);
            try
            {
                nativeWindow.Context.SwapBuffers();
            }
            catch (Exception ex)
            {
                LogMessage($"SwapBuffers failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        finally
        {
            nativeWindow.Context.MakeNoneCurrent();
        }
    }

    private void LoadMap(string mapPath)
    {
        LogMessage($"LoadMap request: {mapPath}");
        DisposeRenderer();
        InitializeNativeWindow();
        if (_nativeWindow == null)
        {
            LogMessage("LoadMap aborted: NativeWindow not available.");
            return;
        }

        _nativeWindow.Context.MakeCurrent();
        try
        {
            if (!EnsureOpenGLBindings())
            {
                return;
            }

            var resolvedMapPath = ResolveMapPath(mapPath, out var mapPackage);
            if (string.IsNullOrWhiteSpace(resolvedMapPath))
            {
                LogMessage("LoadMap aborted: could not resolve map path.");
        DisposeRenderer();
                return;
            }

            _fileLoader = new GameFileLoader(null, mapPath);
            if (mapPackage != null)
            {
                _fileLoader.CurrentPackage = mapPackage;
                _fileLoader.AddPackageToSearch(mapPackage);
                _mapPackage = mapPackage;
                LogMessage($"Using VPK package: {mapPackage.FileName}");
            }

            _rendererContext = new RendererContext(_fileLoader, NullLogger.Instance);
            _renderer = new Renderer(_rendererContext);
            _textRenderer = new TextRenderer(_rendererContext, _renderer.Camera);

            GLEnvironment.Initialize(NullLogger.Instance);
            GLEnvironment.SetDefaultRenderState();

            try
            {
                _renderer.LoadRendererResources();
                _renderer.Postprocess.Load();
                _renderer.Initialize();
                _textRenderer.Load();
                LogMessage("Renderer initialized.");
            }
            catch (Exception ex)
            {
                LogMessage($"Renderer init failed: {ex}");
                DisposeRenderer();
                return;
            }

            _defaultFramebuffer = Framebuffer.GLDefaultFramebuffer;
            _mainFramebuffer = Framebuffer.Prepare(
                nameof(_mainFramebuffer),
                Math.Max(1, _renderWidth),
                Math.Max(1, _renderHeight),
                1,
                new Framebuffer.AttachmentFormat(PixelInternalFormat.Rgba16f, GLPixelFormat.Rgba, PixelType.HalfFloat),
                Framebuffer.DepthAttachmentFormat.Depth32FStencil8);
            var status = _mainFramebuffer.Initialize();
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                _mainFramebuffer.Delete();
                _mainFramebuffer = _defaultFramebuffer;
            }

            _renderer.MainFramebuffer = _mainFramebuffer;

            var worldPath = WorldLoader.GetWorldPathFromMap(resolvedMapPath);
            LogMessage($"Resolved world path: {worldPath}");
            using var worldResource = _fileLoader.LoadFile(worldPath);
            if (worldResource?.DataBlock is not World world)
            {
                LogMessage("LoadMap failed: world resource not found or invalid.");
                DisposeRenderer();
                return;
            }

            _mapHasExternalReferences = worldResource.ExternalReferences != null;
            var worldLoader = new WorldLoader(world, _renderer.Scene, worldResource.ExternalReferences);
            _renderer.SkyboxScene = worldLoader.SkyboxScene;
            _renderer.Skybox2D = worldLoader.Skybox2D;

            PostSceneLoad(worldLoader.DefaultEnabledLayers);
            _rendererReady = true;
            _renderLogged = false;
            LogMessage("LoadMap completed.");
        }
        finally
        {
            _nativeWindow.Context.MakeNoneCurrent();
        }
    }

    private static string? ResolveMapPath(string mapPath, out Package? mapPackage)
    {
        mapPackage = null;

        if (mapPath.EndsWith(".vmap_c", StringComparison.OrdinalIgnoreCase))
        {
            LogMessage($"ResolveMapPath: direct map {mapPath}");
            return mapPath.Replace('\\', '/');
        }

        if (!mapPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
        {
            LogMessage("ResolveMapPath: unsupported file type.");
            return null;
        }

        var package = new Package();
        package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
        package.Read(mapPath);
        mapPackage = package;

        var candidate = FindVmapInPackage(package, Path.GetFileNameWithoutExtension(mapPath));
        LogMessage($"ResolveMapPath: candidate={candidate ?? "null"}");
        return candidate;
    }

    private static string? FindVmapInPackage(Package package, string mapNameHint)
    {
        if (package.Entries == null)
        {
            LogMessage("FindVmapInPackage: entries missing.");
            return null;
        }

        PackageEntry? bestMatch = null;
        var desiredName = $"{mapNameHint}.vmap_c";

        foreach (var entries in package.Entries.Values)
        {
            foreach (var entry in entries)
            {
                var fullPath = entry.GetFullPath();
                if (!fullPath.EndsWith(".vmap_c", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = entry.GetFileName();
                if (fileName.Equals(desiredName, StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage($"FindVmapInPackage: exact match {fullPath}");
                    return NormalizePackagePath(fullPath);
                }

                if (bestMatch == null)
                {
                    bestMatch = entry;
                }
                else if (fullPath.Contains($"{Package.DirectorySeparatorChar}maps{Package.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    bestMatch = entry;
                }
            }
        }

        if (bestMatch != null)
        {
            LogMessage($"FindVmapInPackage: fallback match {bestMatch.GetFullPath()}");
        }
        return bestMatch == null ? null : NormalizePackagePath(bestMatch.GetFullPath());
    }

    private static string NormalizePackagePath(string path)
    {
        return path.Replace(Package.DirectorySeparatorChar, '/');
    }

    private static void LogMessage(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] GLWorldViewport: {message}";
        try
        {
            Console.WriteLine(line);
            if (!_logPathAnnounced)
            {
                _logPathAnnounced = true;
                Console.WriteLine($"[GLWorldViewport] Log file: {LogPath}");
            }
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            if (!_logWriteFailedLogged)
            {
                _logWriteFailedLogged = true;
                Console.WriteLine($"[GLWorldViewport] Log file write failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static string GetLogPath()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "gl_viewport.log");
            using var _ = File.AppendText(path);
            return path;
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "gl_viewport.log");
        }
    }

    private void PostSceneLoad(HashSet<string> defaultEnabledLayers)
    {
        if (_renderer == null)
        {
            return;
        }

        _renderer.Scene.EnableOcclusionCulling = _mapHasExternalReferences;
        _renderer.Scene.Initialize();
        _renderer.SkyboxScene?.Initialize();

        if (_renderer.Scene.FogInfo.CubeFogActive)
        {
            var cubemapTexture = _renderer.Scene.FogInfo.CubemapFog?.CubemapFogTexture;
            if (cubemapTexture != null)
            {
                _renderer.Textures.RemoveAll(t => t.Slot == ReservedTextureSlots.FogCubeTexture);
                _renderer.Textures.Add(new(ReservedTextureSlots.FogCubeTexture, "g_tFogCubeTexture", cubemapTexture));
            }
        }

        defaultEnabledLayers.Remove("Entities");
        defaultEnabledLayers.Remove("Particles");
        _renderer.Scene.SetEnabledLayers(defaultEnabledLayers);
        ApplyModelVisibility();
        _renderer.Scene.UpdateOctrees();

        ResetCameraToScene();
        _renderer.Camera.SetViewportSize(_renderWidth, _renderHeight);
    }

    private void ResetCameraToScene()
    {
        if (_renderer == null)
        {
            return;
        }

        var first = true;
        var bbox = new AABB();
        foreach (var node in _renderer.Scene.AllNodes)
        {
            bbox = first ? node.BoundingBox : bbox.Union(node.BoundingBox);
            first = false;
        }

        if (!first)
        {
            ResetOrbitToBounds(bbox.Min, bbox.Max);
        }
    }

    private void ResetOrbitToBounds(Vector3 min, Vector3 max)
    {
        _target = (min + max) * 0.5f;
        var radius = (max - min).Length() * 0.5f;
        if (radius < 0.1f)
            radius = 0.1f;

        _distance = radius * 2.0f;
        _minDistance = radius * 0.2f;
        _maxDistance = radius * 20f;

        if (_distance < _minDistance)
            _distance = _minDistance;
        if (_distance > _maxDistance)
            _distance = _maxDistance;

        _yaw = DegToRad(45f);
        _pitch = DegToRad(30f);
    }

    private void ApplyModelVisibility()
    {
        if (_renderer == null)
        {
            return;
        }

        foreach (var node in _renderer.Scene.AllNodes)
        {
            if (node is ModelSceneNode || node is SceneAggregate || node is SceneAggregate.Fragment)
            {
                var isEntity = node.EntityData != null
                    || string.Equals(node.LayerName, "Entities", StringComparison.OrdinalIgnoreCase);
                if (isEntity)
                {
                    node.LayerEnabled = _showEntityModels;
                }
            }
        }
    }

    private bool EnsureOpenGLBindings()
    {
        if (_bindingsLoaded)
        {
            return true;
        }

        var provider = new GLFWBindingsContext();
        OpenTK.Graphics.OpenGL.GL.LoadBindings(provider);
        _bindingsLoaded = true;
        return true;
    }

    private void UpdateChildWindowSize()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var width = Math.Max(1, (int)Bounds.Width);
        var height = Math.Max(1, (int)Bounds.Height);
        _renderWidth = width;
        _renderHeight = height;

        lock (_nativeWindowLock)
        {
            if (_nativeWindow != null)
            {
                _nativeWindow.ClientRectangle = new Box2i(0, 0, width, height);
            }
        }

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, width, height, 0x0010);
        RequestNextFrame();
    }

    private void RequestNextFrame()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestNextFrame);
            return;
        }

        float cap = GetEffectiveFpsCap(ViewportFpsCap);
        double targetMs = 1000.0 / cap;
        long nowTicks = _frameLimiter.ElapsedTicks;
        double elapsedMs = (nowTicks - _lastLimiterTicks) * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs >= targetMs)
        {
            _lastLimiterTicks = nowTicks;
            _renderSignal.Set();
            return;
        }

        ScheduleDelayedFrame(targetMs - elapsedMs);
    }

    private void ScheduleDelayedFrame(double delayMs)
    {
        if (_frameLimiterPending)
        {
            return;
        }

        double clampedDelay = Math.Max(1.0, delayMs);
        _frameLimiterTimer ??= new DispatcherTimer();
        _frameLimiterTimer.Stop();
        _frameLimiterTimer.Interval = TimeSpan.FromMilliseconds(clampedDelay);
        _frameLimiterTimer.Tick -= OnFrameLimiterTick;
        _frameLimiterTimer.Tick += OnFrameLimiterTick;
        _frameLimiterPending = true;
        _frameLimiterTimer.Start();
    }

    private void OnFrameLimiterTick(object? sender, EventArgs e)
    {
        _frameLimiterTimer?.Stop();
        _frameLimiterPending = false;
        _lastLimiterTicks = _frameLimiter.ElapsedTicks;
        _renderSignal.Set();
    }

    private static float GetEffectiveFpsCap(float cap)
    {
        return cap <= 0 ? MaxUncappedFps : cap;
    }

    private struct FreecamTransform
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Pitch;
        public float Yaw;
        public float Roll;
        public float Fov;
        public Quaternion Orientation;
    }

    private readonly struct FreecamConfig
    {
        public static readonly FreecamConfig Default = new()
        {
            MouseSensitivity = 0.12f,
            MoveSpeed = 200.0f,
            SprintMultiplier = 2.5f,
            VerticalSpeed = 200.0f,
            SpeedAdjustRate = 1.1f,
            SpeedMinMultiplier = 0.05f,
            SpeedMaxMultiplier = 5.0f,
            RollSpeed = 45.0f,
            RollSmoothing = 0.8f,
            LeanStrength = 1.0f,
            LeanAccelScale = 0.025f,
            LeanVelocityScale = 0.005f,
            LeanMaxAngle = 20.0f,
            LeanHalfTime = 0.18f,
            ClampPitch = false,
            FovMin = 10.0f,
            FovMax = 150.0f,
            FovStep = 2.0f,
            DefaultFov = 90.0f,
            SmoothEnabled = true,
            HalfVec = 0.5f,
            HalfRot = 0.5f,
            HalfFov = 0.5f,
            RotCriticalDamping = false,
            RotDampingRatio = 1.0f
        };

        public float MouseSensitivity { get; init; }
        public float MoveSpeed { get; init; }
        public float SprintMultiplier { get; init; }
        public float VerticalSpeed { get; init; }
        public float SpeedAdjustRate { get; init; }
        public float SpeedMinMultiplier { get; init; }
        public float SpeedMaxMultiplier { get; init; }
        public float RollSpeed { get; init; }
        public float RollSmoothing { get; init; }
        public float LeanStrength { get; init; }
        public float LeanAccelScale { get; init; }
        public float LeanVelocityScale { get; init; }
        public float LeanMaxAngle { get; init; }
        public float LeanHalfTime { get; init; }
        public bool ClampPitch { get; init; }
        public float FovMin { get; init; }
        public float FovMax { get; init; }
        public float FovStep { get; init; }
        public float DefaultFov { get; init; }
        public bool SmoothEnabled { get; init; }
        public float HalfVec { get; init; }
        public float HalfRot { get; init; }
        public float HalfFov { get; init; }
        public bool RotCriticalDamping { get; init; }
        public float RotDampingRatio { get; init; }
    }

    private static bool _bindingsLoaded;

    #region Win32 host
    private static void EnsureClass()
    {
        if (_classRegistered)
        {
            return;
        }

        lock (ClassLock)
        {
            if (_classRegistered)
            {
                return;
            }

            if (_wndProc == null)
            {
                _wndProc = HostWndProc;
                _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
            }

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProcPtr,
                hInstance = GetModuleHandle(null),
                lpszClassName = WndClassName
            };
            _ = RegisterClassEx(ref wc);
            _classRegistered = true;
        }
    }

    private static IntPtr CreateChildWindow(IntPtr parent)
    {
        EnsureClass();
        return CreateWindowEx(
            0,
            WndClassName,
            string.Empty,
            0x40000000 | 0x10000000 | 0x02000000,
            0, 0, 32, 32,
            parent,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);
    }

    private static void RegisterHostWindow(IntPtr hwnd, GLWorldViewport host)
    {
        lock (ClassLock)
        {
            HostMap[hwnd] = new WeakReference<GLWorldViewport>(host);
        }
    }

    private static void UnregisterHostWindow(IntPtr hwnd)
    {
        lock (ClassLock)
        {
            HostMap.Remove(hwnd);
        }
    }

    private static IntPtr HostWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_NCHITTEST = 0x0084;
        const int HTCLIENT = 1;
        const uint WM_MOUSEMOVE = 0x0200;
        const uint WM_LBUTTONDOWN = 0x0201;
        const uint WM_LBUTTONUP = 0x0202;
        const uint WM_RBUTTONDOWN = 0x0204;
        const uint WM_RBUTTONUP = 0x0205;
        const uint WM_MBUTTONDOWN = 0x0207;
        const uint WM_MBUTTONUP = 0x0208;
        const uint WM_XBUTTONDOWN = 0x020B;
        const uint WM_XBUTTONUP = 0x020C;

        if (msg == WM_NCHITTEST)
        {
            return new IntPtr(HTCLIENT);
        }

        GLWorldViewport? host = null;
        lock (ClassLock)
        {
            if (HostMap.TryGetValue(hWnd, out var weak) && weak.TryGetTarget(out var target))
            {
                host = target;
            }
        }

        if (host == null)
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        if (msg == WM_MOUSEMOVE || msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP ||
            msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_MBUTTONDOWN || msg == WM_MBUTTONUP ||
            msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
        {
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            int xButton = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
            host.HandleNativeMouse(msg, x, y, xButton);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void SetWindowAsChild(IntPtr childHwnd, IntPtr parentHwnd)
    {
        if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero)
        {
            return;
        }

        var style = (IntPtr)(WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_DISABLED);
        SetWindowLongPtr(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style);
        style = (IntPtr)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
        SetWindowLongPtr(childHwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, style);
        SetParent(childHwnd, parentHwnd);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx([In] ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, WINDOW_LONG_PTR_INDEX nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    private enum WINDOW_LONG_PTR_INDEX
    {
        GWL_STYLE = -16,
        GWL_EXSTYLE = -20
    }

    [Flags]
    private enum WINDOW_STYLE : uint
    {
        WS_CHILD = 0x40000000,
        WS_DISABLED = 0x08000000
    }

    [Flags]
    private enum WINDOW_EX_STYLE : uint
    {
        WS_EX_NOACTIVATE = 0x08000000
    }
    #endregion
}
