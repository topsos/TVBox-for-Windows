using Microsoft.UI.Dispatching;
using TVBoxForWindows.Core;
using TVBoxForWindows.Live;

namespace TVBoxForWindows.Engine;

/// <summary>在程序运行期间定时重载当前点播与直播配置。</summary>
internal sealed class ConfigAutoRefreshService
{
    const string Tag = "ConfigAutoRefresh";
    readonly ConfigRefreshSchedule _schedule = new();
    DispatcherQueueTimer _timer;

    public static ConfigAutoRefreshService Instance { get; } = new();
    public event Action Changed;

    ConfigAutoRefreshService() => _schedule.Changed += () => Changed?.Invoke();

    public void ApplySettings()
    {
        _timer ??= CreateTimer();
        _timer.Stop();
        _schedule.Configure(TimeSpan.FromMinutes(Setting.ConfigRefreshMinutes));
        if (_schedule.Enabled) _timer.Start();
    }

    DispatcherQueueTimer CreateTimer()
    {
        var timer = App.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        timer.Tick += async (_, _) => await _schedule.RunIfDueAsync(RefreshCurrentAsync);
        return timer;
    }

    public void Stop()
    {
        _timer?.Stop();
        _schedule.Configure(TimeSpan.Zero);
    }

    public string StatusText
    {
        get
        {
            if (_schedule.IsRunning)
                return _schedule.Enabled ? "正在刷新当前配置…" : "已关闭定时刷新，正在收尾当前请求…";
            var next = _schedule.NextRun is { } time
                ? $"下次刷新：{time.ToLocalTime():MM-dd HH:mm:ss}"
                : "定时刷新已关闭";
            return _schedule.LastCompleted is { } completed
                ? $"{next}\n上次执行：{completed.ToLocalTime():MM-dd HH:mm:ss}；{_schedule.LastResult}"
                : next;
        }
    }

    async Task<string> RefreshCurrentAsync()
    {
        var vod = VodConfigService.Instance;
        var live = LiveConfigService.Instance;
        // 在一轮开始时固定来源，后续加载器还会在提交前核对是否已切换。
        var vodUrl = vod.Config?.Url;
        var liveUrl = live.Config?.Url;
        var messages = new List<string>();
        bool vodApplied = false;

        if (!string.IsNullOrWhiteSpace(vodUrl))
        {
            try
            {
                vodApplied = await vod.ReloadCurrentAsync(vodUrl);
                messages.Add(vodApplied ? "点播已重载" : "点播配置已切换，跳过旧源");
            }
            catch (Exception error)
            {
                messages.Add("点播刷新失败");
                Logger.E(Tag, "点播定时刷新失败：" + error.GetType().Name);
            }
        }
        else messages.Add("未加载点播配置");

        // 关闭定时功能或退出时，不再启动本轮尚未开始的直播请求。
        if (!_schedule.Enabled) return string.Join("；", messages);
        if (string.IsNullOrWhiteSpace(liveUrl))
            messages.Add(vodApplied && live.IsSyncedToVod ? "直播已随点播同步" : "未加载直播配置");
        else if (vodApplied && live.IsSyncedToVod && string.Equals(liveUrl, vodUrl, StringComparison.OrdinalIgnoreCase))
            messages.Add("直播已随点播同步");
        else
        {
            try
            {
                var applied = await live.ReloadCurrentAsync(liveUrl);
                messages.Add(applied ? "直播已重载" : "直播配置已切换，跳过旧源");
            }
            catch (Exception error)
            {
                messages.Add("直播刷新失败");
                Logger.E(Tag, "直播定时刷新失败：" + error.GetType().Name);
            }
        }
        var result = string.Join("；", messages);
        Logger.D(Tag, result);
        return result;
    }
}
