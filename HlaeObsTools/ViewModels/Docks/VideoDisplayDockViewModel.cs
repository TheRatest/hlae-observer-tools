using Avalonia.Media.Imaging;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Video;
using HlaeObsTools.Services.Video.RTP;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.Services.Input;
using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.ViewModels.Hud;
using Avalonia.Media;
using System.Linq;
using HlaeObsTools.Views;
using System.Threading.Tasks;
using System.Windows.Input;

namespace HlaeObsTools.ViewModels.Docks;

/// <summary>
/// Video display dock view model
/// </summary>
public class VideoDisplayDockViewModel : Tool, IDisposable
{
    private IVideoSource? _videoSource;
    private WriteableBitmap? _currentFrame;
    private bool _isStreaming;
    private string _statusText = "Not Connected";
    private double _frameRate;
    private DateTime _lastFrameTime;
    private int _frameCount;
    private VideoFrame? _pendingFrame;
    private bool _updateScheduled;
    private bool _useRtpSwapchain;
    private RtpSwapchainViewer? _rtpViewer;
    private DateTime _lastLatencyLog = DateTime.MinValue;
    private IntPtr _rtpParentHwnd;
    private double _rtpFrameAspect;

    // Double-buffering: alternate between two bitmaps to force reference change
    private WriteableBitmap? _bitmap0;
    private WriteableBitmap? _bitmap1;
    private bool _useFirstBitmap;

