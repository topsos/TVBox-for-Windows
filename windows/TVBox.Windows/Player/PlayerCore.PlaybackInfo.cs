using System.Text;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Player;

public partial class PlayerCore
{
    VideoSurfaceMetrics _videoSurfaceMetrics;

    internal void SetVideoSurfaceMetrics(VideoSurfaceMetrics metrics) => Volatile.Write(ref _videoSurfaceMetrics, metrics);

    /// <summary>供播放页读取当前媒体信息；切源期间不展示上一条媒体的统计。</summary>
    public string GetPlaybackInformation()
    {
        if (_disposed || Fly == null) return "播放器尚未就绪。";
        if (_item == null) return "尚未打开媒体。";

        var generation = Volatile.Read(ref _openGeneration);
        var text = new StringBuilder();
        var scheme = UrlUtil.Scheme(_item.Url);
        text.AppendLine($"媒体协议：{(string.IsNullOrEmpty(scheme) ? "本地文件" : scheme.ToUpperInvariant())}");
        if (scheme == "rtsp" && Fly.Config.Demuxer.FormatOpt.TryGetValue("rtsp_transport", out var transport))
            text.AppendLine($"RTSP 传输（本次配置）：{transport.ToUpperInvariant()}");

        if (Volatile.Read(ref _openingGeneration) != 0 ||
            Volatile.Read(ref _activePlaybackGeneration) != generation)
        {
            text.AppendLine("媒体尚未就绪，等待成功打开后显示详细信息。");
            return text.ToString();
        }

        try
        {
            string state = Fly.Status.ToString() switch
            {
                "Playing" => IsBuffering ? "缓冲中" : "播放中",
                "Paused" => "已暂停",
                "Ended" => "播放结束",
                "Stopped" => "已停止",
                "Failed" => "播放失败",
                "Opening" => "正在打开",
                _ => "已就绪",
            };
            text.AppendLine($"播放状态：{state}");
            text.AppendLine($"容器格式：{InfoValue(Fly.VideoDemuxer?.Name)}");
            text.AppendLine($"播放速度：{Speed:0.##} 倍");
            text.AppendLine($"已缓冲：{Math.Max(0, Fly.BufferedDuration) / (double)TimeSpan.TicksPerSecond:0.00} 秒");
            text.AppendLine($"媒体接收速度：{FormatTransferRate(_transferBytesPerSecond)}");
            var output = Volatile.Read(ref _videoSurfaceMetrics);
            if (output != null)
            {
                text.AppendLine($"显示缩放：{output.ScaleX * 100:0.##}% × {output.ScaleY * 100:0.##}%");
                text.AppendLine($"目标渲染尺寸：{output.PixelWidth} × {output.PixelHeight}");
                var renderer = Fly.Renderer;
                text.AppendLine($"当前渲染尺寸：{renderer?.ControlWidth ?? 0} × {renderer?.ControlHeight ?? 0}");
            }

            var video = Fly.Video;
            text.AppendLine();
            text.AppendLine("视频");
            if (video?.IsOpened == true)
            {
                text.AppendLine($"编码：{InfoValue(video.Codec)}");
                text.AppendLine($"分辨率：{(video.Width > 0 && video.Height > 0 ? $"{video.Width} × {video.Height}" : "未知")}");
                text.AppendLine($"源帧率：{InfoRate(video.FPS, "帧/秒")}");
                text.AppendLine($"当前呈现帧率：{Math.Max(0, video.FPSCurrent):0.##} 帧/秒");
                text.AppendLine($"视频码率（统计）：{InfoBitRate(video.BitRate)}");
                text.AppendLine($"解码方式：{(video.VideoAcceleration ? "硬件解码" : "软件解码")}");
                text.AppendLine($"像素格式：{InfoValue(video.PixelFormat)}");
                text.AppendLine($"动态范围：{(video.HDRFormat.ToString() == "None" ? "未报告 HDR" : video.HDRFormat.ToString())}");
                text.AppendLine($"已呈现 / 丢弃帧：{video.FramesDisplayed} / {video.FramesDropped}");
            }
            else text.AppendLine("未打开视频轨道");

            var audio = Fly.Audio;
            text.AppendLine();
            text.AppendLine("音频");
            if (audio?.IsOpened == true)
            {
                text.AppendLine($"编码：{InfoValue(audio.Codec)}");
                text.AppendLine($"音频码率（统计）：{InfoBitRate(audio.BitRate)}");
                text.AppendLine($"采样率：{InfoRate(audio.SampleRate, "Hz")}");
                text.AppendLine($"源声道：{InfoRate(audio.Channels, "声道")}（{InfoValue(audio.ChannelLayout)}）");
                text.AppendLine($"输出声道：{InfoRate(audio.ChannelsOut, "声道")}");
                text.AppendLine($"采样格式：{InfoValue(audio.SampleFormat)}");
            }
            else text.AppendLine("未打开音频轨道");
        }
        catch (Exception)
        {
            // 解码器可能正在异步关闭；下一次刷新重新读取，避免信息面板影响播放。
            return "媒体信息暂不可用，正在等待播放器更新。";
        }

        return generation == Volatile.Read(ref _openGeneration)
            ? text.ToString().TrimEnd()
            : "正在切换媒体…";
    }

    static string InfoValue(string value) => string.IsNullOrWhiteSpace(value) ? "未知" : value;
    static string InfoRate(double value, string unit) =>
        double.IsFinite(value) && value > 0 ? $"{value:0.##} {unit}" : "未知";

    // Flyleaf 的音视频码率单位为十进制 Kbps，不能按接收速度的字节单位换算。
    static string InfoBitRate(double kbps) => !double.IsFinite(kbps) || kbps <= 0
        ? "暂无统计"
        : kbps >= 1000 ? $"{kbps / 1000:0.##} Mbps" : $"{kbps:0.##} Kbps";
}
