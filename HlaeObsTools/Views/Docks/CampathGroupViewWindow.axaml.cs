using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using HlaeObsTools.ViewModels.Docks;
using System.Threading.Tasks;

namespace HlaeObsTools.Views.Docks;

public partial class CampathGroupViewWindow : Window
{
    // Parameterless ctor for XAML loader
    public CampathGroupViewWindow()
    {
        InitializeComponent();
    }

    public CampathGroupViewWindow(CampathsDockViewModel ownerVm, CampathGroupViewModel group)
    {
        InitializeComponent();
        DataContext = new CampathGroupViewWindowVm(ownerVm, group);
    }
}

public class CampathGroupViewWindowVm
{
    private readonly CampathsDockViewModel _ownerVm;
    private readonly CampathGroupViewModel _groupVm;

    public ObservableCollection<CampathInGroupItem> CampathItems { get; }

    public string GroupName => _groupVm.Name;
    public string ModeText => _groupVm.Mode.ToString();

    public ICommand MoveCampathUpCommand { get; }
    public ICommand MoveCampathDownCommand { get; }
    public ICommand RemoveCampathFromGroupCommand { get; }

    public CampathGroupViewWindowVm(CampathsDockViewModel ownerVm, CampathGroupViewModel groupVm)
    {
        _ownerVm = ownerVm;
        _groupVm = groupVm;

        CampathItems = new ObservableCollection<CampathInGroupItem>(
            _groupVm.CampathIds.Select(id => new CampathInGroupItem(id, ResolveName(id))));

        MoveCampathUpCommand = new DelegateCommand(param => { Move(param, -1); return Task.CompletedTask; });
        MoveCampathDownCommand = new DelegateCommand(param => { Move(param, 1); return Task.CompletedTask; });
        RemoveCampathFromGroupCommand = new DelegateCommand(param => { Remove(param); return Task.CompletedTask; });
    }

    private string ResolveName(Guid id)
    {
        var profile = _ownerVm.SelectedProfile;
        var item = profile?.Campaths.FirstOrDefault(c => c.Id == id);
        return item?.Name ?? id.ToString();
    }

    private void Move(object? param, int delta)
    {
        if (param is not CampathInGroupItem item)
            return;

        var idx = CampathItems.IndexOf(item);
        var newIdx = Math.Clamp(idx + delta, 0, CampathItems.Count - 1);
        if (newIdx == idx)
            return;

        CampathItems.RemoveAt(idx);
        CampathItems.Insert(newIdx, item);

        _groupVm.MoveCampath(idx, newIdx);
        _ownerVm.Save();
    }

    private void Remove(object? param)
    {
        if (param is not CampathInGroupItem item)
            return;

        CampathItems.Remove(item);
        _groupVm.RemoveCampath(item.Id);
        _ownerVm.Save();
    }
}

public record CampathInGroupItem(Guid Id, string Name);
