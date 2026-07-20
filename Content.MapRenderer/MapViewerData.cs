using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.MapRenderer;

public sealed class MapViewerData
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>SS14.MapViewer reads displayName for the selector label.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Default view orientation: primary grid world rotation in radians (SS14, CCW from +X).
    /// OpenLayers viewers should use <c>-rotation</c> (OL is clockwise-positive).
    /// </summary>
    public double Rotation { get; set; }

    public List<GridLayer> Grids { get; set; } = new();
    public string? Attributions { get; set; }
    public List<LayerGroup> ParallaxLayers { get; set; } = new();
}

public sealed class GridLayer
{
    public string GridId { get; set; } = string.Empty;
    public Position Offset { get; set; }
    public bool Tiled { get; set; } = false;
    public string Url { get; set; }
    public Extent Extent { get; set; }

    /// <summary>World rotation in radians (SS14/CCW) before the grid was zeroed for rendering.</summary>
    public double Rotation { get; set; }

    public GridLayer(RenderedGridImage<Rgba32> gridImage, string url)
    {
        //Get the internal _uid as string
        if (gridImage.GridUid.HasValue)
            GridId = gridImage.GridUid.Value.GetHashCode().ToString();

        Offset = new Position(gridImage.Offset);
        Extent = new Extent(gridImage.Image.Width, gridImage.Image.Height);
        Rotation = gridImage.Rotation;
        // MapViewer URLs use forward slashes
        Url = url.Replace('\\', '/');
    }
}

public sealed class LayerGroup
{
    public Position Scale { get; set; } = Position.One();
    public Position Offset { get; set; } = Position.Zero();
    public bool Static { get; set; } = false;
    public float? MinScale { get; set; }
    public GroupSource Source { get; set; } = new();
    public List<Layer> Layers { get; set; } = new();

    public static LayerGroup DefaultParallax(IResourceManager resourceManager, ParallaxOutput output)
    {
        return new LayerGroup
        {
            Scale = new Position(0.1f, 0.1f),
            Source = new GroupSource
            {
                Url = output.ReferenceResourceFile(resourceManager, new ResPath("/Textures/Parallaxes/layer1.png")),
                Extent = new Extent(6000, 4000),
            },
            Layers = new List<Layer>
            {
                new()
                {
                    Url = output.ReferenceResourceFile(resourceManager, new ResPath("/Textures/Parallaxes/layer1.png")),
                },
                new()
                {
                    Url = output.ReferenceResourceFile(resourceManager, new ResPath("/Textures/Parallaxes/layer2.png")),
                    Composition = "lighter",
                    ParallaxScale = new Position(0.2f, 0.2f)
                },
                new()
                {
                    Url = output.ReferenceResourceFile(resourceManager, new ResPath("/Textures/Parallaxes/layer3.png")),
                    Composition = "lighter",
                    ParallaxScale = new Position(0.3f, 0.3f)
                }
            }
        };
    }
}

public sealed class GroupSource
{
    public string Url { get; set; } = string.Empty;
    public Extent Extent { get; set; } = new();
}

public sealed class Layer
{
    public string Url { get; set; } = string.Empty;
    public string Composition { get; set; } = "source-over";
    public Position ParallaxScale { get; set; } = new(0.1f, 0.1f);
}

/// <summary>
/// Pixel rect serialized as SS14.MapViewer expects: { "a": { "x", "y" }, "b": { "x", "y" } }.
/// </summary>
[JsonConverter(typeof(ExtentJsonConverter))]
public readonly struct Extent
{
    public readonly float X1;
    public readonly float Y1;
    public readonly float X2;
    public readonly float Y2;

    public Extent()
    {
        X1 = 0;
        Y1 = 0;
        X2 = 0;
        Y2 = 0;
    }

    public Extent(float x2, float y2)
    {
        X1 = 0;
        Y1 = 0;
        X2 = x2;
        Y2 = y2;
    }

    public Extent(float x1, float y1, float x2, float y2)
    {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
    }
}

public sealed class ExtentJsonConverter : JsonConverter<Extent>
{
    public override Extent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.TryGetProperty("a", out var a) && root.TryGetProperty("b", out var b))
        {
            return new Extent(
                a.GetProperty("x").GetSingle(),
                a.GetProperty("y").GetSingle(),
                b.GetProperty("x").GetSingle(),
                b.GetProperty("y").GetSingle());
        }

        // Legacy PascalCase X1/Y1/X2/Y2
        return new Extent(
            root.GetProperty("X1").GetSingle(),
            root.GetProperty("Y1").GetSingle(),
            root.GetProperty("X2").GetSingle(),
            root.GetProperty("Y2").GetSingle());
    }

    public override void Write(Utf8JsonWriter writer, Extent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("a");
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X1);
        writer.WriteNumber("y", value.Y1);
        writer.WriteEndObject();
        writer.WritePropertyName("b");
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X2);
        writer.WriteNumber("y", value.Y2);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}

[JsonConverter(typeof(PositionJsonConverter))]
public readonly struct Position
{
    public readonly float X;
    public readonly float Y;

    public Position(float x, float y)
    {
        X = x;
        Y = y;
    }

    public Position(Vector2 vector2)
    {
        X = vector2.X;
        Y = vector2.Y;
    }

    public static Position Zero()
    {
        return new Position(0, 0);
    }

    public static Position One()
    {
        return new Position(0, 0);
    }
}

public sealed class PositionJsonConverter : JsonConverter<Position>
{
    public override Position Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var x = root.TryGetProperty("x", out var xEl) ? xEl.GetSingle() : root.GetProperty("X").GetSingle();
        var y = root.TryGetProperty("y", out var yEl) ? yEl.GetSingle() : root.GetProperty("Y").GetSingle();
        return new Position(x, y);
    }

    public override void Write(Utf8JsonWriter writer, Position value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteEndObject();
    }
}

public static class MapViewerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize(MapViewerData data) =>
        JsonSerializer.Serialize(data, Options);
}
