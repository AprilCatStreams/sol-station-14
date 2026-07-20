using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.MapRenderer;

public sealed class RenderedGridImage<T> where T : unmanaged, IPixel<T>
{
    public Image<T> Image;
    public Vector2 Offset { get; set; } = Vector2.Zero;
    public EntityUid? GridUid { get; set; }

    /// <summary>
    /// World rotation (radians, SS14/CCW) before MapRenderer zeroes the grid for painting.
    /// </summary>
    public double Rotation { get; set; }

    public RenderedGridImage(Image<T> image)
    {
        Image = image;
    }
}
