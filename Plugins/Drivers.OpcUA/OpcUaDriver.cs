// File: OpcUaDriver.cs  Module: Plugins (Drivers.OpcUA)  Author: IndustrialDAQ Team
using IndustrialDAQ.Core.Interfaces;
using IndustrialDAQ.Core.Models;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace Drivers.OpcUA;

/// <summary>
/// OPC UA 协议驱动 — 使用 OPC Foundation .NET Standard SDK 实现 IProtocolDriver。
/// 支持连接管理、批量读取、单点写入和会话保活。
/// </summary>
public sealed class OpcUaDriver : IProtocolDriver
{
    private Session? _session;
    private ApplicationInstance? _application;
    private bool _connected;
    private string _endpointUrl;
    private readonly string? _username;
    private readonly string? _passwordEnvironmentVariable;
    private readonly int _timeoutMs;

    public string DriverType => "OpcUA";
    public bool IsConnected => _connected;

    /// <summary>
    /// 构造 OPC UA 驱动（无参，需后续配置）。
    /// </summary>
    public OpcUaDriver()
    {
        _endpointUrl = string.Empty;
        _timeoutMs = 3000;
    }

    /// <summary>
    /// 带设备配置构造驱动。
    /// </summary>
    public OpcUaDriver(DeviceConfig config)
    {
        _endpointUrl = $"opc.tcp://{config.IpAddress}:{config.Port}";
        _timeoutMs = config.TimeoutMs > 0 ? config.TimeoutMs : 3000;
        _username = string.IsNullOrWhiteSpace(config.OpcUaUsername) ? null : config.OpcUaUsername.Trim();
        _passwordEnvironmentVariable = string.IsNullOrWhiteSpace(config.OpcUaPasswordEnvironmentVariable)
            ? null
            : config.OpcUaPasswordEnvironmentVariable.Trim();
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_connected) return;
        ct.ThrowIfCancellationRequested();

        // 如果已存在旧会话，先断开
        await DisconnectAsync(ct).ConfigureAwait(false);

