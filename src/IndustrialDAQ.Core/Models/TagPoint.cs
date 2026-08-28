namespace IndustrialDAQ.Core.Models;

/// <summary>
/// Definition of a single data point within a device.
/// </summary>
public sealed class TagPoint
{
    /// <summary>Unique tag identifier within the system.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Human-readable tag name (e.g. "Temp_Reactor_01").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Protocol-specific address string.
    /// Modbus: "40001"; OPC UA: "ns=3;s=Temperature".
    /// </summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Expected data type of the value.</summary>
    public TagDataType DataType { get; init; } = TagDataType.Float32;

    /// <summary>Override of the parent device scan rate (0 = inherit).</summary>
    public int ScanRateMs { get; init; } = 0;

    /// <summary>Deadband / threshold for change detection. Values within this range are not re-published.</summary>
    public double Deadband { get; init; } = 0.0;

    /// <summary>访问权限 — 只读、只写或读写。</summary>
    public TagAccess Access { get; init; } = TagAccess.Read;

    /// <summary>
    /// 寄存器内位索引（0-15）。仅当 <see cref="DataType"/> 为 <see cref="TagDataType.Bool"/>
    /// 且地址指向保持/输入寄存器（HR/IR）时生效：
    /// 读取时提取该位的值，写入时执行“读-改-写”以保留同一寄存器的其他位。
    /// 默认 -1 表示不启用位访问（按整寄存器处理）。
    /// </summary>
    public int BitIndex { get; init; } = -1;

    /// <summary>Description for documentation purposes.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>引用的数据点模板 ID（可选，用于自动生成报警规则和趋势配置）。</summary>
    public string? TemplateId { get; init; }
}

public enum TagDataType : byte
{
    Bool = 1,
    Int16 = 2,
    Int32 = 3,
    Float32 = 4,
    Float64 = 5,
    String = 6,
    Int64 = 7,
    UInt16 = 8,
    UInt32 = 9
}
