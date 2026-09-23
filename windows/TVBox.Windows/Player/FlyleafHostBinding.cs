using System.Numerics;
using FlyleafLib.Controls.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Vortice.DXGI;
using FlyleafSwapChain = FlyleafLib.MediaFramework.MediaRenderer.SwapChain;

namespace TVBoxForWindows.Player;

/// <summary>在 FlyleafHost 完成有效布局后绑定播放器，并同步视频布局。</summary>
public sealed class FlyleafHostBinding : IDisposable
{
    const string TAG = "FlyleafHostBinding";

    readonly FlyleafHost _host;
    PlayerCore _core;
    int _generation;
    int _renderWidth;
    int _renderHeight;
    readonly object _swapChainSync = new();
    SwapChainPanel _trackedSurface;
    FlyleafSwapChain _swapChainOwner;
    Action<IDXGISwapChain2> _swapChainCallback;
    IDXGISwapChain2 _nativeSwapChain;
    VideoSurfaceMetrics _surfaceMetrics;
    double _surfaceCornerRadius;
    Visual _surfaceVisual;
    CompositionRoundedRectangleGeometry _surfaceClipGeometry;
    CompositionGeometricClip _surfaceClip;
    bool _queued;
    bool _synchronizationSuspended;
    bool _disposed;

    public FlyleafHostBinding(FlyleafHost host)
    {
        _host = host;
        _host.Loaded += OnLoaded;
        _host.SizeChanged += OnSizeChanged;
    }

    public void Attach(PlayerCore core)
    {
        if (_disposed) return;
        if (_core != null && !ReferenceEquals(_core, core)) Detach();
        _core = core;
        _generation++;
        _renderWidth = _renderHeight = 0;
        RequestSynchronize();
    }

    public void RequestSynchronize()
    {
        if (_disposed) return;
        if (_synchronizationSuspended)
            return;
        if (_queued) return;
        _queued = true;
        var generation = _generation;
        var queue = _host.DispatcherQueue ?? App.Dispatcher;
        if (queue == null || !queue.TryEnqueue(DispatcherQueuePriority.Low, () => Synchronize(generation)))
            _queued = false;
    }

    public void BeginPresentationTransition()
    {
        if (_disposed) return;
        _synchronizationSuspended = true;
    }

    public void CancelPresentationTransition()
    {
        if (_disposed) return;
        _synchronizationSuspended = false;
    }

    /// <summary>
    /// Flushes the final swap-chain size after an AppWindow presenter transition.
    /// Normal SizeChanged traffic remains coalesced at low priority.
    /// </summary>
    public void SynchronizeAfterLayout()
    {
        if (_disposed) return;
        _synchronizationSuspended = false;
        try { _host.UpdateLayout(); }
        catch { }
        Synchronize(_generation, false);
    }

    /// <summary>
    /// Clips Flyleaf's native swap-chain surface itself. A clip on an ancestor
    /// XAML element does not reliably constrain SwapChainPanel composition.
    /// </summary>
    public void SetSurfaceCornerRadius(double radius)
    {
        if (_disposed) return;
        _surfaceCornerRadius = Math.Max(0, radius);
        ApplySurfaceClip();
        RequestSynchronize();
    }

