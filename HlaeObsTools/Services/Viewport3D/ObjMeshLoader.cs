using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace HlaeObsTools.Services.Viewport3D;

public sealed class ObjMesh
{
    public ObjMesh(float[] vertices, int vertexCount, Vector3 min, Vector3 max)
    {
        Vertices = vertices;
        VertexCount = vertexCount;
        Min = min;
        Max = max;
    }

    public float[] Vertices { get; }
    public int VertexCount { get; }
    public Vector3 Min { get; }
    public Vector3 Max { get; }
}

public static class ObjMeshLoader
{
    public static bool TryLoad(string path, out ObjMesh? mesh, out string? error)
    {
        mesh = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "File not found.";
            return false;
        }

        var positions = new List<Vector3>();
        var vertices = new List<float>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    continue;

                var span = trimmed.AsSpan();
                var idx = 0;
                if (!TryReadToken(span, ref idx, out var tag))
                    continue;

                if (tag.SequenceEqual("v".AsSpan()))
                {
                    if (!TryReadToken(span, ref idx, out var sx) ||
                        !TryReadToken(span, ref idx, out var sy) ||
                        !TryReadToken(span, ref idx, out var sz))
                    {
                        continue;
                    }

                    if (!TryParseFloat(sx, out var x) ||
                        !TryParseFloat(sy, out var y) ||
                        !TryParseFloat(sz, out var z))
                    {
                        continue;
                    }

                    positions.Add(new Vector3(x, y, z));
                    continue;
                }

                if (tag.SequenceEqual("f".AsSpan()))
                {
                    var indices = new List<int>();
                    while (TryReadToken(span, ref idx, out var token))
                    {
                        if (!TryParseIndex(token, positions.Count, out var index))
                            continue;

                        indices.Add(index);
                    }

                    if (indices.Count < 3)
                        continue;

                    var first = indices[0];
                    for (var i = 1; i < indices.Count - 1; i++)
                    {
                        var a = positions[first];
                        var b = positions[indices[i]];
                        var c = positions[indices[i + 1]];
                        var normal = Vector3.Cross(b - a, c - a);
                        if (normal.LengthSquared() > 0.000001f)
                            normal = Vector3.Normalize(normal);
                        else
                            normal = Vector3.UnitZ;

                        AppendVertex(a, normal, vertices, ref min, ref max);
                        AppendVertex(b, normal, vertices, ref min, ref max);
                        AppendVertex(c, normal, vertices, ref min, ref max);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Failed to read OBJ: {ex.Message}";
            return false;
        }

        if (vertices.Count == 0)
        {
            error = "No faces found.";
            return false;
        }

        var vertexCount = vertices.Count / 6;
        mesh = new ObjMesh(vertices.ToArray(), vertexCount, min, max);
        return true;
    }

    private static bool TryReadToken(ReadOnlySpan<char> span, ref int index, out ReadOnlySpan<char> token)
    {
        while (index < span.Length && char.IsWhiteSpace(span[index]))
            index++;

        if (index >= span.Length)
        {
            token = default;
            return false;
        }

        var start = index;
        while (index < span.Length && !char.IsWhiteSpace(span[index]))
            index++;

        token = span.Slice(start, index - start);
        return true;
    }

    private static bool TryParseFloat(ReadOnlySpan<char> value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseIndex(ReadOnlySpan<char> token, int count, out int index)
    {
        index = 0;
        if (count == 0)
            return false;

        var slash = token.IndexOf('/');
        var raw = slash >= 0 ? token[..slash] : token;
        if (raw.Length == 0)
            return false;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return false;

        if (parsed < 0)
            parsed = count + parsed + 1;

        parsed -= 1;
        if (parsed < 0 || parsed >= count)
            return false;

        index = parsed;
        return true;
    }

    private static void AppendVertex(Vector3 position, Vector3 normal, List<float> vertices, ref Vector3 min, ref Vector3 max)
    {
        vertices.Add(position.X);
        vertices.Add(position.Y);
        vertices.Add(position.Z);
        vertices.Add(normal.X);
        vertices.Add(normal.Y);
        vertices.Add(normal.Z);

        min = Vector3.Min(min, position);
        max = Vector3.Max(max, position);
    }
}