    // Freecam state
    private bool _isFreecamActive;
    private HlaeWebSocketClient? _webSocketClient;
    private HlaeInputSender? _inputSender;
    private HudSettings? _hudSettings;
    private FreecamSettings? _freecamSettings;
    private bool _useD3DHost;
    private double _freecamSpeed;
    private RtpReceiverConfig _rtpConfig = new();
    private HlaeWebSocketClient? _speedWebSocketClient;
    private readonly IReadOnlyList<double> _speedTicks;
    private double _speedMultiplier = 1.0;
    private GsiServer? _gsiServer;
    private readonly HudTeamViewModel _teamCt = new("CT");
    private readonly HudTeamViewModel _teamT = new("T");
    private HudPlayerCardViewModel? _focusedHudPlayer;
    private string _roundTimerText = "--:--";
    private string _roundPhase = "LIVE";
    private int _roundNumber;
    private string _mapName = string.Empty;
    private readonly Dictionary<string, HudWeaponViewModel> _weaponCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HudPlayerCardViewModel> _hudPlayerCache = new(StringComparer.Ordinal);
    private readonly ObservableCollection<HudKillfeedEntryViewModel> _killfeedEntries = new();
    private readonly DispatcherTimer _killfeedTimer;
    private HudOverlayWindow? _hudOverlayWindow;
    private bool _isAwaitingAttachTarget;
    private int _pendingAttachSourceObserverSlot;
    private int _pendingAttachPresetIndex = -1;
    private string _hudPromptText = string.Empty;
    private static readonly HashSet<string> PrimaryWeaponTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Machine Gun",
        "Rifle",
        "Shotgun",
        "SniperRifle",
        "Submachine Gun"
    };

    private static readonly Dictionary<string, int> GrenadeOrder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["molotov"] = 0,
        ["incgrenade"] = 1,
        ["decoy"] = 2,
        ["smokegrenade"] = 3,
        ["flashbang"] = 4,
        ["hegrenade"] = 5
    };
    private const int DefaultPlayerActionCount = 5;
    private const string AttachActionId = "player_action_attach";
    private const double KillfeedLifetimeSeconds = 8.0;
    private const double KillfeedFadeSeconds = 1.0;

    public ICommand CancelHudPromptCommand { get; }

    public string HudPromptText
    {
        get => _hudPromptText;
        private set
        {
            if (_hudPromptText == value) return;
            _hudPromptText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasHudPrompt));
        }
    }

    public bool HasHudPrompt => !string.IsNullOrWhiteSpace(HudPromptText);

    public bool ShowNoSignal => !_isStreaming && !_useD3DHost;
    public bool CanStart => !_isStreaming && !_useD3DHost;
    public bool CanStop => _isStreaming && !_useD3DHost;

    public WriteableBitmap? CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            _currentFrame = value;
            OnPropertyChanged();
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            _isStreaming = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowNoSignal));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            OnPropertyChanged();
        }
    }

    public double FrameRate
    {
        get => _frameRate;
        private set
        {
            _frameRate = value;
            OnPropertyChanged();
        }
    }

    public bool IsFreecamActive
    {
        get => _isFreecamActive;
        private set
        {
            _isFreecamActive = value;
            OnPropertyChanged();
        }
    }

    public event EventHandler<bool>? FreecamStateChanged;
    public event EventHandler<bool>? FreecamSprintStateChanged;
    public event EventHandler<IntPtr>? RtpViewerWindowChanged;
    public event EventHandler? FreecamInputLockRequested;
    public event EventHandler? FreecamInputReleaseRequested;
    public double RtpFrameAspect
    {
        get => _rtpFrameAspect;
        private set
        {
            if (Math.Abs(_rtpFrameAspect - value) < 0.0001)
                return;
            _rtpFrameAspect = value;
            OnPropertyChanged();
        }
    }

    public VideoDisplayDockViewModel()
    {
        CanClose = false;
        CanFloat = true;
        CanPin = true;
        UseRtpSwapchain = true;
        _speedTicks = BuildTicks();
        _teamCt.Players.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHudData));
        _teamT.Players.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHudData));
        CancelHudPromptCommand = new Relay(_ => CancelPendingAttachTargetSelection());
        _killfeedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _killfeedTimer.Tick += OnKillfeedTimerTick;
        _killfeedTimer.Start();
    }

    /// <summary>
    /// Set WebSocket client for sending commands to HLAE
    /// </summary>
    public void SetWebSocketClient(HlaeWebSocketClient client)
    {
        _webSocketClient = client;
        _speedWebSocketClient = client;
        _speedWebSocketClient.MessageReceived -= OnWebSocketMessage;
        _speedWebSocketClient.MessageReceived += OnWebSocketMessage;
    }

    /// <summary>
    /// Set input sender for freecam control
    /// </summary>
    public void SetInputSender(HlaeInputSender sender)
    {
        _inputSender = sender;
    }

    public bool AnalogKeyboardEnabled => _freecamSettings?.AnalogKeyboardEnabled ?? false;

    public bool TryGetAnalogSprint(out double sprintInput)
    {
        sprintInput = 0.0;
        if (_freecamSettings?.AnalogKeyboardEnabled != true || _inputSender == null)
            return false;

        if (!_inputSender.TryGetAnalogState(out var enabled, out _, out _, out _, out var rx) || !enabled)
            return false;

        sprintInput = Math.Clamp(rx, 0.0f, 1.0f);
        return true;
    }

    /// <summary>
    /// Configure HUD settings.
    /// </summary>
    public void SetHudSettings(HudSettings settings)
    {
        if (_hudSettings != null)
        {
            _hudSettings.PropertyChanged -= OnHudSettingsChanged;
        }

        _hudSettings = settings;
        _hudSettings.PropertyChanged += OnHudSettingsChanged;

        OnPropertyChanged(nameof(IsHudEnabled));
        OnPropertyChanged(nameof(ShowNativeHud));
        OnPropertyChanged(nameof(ShowKillfeed));
    }

    /// <summary>
    /// Configure freecam settings for sprint multiplier, etc.
    /// </summary>
    public void SetFreecamSettings(FreecamSettings settings)
    {
        _freecamSettings = settings;
    }

    /// <summary>
    /// Wire GSI updates for HUD overlay rendering.
    /// </summary>
    public void SetGsiServer(GsiServer server)
    {
        if (_gsiServer != null)
        {
            _gsiServer.GameStateUpdated -= OnHudGameStateUpdated;
        }

        _gsiServer = server;
        _gsiServer.GameStateUpdated += OnHudGameStateUpdated;
    }

    public void SetRtpConfig(RtpReceiverConfig config)
    {
        _rtpConfig = config;
    }

    public void SetRtpParentWindowHandle(IntPtr hwnd)
    {
        _rtpParentHwnd = hwnd;
    }

    public void RequestFreecamInputLock()
    {
        FreecamInputLockRequested?.Invoke(this, EventArgs.Empty);
        if (_hudOverlayWindow != null && _hudOverlayWindow.IsVisible)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _hudOverlayWindow.Activate();
                _hudOverlayWindow.Focus();
            }, DispatcherPriority.Background);
        }
    }

    public void SetSprintModifierState(bool isPressed)
    {
        FreecamSprintStateChanged?.Invoke(this, isPressed);
    }

    public void RequestFreecamInputRelease()
    {
        FreecamInputReleaseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateRtpViewerBounds(int x, int y, int width, int height)
    {
        _rtpViewer?.SetHostedBounds(x, y, width, height);
    }

    public bool IsHudEnabled => _hudSettings?.IsHudEnabled ?? false;
    public bool ShowNativeHud => IsHudEnabled;

    public HudTeamViewModel TeamCt => _teamCt;
    public HudTeamViewModel TeamT => _teamT;
    public ObservableCollection<HudKillfeedEntryViewModel> KillfeedEntries => _killfeedEntries;
    public bool ShowKillfeed => _hudSettings?.ShowKillfeed ?? true;

    public HudPlayerCardViewModel? FocusedHudPlayer
    {
        get => _focusedHudPlayer;
        private set
        {
            _focusedHudPlayer = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasFocusedHudPlayer));
        }
    }

    public bool HasFocusedHudPlayer => _focusedHudPlayer != null;
    public bool HasHudData => _teamCt.HasPlayers || _teamT.HasPlayers;

    public string RoundTimerText
    {
        get => _roundTimerText;
        private set
        {
            _roundTimerText = value;
            OnPropertyChanged();
        }
    }

    public string RoundPhase
    {
        get => _roundPhase;
        private set
        {
            _roundPhase = value;
            OnPropertyChanged();
        }
    }

    public int RoundNumber
    {
        get => _roundNumber;
        private set
        {
            _roundNumber = value;
            OnPropertyChanged();
        }
    }

    public string MapName
    {
        get => _mapName;
        private set
        {
            _mapName = value;
            OnPropertyChanged();
        }
    }

    public bool UseD3DHost
    {
        get => _useD3DHost;
        set
        {
            if (_useD3DHost == value) return;
            _useD3DHost = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowNoSignal));
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    public bool UseRtpSwapchain
    {
        get => _useRtpSwapchain;
        set
        {
            if (_useRtpSwapchain == value) return;
            _useRtpSwapchain = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Activate freecam (called when right mouse button pressed)
    /// </summary>
    public async void ActivateFreecam()
    {
        if (_webSocketClient == null)
            return;

        // Send freecam enable command to HLAE
        await _webSocketClient.SendCommandAsync("freecam_enable");

        IsFreecamActive = true;
        FreecamStateChanged?.Invoke(this, true);
        Console.WriteLine("Freecam activated");
    }

    /// <summary>
    /// Deactivate freecam (called when right mouse button released)
    /// </summary>
    public async void DeactivateFreecam()
    {
        if (!IsFreecamActive || _webSocketClient == null)
            return;

        // Send freecam disable command to HLAE
        await _webSocketClient.SendCommandAsync("freecam_disable");

        IsFreecamActive = false;
        FreecamStateChanged?.Invoke(this, false);

        Console.WriteLine("Freecam deactivated");
    }

    public async void EnableFreecamHold()
    {
        if (_webSocketClient == null)
            return;

        var mode = _freecamSettings?.HoldMovementFollowsCamera != false ? "camera" : "world";
        await _webSocketClient.SendCommandAsync("freecam_hold", new { mode });

        Console.WriteLine("Freecam input hold requested");
    }

    /// <summary>
    /// Refresh spectator bindings (keys 1-0 to switch players)
    /// </summary>
    public async void RefreshSpectatorBindings()
    {
        if (_webSocketClient == null)
            return;

        // Send refresh_binds command to HLAE
        await _webSocketClient.SendCommandAsync("refresh_binds");

        Console.WriteLine("Spectator bindings refresh requested");
    }

    public Task PauseDemoAsync()
    {
        if (_webSocketClient == null)
            return Task.CompletedTask;

        return _webSocketClient.SendExecCommandAsync("demo_pause");
    }

    public Task ResumeDemoAsync()
    {
        if (_webSocketClient == null)
            return Task.CompletedTask;

        return _webSocketClient.SendExecCommandAsync("demo_resume");
    }

    public void StartStream(RtpReceiverConfig? config = null)
    {
        StopStream();

        try
        {
            StartRtpInternal(config);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            IsStreaming = false;
            Console.WriteLine($"Failed to start video source: {ex}");
        }
    }

    public void StopStream()
    {
        if (_videoSource != null)
        {
            if (_videoSource is RtpVideoReceiver receiver)
            {
                receiver.FrameReceived -= OnFrameReceived;
            }

            _videoSource.Stop();
            _videoSource.Dispose();
            _videoSource = null;
        }

        _rtpViewer?.Stop();
        _rtpViewer?.Dispose();
        _rtpViewer = null;
        RtpViewerWindowChanged?.Invoke(this, IntPtr.Zero);

        IsStreaming = false;
        StatusText = "Not Connected";
        CurrentFrame = null;
        _bitmap0 = null;
        _bitmap1 = null;
        FrameRate = 0;
    }

    private void OnFrameReceived(object? sender, VideoFrame frame)
    {
        // Calculate frame rate
        _frameCount++;
        var now = DateTime.Now;
        var elapsed = (now - _lastFrameTime).TotalSeconds;
        if (elapsed >= 1.0)
        {
            FrameRate = _frameCount / elapsed;
            _frameCount = 0;
            _lastFrameTime = now;
        }

        if (_useRtpSwapchain && _rtpViewer != null)
        {
            UpdateRtpFrameAspect(frame.Width, frame.Height);
            _rtpViewer.PresentFrame(frame);
            return;
        }

        // Always store the latest frame (drop old pending frames for low latency)
        _pendingFrame = frame;

        if (!_updateScheduled)
        {
            _updateScheduled = true;

            // Use Send instead of InvokeAsync for lower latency
            // This processes the frame immediately on the UI thread
            Dispatcher.UIThread.Post(() =>
            {
                _updateScheduled = false;
                if (_pendingFrame != null)
                {
                    UpdateBitmap(_pendingFrame);
                }
            }, DispatcherPriority.MaxValue);
        }
    }

    private void UpdateRtpFrameAspect(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return;

        double aspect = width / (double)height;
        if (Math.Abs(aspect - _rtpFrameAspect) < 0.0001)
            return;

        Dispatcher.UIThread.Post(() => RtpFrameAspect = aspect, DispatcherPriority.Background);
    }

    private void UpdateBitmap(VideoFrame frame)
    {
        try
        {
            // Double-buffering: alternate between two bitmaps to provide different reference each frame
            // This forces Avalonia's Image control to detect the change while avoiding GC pressure
            // Only 2 bitmaps total vs 60+ per second

            var needsRecreate = _bitmap0 == null || _bitmap1 == null ||
                                _bitmap0.PixelSize.Width != frame.Width ||
                                _bitmap0.PixelSize.Height != frame.Height;

            if (needsRecreate)
            {
                // Create both bitmaps with the same dimensions
                _bitmap0 = new WriteableBitmap(
                    new PixelSize(frame.Width, frame.Height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888);

                _bitmap1 = new WriteableBitmap(
                    new PixelSize(frame.Width, frame.Height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888);

                _useFirstBitmap = true;
            }

            // Alternate between the two bitmaps
            var targetBitmap = _useFirstBitmap ? _bitmap0! : _bitmap1!;
            _useFirstBitmap = !_useFirstBitmap;

            // Copy frame data to the target bitmap
            using (var buffer = targetBitmap.Lock())
            {
                unsafe
                {
                    var dest = (byte*)buffer.Address;
                    var destStride = buffer.RowBytes;

                    // Copy line by line
                    for (int y = 0; y < frame.Height; y++)
                    {
                        int srcOffset = y * frame.Stride;
                        int destOffset = y * destStride;

                        Marshal.Copy(
                            frame.Data,
                            srcOffset,
                            (IntPtr)(dest + destOffset),
                            Math.Min(frame.Stride, destStride));
                    }
                }
            }

            // Set the newly updated bitmap - different reference from last frame
            CurrentFrame = targetBitmap;

            if (frame.SourceTimestampUs > 0)
            {
                const long unixEpochTicks = 621355968000000000L; // DateTime ticks at Unix epoch
                var nowUs = (DateTime.UtcNow.Ticks - unixEpochTicks) / 10; // microseconds since Unix epoch
                var presentLatencyMs = Math.Max(0, (nowUs - frame.SourceTimestampUs) / 1000.0);
                var captureToReceiveMs = frame.ReceivedTimestampUs > 0
                    ? Math.Max(0, (frame.ReceivedTimestampUs - frame.SourceTimestampUs) / 1000.0)
                    : double.NaN;

                var now = DateTime.UtcNow;
                if ((now - _lastLatencyLog).TotalSeconds >= 1.0)
                {
                    Console.WriteLine($"Present latency: {presentLatencyMs:F2} ms (capture->receive: {captureToReceiveMs:F2} ms)");
                    _lastLatencyLog = now;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating bitmap: {ex.Message}");
        }
    }

    private void OnHudGameStateUpdated(object? sender, GsiGameState state)
    {
        Dispatcher.UIThread.Post(() => ApplyHudState(state));
    }

    private void ApplyHudState(GsiGameState state)
    {
        TeamCt.Name = state.TeamCt?.Name ?? "CT";
        TeamCt.Score = state.TeamCt?.Score ?? 0;
        TeamCt.TimeoutsRemaining = state.TeamCt?.TimeoutsRemaining ?? 0;

        TeamT.Name = state.TeamT?.Name ?? "T";
        TeamT.Score = state.TeamT?.Score ?? 0;
        TeamT.TimeoutsRemaining = state.TeamT?.TimeoutsRemaining ?? 0;

        RoundNumber = state.RoundNumber;
        RoundPhase = (state.RoundPhase ?? "LIVE").ToUpperInvariant();
        RoundTimerText = FormatPhaseTimer(state.PhaseEndsIn);
        MapName = state.MapName ?? string.Empty;

        var focusedSteamId = state.FocusedPlayerSteamId;
        TeamCt.SetPlayers(BuildTeamPlayers(state.Players, "CT", focusedSteamId));
        TeamT.SetPlayers(BuildTeamPlayers(state.Players, "T", focusedSteamId));

        FocusedHudPlayer = FindFocusedPlayer(focusedSteamId);

        OnPropertyChanged(nameof(HasHudData));
    }

    private static IEnumerable<HudPlayerActionOption> CreateDefaultPlayerActions()
    {
        var options = new List<HudPlayerActionOption>
        {
            new HudPlayerActionOption(AttachActionId, "Attach", 0, hasSubMenu: true)
        };

        options.AddRange(
            Enumerable.Range(1, DefaultPlayerActionCount - 1)
                .Select(i => new HudPlayerActionOption($"player_action_{i + 1}", $"Action {i + 1}", i))
        );

        return options;
    }

    private void ConfigurePlayerRadialActions(HudPlayerCardViewModel player)
    {
        if (player.RadialActions.Count == 0)
        {
            player.SetRadialActions(CreateDefaultPlayerActions());
        }
        else
        {
            player.SetRadialActions(player.RadialActions); // Ensure accent propagation
        }

        player.PlayerActionRequested -= OnPlayerActionRequested;
        player.PlayerActionRequested += OnPlayerActionRequested;

        player.AttachTargetSelected -= OnAttachTargetSelected;
        player.AttachTargetSelected += OnAttachTargetSelected;

        player.IsAttachTargetSelectionActive = _isAwaitingAttachTarget;
    }

    private IEnumerable<HudPlayerCardViewModel> BuildTeamPlayers(IEnumerable<GsiPlayer> players, string team, string? focusedSteamId)
    {
        var ordered = players
            .Where(p => string.Equals(p.Team, team, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Slot switch
            {
                < 0 => int.MaxValue,
                _ => p.Slot
            })
            .ToList();

        var result = new List<HudPlayerCardViewModel>();
        foreach (var player in ordered)
        {
            var isFocused = !string.IsNullOrWhiteSpace(focusedSteamId) &&
                           string.Equals(player.SteamId, focusedSteamId, StringComparison.Ordinal);
            result.Add(BuildHudPlayer(player, isFocused));
        }

        return result;
    }

    private HudPlayerCardViewModel BuildHudPlayer(GsiPlayer player, bool isFocused = false)
    {
        var accent = CreateAccent(player.Team);
        var background = CreateCardBackground(player.Team);

        var weaponVms = player.Weapons?
            .Select(w => BuildWeapon(player.SteamId, w, accent))
            .ToList() ?? new List<HudWeaponViewModel>();

        var primary = weaponVms.FirstOrDefault(w => w.IsPrimary);
        var secondary = weaponVms.FirstOrDefault(w => w.IsSecondary);
        var knife = weaponVms.FirstOrDefault(w => w.IsKnife);
        var bomb = weaponVms.FirstOrDefault(w => w.IsBomb);
        var active = weaponVms.FirstOrDefault(w => w.IsActive) ?? primary ?? secondary ?? knife ?? weaponVms.FirstOrDefault();
        var grenades = BuildGrenadeList(weaponVms.Where(w => w.IsGrenade));

        if (!_hudPlayerCache.TryGetValue(player.SteamId, out var vm))
        {
            vm = new HudPlayerCardViewModel(player.SteamId);
            _hudPlayerCache[player.SteamId] = vm;
        }

        vm.Update(
            player.Name,
            player.Team,
            player.Slot,
            player.Health,
            player.Armor,
            player.HasHelmet,
            player.HasDefuseKit,
            player.IsAlive,
            primary,
            secondary,
            knife,
            bomb,
            grenades,
            active,
            accent,
            background,
            isFocused);

        vm.UseAltBindings = _hudSettings?.UseAltPlayerBinds ?? false;

        ConfigurePlayerRadialActions(vm);

        return vm;
    }

    private HudWeaponViewModel BuildWeapon(string steamId, GsiWeapon weapon, IBrush accent)
    {
        var normalizedName = NormalizeWeaponName(weapon.Name);
        var icon = GetWeaponIconPath(normalizedName);

        var isGrenade = string.Equals(weapon.Type, "Grenade", StringComparison.OrdinalIgnoreCase) ||
                        normalizedName.Contains("grenade", StringComparison.OrdinalIgnoreCase);

        var isBomb = normalizedName.Contains("c4", StringComparison.OrdinalIgnoreCase);
        var isKnife = string.Equals(weapon.Type, "Knife", StringComparison.OrdinalIgnoreCase) || normalizedName.Contains("knife", StringComparison.OrdinalIgnoreCase);
        var isTaser = normalizedName.Contains("taser", StringComparison.OrdinalIgnoreCase);
        var isPrimary = PrimaryWeaponTypes.Contains(weapon.Type);
        var isSecondary = string.Equals(weapon.Type, "Pistol", StringComparison.OrdinalIgnoreCase);
        var isActive = string.Equals(weapon.State, "active", StringComparison.OrdinalIgnoreCase);

        var cacheKey = $"{steamId}:{normalizedName}";
        if (!_weaponCache.TryGetValue(cacheKey, out var vm))
        {
            vm = new HudWeaponViewModel();
            _weaponCache[cacheKey] = vm;
        }

        vm.Update(
            weapon.Name,
            icon,
            isActive,
            isPrimary,
            isSecondary,
            isGrenade,
            isBomb,
            isKnife,
            isTaser,
            weapon.AmmoClip,
            weapon.AmmoReserve,
            accent);

        return vm;
    }

    private IReadOnlyList<HudWeaponViewModel> BuildGrenadeList(IEnumerable<HudWeaponViewModel> grenades)
    {
        var list = new List<HudWeaponViewModel>();
        foreach (var grenade in grenades)
        {
            var count = Math.Max(1, grenade.AmmoReserve > 0 ? grenade.AmmoReserve : 1);
            for (int i = 0; i < count; i++)
            {
                list.Add(grenade);
            }
        }

        return list
            .OrderBy(g => GrenadeOrder.TryGetValue(NormalizeWeaponName(g.Name), out var idx) ? idx : 99)
            .ThenBy(g => g.Name)
            .ToList();
    }

    private HudPlayerCardViewModel? FindFocusedPlayer(string? steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
            return null;

        return TeamCt.Players.FirstOrDefault(p => string.Equals(p.SteamId, steamId, StringComparison.Ordinal))
               ?? TeamT.Players.FirstOrDefault(p => string.Equals(p.SteamId, steamId, StringComparison.Ordinal));
    }

    private static string FormatPhaseTimer(double? seconds)
    {
        if (seconds == null || double.IsNaN(seconds.Value))
            return "--:--";

        var clamped = Math.Max(0, seconds.Value);
        var span = TimeSpan.FromSeconds(clamped);
        return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}";
    }

    private static string NormalizeWeaponName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "knife";

        var normalized = name.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            ? name.Substring("weapon_".Length)
            : name;

        return normalized.ToLowerInvariant();
    }

    private static string GetWeaponIconPath(string weaponName)
    {
        var sanitized = NormalizeWeaponName(weaponName);
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "knife";

        return $"avares://HlaeObsTools/Assets/hud/weapons/{sanitized}.svg";
    }

    private static SolidColorBrush CreateAccent(string team)
    {
        return string.Equals(team, "CT", StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.Parse("#6EB4FF"))
            : new SolidColorBrush(Color.Parse("#FF9B4A"));
    }

    private static SolidColorBrush CreateTeamBrush(int teamId)
    {
        return teamId switch
        {
            3 => new SolidColorBrush(Color.Parse("#6EB4FF")),
            2 => new SolidColorBrush(Color.Parse("#FF9B4A")),
            _ => new SolidColorBrush(Color.Parse("#E6E6E6"))
        };
    }

    private static SolidColorBrush CreateCardBackground(string team)
    {
        return string.Equals(team, "CT", StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.Parse("#192434"))
            : new SolidColorBrush(Color.Parse("#2E1E15"));
    }

    /// <summary>
    /// Show the HUD overlay window (called when using D3DHost mode)
    /// </summary>
    public void ShowHudOverlay()
    {
        if (_hudOverlayWindow == null)
        {
            _hudOverlayWindow = new HudOverlayWindow
            {
                DataContext = this
            };

            // Subscribe to canvas size changes for speed scale updates
            var canvas = _hudOverlayWindow.GetSpeedScaleCanvas();
            if (canvas != null)
            {
                canvas.SizeChanged += (_, _) => OnPropertyChanged(nameof(FreecamSpeed));
            }

            // Subscribe to mouse events for freecam control
            _hudOverlayWindow.RightButtonDown += OnOverlayRightButtonDown;
            _hudOverlayWindow.RightButtonUp += OnOverlayRightButtonUp;

            // Subscribe to keyboard events for shift key detection
            _hudOverlayWindow.ShiftKeyChanged += OnOverlayShiftKeyChanged;
        }

        if (!_hudOverlayWindow.IsVisible)
        {
            // Show with main window as owner so the overlay is only topmost relative to it
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                _hudOverlayWindow.Show(desktop.MainWindow);
            }
            else
            {
                _hudOverlayWindow.Show();
            }
        }
    }

    private void OnOverlayRightButtonDown(object? sender, EventArgs e)
    {
        RaiseOverlayRightButtonDown();
    }

    private void OnOverlayRightButtonUp(object? sender, EventArgs e)
    {
        RaiseOverlayRightButtonUp();
    }

    /// <summary>
    /// Event raised when right button is pressed on the overlay
    /// </summary>
    public event EventHandler? OverlayRightButtonDown;

    /// <summary>
    /// Event raised when right button is released on the overlay
    /// </summary>
    public event EventHandler? OverlayRightButtonUp;

    /// <summary>
    /// Event raised when shift key state changes on the overlay
    /// </summary>
    public event EventHandler<bool>? OverlayShiftKeyChanged;

    private void RaiseOverlayRightButtonDown()
    {
        OverlayRightButtonDown?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseOverlayRightButtonUp()
    {
        OverlayRightButtonUp?.Invoke(this, EventArgs.Empty);
    }

    private void OnOverlayShiftKeyChanged(object? sender, bool isPressed)
    {
        OverlayShiftKeyChanged?.Invoke(this, isPressed);
    }

    /// <summary>
    /// Hide the HUD overlay window
    /// </summary>
    public void HideHudOverlay()
    {
        if (_hudOverlayWindow != null && _hudOverlayWindow.IsVisible)
        {
            _hudOverlayWindow.Hide();
        }
    }

    /// <summary>
    /// Update the HUD overlay window position and size to match the shared texture bounds
    /// </summary>
    public void UpdateHudOverlayBounds(PixelPoint position, PixelSize size)
    {
        _hudOverlayWindow?.UpdatePositionAndSize(position, size);
    }

    /// <summary>
    /// Get the SpeedScaleCanvas from the overlay window (for rendering speed scale in D3DHost mode)
    /// </summary>
    public Avalonia.Controls.Canvas? GetOverlaySpeedScaleCanvas()
    {
        return _hudOverlayWindow?.GetSpeedScaleCanvas();
    }

    public void Dispose()
    {
        if (_speedWebSocketClient != null)
        {
            _speedWebSocketClient.MessageReceived -= OnWebSocketMessage;
        }
        if (_gsiServer != null)
        {
            _gsiServer.GameStateUpdated -= OnHudGameStateUpdated;
        }
        _killfeedTimer.Tick -= OnKillfeedTimerTick;
        _killfeedTimer.Stop();
        _hudOverlayWindow?.Close();
        _hudOverlayWindow = null;
        StopStream();
    }

    private void OnHudSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudSettings.IsHudEnabled))
        {
            OnPropertyChanged(nameof(IsHudEnabled));
            OnPropertyChanged(nameof(ShowNativeHud));
        }
        else if (e.PropertyName == nameof(HudSettings.ShowKillfeed))
        {
            OnPropertyChanged(nameof(ShowKillfeed));
        }
        else if (e.PropertyName == nameof(HudSettings.UseAltPlayerBinds))
        {
            var useAlt = _hudSettings?.UseAltPlayerBinds ?? false;
            foreach (var vm in _hudPlayerCache.Values)
            {
                vm.UseAltBindings = useAlt;
            }
            foreach (var entry in _killfeedEntries)
            {
                entry.UseAltBindings = useAlt;
            }
        }
        else if (e.PropertyName == nameof(HudSettings.ShowKillfeedAttackerSlot))
        {
            var show = _hudSettings?.ShowKillfeedAttackerSlot ?? true;
            foreach (var entry in _killfeedEntries)
            {
                entry.ShowAttackerSlot = show;
            }
        }
    }

    private void StartRtpInternal(RtpReceiverConfig? config = null)
    {
        if (_useRtpSwapchain)
        {
            _rtpViewer?.Stop();
            _rtpViewer?.Dispose();
            _rtpViewer = new RtpSwapchainViewer(_rtpParentHwnd);
            _rtpViewer.Start();
            if (_rtpViewer.IsRunning && _rtpViewer.Hwnd != IntPtr.Zero)
            {
                RtpViewerWindowChanged?.Invoke(this, _rtpViewer.Hwnd);
            }
            else
            {
                RtpViewerWindowChanged?.Invoke(this, IntPtr.Zero);
            }
        }

        var receiver = new RtpVideoReceiver(config ?? _rtpConfig);
        receiver.FrameReceived += OnFrameReceived;
        receiver.Start();

        _videoSource = receiver;
        IsStreaming = true;
        var activeConfig = config ?? _rtpConfig;
        StatusText = $"Connected - {activeConfig.Address}:{activeConfig.Port}";
        _lastFrameTime = DateTime.Now;
        _frameCount = 0;
    }

    public double FreecamSpeed
    {
        get => _freecamSpeed;
        private set
        {
            var clamped = Math.Clamp(value, SpeedMin, SpeedMax);
            if (Math.Abs(clamped - _freecamSpeed) < 0.001) return;
            _freecamSpeed = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FreecamSpeedText));
        }
    }

    public string FreecamSpeedText => ((int)Math.Round(FreecamSpeed)).ToString();
    public double SpeedMin => 10.0;
    public double SpeedMax => 1000.0;
    public IReadOnlyList<double> SpeedTicks => _speedTicks;
    public double SprintMultiplier => _freecamSettings?.SprintMultiplier ?? 2.5;

    private IReadOnlyList<double> BuildTicks()
    {
        var ticks = new List<double>();
        const int tickCount = 12; // includes min/max
        double step = (SpeedMax - SpeedMin) / (tickCount - 1);
        for (int i = 0; i < tickCount; i++)
        {
            ticks.Add(SpeedMax - i * step);
        }
        return ticks;
    }

    private void OnPlayerActionRequested(object? sender, HudPlayerActionRequestedEventArgs e)
    {
        HandlePlayerActionRequest(e.Player, e.Option);
    }

    private void HandlePlayerActionRequest(HudPlayerCardViewModel player, HudPlayerActionOption? option)
    {
        if (_webSocketClient == null || option == null)
            return;
        if (_hudSettings == null)
            return;

        if (_isAwaitingAttachTarget)
        {
            CancelPendingAttachTargetSelection();
        }

        // Attach action opens submenu; presets execute immediately.
        if (option.Id == AttachActionId)
        {
            player.OpenAttachSubMenu(_hudSettings.AttachPresets);
            return;
        }

        if (player.IsInAttachSubMenu)
        {
            var presetIndex = option.Index;
            var preset = _hudSettings.AttachPresets.ElementAtOrDefault(presetIndex);
            if (preset == null) return;

            if (PresetRequiresTarget(preset))
            {
                BeginAwaitAttachTargetSelection(player.ObserverSlot, presetIndex);
                player.CloseAttachSubMenu();
                return;
            }

            _ = _webSocketClient.SendCommandAsync("attach_camera", BuildAttachCameraArgs(player.ObserverSlot, preset, targetObserverSlot: null));
            player.CloseAttachSubMenu();
            return;
        }
    }

    private void OnAttachTargetSelected(object? sender, EventArgs e)
    {
        if (!_isAwaitingAttachTarget) return;
        if (_webSocketClient == null) return;
        if (_hudSettings == null) return;
        if (sender is not HudPlayerCardViewModel targetPlayer) return;

        var preset = _hudSettings.AttachPresets.ElementAtOrDefault(_pendingAttachPresetIndex);
        if (preset == null)
        {
            CancelPendingAttachTargetSelection();
            return;
        }

        _ = _webSocketClient.SendCommandAsync(
            "attach_camera",
            BuildAttachCameraArgs(_pendingAttachSourceObserverSlot, preset, targetObserverSlot: targetPlayer.ObserverSlot));

        CancelPendingAttachTargetSelection();
    }

    private void BeginAwaitAttachTargetSelection(int sourceObserverSlot, int presetIndex)
    {
        _pendingAttachSourceObserverSlot = sourceObserverSlot;
        _pendingAttachPresetIndex = presetIndex;
        _isAwaitingAttachTarget = true;

        SetAttachTargetSelectionMode(true);
        HudPromptText = "Select target player for transition (click a player card)";
    }

    private void CancelPendingAttachTargetSelection()
    {
        _isAwaitingAttachTarget = false;
        _pendingAttachSourceObserverSlot = 0;
        _pendingAttachPresetIndex = -1;
        SetAttachTargetSelectionMode(false);
        HudPromptText = string.Empty;
    }

    private void SetAttachTargetSelectionMode(bool enabled)
    {
        foreach (var vm in _hudPlayerCache.Values)
        {
            vm.IsAttachTargetSelectionActive = enabled;
        }
    }

    private static bool PresetRequiresTarget(HudSettings.AttachmentPreset preset)
    {
        return preset.Animation.Enabled &&
               preset.Animation.Events.Any(e => e.Type == HudSettings.AttachmentPresetAnimationEventType.Transition);
    }

    private static object BuildAttachCameraArgs(int observerSlot, HudSettings.AttachmentPreset preset, int? targetObserverSlot)
    {
        object? animation = null;
        if (preset.Animation.Enabled)
        {
            var events = (preset.Animation.Events ?? new List<HudSettings.AttachmentPresetAnimationEvent>())
                .Select(ev =>
                {
                    if (ev.Type == HudSettings.AttachmentPresetAnimationEventType.Transition)
                    {
                        return (object)new
                        {
                            type = "transition",
                            time = ev.Time,
                            order = ev.Order
                        };
                    }

                    return (object)new
                    {
                        type = "keyframe",
                        time = ev.Time,
                        order = ev.Order,
                        delta_pos = new { x = ev.DeltaPosX, y = ev.DeltaPosY, z = ev.DeltaPosZ },
                        delta_angles = new { pitch = ev.DeltaPitch, yaw = ev.DeltaYaw, roll = ev.DeltaRoll },
                        fov = ev.Fov
                    };
                })
                .ToList();

            animation = new
            {
                enabled = preset.Animation.Enabled,
                events
            };
        }

        return new
        {
            observer_slot = observerSlot,
            target_observer_slot = targetObserverSlot,
            attachment = preset.AttachmentName,
            offset_pos = new { x = preset.OffsetPosX, y = preset.OffsetPosY, z = preset.OffsetPosZ },
            offset_angles = new { pitch = preset.OffsetPitch, yaw = preset.OffsetYaw, roll = preset.OffsetRoll },
            fov = preset.Fov,
            animation
        };
    }

    private void OnWebSocketMessage(object? sender, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var messageType = typeProp.GetString();
            if (string.Equals(messageType, "freecam_speed", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("speed", out var speedProp) &&
                    speedProp.TryGetDouble(out var speed))
                {
                    Dispatcher.UIThread.Post(() => FreecamSpeed = speed, DispatcherPriority.Background);
                }

                return;
            }

            if (string.Equals(messageType, "killfeed_event", StringComparison.Ordinal) &&
                TryParseKillfeedPayload(root, out var payload))
            {
                Dispatcher.UIThread.Post(() => AddKillfeedEntry(payload), DispatcherPriority.Background);
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private readonly record struct KillfeedPayload(
        int AttackerObserverSlot,
        string AttackerName,
        int AttackerTeam,
        string VictimName,
        int VictimTeam,
        string AssisterName,
        int AssisterTeam,
        string Weapon,
        bool Blind,
        bool FlashAssist,
        bool InAir,
        bool NoScope,
        bool ThroughSmoke,
        bool Wallbang,
        bool Headshot,
        bool UseAltBindings,
        bool ShowAttackerSlot);

    private bool TryParseKillfeedPayload(JsonElement root, out KillfeedPayload payload)
    {
        payload = default;

        var attacker = ReadPlayer(root, "attacker");
        var victim = ReadPlayer(root, "victim");
        var assister = ReadPlayer(root, "assister");
        var weapon = ReadString(root, "weapon");
        var attackerObserverSlot = attacker.raw.ValueKind == JsonValueKind.Object
            ? ReadInt(attacker.raw, "observer_slot")
            : -1;

        payload = new KillfeedPayload(
            attackerObserverSlot,
            attacker.name,
            attacker.team,
            victim.name,
            victim.team,
            assister.name,
            assister.team,
            weapon,
            ReadBool(root, "blind"),
            ReadBool(root, "flash_assist"),
            ReadBool(root, "in_air"),
            ReadBool(root, "noscope"),
            ReadBool(root, "through_smoke"),
            ReadBool(root, "wallbang"),
            ReadBool(root, "headshot"),
            _hudSettings?.UseAltPlayerBinds ?? false,
            _hudSettings?.ShowKillfeedAttackerSlot ?? true);

        return true;
    }

    private static (string name, int team, JsonElement raw) ReadPlayer(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var playerProp) ||
            playerProp.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, 0, default);
        }

        var name = ReadString(playerProp, "name");
        var team = ReadInt(playerProp, "team");
        return (name, team, playerProp);
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return prop.GetString() ?? string.Empty;
    }

    private static int ReadInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return 0;

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(prop.GetString(), out var value) => value,
            _ => 0
        };
    }

    private static bool ReadBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when prop.TryGetInt32(out var value) => value != 0,
            JsonValueKind.String when bool.TryParse(prop.GetString(), out var value) => value,
            _ => false
        };
    }

    private void AddKillfeedEntry(KillfeedPayload payload)
    {
        var weaponIcon = string.IsNullOrWhiteSpace(payload.Weapon)
            ? string.Empty
            : GetWeaponIconPath(payload.Weapon);

        var entry = new HudKillfeedEntryViewModel(
            payload.AttackerObserverSlot,
            payload.AttackerName,
            CreateTeamBrush(payload.AttackerTeam),
            payload.VictimName,
            CreateTeamBrush(payload.VictimTeam),
            payload.AssisterName,
            CreateTeamBrush(payload.AssisterTeam),
            weaponIcon,
            payload.Blind,
            payload.FlashAssist,
            payload.InAir,
            payload.NoScope,
            payload.ThroughSmoke,
            payload.Wallbang,
            payload.Headshot,
            payload.UseAltBindings,
            payload.ShowAttackerSlot);

        _killfeedEntries.Add(entry);
    }

    private void OnKillfeedTimerTick(object? sender, EventArgs e)
    {
        if (_killfeedEntries.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var fadeStart = KillfeedLifetimeSeconds - KillfeedFadeSeconds;

        for (int i = _killfeedEntries.Count - 1; i >= 0; i--)
        {
            var entry = _killfeedEntries[i];
            var ageSeconds = (now - entry.CreatedAt).TotalSeconds;

            if (ageSeconds >= KillfeedLifetimeSeconds)
            {
                _killfeedEntries.RemoveAt(i);
                continue;
            }

            var opacity = 1.0;
            if (KillfeedFadeSeconds > 0 && ageSeconds > fadeStart)
            {
                opacity = Math.Clamp((KillfeedLifetimeSeconds - ageSeconds) / KillfeedFadeSeconds, 0.0, 1.0);
            }

            entry.Opacity = opacity;
        }
    }

    /// <summary>
    /// Apply a speed multiplier (e.g., 2.0 when Shift is held)
    /// </summary>
    public async void ApplySpeedMultiplier(double multiplier)
    {
        if (_webSocketClient == null)
            return;

        _speedMultiplier = multiplier;
        var effectiveSpeed = _freecamSpeed * _speedMultiplier;

        // Send the modified speed to HLAE
        var command = $"freecam_speed {effectiveSpeed:F1}";
        await _webSocketClient.SendCommandAsync(command);
    }

    private sealed class Relay : ICommand
    {
        private readonly Action<object?> _action;

        public Relay(Action<object?> action)
        {
            _action = action;
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _action(parameter);

        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
