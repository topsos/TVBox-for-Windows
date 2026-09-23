using TVBoxForWindows.Engine;

var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
var schedule = new ConfigRefreshSchedule(() => now);
int calls = 0;
Task<string> Refresh()
{
    calls++;
    return Task.FromResult("完成");
}
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

await schedule.RunIfDueAsync(Refresh);
Check(calls == 0 && schedule.NextRun == null, "默认关闭时不应发起请求");

schedule.Configure(TimeSpan.FromMinutes(1));
now += TimeSpan.FromSeconds(59);
await schedule.RunIfDueAsync(Refresh);
Check(calls == 0, "到期前不应发起请求");
now += TimeSpan.FromSeconds(1);
await schedule.RunIfDueAsync(Refresh);
Check(calls == 1 && schedule.NextRun == now.AddMinutes(1), "到期后应刷新并重新计时");

now += TimeSpan.FromMinutes(1);
var pending = new TaskCompletionSource<string>();
var running = schedule.RunIfDueAsync(() => { calls++; return pending.Task; });
now += TimeSpan.FromMinutes(10);
await schedule.RunIfDueAsync(Refresh);
Check(calls == 2 && schedule.IsRunning, "慢请求期间不能重叠刷新");
schedule.Configure(TimeSpan.FromMinutes(5));
pending.SetResult("慢请求完成");
await running;
Check(schedule.NextRun == now.AddMinutes(5), "运行中修改间隔，应在完成后使用新间隔");

now += TimeSpan.FromMinutes(5);
pending = new TaskCompletionSource<string>();
running = schedule.RunIfDueAsync(() => { calls++; return pending.Task; });
schedule.Configure(TimeSpan.Zero);
pending.SetResult("关闭前已发出的请求完成");
await running;
now += TimeSpan.FromDays(1);
await schedule.RunIfDueAsync(Refresh);
Check(calls == 3 && !schedule.IsRunning && schedule.NextRun == null, "运行中关闭后不能重启计时");

schedule.Configure(TimeSpan.FromMinutes(1));
now += TimeSpan.FromMinutes(1);
await schedule.RunIfDueAsync(() => throw new IOException("模拟网络异常"));
Check(!schedule.IsRunning && schedule.NextRun == now.AddMinutes(1) && schedule.LastResult.Contains("失败"),
    "失败后应释放运行状态，并安排下次重试");
now += TimeSpan.FromMinutes(1);
await schedule.RunIfDueAsync(Refresh);
Check(calls == 4 && schedule.LastResult == "完成", "失败后的下一周期应能恢复");

Console.WriteLine("配置刷新调度检查通过：默认关闭、到期执行、避免重叠、运行中修改间隔、运行中关闭、失败重试和恢复。");