    void OnLoaded(object sender, RoutedEventArgs e) => RequestSynchronize();

    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width >= 2 && e.NewSize.Height >= 2) RequestSynchronize();
    }

    void TrackSurface(SwapChainPanel surface)
    {
        if (ReferenceEquals(_trackedSurface, surface)) return;
        if (_trackedSurface != null)
        {
            _trackedSurface.SizeChanged -= OnSizeChanged;
            _trackedSurface.CompositionScaleChanged -= OnCompositionScaleChanged;
        }
        _trackedSurface = surface;
        _swapChainCallback = null;
        if (surface != null)
        {
            surface.SizeChanged += OnSizeChanged;
            surface.CompositionScaleChanged += OnCompositionScaleChanged;
        }
    }

    void OnCompositionScaleChanged(SwapChainPanel sender, object args) => RequestSynchronize();

    void BindSwapChain(FlyleafSwapChain owner, SwapChainPanel surface, IDXGISwapChain2 native)
    {
        // 回调也会在设备重建时触发；只使用已缓存的布局，避免后台线程读取 XAML 属性。
        using var panel = SharpGen.Runtime.ComObject.As<Vortice.WinUI.ISwapChainPanelNative2>(surface);
        panel.SetSwapChain(native);
        lock (_swapChainSync)
        {
            _nativeSwapChain?.Dispose();
            _nativeSwapChain = null;
            if (native == null) return;
            // 单独持有一个 COM 引用，Flyleaf 仍负责回调参数原有引用的生命周期。
            _nativeSwapChain = native.QueryInterface<IDXGISwapChain2>();
            var metrics = Volatile.Read(ref _surfaceMetrics);
            if (metrics == null) return;
            _nativeSwapChain.MatrixTransform = metrics.InverseScale;
            owner.Resize(metrics.PixelWidth, metrics.PixelHeight);
        }
    }

    void Synchronize(int generation, bool queued = true)
    {
        if (queued) _queued = false;
        if (_synchronizationSuspended)
            return;
        if (_disposed || generation != _generation || _core?.Fly == null || !_host.IsLoaded) return;
        var width = _host.ActualWidth;
        var height = _host.ActualHeight;
        if (width < 2 || height < 2) return;
        try
        {
            if (!ReferenceEquals(_host.Player, _core.Fly))
            {
                _host.Player = _core.Fly;
                ApplySurfaceClip();
                // Allow FlyleafHost to finish SetupWinUI before applying the video layout.
                RequestSynchronize();
                return;
            }
            ApplySurfaceClip();
            var surface = _host.SCP;
            if (surface == null) return;
            TrackSurface(surface);
            var metrics = VideoSurfaceMetrics.Create(surface.ActualWidth, surface.ActualHeight,
                surface.CompositionScaleX, surface.CompositionScaleY);
            if (metrics == null) return;
            var previousMetrics = Volatile.Read(ref _surfaceMetrics);
            Volatile.Write(ref _surfaceMetrics, metrics);
            var resized = metrics != previousMetrics || metrics.PixelWidth != _renderWidth || metrics.PixelHeight != _renderHeight;
            var swapChain = _core.Fly.Renderer?.SwapChain;
            if (swapChain == null) return;
            if (!ReferenceEquals(_swapChainOwner, swapChain) || _swapChainCallback == null)
            {
                _swapChainOwner = swapChain;
                _swapChainCallback = native => BindSwapChain(swapChain, surface, native);
            }
            // 使用公开回调绑定原生交换链，补上高 DPI 所需的逆缩放矩阵。
            // 相同回调不会重复创建交换链；设备丢失后仍会通过它重新绑定。
            swapChain.SetupWinUI(_swapChainCallback);
            lock (_swapChainSync)
            {
                if (_nativeSwapChain == null) return;
                _nativeSwapChain.MatrixTransform = metrics.InverseScale;
                // FlyleafHost 的 SizeChanged 会写入逻辑尺寸，每次布局后都重新确认物理尺寸。
                swapChain.Resize(metrics.PixelWidth, metrics.PixelHeight);
            }
            _renderWidth = metrics.PixelWidth;
            _renderHeight = metrics.PixelHeight;
            _core.SetVideoSurfaceMetrics(metrics);
            _core.RefreshVideoLayout();
            if (resized)
            {
                var renderer = _core.Fly.Renderer;
                Core.Logger.D(TAG, $"渲染尺寸：逻辑={surface.ActualWidth:0.##}x{surface.ActualHeight:0.##}，缩放={metrics.ScaleX:0.##}x{metrics.ScaleY:0.##}，目标像素={_renderWidth}x{_renderHeight}，当前渲染={renderer?.ControlWidth ?? 0}x{renderer?.ControlHeight ?? 0}");
            }
        }
        catch (Exception e) { Core.Logger.E(TAG, "同步视频布局失败: " + e.Message); }
    }

    void ApplySurfaceClip()
    {
        var surface = _host.SCP;
        if (surface == null) return;

        var visual = ElementCompositionPreview.GetElementVisual(surface);
        if (_surfaceCornerRadius <= 0)
        {
            visual.Clip = null;
            return;
        }

        var width = surface.ActualWidth;
        var height = surface.ActualHeight;
        if (width <= 0 || height <= 0) return;

        if (!ReferenceEquals(_surfaceVisual, visual))
        {
            _surfaceVisual = visual;
            _surfaceClipGeometry = visual.Compositor.CreateRoundedRectangleGeometry();
            _surfaceClip = visual.Compositor.CreateGeometricClip(_surfaceClipGeometry);
        }

        var offsetX = 0d;
        var offsetY = 0d;
        var clipWidth = width;
        var clipHeight = height;
        try
        {
            var scale = surface.XamlRoot?.RasterizationScale ?? 1d;
            var origin = surface.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var left = Math.Ceiling(origin.X * scale - 0.001d) / scale;
            var top = Math.Ceiling(origin.Y * scale - 0.001d) / scale;
            var right = Math.Floor((origin.X + width) * scale + 0.001d) / scale;
            var bottom = Math.Floor((origin.Y + height) * scale + 0.001d) / scale;
            if (right > left && bottom > top)
            {
                offsetX = left - origin.X;
                offsetY = top - origin.Y;
                clipWidth = right - left;
                clipHeight = bottom - top;
            }
        }
        catch { }

        _surfaceClipGeometry.Offset = new Vector2((float)offsetX, (float)offsetY);
        _surfaceClipGeometry.Size = new Vector2((float)clipWidth, (float)clipHeight);
        _surfaceClipGeometry.CornerRadius = new Vector2((float)_surfaceCornerRadius);
        visual.Clip = _surfaceClip;
    }

    public void Detach()
    {
        if (_disposed) return;
        _generation++;
        _queued = false;
        _synchronizationSuspended = false;
        _renderWidth = _renderHeight = 0;
        _core?.SetVideoSurfaceMetrics(null);
        if (_surfaceVisual != null) _surfaceVisual.Clip = null;
        try { _host.Player = null; } catch { }
        TrackSurface(null);
        lock (_swapChainSync)
        {
            _nativeSwapChain?.Dispose();
            _nativeSwapChain = null;
        }
        _swapChainOwner = null;
        Volatile.Write(ref _surfaceMetrics, null);
        _core = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Detach();
        _disposed = true;
        _host.Loaded -= OnLoaded;
        _host.SizeChanged -= OnSizeChanged;
    }
}
