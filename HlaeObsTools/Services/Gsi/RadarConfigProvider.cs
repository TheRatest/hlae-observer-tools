using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using Avalonia;
using Avalonia.Platform;

namespace HlaeObsTools.Services.Gsi;

public sealed class RadarConfig
{
    public string MapName { get; init; } = string.Empty;
    public double PosX { get; init; }
    public double PosY { get; init; }
    public double Scale { get; init; }
    public bool TransparentBackground { get; init; }
    public string? ImagePath { get; init; }
    public IReadOnlyList<RadarLevel> Levels { get; init; } = Array.Empty<RadarLevel>();
}

public sealed class RadarLevel
{
    public string Name { get; init; } = "default";
    public double AltitudeMin { get; init; }
    public double AltitudeMax { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
}

/// <summary>
/// Loads radar metadata (pos/scale/image) from the bundled radars.json.
/// </summary>
public sealed class RadarConfigProvider
{
    private readonly Dictionary<string, RadarConfig> _configs = new(StringComparer.OrdinalIgnoreCase);

    public RadarConfigProvider()
    {
        LoadConfigs();
    }

    public bool TryGet(string? mapName, out RadarConfig config)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            config = null!;
            return false;
        }

        var key = Sanitize(mapName);
        return _configs.TryGetValue(key, out config!);
    }

    private void LoadConfigs()
    {
        try
        {
            var uri = new Uri("avares://HlaeObsTools/Assets/hud/radars.json");
            using var asset = AssetLoader.Open(uri);
            using var reader = new StreamReader(asset);
            var json = reader.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var name = entry.Name;
                var obj = entry.Value;
                var posX = obj.TryGetProperty("pos_x", out var px) ? GetDouble(px) : 0;
                var posY = obj.TryGetProperty("pos_y", out var py) ? GetDouble(py) : 0;
                var scale = obj.TryGetProperty("scale", out var sc) ? GetDouble(sc) : 1;
                var transparent = obj.TryGetProperty("radarImageTransparentBackgrond", out var tb) && tb.GetBoolean();
                string? imageUrl = obj.TryGetProperty("radarImageUrl", out var ru) ? ru.GetString() : null;

                var levels = new List<RadarLevel>();
                if (obj.TryGetProperty("verticalsections", out var vsElem))
                {
                    foreach (var level in vsElem.EnumerateObject())
                    {
                        var levelObj = level.Value;
                        var altMin = levelObj.TryGetProperty("AltitudeMin", out var altMinProp) ? GetDouble(altMinProp) : 0;
                        var altMax = levelObj.TryGetProperty("AltitudeMax", out var altMaxProp) ? GetDouble(altMaxProp) : 0;
                        var offsetX = levelObj.TryGetProperty("OffsetX", out var offsetXProp) ? GetDouble(offsetXProp) : 0;
                        var offsetY = levelObj.TryGetProperty("OffsetY", out var offsetYProp) ? GetDouble(offsetYProp) : 0;

                        levels.Add(new RadarLevel
                        {
                            Name = level.Name,
                            AltitudeMin = altMin,
                            AltitudeMax = altMax,
                            OffsetX = offsetX,
                            OffsetY = offsetY
                        });
                    }
                }

                _configs[Sanitize(name)] = new RadarConfig
                {
                    MapName = name,
                    PosX = posX,
                    PosY = posY,
                    Scale = scale,
                    TransparentBackground = transparent,
                    ImagePath = imageUrl,
                    Levels = levels.OrderByDescending(l => l.AltitudeMin).ToList()
                };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load radar configs: {ex.Message}");
        }
    }

    public static string Sanitize(string mapName)
    {
        return mapName.Trim().ToLowerInvariant();
    }

    private static double GetDouble(JsonElement elem)
    {
        try
        {
            return elem.ValueKind switch
            {
                JsonValueKind.Number => elem.GetDouble(),
                JsonValueKind.String when double.TryParse(elem.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
                _ => 0d
            };
        }
        catch
        {
            return 0d;
        }
    }
}
