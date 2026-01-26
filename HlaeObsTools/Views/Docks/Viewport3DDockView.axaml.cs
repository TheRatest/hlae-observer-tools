using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using HlaeObsTools.Controls;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;
namespace HlaeObsTools.Views.Docks;

public partial class Viewport3DDockView : UserControl
{
    private Viewport3DDockViewModel? _viewModel;
    private IViewport3DControl? _viewport;
    private Control? _viewportControl;
    private IReadOnlyList<ViewportPin>? _lastPins;

    public Viewport3DDockView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerMovedEvent, OnViewportPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerWheelChangedEvent, OnViewportPointerWheelChanged, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(KeyDownEvent, OnViewportKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel != null)
        {
            _viewModel.PinsUpdated -= OnPinsUpdated;
            _viewModel.Viewport3DSettings.PropertyChanged -= OnViewportSettingsChanged;
        }

        _viewModel = DataContext as Viewport3DDockViewModel;
        if (_viewModel != null)
        {
            _viewModel.PinsUpdated += OnPinsUpdated;
            _viewModel.Viewport3DSettings.PropertyChanged += OnViewportSettingsChanged;
        }

        EnsureViewport();
    }

    private void OnViewportSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.UseLegacyD3D11Viewport))
        {
            EnsureViewport();
        }
    }

    private void EnsureViewport()
    {
        if (_viewModel == null)
        {
            ClearViewport();
            return;
        }

        var useLegacy = _viewModel.Viewport3DSettings.UseLegacyD3D11Viewport;
        if (_viewportControl is D3D11Viewport && useLegacy)
            return;
        if (_viewportControl is VRFViewport && !useLegacy)
            return;

        ClearViewport();

        _viewportControl = useLegacy ? CreateD3D11Viewport() : CreateVrfViewport();
        _viewport = (IViewport3DControl)_viewportControl;
        ViewportHost.Content = _viewportControl;

        if (_lastPins != null)
        {
            _viewport.SetPins(_lastPins);
        }
    }

    private void ClearViewport()
    {
        ViewportHost.Content = null;
        _viewport = null;
        _viewportControl = null;
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewport?.ForwardPointerPressed(e);
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _viewport?.ForwardPointerReleased(e);

        if (_viewModel == null)
            return;

        var point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            _viewModel.ReleaseHandoffFreecamInput();
        }
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        _viewport?.ForwardPointerMoved(e);
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _viewport?.ForwardPointerWheel(e);
    }

    private void OnPinsUpdated(IReadOnlyList<ViewportPin> pins)
    {
        _lastPins = pins;
        _viewport?.SetPins(pins);
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.B)
            return;

        if (_viewportControl == null || !_viewportControl.IsKeyboardFocusWithin)
            return;

        if (_viewport == null)
            return;

        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        if (!_viewport.IsFreecamActive || !_viewport.IsFreecamInputEnabled)
            return;

        if (!_viewport.TryGetFreecamState(out var state))
            return;

        vm.HandoffFreecam(state);
        _viewport.DisableFreecamInput();
        e.Handled = true;
    }

    private VRFViewport CreateVrfViewport()
    {
        var viewport = new VRFViewport();
        viewport.Bind(VRFViewport.MapPathProperty, new Binding("Viewport3DSettings.MapObjPath"));
        viewport.Bind(VRFViewport.PinScaleProperty, new Binding("Viewport3DSettings.PinScale"));
        viewport.Bind(VRFViewport.PinOffsetZProperty, new Binding("Viewport3DSettings.PinOffsetZ"));
        viewport.Bind(VRFViewport.PostprocessEnabledProperty, new Binding("Viewport3DSettings.PostprocessEnabled"));
        viewport.Bind(VRFViewport.ColorCorrectionEnabledProperty, new Binding("Viewport3DSettings.ColorCorrectionEnabled"));
        viewport.Bind(VRFViewport.DynamicShadowsEnabledProperty, new Binding("Viewport3DSettings.DynamicShadowsEnabled"));
        viewport.Bind(VRFViewport.WireframeEnabledProperty, new Binding("Viewport3DSettings.WireframeEnabled"));
        viewport.Bind(VRFViewport.SkipWaterEnabledProperty, new Binding("Viewport3DSettings.SkipWaterEnabled"));
        viewport.Bind(VRFViewport.SkipTranslucentEnabledProperty, new Binding("Viewport3DSettings.SkipTranslucentEnabled"));
        viewport.Bind(VRFViewport.ShowFpsProperty, new Binding("Viewport3DSettings.ShowFps"));
        viewport.Bind(VRFViewport.ShadowTextureSizeProperty, new Binding("Viewport3DSettings.ShadowTextureSize"));
        viewport.Bind(VRFViewport.MaxTextureSizeProperty, new Binding("Viewport3DSettings.MaxTextureSize"));
        viewport.Bind(VRFViewport.RenderModeProperty, new Binding("Viewport3DSettings.RenderMode"));
        viewport.Bind(VRFViewport.FreecamSettingsProperty, new Binding("FreecamSettings"));
        viewport.Bind(VRFViewport.InputSenderProperty, new Binding("InputSender"));
        viewport.Bind(VRFViewport.ViewportMouseScaleProperty, new Binding("Viewport3DSettings.ViewportMouseScale"));
        viewport.Bind(VRFViewport.ViewportFpsCapProperty, new Binding("Viewport3DSettings.ViewportFpsCap"));
        return viewport;
    }

    private D3D11Viewport CreateD3D11Viewport()
    {
        var viewport = new D3D11Viewport();
        viewport.Bind(D3D11Viewport.MapPathProperty, new Binding("Viewport3DSettings.MapObjPath"));
        viewport.Bind(D3D11Viewport.PinScaleProperty, new Binding("Viewport3DSettings.PinScale"));
        viewport.Bind(D3D11Viewport.PinOffsetZProperty, new Binding("Viewport3DSettings.PinOffsetZ"));
        viewport.Bind(D3D11Viewport.ViewportMouseScaleProperty, new Binding("Viewport3DSettings.ViewportMouseScale"));
        viewport.Bind(D3D11Viewport.ViewportFpsCapProperty, new Binding("Viewport3DSettings.ViewportFpsCap"));
        viewport.Bind(D3D11Viewport.ShowFpsProperty, new Binding("Viewport3DSettings.ShowFps"));
        viewport.Bind(D3D11Viewport.MapScaleProperty, new Binding("Viewport3DSettings.MapScale"));
        viewport.Bind(D3D11Viewport.MapYawProperty, new Binding("Viewport3DSettings.MapYaw"));
        viewport.Bind(D3D11Viewport.MapPitchProperty, new Binding("Viewport3DSettings.MapPitch"));
        viewport.Bind(D3D11Viewport.MapRollProperty, new Binding("Viewport3DSettings.MapRoll"));
        viewport.Bind(D3D11Viewport.MapOffsetXProperty, new Binding("Viewport3DSettings.MapOffsetX"));
        viewport.Bind(D3D11Viewport.MapOffsetYProperty, new Binding("Viewport3DSettings.MapOffsetY"));
        viewport.Bind(D3D11Viewport.MapOffsetZProperty, new Binding("Viewport3DSettings.MapOffsetZ"));
        viewport.Bind(D3D11Viewport.FreecamSettingsProperty, new Binding("FreecamSettings"));
        viewport.Bind(D3D11Viewport.InputSenderProperty, new Binding("InputSender"));
        return viewport;
    }
}
