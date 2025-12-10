using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views;

public partial class HudOverlayWindow : Window
{
    public event EventHandler? RightButtonDown;
    public event EventHandler? RightButtonUp;
    public event EventHandler<bool>? ShiftKeyChanged;

    public HudOverlayWindow()
    {
        InitializeComponent();

        // Make the window layered for transparency, but NOT click-through
        // This window handles all mouse interactions for the HUD
        if (OperatingSystem.IsWindows())
        {
            this.Opened += OnWindowOpened;
        }

        // Subscribe to pointer events for freecam control
        this.PointerPressed += OnPointerPressed;
        this.PointerReleased += OnPointerReleased;

        // Subscribe to keyboard events for shift key detection
        this.KeyDown += OnKeyDown;
        this.KeyUp += OnKeyUp;
    }

    public Canvas? GetSpeedScaleCanvas()
    {
        return HudContent?.GetSpeedScaleCanvas();
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (!OperatingSystem.IsWindows()) return;

        // Make window layered for transparency but still receive mouse events
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            MakeLayered(hwnd);
        }
    }

    private void MakeLayered(IntPtr hwnd)
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_LAYERED = 0x00080000;

        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle |= WS_EX_LAYERED;
        // Note: NOT adding WS_EX_TRANSPARENT so the window can receive mouse events
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            RightButtonDown?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsRightButtonPressed)
        {
            RightButtonUp?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            ShiftKeyChanged?.Invoke(this, true);
            e.Handled = true;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            ShiftKeyChanged?.Invoke(this, false);
            e.Handled = true;
        }
    }

    public void UpdatePositionAndSize(PixelPoint position, PixelSize size)
    {
        Position = position;
        Width = size.Width;
        Height = size.Height;

        // Update SpeedScaleRegion size when window resizes
        var speedScaleRegion = HudContent?.GetSpeedScaleRegion();
        if (speedScaleRegion != null && size.Width > 0 && size.Height > 0)
        {
            speedScaleRegion.Width = size.Width * 0.3;
            speedScaleRegion.Height = size.Height * 0.4;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
