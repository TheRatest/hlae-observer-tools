using System;
using Avalonia.Controls;
using HlaeObsTools.Controls;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Viewport;

public partial class CampathSequencerView : UserControl
{
    public CampathSequencerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        var timeline = this.FindControl<CampathTimelineControl>("Timeline");
        if (timeline == null)
            return;

        timeline.FreecamPreviewRequested -= OnFreecamPreviewRequested;
        timeline.FreecamPreviewEnded -= OnFreecamPreviewEnded;
        timeline.FreecamPreviewRequested += OnFreecamPreviewRequested;
        timeline.FreecamPreviewEnded += OnFreecamPreviewEnded;
    }

    private void OnFreecamPreviewRequested(double time)
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.ApplyFreecamPreviewAtTime(time);
    }

    private void OnFreecamPreviewEnded()
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.EndFreecamPreview();
    }
}
