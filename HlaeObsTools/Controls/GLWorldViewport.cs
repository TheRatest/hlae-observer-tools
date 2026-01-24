using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
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
using HlaeObsTools.ViewModels;
using SteamDatabase.ValvePak;
using ValveResourceFormat.IO;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.ResourceTypes;

namespace HlaeObsTools.Controls;

public sealed class GLWorldViewport : NativeControlHost
{
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
    private UserInput? _userInput;
    private bool _rendererReady;
    private bool _mapLoadPending;
    private string? _pendingMapPath;
    private bool _showEntityModels;
    private bool _renderLogged;

    private readonly HashSet<Key> _keysDown = new();
    private Point _lastPointer;
    private Vector2 _mouseDelta;
    private bool _mouseLookActive;
    private bool _mouseLeft;
    private bool _mouseRight;

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
        _mouseLookActive = false;
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
        var point = e.GetCurrentPoint(this);
        _mouseLeft = point.Properties.IsLeftButtonPressed;
        _mouseRight = point.Properties.IsRightButtonPressed;

        if (_mouseRight)
        {
            _mouseLookActive = true;
            _lastPointer = point.Position;
            Focus();
            e.Pointer.Capture(this);
            RequestNextFrame();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetCurrentPoint(this);
        _mouseLeft = point.Properties.IsLeftButtonPressed;
        _mouseRight = point.Properties.IsRightButtonPressed;

        if (!_mouseRight)
        {
            _mouseLookActive = false;
            _mouseDelta = Vector2.Zero;
            e.Pointer.Capture(null);
            RequestNextFrame();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (!_mouseLookActive)
        {
            return;
        }

        var current = e.GetPosition(this);
        var delta = current - _lastPointer;
        _lastPointer = current;
        _mouseDelta += new Vector2((float)delta.X, (float)delta.Y) * ViewportMouseScale;
        RequestNextFrame();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _userInput?.OnMouseWheel((float)e.Delta.Y);
        RequestNextFrame();
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

        var position = new Point(x, y);
        switch (msg)
        {
            case WM_LBUTTONDOWN:
                _mouseLeft = true;
                Focus();
                break;
            case WM_LBUTTONUP:
                _mouseLeft = false;
                break;
            case WM_RBUTTONDOWN:
                _mouseRight = true;
                _mouseLookActive = true;
                _lastPointer = position;
                Focus();
                break;
            case WM_RBUTTONUP:
                _mouseRight = false;
                _mouseLookActive = false;
                _mouseDelta = Vector2.Zero;
                break;
            case WM_MBUTTONDOWN:
            case WM_MBUTTONUP:
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
                break;
            case WM_MOUSEMOVE:
                if (_mouseLookActive)
                {
                    var delta = position - _lastPointer;
                    _lastPointer = position;
                    _mouseDelta += new Vector2((float)delta.X, (float)delta.Y) * ViewportMouseScale;
                }
                break;
        }
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
        _userInput = null;

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
                float cap = _viewportFpsCapCached;
                if (cap > 0)
                {
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
                }
                else
                {
                    _renderSignal.Wait(1, token);
                    _renderSignal.Reset();
                }

                RenderFrame();
                Thread.Sleep(1);
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
                _renderer.Camera.SetViewportSize(width, height);
                _userInput?.Camera.SetViewportSize(width, height);
            }

            var updateContext = new Scene.UpdateContext
            {
                Camera = _renderer.Camera,
                TextRenderer = _textRenderer!,
                Timestep = delta,
            };

            var tracked = BuildTrackedKeys();
            _userInput?.Tick(delta, tracked, _mouseDelta, _renderer.Camera);
            _mouseDelta = Vector2.Zero;

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

    private TrackedKeys BuildTrackedKeys()
    {
        var keys = TrackedKeys.None;
        if (_mouseLeft)
        {
            keys |= TrackedKeys.MouseLeft;
        }
        if (_mouseRight)
        {
            keys |= TrackedKeys.MouseRight;
        }

        if (IsKeyDown(Key.LeftShift) || IsKeyDown(Key.RightShift))
        {
            keys |= TrackedKeys.Shift;
        }
        if (IsKeyDown(Key.LeftAlt) || IsKeyDown(Key.RightAlt))
        {
            keys |= TrackedKeys.Alt;
        }
        if (IsKeyDown(Key.LeftCtrl) || IsKeyDown(Key.RightCtrl))
        {
            keys |= TrackedKeys.Control;
        }

        if (IsKeyDown(Key.W) || IsKeyDown(Key.Up))
        {
            keys |= TrackedKeys.Forward;
        }
        if (IsKeyDown(Key.S) || IsKeyDown(Key.Down))
        {
            keys |= TrackedKeys.Back;
        }
        if (IsKeyDown(Key.A) || IsKeyDown(Key.Left))
        {
            keys |= TrackedKeys.Left;
        }
        if (IsKeyDown(Key.D) || IsKeyDown(Key.Right))
        {
            keys |= TrackedKeys.Right;
        }
        if (IsKeyDown(Key.Space))
        {
            keys |= TrackedKeys.Up;
        }
        if (IsKeyDown(Key.LeftCtrl) || IsKeyDown(Key.RightCtrl))
        {
            keys |= TrackedKeys.Down;
        }

        return keys;
    }

    private bool IsKeyDown(Key key) => _keysDown.Contains(key);

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
            _userInput = new UserInput(_renderer);
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
        SyncUserInputCamera();
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
            var offset = Math.Max(bbox.Max.X, bbox.Max.Z) + 1f * 1.5f;
            offset = Math.Clamp(offset, 0f, 2000f);
            var location = new Vector3(offset, 0, offset);
            _renderer.Camera.SetLocation(location);
            _renderer.Camera.LookAt(bbox.Center);
        }
    }

    private void SyncUserInputCamera()
    {
        if (_renderer == null || _userInput == null)
        {
            return;
        }

        var width = Math.Max(1, _renderWidth);
        var height = Math.Max(1, _renderHeight);
        _userInput.Camera.SetViewportSize(width, height);

        var camera = _renderer.Camera;
        _userInput.Camera.SetLocationPitchYaw(camera.Location, camera.Pitch, camera.Yaw);
        _userInput.Camera.RecalculateDirectionVectors();
        _userInput.ForceUpdate = true;
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

        float cap = ViewportFpsCap;
        if (cap <= 0)
        {
            _lastLimiterTicks = _frameLimiter.ElapsedTicks;
            _renderSignal.Set();
            return;
        }

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