        // 初始化 ApplicationInstance（仅一次）
        if (_application == null)
        {
            var appConfig = new ApplicationConfiguration
            {
                ApplicationName = "IndustrialDAQ OPC UA Client",
                ApplicationType = ApplicationType.Client,
                ApplicationUri = $"urn:IndustrialDAQ:{Environment.MachineName}",
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = GetCertificateStorePath()
                    },
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false,
                    MinimumCertificateKeySize = 1024
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = _timeoutMs },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 }
            };

            // 确保证书目录存在
            Directory.CreateDirectory(GetCertificateStorePath());

            await appConfig.Validate(ApplicationType.Client).ConfigureAwait(false);

            _application = new ApplicationInstance
            {
                ApplicationType = ApplicationType.Client,
                ApplicationName = "IndustrialDAQ OPC UA Client",
                ApplicationConfiguration = appConfig
            };

            // 检查并创建证书（不存在则自动生成自签名证书，存在则复用）
            bool hasCert = await _application.CheckApplicationInstanceCertificate(false, 2048).ConfigureAwait(false);
            if (!hasCert)
            {
                throw new Exception("客户端证书初始化失败，请检查证书存储路径权限。");
            }
        }

        // 获取 ApplicationConfiguration（从已初始化的 _application 中取出）
        var config = _application.ApplicationConfiguration;

        // 发现端点（优先尝试无安全连接，若不支持则尝试安全连接）
        var endpoint = CoreClientUtils.SelectEndpoint(config, _endpointUrl, useSecurity: false);
        if (endpoint == null)
        {
            endpoint = CoreClientUtils.SelectEndpoint(config, _endpointUrl, useSecurity: true);
        }

        if (endpoint == null)
        {
            throw new Exception($"无法在 {_endpointUrl} 发现有效的 OPC UA 端点，请确认地址正确且服务器已开启。");
        }


        var endpointConfig = EndpointConfiguration.Create(config);
        var configuredEndpoint = new ConfiguredEndpoint(null, endpoint, endpointConfig);



        // 构建用户身份
        IUserIdentity identity;
        if (!string.IsNullOrEmpty(_username))
        {
            if (string.IsNullOrWhiteSpace(_passwordEnvironmentVariable))
                throw new InvalidOperationException("OPC UA 用户认证必须配置密码环境变量名称。");

            // 密码只在连接瞬间从进程环境读取，不进入代码、JSON 配置或日志。
            var password = Environment.GetEnvironmentVariable(_passwordEnvironmentVariable);
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException($"未设置 OPC UA 密码环境变量 '{_passwordEnvironmentVariable}'。");

            identity = new UserIdentity(_username, password);
        }
        else
            identity = new UserIdentity(new AnonymousIdentityToken());

        // 创建会话
        _session = await Session.Create(
            config,
            configuredEndpoint,
            false,                      // 不更新配置
            "IndustrialDAQ Session",
            60000,                      // 会话超时（毫秒）
            identity,
            null,                       // 可选的 preferred locales
            ct
        ).ConfigureAwait(false);

        // 注册保活事件，探测静默断连
        _session.KeepAlive += (s, e) =>
        {
            if (ServiceResult.IsBad(e.Status))
            {
                _connected = false;
                // 注意：这里仅标记状态，由外部采集循环检测并触发重连
            }
        };

        _connected = true;
    }

    /// <summary>
    /// 获取证书存储目录路径（程序集同级 certs 目录）。
    /// </summary>
    private static string GetCertificateStorePath()
    {
        string? location = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(location))
            location = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(location, "certs");
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _connected = false;

        if (_session is not null)
        {
            await _session.CloseAsync(ct).ConfigureAwait(false);
            _session.Dispose();
            _session = null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagValue>> ReadTagsAsync(
        IEnumerable<TagPoint> tags, CancellationToken ct = default)
    {
        if (!_connected || _session is null)
            throw new InvalidOperationException("OPC UA 会话未建立");

        ct.ThrowIfCancellationRequested();

        var tagList = tags.ToList();
        if (tagList.Count == 0) return Array.Empty<TagValue>();

        var timestamp = DateTimeOffset.UtcNow;
        var resultMap = new Dictionary<string, TagValue>(tagList.Count);

        // 过滤只写标签（不参与读取），构建 ReadValueId 集合
        var readableTags = tagList.Where(t => t.Access != TagAccess.Write).ToList();
        var validTags = new List<(int Index, TagPoint Tag, NodeId NodeId)>();
        var readValueIds = new ReadValueIdCollection();

        for (int i = 0; i < readableTags.Count; i++)
        {
            try
            {
                NodeId nodeId = ParseNodeId(readableTags[i].Address);
                validTags.Add((i, readableTags[i], nodeId));

                readValueIds.Add(new ReadValueId
                {
                    NodeId = nodeId,
                    AttributeId = Attributes.Value
                });
            }
            catch
            {
                resultMap[tagList[i].Id] = new TagValue
                {
                    TagId = tagList[i].Id,
                    TagName = tagList[i].Name,
                    Value = null,
                    Quality = Quality.Bad,
                    Timestamp = timestamp,
                    DataType = MapToType(tagList[i].DataType)
                };
            }
        }

        if (readValueIds.Count > 0)
        {
            try
            {
                // 批量读取 — 结果顺序与 readValueIds 顺序一致
                var response = await _session.ReadAsync(
                    null, 0, TimestampsToReturn.Both, readValueIds, ct).ConfigureAwait(false);

                for (int i = 0; i < response.Results.Count && i < validTags.Count; i++)
                {
                    var (originalIndex, tag, _) = validTags[i];
                    var result = response.Results[i];

                    Quality quality = StatusCode.IsGood(result.StatusCode) ? Quality.Good
                        : StatusCode.IsUncertain(result.StatusCode) ? Quality.Uncertain
                        : Quality.Bad;

                    object? value = StatusCode.IsGood(result.StatusCode)
                        ? ConvertOpcValue(result.Value, tag.DataType)
                        : null;

                    resultMap[tag.Id] = new TagValue
                    {
                        TagId = tag.Id,
                        TagName = tag.Name,
                        Value = value,
                        Quality = quality,
                        Timestamp = result.SourceTimestamp != DateTime.MinValue
                            ? new DateTimeOffset(result.SourceTimestamp, TimeSpan.Zero)
                            : timestamp,
                        DataType = MapToType(tag.DataType)
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _connected = false; // 关键：标记为断开，以便 UI 触发报警并启动重连循环
                try { _session?.Close(); } catch { }
                
                foreach (var (_, tag, _) in validTags)
                {
                    if (resultMap.ContainsKey(tag.Id)) continue;
                    resultMap[tag.Id] = new TagValue
                    {
                        TagId = tag.Id, TagName = tag.Name, Value = null,
                        Quality = Quality.Bad, Timestamp = timestamp,
                        DataType = MapToType(tag.DataType)
                    };
                }
            }
        }

        return tagList.Select(t =>
            resultMap.TryGetValue(t.Id, out var v) ? v
            : new TagValue { TagId = t.Id, TagName = t.Name, Quality = Quality.Bad, Timestamp = timestamp }
        ).ToList();
    }

    /// <inheritdoc />
    public async Task WriteTagAsync(TagPoint tag, object value, CancellationToken ct = default)
    {
        if (!_connected || _session is null)
            throw new InvalidOperationException("OPC UA 会话未建立");

        ct.ThrowIfCancellationRequested();

        if (tag.Access == TagAccess.Read)
            throw new InvalidOperationException($"标签 {tag.Name} 为只读，不可写入");

        NodeId nodeId = ParseNodeId(tag.Address);
        object converted = ConvertToOpcType(value, tag.DataType);

        var writeValue = new WriteValue
        {
            NodeId = nodeId,
            AttributeId = Attributes.Value,
            Value = new DataValue(new Variant(converted))
        };

        var writeValues = new WriteValueCollection { writeValue };
        try
        {
            var response = await _session.WriteAsync(null, writeValues, ct).ConfigureAwait(false);

            if (response.Results.Count > 0 && StatusCode.IsBad(response.Results[0]))
                throw new InvalidOperationException($"OPC UA 写入失败: {response.Results[0]}");
        }
        catch (Exception)
        {
            _connected = false;
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    // ─── NodeId 解析 ───

    /// <summary>
    /// 从地址字符串解析 OPC UA NodeId。
    /// 支持格式: "ns=3;s=Temperature" (字符串), "ns=3;i=1234" (数字),
    /// "ns=3;g=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" (GUID)。
    /// </summary>
    private static NodeId ParseNodeId(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("OPC UA 地址不能为空");

        if (address.Contains(";"))
        {
            ushort ns = 0;

            foreach (string part in address.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = part.Trim();
                if (trimmed.StartsWith("ns=", StringComparison.OrdinalIgnoreCase))
                    ns = ushort.Parse(trimmed[3..]);
                else if (trimmed.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
                    return new NodeId(trimmed[2..], ns);
                else if (trimmed.StartsWith("i=", StringComparison.OrdinalIgnoreCase))
                    return new NodeId(uint.Parse(trimmed[2..]), ns);
                else if (trimmed.StartsWith("g=", StringComparison.OrdinalIgnoreCase))
                    return new NodeId(Guid.Parse(trimmed[2..]), ns);
                else if (trimmed.StartsWith("b=", StringComparison.OrdinalIgnoreCase))
                    return new NodeId(Convert.FromBase64String(trimmed[2..]), ns);
            }
        }

        // 默认处理为 ns=0 字符串 ID
        return new NodeId(address, 0);
    }

    // ─── 类型转换 ───

    /// <summary>
    /// 将 OPC UA Variant 值转换为 CLR 类型。
    /// </summary>
    private static object? ConvertOpcValue(object raw, TagDataType dataType)
    {
        if (raw is null) return null;

        try
        {
            return dataType switch
            {
                TagDataType.Bool => Convert.ToBoolean(raw),
                TagDataType.Int16 => Convert.ToInt16(raw),
                TagDataType.Int32 => Convert.ToInt32(raw),
                TagDataType.Int64 => Convert.ToInt64(raw),
                TagDataType.UInt16 => Convert.ToUInt16(raw),
                TagDataType.UInt32 => Convert.ToUInt32(raw),
                TagDataType.Float32 => Convert.ToSingle(raw),
                TagDataType.Float64 => Convert.ToDouble(raw),
                TagDataType.String => raw.ToString() ?? string.Empty,
                _ => raw
            };
        }
        catch
        {
            return raw;
        }
    }

    /// <summary>
    /// 将 CLR 值转换为 OPC UA Variant 兼容类型。
    /// </summary>
    private static object ConvertToOpcType(object value, TagDataType dataType)
    {
        return dataType switch
        {
            TagDataType.Bool => Convert.ToBoolean(value),
            TagDataType.Int16 => Convert.ToInt16(value),
            TagDataType.Int32 => Convert.ToInt32(value),
            TagDataType.Int64 => Convert.ToInt64(value),
            TagDataType.UInt16 => Convert.ToUInt16(value),
            TagDataType.UInt32 => Convert.ToUInt32(value),
            TagDataType.Float32 => Convert.ToSingle(value),
            TagDataType.Float64 => Convert.ToDouble(value),
            TagDataType.String => value.ToString() ?? string.Empty,
            _ => value
        };
    }

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
}
