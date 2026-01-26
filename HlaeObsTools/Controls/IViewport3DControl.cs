using System.Collections.Generic;
using Avalonia.Input;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Controls;

public interface IViewport3DControl
{
    bool IsFreecamActive { get; }
    bool IsFreecamInputEnabled { get; }

    void ForwardPointerPressed(PointerPressedEventArgs e);
    void ForwardPointerReleased(PointerReleasedEventArgs e);
    void ForwardPointerMoved(PointerEventArgs e);
    void ForwardPointerWheel(PointerWheelEventArgs e);

    bool TryGetFreecamState(out ViewportFreecamState state);
    void DisableFreecamInput();
    void SetPins(IReadOnlyList<ViewportPin> pins);
}
