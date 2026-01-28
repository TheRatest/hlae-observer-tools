using System.Collections.Generic;
using System.Numerics;

namespace HlaeObsTools.Services.Campaths;

public readonly struct CampathOverlayVertex
{
    public CampathOverlayVertex(Vector3 position, Vector3 color)
    {
        Position = position;
        Color = color;
    }

    public Vector3 Position { get; }
    public Vector3 Color { get; }
}

public sealed class CampathOverlayData
{
    public CampathOverlayData(IReadOnlyList<CampathOverlayVertex> vertices)
    {
        Vertices = vertices ?? new List<CampathOverlayVertex>();
    }

    public IReadOnlyList<CampathOverlayVertex> Vertices { get; }
}
