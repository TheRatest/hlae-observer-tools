namespace HlaeObsTools.ViewModels;

/// <summary>
/// Shared settings for the 3D viewport.
/// </summary>
public sealed class Viewport3DSettings : ViewModelBase
{
    private string _mapObjPath = string.Empty;
    private bool _useAltPlayerBinds;
    private float _pinScale = 1.0f;
    private float _pinOffsetZ;
    private float _viewportMouseScale = 1.0f;
    private float _mapScale = 1.0f;
    private float _mapYaw;
    private float _mapPitch;
    private float _mapRoll;
    private float _mapOffsetX;
    private float _mapOffsetY;
    private float _mapOffsetZ;

    /// <summary>
    /// Path to the .obj map file.
    /// </summary>
    public string MapObjPath
    {
        get => _mapObjPath;
        set => SetProperty(ref _mapObjPath, value ?? string.Empty);
    }

    /// <summary>
    /// Whether to use alternative player bind labels (Q,E,R,T,Z for slots 6-0).
    /// </summary>
    public bool UseAltPlayerBinds
    {
        get => _useAltPlayerBinds;
        set => SetProperty(ref _useAltPlayerBinds, value);
    }

    /// <summary>
    /// Scale factor for player pins in the 3D viewport.
    /// </summary>
    public float PinScale
    {
        get => _pinScale;
        set => SetProperty(ref _pinScale, value);
    }

    /// <summary>
    /// Pin offset in world space (Z).
    /// </summary>
    public float PinOffsetZ
    {
        get => _pinOffsetZ;
        set => SetProperty(ref _pinOffsetZ, value);
    }

    /// <summary>
    /// Mouse sensitivity scale for the 3D viewport freecam.
    /// </summary>
    public float ViewportMouseScale
    {
        get => _viewportMouseScale;
        set => SetProperty(ref _viewportMouseScale, value);
    }

    /// <summary>
    /// Uniform scale for the map mesh.
    /// </summary>
    public float MapScale
    {
        get => _mapScale;
        set => SetProperty(ref _mapScale, value);
    }

    /// <summary>
    /// Map yaw rotation (degrees).
    /// </summary>
    public float MapYaw
    {
        get => _mapYaw;
        set => SetProperty(ref _mapYaw, value);
    }

    /// <summary>
    /// Map pitch rotation (degrees).
    /// </summary>
    public float MapPitch
    {
        get => _mapPitch;
        set => SetProperty(ref _mapPitch, value);
    }

    /// <summary>
    /// Map roll rotation (degrees).
    /// </summary>
    public float MapRoll
    {
        get => _mapRoll;
        set => SetProperty(ref _mapRoll, value);
    }

    /// <summary>
    /// Map offset (X).
    /// </summary>
    public float MapOffsetX
    {
        get => _mapOffsetX;
        set => SetProperty(ref _mapOffsetX, value);
    }

    /// <summary>
    /// Map offset (Y).
    /// </summary>
    public float MapOffsetY
    {
        get => _mapOffsetY;
        set => SetProperty(ref _mapOffsetY, value);
    }

    /// <summary>
    /// Map offset (Z).
    /// </summary>
    public float MapOffsetZ
    {
        get => _mapOffsetZ;
        set => SetProperty(ref _mapOffsetZ, value);
    }
}
