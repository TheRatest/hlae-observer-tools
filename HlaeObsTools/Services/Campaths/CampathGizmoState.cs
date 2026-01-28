using System.Numerics;

namespace HlaeObsTools.Services.Campaths;

public readonly struct CampathGizmoState
{
    public CampathGizmoState(bool visible, Vector3 position, Quaternion rotation, bool useLocalSpace)
    {
        Visible = visible;
        Position = position;
        Rotation = rotation;
        UseLocalSpace = useLocalSpace;
    }

    public bool Visible { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public bool UseLocalSpace { get; }
}
