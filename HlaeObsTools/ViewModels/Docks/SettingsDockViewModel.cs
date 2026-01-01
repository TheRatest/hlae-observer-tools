using System;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Settings;


namespace HlaeObsTools.ViewModels.Docks
{
    /// <summary>
    /// Settings dock for configuring UI options like radar markers and camera paths.
    /// </summary>
    public class SettingsDockViewModel : Tool
    {
        private readonly RadarSettings _radarSettings;
        private readonly HudSettings _hudSettings;
        private readonly FreecamSettings _freecamSettings;
        private readonly Viewport3DSettings _viewport3DSettings;
        private readonly SettingsStorage _settingsStorage;
        private readonly HlaeWebSocketClient? _ws;
        private readonly Func<NetworkSettingsData, Task>? _applyNetworkSettingsAsync;
        private readonly Action<AttachPresetViewModel>? _openAttachPresetAnimation;
        private bool _suppressFreecamSave;

        public record NetworkSettingsData(string WebSocketHost, int WebSocketPort, int UdpPort, int RtpPort, int GsiPort);

        public SettingsDockViewModel(RadarSettings radarSettings, HudSettings hudSettings, FreecamSettings freecamSettings, Viewport3DSettings viewport3DSettings, SettingsStorage settingsStorage, HlaeWebSocketClient wsClient, Action<AttachPresetViewModel>? openAttachPresetAnimation = null, Func<NetworkSettingsData, Task>? applyNetworkSettingsAsync = null, AppSettingsData? storedSettings = null)
        {
            _radarSettings = radarSettings;
            _hudSettings = hudSettings;
            _freecamSettings = freecamSettings;
            _viewport3DSettings = viewport3DSettings;
            _settingsStorage = settingsStorage;
            _ws = wsClient;
            _openAttachPresetAnimation = openAttachPresetAnimation;
            _applyNetworkSettingsAsync = applyNetworkSettingsAsync;

            Title = "Settings";
            CanClose = false;
            CanFloat = true;
            CanPin = true;

            // Initialize network fields
            var settings = storedSettings ?? new AppSettingsData();
            _webSocketHost = settings.WebSocketHost;
            _webSocketPort = settings.WebSocketPort;
            _udpPort = settings.UdpPort;
            _rtpPort = settings.RtpPort;
            _gsiPort = settings.GsiPort;
            _useAltPlayerBinds = settings.UseAltPlayerBinds;
            _mapObjPath = settings.MapObjPath ?? string.Empty;
            _pinScale = (float)settings.PinScale;
            _pinOffsetZ = (float)settings.PinOffsetZ;
            _viewportMouseScale = (float)settings.ViewportMouseScale;
            _mapScale = (float)settings.MapScale;
            _mapYaw = (float)settings.MapYaw;
            _mapPitch = (float)settings.MapPitch;
            _mapRoll = (float)settings.MapRoll;
            _mapOffsetX = (float)settings.MapOffsetX;
            _mapOffsetY = (float)settings.MapOffsetY;
            _mapOffsetZ = (float)settings.MapOffsetZ;
            _radarSettings.UseAltPlayerBinds = _useAltPlayerBinds;
            _hudSettings.UseAltPlayerBinds = _useAltPlayerBinds;
            _viewport3DSettings.UseAltPlayerBinds = _useAltPlayerBinds;
            _viewport3DSettings.MapObjPath = _mapObjPath;
            _viewport3DSettings.PinScale = _pinScale;
            _viewport3DSettings.PinOffsetZ = _pinOffsetZ;
            _viewport3DSettings.ViewportMouseScale = _viewportMouseScale;
            _viewport3DSettings.MapScale = _mapScale;
            _viewport3DSettings.MapYaw = _mapYaw;
            _viewport3DSettings.MapPitch = _mapPitch;
            _viewport3DSettings.MapRoll = _mapRoll;
            _viewport3DSettings.MapOffsetX = _mapOffsetX;
            _viewport3DSettings.MapOffsetY = _mapOffsetY;
            _viewport3DSettings.MapOffsetZ = _mapOffsetZ;

            if (_ws != null)
            {
                _ws.Connected += OnWebSocketConnected;
            }

            OpenAttachPresetAnimationCommand = new RelayParam<AttachPresetViewModel>(
                preset =>
                {
                    if (preset == null) return;
                    _openAttachPresetAnimation?.Invoke(preset);
                },
                preset => preset != null && _openAttachPresetAnimation != null);

            LoadAttachPresets();
            SendAltPlayerBindsMode();
            if (_ws?.IsConnected == true)
                _ = SendAllFreecamConfigAsync();
            _freecamSettings.PropertyChanged += OnFreecamSettingsChanged;
        }

