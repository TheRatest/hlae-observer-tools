using System;
using System.Linq;
using Avalonia;
using Avalonia.Layout;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class CampathsDockView : UserControl
{
    public CampathsDockView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private const string CampathDragFormat = "campath-item";
    private const string GroupDragFormat = "group-item";

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is CampathsDockViewModel vm)
        {
            vm.PromptAsync = PromptAsync;
            vm.BrowseFileAsync = BrowseFileAsync;
            vm.BrowseFolderAsync = BrowseFolderAsync;
            vm.ViewGroupRequested += OnViewGroupRequested;
        }
    }

    private async Task<string?> PromptAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var textBox = new TextBox { Margin = new Thickness(0, 6, 0, 6) };
        var okButton = new Button { Content = "OK", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 80 };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message });
        panel.Children.Add(textBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        string? result = null;
        okButton.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.Close(true);
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        var host = TopLevel.GetTopLevel(this) as Window;
        await dialog.ShowDialog<bool?>(host);
        return result;
    }

    private async Task<string?> BrowseFileAsync(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            AllowMultiple = false
        };
        var host = TopLevel.GetTopLevel(this) as Window;
        var result = await dlg.ShowAsync(host);
        return result?.FirstOrDefault();
    }

    private async Task<string?> BrowseFolderAsync(string title)
    {
        var dlg = new OpenFolderDialog
        {
            Title = title
        };
        var host = TopLevel.GetTopLevel(this) as Window;
        return await dlg.ShowAsync(host);
    }

    private void OnViewGroupRequested(object? sender, CampathGroupViewModel? group)
    {
        if (group == null || DataContext is not CampathsDockViewModel vm)
            return;

        var host = TopLevel.GetTopLevel(this) as Window;
        var window = new CampathGroupViewWindow(vm, group);
        window.Show(host);
    }

    private async void OnCampathPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control control &&
            control.DataContext is CampathItemViewModel campathVm)
        {
            var data = new DataObject();
            data.Set(CampathDragFormat, campathVm);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
    }

    private void OnCampathDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(CampathDragFormat) && sender is Control { DataContext: CampathItemViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnCampathDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CampathsDockViewModel vm)
            return;

        var dragged = e.Data.Get(CampathDragFormat) as CampathItemViewModel;
        if (dragged == null)
            return;

        var target = (sender as Control)?.DataContext as CampathItemViewModel;
        if (target == null || ReferenceEquals(dragged, target))
            return;

        vm.MoveCampath(dragged, target);
        e.Handled = true;
    }

    private void OnGroupDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(GroupDragFormat) && sender is Control { DataContext: CampathGroupViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.Data.Contains(CampathDragFormat) && sender is Control { DataContext: CampathGroupViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnGroupDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CampathsDockViewModel vm)
            return;

        var draggedGroup = e.Data.Get(GroupDragFormat) as CampathGroupViewModel;
        var draggedCampath = e.Data.Get(CampathDragFormat) as CampathItemViewModel;
        var group = (sender as Control)?.DataContext as CampathGroupViewModel;
        if (group == null)
            return;

        if (draggedGroup != null)
        {
            if (!ReferenceEquals(draggedGroup, group))
            {
                vm.MoveGroup(draggedGroup, group);
                e.Handled = true;
            }
        }
        else if (draggedCampath != null)
        {
            vm.AddCampathToGroup(draggedCampath, group);
            e.Handled = true;
        }
    }

    private async void OnGroupPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control control &&
            control.DataContext is CampathGroupViewModel groupVm)
        {
            var data = new DataObject();
            data.Set(GroupDragFormat, groupVm);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
    }
}
