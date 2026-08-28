// File: ModbusTcpDriver.cs
using System.Net.Sockets;
using IndustrialDAQ.Core.Interfaces;
using IndustrialDAQ.Core.Models;
using Modbus;
using Modbus.Device;

namespace Drivers.Modbus;

public sealed class ModbusTcpDriver : IProtocolDriver
{
    private TcpClient? _tcpClient;
    private IModbusMaster? _master;
    private bool _connected;
    private readonly string _ipAddress;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly byte _slaveId;

    public string DriverType => "Modbus";
    public bool IsConnected => _connected;

    public ModbusTcpDriver()
    {
        _ipAddress = string.Empty;
        _port = 502;
        _timeoutMs = 3000;
        _slaveId = 1;
    }

    public ModbusTcpDriver(DeviceConfig config)
    {
        _ipAddress = config.IpAddress;
        _port = config.Port > 0 ? config.Port : 502;
        _timeoutMs = config.TimeoutMs > 0 ? config.TimeoutMs : 3000;
        _slaveId = config.StationAddress;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_ipAddress))
            throw new InvalidOperationException("Modbus 驱动未配置 IP 地址");

        _tcpClient = new TcpClient
        {
            ReceiveTimeout = _timeoutMs,
            SendTimeout = _timeoutMs
        };

        await _tcpClient.ConnectAsync(_ipAddress, _port, ct);
        _master = ModbusIpMaster.CreateIp(_tcpClient);
        _connected = true;

        // ── Modbus 层握手探测（非致命） ──
        // 对 S7 MB_SERVER，TCP 能连上 502 端口本身就证明 MB_SERVER 在监听；
        // 此探测仅作协议栈活性检查。失败不阻断连接，避免慢速/抖动网络下
        // 误判为假连接；真正的"假成功"由 WriteTagAsync 的写后回读校验捕获。
        try
        {
            await _master.ReadHoldingRegistersAsync(_slaveId, 0, 1);
        }
        catch (SlaveException)
        {
            // 从机回应了异常帧 → 协议栈正常
        }
        catch
        {
            // 探测失败（超时/IO 等），忽略：TCP 已建立即视为可用。
        }
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _connected = false;
        _master?.Dispose();
        _master = null;
        _tcpClient?.Close();
        _tcpClient?.Dispose();
        _tcpClient = null;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IEnumerable<TagPoint> tags, CancellationToken ct = default)
    {
        if (!_connected || _master is null)
            throw new InvalidOperationException("Modbus 驱动未连接");

        ct.ThrowIfCancellationRequested();
        var tagList = tags.ToList();
        if (tagList.Count == 0) return Array.Empty<TagValue>();

        var groups = tagList
            .Where(tag => tag.Access != TagAccess.Write)
            .Select(tag => (Tag: tag, Entry: ParseAddress(tag.Address)))
            .GroupBy(x => x.Entry.FunctionCode)
            .ToList();

        var timestamp = DateTimeOffset.UtcNow;
        var resultMap = new Dictionary<string, TagValue>(tagList.Count);

        foreach (var group in groups)
        {
            try
            {
                var functionCode = group.Key;
                var sortedEntries = group.OrderBy(x => x.Entry.StartAddress).ToList();
                var mergedRanges = MergeAdjacentAddresses(sortedEntries, functionCode);

                foreach (var range in mergedRanges)
                {
                    ushort start = range.StartAddress;
                    ushort count = range.Count;

                    ushort[] rawValues;
                    try
                    {
                        rawValues = functionCode switch
                        {
                            ModbusFunctionCode.ReadCoils =>
                                (await _master.ReadCoilsAsync(_slaveId, start, count))
                                .Select(b => b ? (ushort)1 : (ushort)0).ToArray(),

                            ModbusFunctionCode.ReadDiscreteInputs =>
                                (await _master.ReadInputsAsync(_slaveId, start, count))
                                .Select(b => b ? (ushort)1 : (ushort)0).ToArray(),

                            ModbusFunctionCode.ReadInputRegisters =>
                                await _master.ReadInputRegistersAsync(_slaveId, start, count),

                            ModbusFunctionCode.ReadHoldingRegisters =>
                                await _master.ReadHoldingRegistersAsync(_slaveId, start, count),

                            _ => throw new NotSupportedException($"不支持的功能码: {functionCode}")
                        };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (ex is IOException or SocketException or ObjectDisposedException or TimeoutException)
                        {
                            _connected = false;
                            throw; // 传输层故障，交由外层统一处理
                        }

                        // 协议层拒绝（如地址越界 SlaveException）：批量读失败不拖垮整组，
                        // 降级为逐标签单点读取，隔离坏点，其余标签仍可取到值。
                        foreach (var (tag, entry) in range.Tags)
                        {
                            TagValue? single = await ReadSingleTagAsync(tag, entry, timestamp);
                            if (single is not null)
                                resultMap[tag.Id] = single;
                        }
                        continue;
                    }

                    foreach (var (tag, entry) in range.Tags)
                    {
                        int offset = entry.StartAddress - start;
                        object? value;
                        if (functionCode is ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs)
                        {
                            value = rawValues[offset] == 1;
                        }
                        else if (tag.DataType == TagDataType.Bool && tag.BitIndex >= 0)
                        {
                            value = ((rawValues[offset] >> tag.BitIndex) & 1) == 1;
                        }
                        else
                        {
                            value = ExtractTypedValue(rawValues, offset, tag.DataType);
                        }

                        resultMap[tag.Id] = new TagValue
                        {
                            TagId = tag.Id,
                            TagName = tag.Name,
                            Value = value,
                            Quality = Quality.Good,
                            Timestamp = timestamp,
                            DataType = MapToType(tag.DataType)
                        };
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is IOException or SocketException or ObjectDisposedException or TimeoutException or InvalidOperationException)
                {
                    _connected = false;
                    throw;
                }

                foreach (var (tag, _) in group)
                {
                    resultMap[tag.Id] = new TagValue
                    {
                        TagId = tag.Id,
                        TagName = tag.Name,
                        Value = null,
                        Quality = Quality.Bad,
                        Timestamp = timestamp,
                        DataType = MapToType(tag.DataType)
                    };
                }
            }
        }

        return tagList
            .Select(t => resultMap.TryGetValue(t.Id, out var v)
                ? v
                : new TagValue { TagId = t.Id, TagName = t.Name, Quality = Quality.Bad, Timestamp = timestamp })
            .ToList();
    }

    /// <summary>
    /// 单标签单点读取：批量读取失败（如地址越界 SlaveException）时降级使用，
    /// 隔离坏点，避免一个坏地址拖垮整组标签。
    /// 成功返回 TagValue，失败返回 null（保持 Bad 状态）。
    /// </summary>
    private async Task<TagValue?> ReadSingleTagAsync(
        TagPoint tag, ModbusAddressEntry entry, DateTimeOffset timestamp)
    {
        try
        {
            object? value;
            if (entry.FunctionCode is ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs)
            {
                bool[] bits = entry.FunctionCode == ModbusFunctionCode.ReadCoils
                    ? await _master!.ReadCoilsAsync(_slaveId, entry.StartAddress, 1)
                    : await _master!.ReadInputsAsync(_slaveId, entry.StartAddress, 1);
                value = bits.Length > 0 && bits[0];
            }
            else
            {
                ushort regCount = (ushort)RegistersNeeded(tag.DataType);
                ushort[] regs = entry.FunctionCode == ModbusFunctionCode.ReadInputRegisters
                    ? await _master!.ReadInputRegistersAsync(_slaveId, entry.StartAddress, regCount)
                    : await _master!.ReadHoldingRegistersAsync(_slaveId, entry.StartAddress, regCount);

                value = tag.DataType == TagDataType.Bool && tag.BitIndex >= 0
                    ? regs.Length > 0 && ((regs[0] >> tag.BitIndex) & 1) == 1
                    : ExtractTypedValue(regs, 0, tag.DataType);
            }

            return new TagValue
            {
                TagId = tag.Id,
                TagName = tag.Name,
                Value = value,
                Quality = Quality.Good,
                Timestamp = timestamp,
                DataType = MapToType(tag.DataType)
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task WriteTagAsync(TagPoint tag, object value, CancellationToken ct = default)
    {
        if (!_connected || _master is null)
            throw new InvalidOperationException("Modbus 驱动未连接");

        ct.ThrowIfCancellationRequested();
        if (tag.Access == TagAccess.Read)
            throw new InvalidOperationException($"标签 {tag.Name} 为只读，不可写入");

        var entry = ParseAddress(tag.Address);

        try
        {
            switch (entry.FunctionCode)
            {
                case ModbusFunctionCode.ReadCoils:
                    bool coilValue = value is bool coilBool ? coilBool : Convert.ToBoolean(value);
                    await _master.WriteSingleCoilAsync(_slaveId, entry.StartAddress, coilValue);
                    // 写后回读校验：确认从机确实把线圈置位/复位，杜绝"协议 ACK 但数据未生效"的假成功
                    await VerifyCoilWriteAsync(tag, entry.StartAddress, coilValue);
                    break;

                case ModbusFunctionCode.ReadHoldingRegisters:
                    if (tag.DataType == TagDataType.Bool && tag.BitIndex >= 0)
                    {
                        // 按位写入：先读当前寄存器，仅翻转目标位，再写回，保留同寄存器其他位
                        ushort current = (await _master.ReadHoldingRegistersAsync(_slaveId, entry.StartAddress, 1))[0];
                        bool bit = value is bool vb ? vb : Convert.ToBoolean(value);
                        ushort updated = bit
                            ? (ushort)(current | (1 << tag.BitIndex))
                            : (ushort)(current & ~(1 << tag.BitIndex));
                        await _master.WriteSingleRegisterAsync(_slaveId, entry.StartAddress, updated);

                        // 写后回读校验（按位比对）
                        ushort verifyReg = (await _master.ReadHoldingRegistersAsync(_slaveId, entry.StartAddress, 1))[0];
                        bool verifyBit = ((verifyReg >> tag.BitIndex) & 1) == 1;
                        if (verifyBit != bit)
                            throw new InvalidOperationException(
                                $"写入回读校验失败: 标签 [{tag.Name}] 位 {tag.BitIndex} 期望 {bit}，实际 {verifyBit} (寄存器 0x{verifyReg:X4})");
                    }
                    else
                    {
                        ushort[] registerValues = PackRegisterValues(value, tag.DataType);
                        if (registerValues.Length == 1)
                            await _master.WriteSingleRegisterAsync(_slaveId, entry.StartAddress, registerValues[0]);
                        else
                            await _master.WriteMultipleRegistersAsync(_slaveId, entry.StartAddress, registerValues);

                        // 写后回读校验（整寄存器比对）
                        await VerifyRegisterWriteAsync(tag, entry.StartAddress, registerValues);
                    }
                    break;

                default:
                    throw new NotSupportedException($"不支持写入功能码 {entry.FunctionCode}");
            }
        }
        catch (SlaveException ex)
        {
            // 从机返回 Modbus 异常帧（如 IllegalDataAddress=0x02）：连接仍有效，是请求本身被拒绝。
            // 包装成直观信息，让 UI 能显示"从机拒绝"而不是笼统的异常。
            throw new InvalidOperationException(
                $"Modbus 从机拒绝写入请求 (异常码 {ex.SlaveExceptionCode}): {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is IOException or SocketException or ObjectDisposedException or TimeoutException or InvalidOperationException)
            {
                _connected = false;
            }
            throw;
        }
    }

    // ─── 写后回读校验 ───

    /// <summary>
    /// 写线圈后回读校验：确认从机确实把线圈置位/复位。
    /// </summary>
    private async Task VerifyCoilWriteAsync(TagPoint tag, ushort coilAddress, bool expected)
    {
        bool[] actual = await _master!.ReadCoilsAsync(_slaveId, coilAddress, 1);
        bool actualValue = actual.Length > 0 && actual[0];
        if (actualValue != expected)
            throw new InvalidOperationException(
                $"写入回读校验失败: 标签 [{tag.Name}] 线圈 {coilAddress + 1} 期望 {expected}，实际 {actualValue}");
    }

    /// <summary>
    /// 写寄存器后回读校验：逐个寄存器比对写入值。
    /// </summary>
    private async Task VerifyRegisterWriteAsync(TagPoint tag, ushort startAddress, ushort[] expected)
    {
        ushort[] actual = await _master!.ReadHoldingRegistersAsync(_slaveId, startAddress, (ushort)expected.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
                throw new InvalidOperationException(
                    $"写入回读校验失败: 标签 [{tag.Name}] 寄存器 {startAddress + i} 期望 0x{expected[i]:X4}，实际 0x{actual[i]:X4}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    // ─── 地址解析 ───

    private record ModbusAddressEntry(ModbusFunctionCode FunctionCode, ushort StartAddress);

    /// <summary>
    /// 解析 Modbus 地址字符串。
    /// 支持格式: "HR:0", "IR:1", "CO:2", "DI:3", "40001", "0x0000"
    /// </summary>
    private static ModbusAddressEntry ParseAddress(string address)
    {
        string cleaned = address.Trim().ToUpperInvariant();

        // 支持 HR:0, IR:1, CO:2, DI:3 格式
        if (cleaned.Contains(':'))
        {
            var parts = cleaned.Split(':');
            if (parts.Length == 2 && ushort.TryParse(parts[1], out ushort offset))
            {
                return parts[0] switch
                {
                    "HR" => new ModbusAddressEntry(ModbusFunctionCode.ReadHoldingRegisters, offset),
                    "IR" => new ModbusAddressEntry(ModbusFunctionCode.ReadInputRegisters, offset),
                    "CO" or "C" => new ModbusAddressEntry(ModbusFunctionCode.ReadCoils, offset),
                    "DI" or "D" => new ModbusAddressEntry(ModbusFunctionCode.ReadDiscreteInputs, offset),
                    _ => throw new ArgumentException($"不支持的 Modbus 地址前缀: {parts[0]}")
                };
            }
        }

        // 去掉前缀
        string numeric = cleaned
            .Replace("HR", "").Replace("IR", "").Replace("CO", "")
            .Replace("DI", "").Replace("H", "").Replace("C", "").Replace("D", "");

        if (numeric.Length == 0)
            throw new ArgumentException($"无效的 Modbus 地址: {address}");

        // 16 进制
        if (numeric.StartsWith("0X"))
        {
            ushort hexVal = Convert.ToUInt16(numeric, 16);
            return new ModbusAddressEntry(ModbusFunctionCode.ReadHoldingRegisters, hexVal);
        }

        // 传统 5 位地址格式
        if (numeric.Length == 5 && numeric.All(char.IsDigit))
        {
            int prefix = numeric[0] - '0';
            ushort offset = checked((ushort)(ushort.Parse(numeric.Substring(1)) - 1));
            return prefix switch
            {
                0 => new ModbusAddressEntry(ModbusFunctionCode.ReadCoils, offset),
                1 => new ModbusAddressEntry(ModbusFunctionCode.ReadDiscreteInputs, offset),
                3 => new ModbusAddressEntry(ModbusFunctionCode.ReadInputRegisters, offset),
                4 => new ModbusAddressEntry(ModbusFunctionCode.ReadHoldingRegisters, offset),
                _ => throw new ArgumentException($"不支持的 Modbus 地址前缀: {prefix}")
            };
        }

        // 6 位格式
        if (ushort.TryParse(numeric, out ushort num) && num >= 10000)
        {
            int prefix = num / 10000;
            ushort offset = checked((ushort)((num % 10000) - 1));
            return prefix switch
            {
                0 => new ModbusAddressEntry(ModbusFunctionCode.ReadCoils, offset),
                1 => new ModbusAddressEntry(ModbusFunctionCode.ReadDiscreteInputs, offset),
                3 => new ModbusAddressEntry(ModbusFunctionCode.ReadInputRegisters, offset),
                4 => new ModbusAddressEntry(ModbusFunctionCode.ReadHoldingRegisters, offset),
                _ => throw new ArgumentException($"不支持的 Modbus 地址前缀: {prefix}")
            };
        }

        // 纯数字，默认 Holding Register
        if (!ushort.TryParse(numeric, out num))
            throw new ArgumentException($"无法解析 Modbus 地址: {address}");

        return new ModbusAddressEntry(ModbusFunctionCode.ReadHoldingRegisters, num);
    }

    private static List<ModbusReadRange> MergeAdjacentAddresses(
        List<(TagPoint Tag, ModbusAddressEntry Entry)> sortedEntries,
        ModbusFunctionCode functionCode)
    {
        const ushort maxGap = 8;
        const ushort maxBatch = 120;

        var ranges = new List<ModbusReadRange>();
        if (sortedEntries.Count == 0) return ranges;

        ushort rangeStart = sortedEntries[0].Entry.StartAddress;
        ushort rangeEnd = rangeStart;
        var rangeTags = new List<(TagPoint, ModbusAddressEntry)> { sortedEntries[0] };

        for (int i = 1; i < sortedEntries.Count; i++)
        {
            var (tag, entry) = sortedEntries[i];
            ushort required = (ushort)(entry.StartAddress + RegistersNeeded(tag.DataType) - 1);

            if (entry.StartAddress <= rangeEnd + maxGap &&
                required - rangeStart + 1 <= maxBatch)
            {
                rangeEnd = Math.Max(rangeEnd, required);
            }
            else
            {
                ranges.Add(new ModbusReadRange(rangeStart,
                    checked((ushort)(rangeEnd - rangeStart + 1)), rangeTags));
                rangeStart = entry.StartAddress;
                rangeEnd = required;
                rangeTags = new List<(TagPoint, ModbusAddressEntry)> { (tag, entry) };
                continue;
            }
            rangeTags.Add((tag, entry));
        }

        ranges.Add(new ModbusReadRange(rangeStart,
            checked((ushort)(rangeEnd - rangeStart + 1)), rangeTags));

        return ranges;
    }

    private record ModbusReadRange(
        ushort StartAddress, ushort Count, List<(TagPoint Tag, ModbusAddressEntry Entry)> Tags);

    // ─── 数据类型转换 ───

    /// <summary>
    /// 关键修改：支持 Little Endian 和 Big Endian
    /// </summary>
    private static object? ExtractTypedValue(ushort[] registers, int offset, TagDataType dataType)
    {
        return dataType switch
        {
            TagDataType.Bool => registers[offset] != 0,
            TagDataType.Int16 => (short)registers[offset],
            TagDataType.UInt16 => registers[offset],
            TagDataType.Int32 => (int)PackTwoRegistersLittleEndian(registers, offset),
            TagDataType.UInt32 => PackTwoRegistersLittleEndian(registers, offset),
            TagDataType.Float32 => RegistersToFloatLittleEndian(registers, offset),  // ← 改成 Little Endian
            TagDataType.Float64 => RegistersToDoubleLittleEndian(registers, offset),
            TagDataType.Int64 => (long)PackFourRegistersLittleEndian(registers, offset),
            TagDataType.String => RegistersToString(registers, offset, 20),
            _ => registers[offset]
        };
    }

    // ─── Little Endian 转换（匹配 Python 模拟器）───

    private static uint PackTwoRegistersLittleEndian(ushort[] regs, int offset)
        => ((uint)regs[offset + 1] << 16) | regs[offset];

    private static ulong PackFourRegistersLittleEndian(ushort[] regs, int offset)
        => ((ulong)regs[offset + 3] << 48) | ((ulong)regs[offset + 2] << 32)
           | ((ulong)regs[offset + 1] << 16) | regs[offset];

    /// <summary>
    /// Little Endian: 寄存器 [offset] = 低 16 位, [offset+1] = 高 16 位
    /// 字节顺序: 低字节在前
    /// </summary>
    private static float RegistersToFloatLittleEndian(ushort[] regs, int offset)
    {
        byte[] bytes = new byte[4];
        bytes[0] = (byte)(regs[offset] & 0xFF);      // 低字节
        bytes[1] = (byte)(regs[offset] >> 8);        // 高字节
        bytes[2] = (byte)(regs[offset + 1] & 0xFF);  // 低字节
        bytes[3] = (byte)(regs[offset + 1] >> 8);     // 高字节
        return BitConverter.ToSingle(bytes, 0);
    }

    private static double RegistersToDoubleLittleEndian(ushort[] regs, int offset)
    {
        byte[] bytes = new byte[8];
        for (int i = 0; i < 4; i++)
        {
            bytes[i * 2] = (byte)(regs[offset + i] & 0xFF);
            bytes[i * 2 + 1] = (byte)(regs[offset + i] >> 8);
        }
        return BitConverter.ToDouble(bytes, 0);
    }

    // ─── 字符串转换 ───

    private static string RegistersToString(ushort[] regs, int offset, int maxRegs)
    {
        var chars = new List<char>();
        for (int i = 0; i < maxRegs && offset + i < regs.Length; i++)
        {
            ushort r = regs[offset + i];
            if (r == 0) break;
            char lo = (char)(r & 0xFF);
            if (lo != 0) chars.Add(lo);
            char hi = (char)(r >> 8);
            if (hi != 0) chars.Add(hi);
        }
        return new string(chars.ToArray());
    }

    // ─── 写入打包 ───

    private static ushort[] PackRegisterValues(object value, TagDataType dataType)
    {
        byte[] bytes = dataType switch
        {
            TagDataType.Int16 => BitConverter.GetBytes(Convert.ToInt16(value)),
            TagDataType.UInt16 => BitConverter.GetBytes(Convert.ToUInt16(value)),
            TagDataType.Int32 => BitConverter.GetBytes(Convert.ToInt32(value)),
            TagDataType.UInt32 => BitConverter.GetBytes(Convert.ToUInt32(value)),
            TagDataType.Float32 => BitConverter.GetBytes(Convert.ToSingle(value)),
            TagDataType.Float64 => BitConverter.GetBytes(Convert.ToDouble(value)),
            TagDataType.Int64 => BitConverter.GetBytes(Convert.ToInt64(value)),
            _ => throw new NotSupportedException($"不支持的 Modbus 写入类型: {dataType}")
        };

        int regCount = (bytes.Length + 1) / 2;
        ushort[] regs = new ushort[regCount];

        // Little Endian: 低字节在前
        for (int i = 0; i < regCount; i++)
        {
            int byteIdx = i * 2;
            regs[i] = (ushort)(bytes[byteIdx] |
                (byteIdx + 1 < bytes.Length ? bytes[byteIdx + 1] << 8 : 0));
        }
        return regs;
    }

    private static ushort RegistersNeeded(TagDataType dataType) => dataType switch
    {
        TagDataType.Bool or TagDataType.Int16 or TagDataType.UInt16 => 1,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 2,
        TagDataType.Float64 or TagDataType.Int64 => 4,
        TagDataType.String => 10,
        _ => 1
    };

    private static Type MapToType(TagDataType dataType) => dataType switch
    {
        TagDataType.Bool => typeof(bool),
        TagDataType.Int16 => typeof(short),
        TagDataType.Int32 => typeof(int),
        TagDataType.Int64 => typeof(long),
        TagDataType.UInt16 => typeof(ushort),
        TagDataType.UInt32 => typeof(uint),
        TagDataType.Float32 => typeof(float),
        TagDataType.Float64 => typeof(double),
        TagDataType.String => typeof(string),
        _ => typeof(object)
    };

    private enum ModbusFunctionCode : byte
    {
        ReadCoils = 1,
        ReadDiscreteInputs = 2,
        ReadInputRegisters = 4,
        ReadHoldingRegisters = 3
    }
}