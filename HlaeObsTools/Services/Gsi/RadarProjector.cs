using System;

namespace HlaeObsTools.Services.Gsi;

public sealed class RadarProjector
{
    private readonly RadarConfigProvider _configProvider;

    public RadarProjector(RadarConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    public bool TryProject(string? mapName, Vec3 worldPos, out double relX, out double relY, out string level)
    {
        return TryProject(mapName, worldPos, null, out relX, out relY, out level);
    }

    public bool TryProject(string? mapName, Vec3 worldPos, string? forcedLevel, out double relX, out double relY, out string level)
    {
        relX = relY = 0;
        level = "default";

        if (!_configProvider.TryGet(mapName, out var config) || config.Scale == 0)
            return false;

        double offsetX = 0;
        double offsetY = 0;

        if (config.Levels.Count > 0)
        {
            RadarLevel? selected = null;
            if (!string.IsNullOrWhiteSpace(forcedLevel))
            {
                foreach (var lvl in config.Levels)
                {
                    if (string.Equals(lvl.Name, forcedLevel, StringComparison.OrdinalIgnoreCase))
                    {
                        selected = lvl;
                        break;
                    }
                }
            }

            if (selected == null)
            {
                foreach (var lvl in config.Levels)
                {
                    if (worldPos.Z > lvl.AltitudeMin)
                    {
                        selected = lvl;
                        break;
                    }
                }
            }

            if (selected != null)
            {
                level = selected.Name;
                offsetX = selected.OffsetX;
                offsetY = selected.OffsetY;
            }
        }

        relX = ((worldPos.X - config.PosX) / config.Scale + offsetX) / 1024.0;
        relY = ((worldPos.Y - config.PosY) / -config.Scale + offsetY) / 1024.0;
        return true;
    }
}
