using System;

namespace HlaeObsTools.ViewModels;

public sealed class VmixReplaySettings : ViewModelBase
{
    private bool _enabled;
    private string _host = "127.0.0.1";
    private int _port = 8088;
    private double _preSeconds = 2.0;
    private double _postSeconds = 2.0;
    private double _extendWindowSeconds = 3.0;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value ?? string.Empty);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    /// <summary>
    /// Seconds before the first kill to include.
    /// </summary>
    public double PreSeconds
    {
        get => _preSeconds;
        set => SetProperty(ref _preSeconds, Math.Max(0, value));
    }

    /// <summary>
    /// Seconds after the last kill to include.
    /// </summary>
    public double PostSeconds
    {
        get => _postSeconds;
        set => SetProperty(ref _postSeconds, Math.Max(0, value));
    }

    /// <summary>
    /// If another kill happens within this window (seconds) we extend the same replay.
    /// </summary>
    public double ExtendWindowSeconds
    {
        get => _extendWindowSeconds;
        set => SetProperty(ref _extendWindowSeconds, Math.Max(0, value));
    }
}
