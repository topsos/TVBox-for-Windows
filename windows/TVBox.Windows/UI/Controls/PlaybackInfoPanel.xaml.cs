using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TVBoxForWindows.Player;

namespace TVBoxForWindows.UI.Controls;

/// <summary>点播和直播共用的信息面板，仅在展开期间刷新统计。</summary>
public sealed partial class PlaybackInfoPanel : UserControl
{
    readonly DispatcherTimer _refreshTimer;
    PlayerCore _core;
    Control _returnFocus;

    public bool IsOpen => Visibility == Visibility.Visible;

    public PlaybackInfoPanel()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshInformation();
        Unloaded += (_, _) => Hide(false);
    }

    public void Show(PlayerCore core, Control returnFocus)
    {
        _core = core;
        _returnFocus = returnFocus;
        RefreshInformation();
        Visibility = Visibility.Visible;
        _refreshTimer.Start();
        CloseButton.Focus(FocusState.Programmatic);
    }

    public void Hide(bool restoreFocus = true)
    {
        _refreshTimer.Stop();
        Visibility = Visibility.Collapsed;
        _core = null;
        if (restoreFocus) _returnFocus?.Focus(FocusState.Programmatic);
        _returnFocus = null;
    }

    void RefreshInformation()
    {
        var details = _core?.GetPlaybackInformation() ?? "播放器尚未就绪。";
        if (DetailsText.Text != details) DetailsText.Text = details;
    }

    // 在输入路由结束后再关闭，避免视频叠加层在指针事件处理中被移除。
    public void QueueClose() => DispatcherQueue.TryEnqueue(() => Hide());
    void OnCloseClick(object sender, RoutedEventArgs e) => QueueClose();
    void OnBackdropTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        QueueClose();
    }
    void OnPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;
    void OnPanelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => e.Handled = true;
}
