namespace HlaeObsTools.ViewModels;

/// <summary>
/// Shared settings for the 3D viewport.
/// </summary>
public sealed class Viewport3DSettings : ViewModelBase
{
    private string _mapObjPath = string.Empty;

    /// <summary>
    /// Path to the .obj map file.
    /// </summary>
    public string MapObjPath
    {
        get => _mapObjPath;
        set => SetProperty(ref _mapObjPath, value ?? string.Empty);
    }
}