        #region === Network Settings ===
        private string _webSocketHost = "127.0.0.1";
        public string WebSocketHost
        {
            get => _webSocketHost;
            set
            {
                if (_webSocketHost != value)
                {
                    _webSocketHost = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _webSocketPort = 31338;
        public int WebSocketPort
        {
            get => _webSocketPort;
            set
            {
                if (_webSocketPort != value)
                {
                    _webSocketPort = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _udpPort = 31339;
        public int UdpPort
        {
            get => _udpPort;
            set
            {
                if (_udpPort != value)
                {
                    _udpPort = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _rtpPort = 5000;
        public int RtpPort
        {
            get => _rtpPort;
            set
            {
                if (_rtpPort != value)
                {
                    _rtpPort = value;
                    OnPropertyChanged();
                }
            }
        }

        private int _gsiPort = 31337;
        public int GsiPort
        {
            get => _gsiPort;
            set
            {
                if (_gsiPort != value)
                {
                    _gsiPort = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand ApplyNetworkSettingsCommand => new AsyncRelay(ApplyNetworkSettingsInternalAsync);

        private async Task ApplyNetworkSettingsInternalAsync()
        {
            SaveSettings();
            if (_applyNetworkSettingsAsync != null)
            {
                var payload = new NetworkSettingsData(WebSocketHost, WebSocketPort, UdpPort, RtpPort, GsiPort);
                await _applyNetworkSettingsAsync(payload);
            }
        }
        #endregion

        #region ==== 3D Viewport ====

        private string _mapObjPath = string.Empty;
        public string MapObjPath
        {
            get => _mapObjPath;
            set
            {
                if (_mapObjPath != value)
                {
                    _mapObjPath = value ?? string.Empty;
                    _viewport3DSettings.MapObjPath = _mapObjPath;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _pinScale = 1.0f;
        public float PinScale
        {
            get => _pinScale;
            set
            {
                if (Math.Abs(_pinScale - value) > 0.0001f)
                {
                    _pinScale = value;
                    _viewport3DSettings.PinScale = _pinScale;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _pinOffsetZ;
        public float PinOffsetZ
        {
            get => _pinOffsetZ;
            set
            {
                if (Math.Abs(_pinOffsetZ - value) > 0.0001f)
                {
                    _pinOffsetZ = value;
                    _viewport3DSettings.PinOffsetZ = _pinOffsetZ;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _viewportMouseScale = 1.0f;
        public float ViewportMouseScale
        {
            get => _viewportMouseScale;
            set
            {
                if (Math.Abs(_viewportMouseScale - value) > 0.0001f)
                {
                    _viewportMouseScale = value;
                    _viewport3DSettings.ViewportMouseScale = _viewportMouseScale;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapScale = 1.0f;
        public float MapScale
        {
            get => _mapScale;
            set
            {
                if (Math.Abs(_mapScale - value) > 0.0001f)
                {
                    _mapScale = value;
                    _viewport3DSettings.MapScale = _mapScale;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapYaw;
        public float MapYaw
        {
            get => _mapYaw;
            set
            {
                if (Math.Abs(_mapYaw - value) > 0.0001f)
                {
                    _mapYaw = value;
                    _viewport3DSettings.MapYaw = _mapYaw;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapPitch;
        public float MapPitch
        {
            get => _mapPitch;
            set
            {
                if (Math.Abs(_mapPitch - value) > 0.0001f)
                {
                    _mapPitch = value;
                    _viewport3DSettings.MapPitch = _mapPitch;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapRoll;
        public float MapRoll
        {
            get => _mapRoll;
            set
            {
                if (Math.Abs(_mapRoll - value) > 0.0001f)
                {
                    _mapRoll = value;
                    _viewport3DSettings.MapRoll = _mapRoll;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapOffsetX;
        public float MapOffsetX
        {
            get => _mapOffsetX;
            set
            {
                if (Math.Abs(_mapOffsetX - value) > 0.0001f)
                {
                    _mapOffsetX = value;
                    _viewport3DSettings.MapOffsetX = _mapOffsetX;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapOffsetY;
        public float MapOffsetY
        {
            get => _mapOffsetY;
            set
            {
                if (Math.Abs(_mapOffsetY - value) > 0.0001f)
                {
                    _mapOffsetY = value;
                    _viewport3DSettings.MapOffsetY = _mapOffsetY;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        private float _mapOffsetZ;
        public float MapOffsetZ
        {
            get => _mapOffsetZ;
            set
            {
                if (Math.Abs(_mapOffsetZ - value) > 0.0001f)
                {
                    _mapOffsetZ = value;
                    _viewport3DSettings.MapOffsetZ = _mapOffsetZ;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        public ICommand BrowseMapObjCommand => new AsyncRelay(BrowseMapObjAsync);
        public ICommand ClearMapObjCommand => new Relay(() =>
        {
            MapObjPath = string.Empty;
        });

        private async Task BrowseMapObjAsync()
        {
            var path = await PickObjFileToLoadAsync();
            if (string.IsNullOrWhiteSpace(path))
                return;

            MapObjPath = path;
        }

        private async Task<string?> PickObjFileToLoadAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                return null;

            var window = lifetime.MainWindow;
            if (window is null)
                return null;

            var result = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Load Map OBJ",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Wavefront OBJ")
                        {
                            Patterns = ["*.obj"]
                        }
                    ]
                });

            if (result is { Count: > 0 })
                return result[0].Path.LocalPath;

            return null;
        }

        #endregion

        #region === General Settings ===
        private bool _useAltPlayerBinds;
        public bool UseAltPlayerBinds
        {
            get => _useAltPlayerBinds;
            set
            {
                if (_useAltPlayerBinds != value)
                {
                    _useAltPlayerBinds = value;
                    OnPropertyChanged();

                    _radarSettings.UseAltPlayerBinds = value;
                    _hudSettings.UseAltPlayerBinds = value;
                    _viewport3DSettings.UseAltPlayerBinds = value;
                    SaveSettings();
                    SendAltPlayerBindsMode();
                }
            }
        }

        private bool _IsDrawHudEnabled;
        public bool IsDrawHudEnabled
        {
            get => _IsDrawHudEnabled;
            set
            {
                if (_IsDrawHudEnabled != value)
                {
                    _IsDrawHudEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "cl_drawhud 0"
                        : "cl_drawhud 1";
                    _ws.SendExecCommandAsync(cmd);
                }
            }
        }
        private bool _IsOnlyDeathnotesEnabled;
        public bool IsOnlyDeathnotesEnabled
        {
            get => _IsOnlyDeathnotesEnabled;
            set
            {
                if (_IsOnlyDeathnotesEnabled != value)
                {
                    _IsOnlyDeathnotesEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "cl_draw_only_deathnotices 1"
                        : "cl_draw_only_deathnotices 0";
                    _ws.SendExecCommandAsync(cmd);
                }
            }
        }

        private int _forceDeathnoticesMode;
        public string ForceDeathnoticesLabel => _forceDeathnoticesMode.ToString();

        public ICommand CycleForceDeathnoticesCommand => new Relay(CycleForceDeathnoticesMode);

        private void CycleForceDeathnoticesMode()
        {
            var nextMode = _forceDeathnoticesMode switch
            {
                0 => 1,
                1 => -1,
                _ => 0
            };

            if (_forceDeathnoticesMode == nextMode)
                return;

            _forceDeathnoticesMode = nextMode;
            OnPropertyChanged(nameof(ForceDeathnoticesLabel));

            var cmd = $"cl_drawhud_force_deathnotices {_forceDeathnoticesMode}";
            _ws.SendExecCommandAsync(cmd);
        }
        public ICommand ToggleDemouiCommand => new AsyncRelay(() => _ws.SendExecCommandAsync("demoui"));

        private void OnWebSocketConnected(object? sender, EventArgs e)
        {
            SendAltPlayerBindsMode();
            _ = SendAllFreecamConfigAsync();
        }

        private void SendAltPlayerBindsMode()
        {
            if (_ws == null) return;
            _ = _ws.SendCommandAsync("spectator_bindings_mode", new { useAlt = _useAltPlayerBinds });
        }
        #endregion

        #region ==== Radar Settings ====

        public double MarkerScale
        {
            get => _radarSettings.MarkerScale;
            set
            {
                if (value < 0.3) value = 0.3;
                if (value > 3.0) value = 3.0;
                if (_radarSettings.MarkerScale != value)
                {
                    _radarSettings.MarkerScale = value;
                    OnPropertyChanged();
                    SaveSettings();
                }
            }
        }

        #endregion

        #region ==== HUD ====


        public bool IsHudEnabled
        {
            get => _hudSettings.IsHudEnabled;
            set
            {
                if (_hudSettings.IsHudEnabled != value)
                {
                    _hudSettings.IsHudEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowKillfeed
        {
            get => _hudSettings.ShowKillfeed;
            set
            {
                if (_hudSettings.ShowKillfeed != value)
                {
                    _hudSettings.ShowKillfeed = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool ShowKillfeedAttackerSlot
        {
            get => _hudSettings.ShowKillfeedAttackerSlot;
            set
            {
                if (_hudSettings.ShowKillfeedAttackerSlot != value)
                {
                    _hudSettings.ShowKillfeedAttackerSlot = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        #region ==== Actions / Attach Presets ====

        public ObservableCollection<AttachPresetViewModel> AttachPresets { get; }
            = new ObservableCollection<AttachPresetViewModel>(
                Enumerable.Range(0, 5).Select(i => new AttachPresetViewModel($"Preset {i + 1}")));

        public ICommand OpenAttachPresetAnimationCommand { get; }

        private void LoadAttachPresets()
        {
            var presets = _hudSettings.AttachPresets;
            for (int i = 0; i < AttachPresets.Count && i < presets.Count; i++)
            {
                AttachPresets[i].LoadFrom(presets[i]);
                AttachPresets[i].PropertyChanged -= OnPresetChanged;
                AttachPresets[i].PropertyChanged += OnPresetChanged;
            }
            SaveSettings();
        }

        private void OnPresetChanged(object? sender, PropertyChangedEventArgs e)
        {
            var vm = sender as AttachPresetViewModel;
            if (vm == null) return;
            var index = AttachPresets.IndexOf(vm);
            if (index < 0 || index >= _hudSettings.AttachPresets.Count) return;
            _hudSettings.AttachPresets[index] = vm.ToModel();
            SaveSettings();
        }

        private void SaveSettings()
        {
            var data = new AppSettingsData
            {
                AttachPresets = _hudSettings.ToAttachPresetData().ToList(),
                MarkerScale = _radarSettings.MarkerScale,
                UseAltPlayerBinds = _useAltPlayerBinds,
                WebSocketHost = WebSocketHost,
                WebSocketPort = WebSocketPort,
                UdpPort = UdpPort,
                RtpPort = RtpPort,
                MapObjPath = _mapObjPath,
                PinScale = _pinScale,
                PinOffsetZ = _pinOffsetZ,
                ViewportMouseScale = _viewportMouseScale,
                MapScale = _mapScale,
                MapYaw = _mapYaw,
                MapPitch = _mapPitch,
                MapRoll = _mapRoll,
                MapOffsetX = _mapOffsetX,
                MapOffsetY = _mapOffsetY,
                MapOffsetZ = _mapOffsetZ,
                FreecamSettings = _freecamSettings.ToData()
            };
            _settingsStorage.Save(data);
        }

        #endregion

        #region ==== Camera Path / Create Tab ====

        private bool _isCameraPathPreviewEnabled;
        public bool IsCameraPathPreviewEnabled
        {
            get => _isCameraPathPreviewEnabled;
            set
            {
                if (_isCameraPathPreviewEnabled != value)
                {
                    _isCameraPathPreviewEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "mirv_campath draw enabled 1"
                        : "mirv_campath draw enabled 0";
                    _ws.SendExecCommandAsync(cmd);
                }
            }
        }

        private bool _isCampathEnabled;
        public bool IsCampathEnabled
        {
            get => _isCampathEnabled;
            set
            {
                if (_isCampathEnabled != value)
                {
                    _isCampathEnabled = value;
                    OnPropertyChanged();

                    var cmd = value
                        ? "mirv_campath enabled 1"
                        : "mirv_campath enabled 0";
                    _ws.SendExecCommandAsync(cmd);
                }
            }
        }

        private async Task<string?> PickCampathFileToLoadAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                return null;

            var window = lifetime.MainWindow;
            if (window is null)
                return null;

            var result = await window.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Load Campath",
                    AllowMultiple = false
                });

            if (result is { Count: > 0 })
                return result[0].Path.LocalPath;

            return null;
        }

        private async Task<string?> PickCampathFileToSaveAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
                return null;

            var window = lifetime.MainWindow;
            if (window is null)
                return null;

            var result = await window.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Save Campath"
                });

            return result?.Path.LocalPath;
        }

        private class AsyncRelay : ICommand
        {
            private readonly Func<Task> _action;
            public AsyncRelay(Func<Task> action) => _action = action;
            public bool CanExecute(object parameter) => true;
            public async void Execute(object parameter) => await _action();
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }

        // Dummy interpolation state
        private bool _useCubic = true;
        public string InterpLabel => _useCubic ? "Interp: Cubic" : "Interp: Linear";

        public ICommand ToggleInterpModeCommand => new Relay(() =>
        {
            _useCubic = !_useCubic;
            OnPropertyChanged(nameof(InterpLabel));

            var cmd = _useCubic
                ? "mirv_campath edit interp position cubic; mirv_campath edit interp rotation cubic; mirv_campath edit interp fov cubic"
                : "mirv_campath edit interp position linear; mirv_campath edit interp rotation sLinear; mirv_campath edit interp fov linear";
            _ws.SendExecCommandAsync(cmd);
        });

        // Dummy camera path actions
        public ICommand AddPointCommand => new AsyncRelay(() => _ws.SendExecCommandAsync("mirv_campath add"));
        public ICommand ClearCampathCommand => new AsyncRelay(() => _ws.SendExecCommandAsync("mirv_campath clear"));
        public ICommand GotoStartCommand => new AsyncRelay(() => _ws.SendExecCommandAsync("echo \"Implement this\""));
        
        public ICommand LoadCampathCommand => new AsyncRelay(async () =>
        {
            var path = await PickCampathFileToLoadAsync();
            if (string.IsNullOrWhiteSpace(path))
                return; // user cancelled

            // You might want to escape quotes in path if that’s ever an issue
            var cmd = $"mirv_campath load \"{path}\"";
            await _ws.SendExecCommandAsync(cmd);
        });

        public ICommand SaveCampathCommand => new AsyncRelay(async () =>
        {
            var path = await PickCampathFileToSaveAsync();
            if (string.IsNullOrWhiteSpace(path))
                return; // user cancelled

            var cmd = $"mirv_campath save \"{path}\"";
            await _ws.SendExecCommandAsync(cmd);
        });


        #endregion

        #region ==== Freecam Settings ====

        private void OnFreecamSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName))
                OnPropertyChanged(e.PropertyName);

            if (!_suppressFreecamSave)
                SaveSettings();
        }

        public ICommand ResetFreecamSettingsCommand => new Relay(ResetFreecamSettings);

        private void ResetFreecamSettings()
        {
            _suppressFreecamSave = true;
            _freecamSettings.ResetToDefaults();
            _suppressFreecamSave = false;
            SaveSettings();
            _ = SendAllFreecamConfigAsync();
        }

        // Helper method to send freecam config updates
        private async Task SendFreecamConfigAsync(object config)
        {
            if (_ws == null)
                return;

            await _ws.SendCommandAsync("freecam_config", config);
        }

        private async Task SendAllFreecamConfigAsync()
        {
            if (_ws == null)
                return;

            var config = new
            {
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
                rotDampingRatio = (float)_freecamSettings.RotDampingRatio,
                clampPitch = _freecamSettings.ClampPitch
            };

            await _ws.SendCommandAsync("freecam_config", config);
        }

        // Mouse Settings
        public double MouseSensitivity
        {
            get => _freecamSettings.MouseSensitivity;
            set
            {
                if (_freecamSettings.MouseSensitivity != value)
                {
                    _freecamSettings.MouseSensitivity = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { mouseSensitivity = (float)value });
                }
            }
        }

        // Movement Settings
        public double MoveSpeed
        {
            get => _freecamSettings.MoveSpeed;
            set
            {
                if (_freecamSettings.MoveSpeed != value)
                {
                    _freecamSettings.MoveSpeed = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { moveSpeed = (float)value });
                }
            }
        }

        public double SprintMultiplier
        {
            get => _freecamSettings.SprintMultiplier;
            set
            {
                if (_freecamSettings.SprintMultiplier != value)
                {
                    _freecamSettings.SprintMultiplier = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { sprintMultiplier = (float)value });
                }
            }
        }

        public double VerticalSpeed
        {
            get => _freecamSettings.VerticalSpeed;
            set
            {
                if (_freecamSettings.VerticalSpeed != value)
                {
                    _freecamSettings.VerticalSpeed = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { verticalSpeed = (float)value });
                }
            }
        }

        public double SpeedAdjustRate
        {
            get => _freecamSettings.SpeedAdjustRate;
            set
            {
                if (_freecamSettings.SpeedAdjustRate != value)
                {
                    _freecamSettings.SpeedAdjustRate = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { speedAdjustRate = (float)value });
                }
            }
        }

        public double SpeedMinMultiplier
        {
            get => _freecamSettings.SpeedMinMultiplier;
            set
            {
                if (_freecamSettings.SpeedMinMultiplier != value)
                {
                    _freecamSettings.SpeedMinMultiplier = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { speedMinMultiplier = (float)value });
                }
            }
        }

        public double SpeedMaxMultiplier
        {
            get => _freecamSettings.SpeedMaxMultiplier;
            set
            {
                if (_freecamSettings.SpeedMaxMultiplier != value)
                {
                    _freecamSettings.SpeedMaxMultiplier = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { speedMaxMultiplier = (float)value });
                }
            }
        }

        // Roll Settings
        public double RollSpeed
        {
            get => _freecamSettings.RollSpeed;
            set
            {
                if (_freecamSettings.RollSpeed != value)
                {
                    _freecamSettings.RollSpeed = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { rollSpeed = (float)value });
                }
            }
        }

        public double RollSmoothing
        {
            get => _freecamSettings.RollSmoothing;
            set
            {
                if (_freecamSettings.RollSmoothing != value)
                {
                    _freecamSettings.RollSmoothing = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { rollSmoothing = (float)value });
                }
            }
        }

        public double LeanStrength
        {
            get => _freecamSettings.LeanStrength;
            set
            {
                if (_freecamSettings.LeanStrength != value)
                {
                    _freecamSettings.LeanStrength = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { leanStrength = (float)value });
                }
            }
        }

        public double LeanAccelScale
        {
            get => _freecamSettings.LeanAccelScale;
            set
            {
                if (_freecamSettings.LeanAccelScale != value)
                {
                    _freecamSettings.LeanAccelScale = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { leanAccelScale = (float)value });
                }
            }
        }

        public double LeanVelocityScale
        {
            get => _freecamSettings.LeanVelocityScale;
            set
            {
                if (_freecamSettings.LeanVelocityScale != value)
                {
                    _freecamSettings.LeanVelocityScale = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { leanVelocityScale = (float)value });
                }
            }
        }

        public double LeanMaxAngle
        {
            get => _freecamSettings.LeanMaxAngle;
            set
            {
                if (_freecamSettings.LeanMaxAngle != value)
                {
                    _freecamSettings.LeanMaxAngle = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { leanMaxAngle = (float)value });
                }
            }
        }

        public double LeanHalfTime
        {
            get => _freecamSettings.LeanHalfTime;
            set
            {
                if (_freecamSettings.LeanHalfTime != value)
                {
                    _freecamSettings.LeanHalfTime = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { leanHalfTime = (float)value });
                }
            }
        }

        // FOV Settings
        public double FovMin
        {
            get => _freecamSettings.FovMin;
            set
            {
                if (_freecamSettings.FovMin != value)
                {
                    _freecamSettings.FovMin = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { fovMin = (float)value });
                }
            }
        }

        public double FovMax
        {
            get => _freecamSettings.FovMax;
            set
            {
                if (_freecamSettings.FovMax != value)
                {
                    _freecamSettings.FovMax = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { fovMax = (float)value });
                }
            }
        }

        public double FovStep
        {
            get => _freecamSettings.FovStep;
            set
            {
                if (_freecamSettings.FovStep != value)
                {
                    _freecamSettings.FovStep = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { fovStep = (float)value });
                }
            }
        }

        public double DefaultFov
        {
            get => _freecamSettings.DefaultFov;
            set
            {
                if (_freecamSettings.DefaultFov != value)
                {
                    _freecamSettings.DefaultFov = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { defaultFov = (float)value });
                }
            }
        }

        // Smoothing Settings
        public bool SmoothEnabled
        {
            get => _freecamSettings.SmoothEnabled;
            set
            {
                if (_freecamSettings.SmoothEnabled != value)
                {
                    _freecamSettings.SmoothEnabled = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { smoothEnabled = value });
                }
            }
        }

        public double HalfVec
        {
            get => _freecamSettings.HalfVec;
            set
            {
                if (_freecamSettings.HalfVec != value)
                {
                    _freecamSettings.HalfVec = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { halfVec = (float)value });
                }
            }
        }

        public double HalfRot
        {
            get => _freecamSettings.HalfRot;
            set
            {
                if (_freecamSettings.HalfRot != value)
                {
                    _freecamSettings.HalfRot = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { halfRot = (float)value });
                }
            }
        }

        public double LockHalfRot
        {
            get => _freecamSettings.LockHalfRot;
            set
            {
                if (_freecamSettings.LockHalfRot != value)
                {
                    _freecamSettings.LockHalfRot = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { lockHalfRot = (float)value });
                }
            }
        }

        public double LockHalfRotTransition
        {
            get => _freecamSettings.LockHalfRotTransition;
            set
            {
                if (_freecamSettings.LockHalfRotTransition != value)
                {
                    _freecamSettings.LockHalfRotTransition = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { lockHalfRotTransition = (float)value });
                }
            }
        }

        public double HalfFov
        {
            get => _freecamSettings.HalfFov;
            set
            {
                if (_freecamSettings.HalfFov != value)
                {
                    _freecamSettings.HalfFov = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { halfFov = (float)value });
                }
            }
        }

        public bool RotCriticalDamping
        {
            get => _freecamSettings.RotCriticalDamping;
            set
            {
                if (_freecamSettings.RotCriticalDamping != value)
                {
                    _freecamSettings.RotCriticalDamping = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { rotCriticalDamping = value });
                }
            }
        }

        public double RotDampingRatio
        {
            get => _freecamSettings.RotDampingRatio;
            set
            {
                if (_freecamSettings.RotDampingRatio != value)
                {
                    _freecamSettings.RotDampingRatio = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { rotDampingRatio = (float)_freecamSettings.RotDampingRatio });
                }
            }
        }

        public bool HoldMovementFollowsCamera
        {
            get => _freecamSettings.HoldMovementFollowsCamera;
            set
            {
                if (_freecamSettings.HoldMovementFollowsCamera != value)
                {
                    _freecamSettings.HoldMovementFollowsCamera = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool AnalogKeyboardEnabled
        {
            get => _freecamSettings.AnalogKeyboardEnabled;
            set
            {
                if (_freecamSettings.AnalogKeyboardEnabled != value)
                {
                    _freecamSettings.AnalogKeyboardEnabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public double AnalogLeftDeadzone
        {
            get => _freecamSettings.AnalogLeftDeadzone;
            set
            {
                if (_freecamSettings.AnalogLeftDeadzone != value)
                {
                    _freecamSettings.AnalogLeftDeadzone = value;
                    OnPropertyChanged();
                }
            }
        }

        public double AnalogRightDeadzone
        {
            get => _freecamSettings.AnalogRightDeadzone;
            set
            {
                if (_freecamSettings.AnalogRightDeadzone != value)
                {
                    _freecamSettings.AnalogRightDeadzone = value;
                    OnPropertyChanged();
                }
            }
        }

        public double AnalogCurve
        {
            get => _freecamSettings.AnalogCurve;
            set
            {
                if (_freecamSettings.AnalogCurve != value)
                {
                    _freecamSettings.AnalogCurve = value;
                    OnPropertyChanged();
                }
            }
        }

        #endregion

        public bool ClampPitch
        {
            get => _freecamSettings.ClampPitch;
            set
            {
                if (_freecamSettings.ClampPitch != value)
                {
                    _freecamSettings.ClampPitch = value;
                    OnPropertyChanged();
                    SendFreecamConfigAsync(new { clampPitch = value });
                }
            }
        }

        // Simple ICommand helper (no MVVM library required)
        private class Relay : ICommand
        {
            private readonly Action _action;
            public Relay(Action action) => _action = action;
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _action();
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }

        private sealed class RelayParam<T> : ICommand where T : class
        {
            private readonly Action<T?> _action;
            private readonly Func<T?, bool>? _canExecute;

            public RelayParam(Action<T?> action, Func<T?, bool>? canExecute = null)
            {
                _action = action;
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter as T) ?? true;

            public void Execute(object? parameter) => _action(parameter as T);

            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }
    }
}
