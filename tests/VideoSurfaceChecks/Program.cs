using System.Numerics;
using TVBoxForWindows.Player;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
{
    var metrics = VideoSurfaceMetrics.Create(2560d / scale, 1440d / scale, scale, scale);
    Check(metrics.PixelWidth == 2560 && metrics.PixelHeight == 1440, $"{scale} 倍缩放未恢复物理分辨率");
    var restored = metrics.InverseScale * Matrix3x2.CreateScale(scale);
    Check(Math.Abs(restored.M11 - 1) < 0.00001 && Math.Abs(restored.M22 - 1) < 0.00001,
        "逆缩放后不应再次放大或裁切");
}
var differentAxes = VideoSurfaceMetrics.Create(800, 600, 1.25f, 1.5f);
Check(differentAxes.PixelWidth == 1000 && differentAxes.PixelHeight == 900, "两个方向必须独立计算");
var fractional = VideoSurfaceMetrics.Create(837.6, 612.8, 1.25f, 1.25f);
Check(fractional.PixelWidth == 1047 && fractional.PixelHeight == 766, "小数逻辑尺寸舍入错误");
Check(VideoSurfaceMetrics.Create(0, 600, 1, 1) == null, "隐藏窗口不应提交零尺寸");
Check(VideoSurfaceMetrics.Create(double.NaN, 600, 1, 1) == null, "无效布局必须跳过");
Check(VideoSurfaceMetrics.Create(double.MaxValue, 600, 2, 2) == null, "像素尺寸溢出必须跳过");
var fallback = VideoSurfaceMetrics.Create(800, 600, float.NaN, 0);
Check(fallback.PixelWidth == 800 && fallback.PixelHeight == 600 && fallback.InverseScale == Matrix3x2.Identity,
    "无效缩放应回退到原始比例");

// 使用 Windows 软件图形设备实际验证 DXGI 合成交换链的尺寸和逆缩放矩阵。
D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
    new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
using (device)
using (context)
using (var factory = CreateDXGIFactory1<IDXGIFactory2>())
using (var chain = factory.CreateSwapChainForComposition(device, new SwapChainDescription1
{
    Width = 2,
    Height = 2,
    Format = Format.B8G8R8A8_UNorm,
    BufferCount = 2,
    BufferUsage = Usage.RenderTargetOutput,
    SampleDescription = new SampleDescription(1, 0),
    SwapEffect = SwapEffect.FlipDiscard,
    Scaling = Scaling.Stretch,
    AlphaMode = AlphaMode.Premultiplied,
}))
using (var native = chain.QueryInterface<IDXGISwapChain2>())
{
    foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f, 1f })
    {
        var metrics = VideoSurfaceMetrics.Create(1280, 720, scale, scale);
        native.MatrixTransform = metrics.InverseScale;
        chain.ResizeBuffers(0, (uint)metrics.PixelWidth, (uint)metrics.PixelHeight, Format.Unknown, SwapChainFlags.None).CheckError();
        var description = chain.Description1;
        Check(description.Width == metrics.PixelWidth && description.Height == metrics.PixelHeight,
            "实际交换链未采用物理尺寸");
        var matrix = native.MatrixTransform;
        Check(Math.Abs(matrix.M11 - 1f / scale) < 0.00001 && Math.Abs(matrix.M22 - 1f / scale) < 0.00001,
            "实际交换链矩阵未正确更新");
    }
}
Console.WriteLine("视频尺寸检查通过：100%、125%、150%、200%、非等比缩放、小数尺寸和异常输入；DXGI 原生交换链尺寸与矩阵往返验证通过。");
