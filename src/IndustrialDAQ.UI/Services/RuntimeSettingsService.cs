using System.Text.Json;
using System.IO;
using IndustrialDAQ.Core.Models;

namespace IndustrialDAQ.UI.Services;

/// <summary>系统运行参数持久化服务，使用用户级目录避免安装目录只读。</summary>
public sealed class RuntimeSettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _filePath;
    public RuntimeSettings Current { get; private set; }
    public event EventHandler<RuntimeSettings>? Changed;

    public RuntimeSettingsService()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndustrialDAQ");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "runtime-settings.json");
        Current = Load();
    }

    public async Task SaveAsync(RuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var temporaryPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(settings, Options), cancellationToken);
        File.Move(temporaryPath, _filePath, true);
        Current = settings;
        Changed?.Invoke(this, settings);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default) => await SaveAsync(new RuntimeSettings(), cancellationToken);

    /// <summary>将全局采集参数覆盖到设备运行副本，不修改现场 JSON 原文件。</summary>
    public DeviceConfig ApplyTo(DeviceConfig source) => new()
    {
        Id = source.Id, Name = source.Name, DriverType = source.DriverType, IpAddress = source.IpAddress, Port = source.Port,
        StationAddress = source.StationAddress, CpuSlot = source.CpuSlot, CycleTimeMs = source.CycleTimeMs,
        TimeoutMs = Current.AcquisitionTimeoutMs, RetryCount = Current.RetryCount,
        OpcUaUsername = source.OpcUaUsername, OpcUaPasswordEnvironmentVariable = source.OpcUaPasswordEnvironmentVariable,
        MqttClientId = source.MqttClientId, MqttUsername = source.MqttUsername,
        MqttPasswordEnvironmentVariable = source.MqttPasswordEnvironmentVariable, MqttUseTls = source.MqttUseTls,
        Tags = source.Tags.Select(tag => new TagPoint
        {
            Id = tag.Id, Name = tag.Name, Address = tag.Address, DataType = tag.DataType, ScanRateMs = tag.ScanRateMs,
            Deadband = Current.EnableDeadband ? tag.Deadband : 0, Access = tag.Access, BitIndex = tag.BitIndex,
            Description = tag.Description, TemplateId = tag.TemplateId
        }).ToList()
    };

    private RuntimeSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new RuntimeSettings();
            var settings = JsonSerializer.Deserialize<RuntimeSettings>(File.ReadAllText(_filePath), Options) ?? new RuntimeSettings();
            Validate(settings);
            return settings;
        }
        catch
        {
            // 损坏的设置文件不阻止客户端启动，恢复安全默认值。
            return new RuntimeSettings();
        }
    }

    private static void Validate(RuntimeSettings settings)
    {
        if (settings.AcquisitionTimeoutMs is < 100 or > 120_000) throw new InvalidOperationException("采集超时必须在 100~120000 ms 之间。");
        if (settings.RetryCount is < 0 or > 20) throw new InvalidOperationException("重试次数必须在 0~20 之间。");
        if (settings.HistoryRetentionDays is < 1 or > 3650) throw new InvalidOperationException("历史保留天数必须在 1~3650 之间。");
        if (settings.Theme is not ("DarkTheme" or "LightTheme")) throw new InvalidOperationException("主题配置无效。");
        if (settings.DatabaseProfiles is null || settings.DatabaseProfiles.Count == 0) throw new InvalidOperationException("至少需要保留一个数据库配置。");
        if (settings.DatabaseProfiles.Any(profile => string.IsNullOrWhiteSpace(profile.Id) || string.IsNullOrWhiteSpace(profile.Name)))
            throw new InvalidOperationException("数据库配置名称和标识不能为空。");
        if (settings.DatabaseProfiles.Select(profile => profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.DatabaseProfiles.Count)
            throw new InvalidOperationException("数据库配置标识不能重复。");
        if (!settings.DatabaseProfiles.Any(profile => string.Equals(profile.Id, settings.ActiveDatabaseProfileId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("当前启动数据库配置不存在。");
        if (settings.DatabaseProfiles.Any(profile => profile.Provider is not ("SQLite" or "PostgreSQL")))
            throw new InvalidOperationException("仅支持 SQLite 或 PostgreSQL 数据库。");
    }
}

public sealed record RuntimeSettings
{
    public int AcquisitionTimeoutMs { get; init; } = 3000;
    public int RetryCount { get; init; } = 3;
    public bool EnableDeadband { get; init; } = true;
    public int HistoryRetentionDays { get; init; } = 90;
    public string Theme { get; init; } = "DarkTheme";
    public string LogLevel { get; init; } = "Information";
    public string ActiveDatabaseProfileId { get; init; } = "sqlite-default";
    public IReadOnlyList<DatabaseProfile> DatabaseProfiles { get; init; } = [DatabaseProfile.CreateDefaultSqlite()];
}
