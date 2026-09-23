namespace TVBoxForWindows.Engine;

/// <summary>配置刷新计时状态；由界面线程调用，一轮完成后再计算下次时间。</summary>
internal sealed class ConfigRefreshSchedule
{
    readonly Func<DateTimeOffset> _now;
    TimeSpan _interval;

    internal ConfigRefreshSchedule(Func<DateTimeOffset> now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    public bool IsRunning { get; private set; }
    public bool Enabled => _interval > TimeSpan.Zero;
    public DateTimeOffset? NextRun { get; private set; }
    public DateTimeOffset? LastCompleted { get; private set; }
    public string LastResult { get; private set; }
    public event Action Changed;

    public void Configure(TimeSpan interval)
    {
        _interval = interval > TimeSpan.Zero ? interval : TimeSpan.Zero;
        NextRun = Enabled ? _now() + _interval : null;
        Changed?.Invoke();
    }

    public async Task RunIfDueAsync(Func<Task<string>> refresh)
    {
        if (!Enabled || IsRunning || NextRun == null || _now() < NextRun.Value) return;
        IsRunning = true;
        NextRun = null;
        try
        {
            Changed?.Invoke();
            LastResult = await refresh();
        }
        catch (Exception)
        {
            LastResult = "本轮刷新失败，下个周期重试";
        }
        finally
        {
            LastCompleted = _now();
            IsRunning = false;
            NextRun = Enabled ? _now() + _interval : null;
            Changed?.Invoke();
        }
    }
}
