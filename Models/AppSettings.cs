using HikDeployTool.ViewModels;

namespace HikDeployTool.Models;

/// <summary>一条运行日志。</summary>
public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;
    public string Source { get; init; } = "App";
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string LevelText => Level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        LogLevel.Success => "成功",
        _ => "信息",
    };
}

public enum LogLevel
{
    Debug,
    Info,
    Success,
    Warn,
    Error,
}

/// <summary>应用设置，持久化到程序目录 settings.json。</summary>
public sealed class AppSettings : ObservableObject
{
    private string _defaultUserName = "admin";
    private string _sdkLogDir = string.Empty;
    private int _sdkLogLevel = 3;
    private int _maxParallelism = 4;
    private int _connectTimeoutMs = 5000;
    private int _captureMaxChannels = 4;
    private bool _captureEnabled = true;
    private ushort _defaultPort = 8000;

    /// <summary>默认登录用户名。</summary>
    public string DefaultUserName { get => _defaultUserName; set => Set(ref _defaultUserName, value); }

    /// <summary>SADP / NetSDK 自身日志目录（留空则使用程序目录下 sdk_log）。</summary>
    public string SdkLogDir { get => _sdkLogDir; set => Set(ref _sdkLogDir, value); }

    /// <summary>SDK 日志等级 0-6，默认 3。</summary>
    public int SdkLogLevel { get => _sdkLogLevel; set => Set(ref _sdkLogLevel, value); }

    /// <summary>批量操作并发数（SADP 为广播协议，建议 4~8）。</summary>
    public int MaxParallelism { get => _maxParallelism; set => Set(ref _maxParallelism, Math.Clamp(value, 1, 32)); }

    /// <summary>网络 SDK 连接超时（毫秒）。</summary>
    public int ConnectTimeoutMs { get => _connectTimeoutMs; set => Set(ref _connectTimeoutMs, value); }

    /// <summary>验收时每台设备最多抓图的通道数。</summary>
    public int CaptureMaxChannels { get => _captureMaxChannels; set => Set(ref _captureMaxChannels, Math.Clamp(value, 0, 64)); }

    /// <summary>验收时是否抓图存证。</summary>
    public bool CaptureEnabled { get => _captureEnabled; set => Set(ref _captureEnabled, value); }

    /// <summary>默认 SDK 端口。</summary>
    public ushort DefaultPort { get => _defaultPort; set => Set(ref _defaultPort, value); }

    private string _outputDir = string.Empty;
    private string _projectName = string.Empty;
    private string _operator = string.Empty;
    private string _defaultGateway = string.Empty;

    /// <summary>报告/导出输出目录（留空则使用程序目录下 output）。</summary>
    public string OutputDir { get => _outputDir; set => Set(ref _outputDir, value); }

    /// <summary>项目名称，会写进验收报告封面。</summary>
    public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }

    /// <summary>施工/验收单位，会写进验收报告。</summary>
    public string Operator { get => _operator; set => Set(ref _operator, value); }

    /// <summary>现场常用网关，用于网络配置页一键填充。</summary>
    public string DefaultGateway { get => _defaultGateway; set => Set(ref _defaultGateway, value); }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
