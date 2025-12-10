using Avalonia.Controls;

namespace HlaeObsTools.Views;

public partial class HudOverlayContent : UserControl
{
    public HudOverlayContent()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Get the SpeedScaleCanvas for rendering freecam speed scale
    /// </summary>
    public Canvas? GetSpeedScaleCanvas()
    {
        return SpeedScaleCanvas;
    }

    /// <summary>
    /// Get the SpeedScaleRegion for size calculations
    /// </summary>
    public Grid? GetSpeedScaleRegion()
    {
        return SpeedScaleRegion;
    }
}
