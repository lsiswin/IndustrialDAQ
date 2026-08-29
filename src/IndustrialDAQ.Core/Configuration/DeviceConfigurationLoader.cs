// File: DeviceConfigurationLoader.cs  Module: Core (Configuration)  Author: IndustrialDAQ Team
using System.Text.Json;
using System.Text.Json.Serialization;
using IndustrialDAQ.Core.Models;

namespace IndustrialDAQ.Core.Configuration;

/// <summary>
/// JSON 设备配置加载器 — 从 JSON 文件反序列化 <see cref="DeviceConfig"/> 列表。
/// 支持为每个 TagPoint 配置三种协议地址（OPC UA / Modbus / S7），
/// 加载时自动根据设备 DriverType 选择对应的协议地址填入 TagPoint.Address。
/// </summary>
public static class DeviceConfigurationLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// 从 JSON 文件路径加载设备配置列表。
    /// 自动根据每台设备的 DriverType 为 TagPoint 选择正确的协议地址。
    /// </summary>
    /// <param name="filePath">JSON 配置文件路径</param>
    /// <returns>设备配置列表</returns>
    public static async Task<List<DeviceConfig>> LoadFromFileAsync(string filePath)
    {
        string json = await File.ReadAllTextAsync(filePath);
        var dtos = JsonSerializer.Deserialize<List<DeviceConfigDto>>(json, Options)
                   ?? throw new InvalidOperationException("JSON 配置文件解析失败");

        return dtos.Select(dto => dto.ToDeviceConfig()).ToList();
    }

    /// <summary>
    /// JSON DTO — 对应配置文件根数组元素。
    /// </summary>
    private sealed class DeviceConfigDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DriverType { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }
        public byte StationAddress { get; set; } = 1;
        public short CpuSlot { get; set; } = 1;
        public int CycleTimeMs { get; set; } = 1000;
        public int TimeoutMs { get; set; } = 3000;
        public int RetryCount { get; set; } = 3;
        public string? OpcUaUsername { get; set; }
        public string? OpcUaPasswordEnvironmentVariable { get; set; }
        public string? MqttClientId { get; set; }
        public string? MqttUsername { get; set; }
        public string? MqttPasswordEnvironmentVariable { get; set; }
        public bool MqttUseTls { get; set; }
        public string? Description { get; set; }
        public List<TagPointDto> Tags { get; set; } = new();

        public DeviceConfig ToDeviceConfig() => new()
        {
            Id = Id,
            Name = Name,
            DriverType = DriverType,
            IpAddress = IpAddress,
            Port = Port,
            StationAddress = StationAddress,
            CpuSlot = CpuSlot,
            CycleTimeMs = CycleTimeMs,
            TimeoutMs = TimeoutMs,
            RetryCount = RetryCount,
            OpcUaUsername = OpcUaUsername,
            OpcUaPasswordEnvironmentVariable = OpcUaPasswordEnvironmentVariable,
            MqttClientId = MqttClientId,
            MqttUsername = MqttUsername,
            MqttPasswordEnvironmentVariable = MqttPasswordEnvironmentVariable,
            MqttUseTls = MqttUseTls,
            Tags = Tags.Select(t => t.ToTagPoint(DriverType)).ToList()
        };
    }

    /// <summary>
    /// JSON DTO — 单个测点定义，支持多协议地址。
    /// </summary>
    private sealed class TagPointDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>OPC UA 地址 (ns=3;s="Line"."Start").</summary>
        public string? OpcUaAddress { get; set; }

        /// <summary>Modbus 地址 (00001 线圈 或 40001 保持寄存器).</summary>
        public string? ModbusAddress { get; set; }

        /// <summary>S7 直接地址 (DB1.DBX0.0 等).</summary>
        public string? S7Address { get; set; }

        /// <summary>通用地址回退字段。</summary>
        public string? Address { get; set; }

        public string DataType { get; set; } = "Float32";
        public int ScanRateMs { get; set; }
        public double Deadband { get; set; }
        public string Access { get; set; } = "Read";
        public string? Description { get; set; }
        public string? TemplateId { get; set; }

        /// <summary>寄存器内位索引（0-15），用于 Bool 在 HR/IR 上的按位读写。</summary>
        public int BitIndex { get; set; } = -1;

        /// <summary>
        /// 转换为领域模型 — 根据设备 DriverType 自动选择对应的协议地址。
        /// </summary>
        public TagPoint ToTagPoint(string driverType)
        {
            string effectiveAddress = driverType.ToUpperInvariant() switch
            {
                "OPCUA" => OpcUaAddress ?? Address ?? string.Empty,
                "MODBUS" => ModbusAddress ?? Address ?? string.Empty,
                "S7" => S7Address ?? Address ?? string.Empty,
                "MQTT" => Address ?? string.Empty,
                _ => Address ?? string.Empty
            };

            return new TagPoint
            {
                Id = Id,
                Name = Name,
                Address = effectiveAddress,
                DataType = Enum.Parse<TagDataType>(DataType, ignoreCase: true),
                ScanRateMs = ScanRateMs,
                Deadband = Deadband,
                Access = Enum.Parse<TagAccess>(Access, ignoreCase: true),
                Description = Description ?? string.Empty,
                BitIndex = BitIndex,
                TemplateId = TemplateId
            };
        }
    }
}
