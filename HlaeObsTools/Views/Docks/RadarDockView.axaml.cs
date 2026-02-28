using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels.Docks;
using System.Security.Cryptography;

namespace HlaeObsTools.Views.Docks;

public partial class RadarDockView : UserControl
{
    public RadarDockView()
    {
        InitializeComponent();
    }

    private void CampathCamera_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RadarDockViewModel vm && sender is Control ctrl && ctrl.DataContext is CampathPathViewModel path)
        {
            vm.PlayCampath(path);
            e.Handled = true;
        }
    }

    private void CampathIcon_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is RadarDockViewModel vm && sender is Control ctrl && ctrl.DataContext is CampathPathViewModel path)
        {
            vm.SetCampathHighlight(path, true);
        }
    }

    private void CampathIcon_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is RadarDockViewModel vm && sender is Control ctrl && ctrl.DataContext is CampathPathViewModel path)
        {
            vm.SetCampathHighlight(path, false);
        }
    }
    private void RadarPlayer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RadarDockViewModel vm && sender is Control ctrl && ctrl.DataContext is RadarPlayerViewModel player)
        {
            if(e.Properties.IsLeftButtonPressed)
                vm.SwitchToPlayer(player);
            else if(e.Properties.IsMiddleButtonPressed)
                vm.TeleportViewportCameraToPlayer(player);

            e.Handled = true;
        }
    }
}
