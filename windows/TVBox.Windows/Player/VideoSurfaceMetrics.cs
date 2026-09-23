using System.Numerics;

namespace TVBoxForWindows.Player;

/// <summary>视频输出的物理像素尺寸及其到 XAML 逻辑坐标的转换。</summary>
internal sealed record VideoSurfaceMetrics(int PixelWidth, int PixelHeight, float ScaleX, float ScaleY)
{
    public Matrix3x2 InverseScale => Matrix3x2.CreateScale(1f / ScaleX, 1f / ScaleY);

    public static VideoSurfaceMetrics Create(double width, double height, float scaleX, float scaleY)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return null;
        scaleX = float.IsFinite(scaleX) && scaleX > 0 ? scaleX : 1f;
        scaleY = float.IsFinite(scaleY) && scaleY > 0 ? scaleY : 1f;
        var pixelWidth = Math.Round(width * scaleX);
        var pixelHeight = Math.Round(height * scaleY);
        if (!double.IsFinite(pixelWidth) || !double.IsFinite(pixelHeight) ||
            pixelWidth > int.MaxValue || pixelHeight > int.MaxValue) return null;
        return new(Math.Max(2, (int)pixelWidth), Math.Max(2, (int)pixelHeight), scaleX, scaleY);
    }
}
