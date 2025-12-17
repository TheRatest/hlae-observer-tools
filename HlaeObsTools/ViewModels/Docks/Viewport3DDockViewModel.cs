using System;
using System.ComponentModel;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class Viewport3DDockViewModel : Tool, IDisposable
{
    private readonly Viewport3DSettings _settings;

    public Viewport3DDockViewModel(Viewport3DSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;

        Title = "3D Viewport";
        CanFloat = true;
        CanPin = true;
    }

    public string MapObjPath
    {
        get => _settings.MapObjPath;
        set
        {
            if (_settings.MapObjPath != value)
            {
                _settings.MapObjPath = value ?? string.Empty;
                OnPropertyChanged();
            }
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.MapObjPath))
            OnPropertyChanged(nameof(MapObjPath));
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
