using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;
namespace HlaeObsTools.Views.Docks;

public partial class Viewport3DDockView : UserControl
{
    private Viewport3DDockViewModel? _viewModel;

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
        }

        _viewModel = DataContext as Viewport3DDockViewModel;
        if (_viewModel != null)
        {
            _viewModel.PinsUpdated += OnPinsUpdated;
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Viewport?.ForwardPointerPressed(e);
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Viewport?.ForwardPointerReleased(e);
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        Viewport?.ForwardPointerMoved(e);
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        Viewport?.ForwardPointerWheel(e);
    }

    private void OnPinsUpdated(IReadOnlyList<ViewportPin> pins)
    {
        Viewport?.SetPins(pins);
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.B)
            return;

        if (Viewport == null || !Viewport.IsKeyboardFocusWithin)
            return;

        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        if (!Viewport.IsFreecamActive || !Viewport.IsFreecamInputEnabled)
            return;

        if (!Viewport.TryGetFreecamState(out var state))
            return;

        vm.HandoffFreecam(state);
        Viewport.DisableFreecamInput();
        e.Handled = true;
    }
}
