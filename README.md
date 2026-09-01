# IndustrialDAQ

IndustrialDAQ 是一个基于 .NET 8、WPF 和 Prism 构建的工业数据采集与监控运行时平台，覆盖设备采集、实时监控、历史趋势、规则报警、资源树以及动态权限管理。

## 核心能力

- **多协议采集**：采用插件式驱动架构，已包含 Modbus TCP、OPC UA、S7 和 MQTT 驱动模块。
- **配置驱动**：设备、数据点和驱动参数由 JSON 配置加载，支持运行时热更新。
- **资源树模型**：将设备配置同步为 `Devices/{设备}/{数据点}` 层级资源，统一供趋势、报警、写入和权限模块使用。
- **实时与历史趋势**：支持按设备复选数值型数据点、实时滚动、历史区间查询、十字线悬浮和报警点标识。
- **规则与报警**：规则定义支持动态加载，报警状态机负责触发、恢复、确认和历史记录。
- **用户与权限**：账号、角色和关联关系独立持久化，按 `ResourcePath + Action` 执行动态授权。
- **安全审计**：记录登录、登录失败、退出、权限变更、授权拒绝及数据点写入结果。
- **数据加工**：计算规则持久化后热加载，支持表达式计算和虚拟数据点输出。
- **机器视觉**：提供瓶盖有无检测 MVP，支持目录模拟相机、ROI 模板教学、NG 存图、动态数据点和报警联动。
- **可靠存储**：支持 SQLite/PostgreSQL、历史保留清理、类型恢复、写入重试及死信回放。
- **外部通知**：报警中心支持钉钉/企业微信兼容文本 Webhook 适配器。
- **主题与响应式布局**：提供深色/浅色主题，并针对不同窗口尺寸调整首页卡片和业务页面布局。

## 权限模型

系统采用默认拒绝和拒绝优先策略：

```text
User + Role
    ↓
PermissionSubject
    ↓
ResourcePath + Action
    ↓
PermissionSnapshot
    ↓
Deny 优先 → 路径深度 → Priority
    ↓
允许业务操作 / 拒绝并写入审计日志
```

内置角色：

- `Guest`：只读访问。
- `Operator`：生产操作角色，可按现场资源配置权限。
- `Engineer`：工程维护角色，默认可写入设备数据点。
- `Admin`：账号、权限策略和系统配置管理角色。

权限策略保存后会热重载为不可变运行时快照，无需重启客户端。详细设计和测试结果见 [`docs/权限与ResourcePath动态授权说明.md`](docs/权限与ResourcePath动态授权说明.md)。

## 技术栈

- .NET 8 / WPF
- Prism MVVM、DI、EventAggregator、DialogService
- Entity Framework Core / SQLite / PostgreSQL
- LiveChartsCore.SkiaSharpView.WPF
- Serilog
- NModbus、OPC UA、S7
- Python / Pymodbus 模拟设备

## 项目结构

```text
src/
├── IndustrialDAQ.UI              WPF 客户端、页面、对话框和应用服务
├── IndustrialDAQ.Core            领域模型、资源树、动态授权契约
├── IndustrialDAQ.Acquisition     采集主机、驱动调度和实时发布
├── IndustrialDAQ.Storage         实时流和历史数据持久化
├── IndustrialDAQ.Alarm           规则工作流、报警状态机和报警中心
├── IndustrialDAQ.Trend           趋势数据服务
├── IndustrialDAQ.Vision          相机驱动、视觉算法、任务运行时与视觉配置仓储
└── IndustrialDAQ.Infrastructure  EF Core 仓储和数据库实体

Plugins/
├── Drivers.Modbus
├── Drivers.OpcUA
├── Drivers.Mqtt
└── Drivers.S7

config/                            设备配置与 Modbus 模拟脚本
docs/                              架构和功能说明
```

## 快速启动

### 环境要求

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022、JetBrains Rider 或 PowerShell 7
- Python 3.10+（运行 Modbus 模拟器时需要）

### 启动 Modbus 模拟设备

```powershell
pip install pymodbus
python config/python_modbus_slave.py
```

模拟脚本与 `config/production-line.json` 配合使用，用于验证实时采集、参数写入、规则触发和报警恢复。

### 编译客户端

```powershell
dotnet restore Projects.sln
dotnet build src/IndustrialDAQ.UI/IndustrialDAQ.UI.csproj
```

### 运行客户端

```powershell
dotnet run --project src/IndustrialDAQ.UI/IndustrialDAQ.UI.csproj
```

SQLite 数据库固定保存在客户端程序目录，避免因 IDE、终端或快捷方式的启动目录不同而创建多份数据库。

客户端可在“系统设置 → 存储管理”维护多个 SQLite/PostgreSQL 连接档案、测试连接并选择下次启动使用的数据库。数据库密码不会写入 JSON，只保存环境变量名称；保存配置后需重启客户端完成切换。`INDUSTRIALDAQ_STORAGE_PROVIDER` 与 `INDUSTRIALDAQ_POSTGRES_CONNECTION` 仍作为部署环境的最高优先级覆盖项。

首次初始化会创建管理员账号。部署到正式现场前，应立即修改初始密码并根据产线、设备和数据点配置最小权限策略。

### 可选运行环境变量

```powershell
# PostgreSQL（默认仍为 SQLite）
$env:INDUSTRIALDAQ_STORAGE_PROVIDER = "PostgreSQL"
$env:INDUSTRIALDAQ_POSTGRES_CONNECTION = "Host=localhost;Database=industrialdaq;Username=daq;Password=..."

# 多个报警机器人地址使用分号分隔
$env:INDUSTRIALDAQ_ALARM_WEBHOOKS = "https://example/webhook-a;https://example/webhook-b"

# OPC UA / MQTT 密码使用设备 JSON 中配置的环境变量名称读取
$env:MY_OPCUA_PASSWORD = "..."
$env:MY_MQTT_PASSWORD = "..."
```

MQTT 驱动将数据点 `Address` 作为主题：读取时订阅主题并缓存最新值，写入时向主题发布值。本机联调可安装 Mosquitto，并确认 Windows 服务 `mosquitto` 正在监听 `1883` 端口。

## 配置说明

- `config/production-line.json`：产线、设备、驱动和数据点定义。
- `config/python_modbus_slave.py`：Modbus TCP 模拟设备。
- 数据点必须通过资源树同步后，才能被规则、趋势和动态权限统一引用。
- `alarm-definitions.json`、`permission-policies.json`、`calculation-rules.json` 支持文件变更热加载。
- 业务代码不得写死设备、数据点、菜单权限或报警判断条件。
- 计算规则与报警规则完整流程见 [`docs/CALCULATION_AND_ALARM_RULES.md`](docs/CALCULATION_AND_ALARM_RULES.md)。
- 瓶盖有无检测配置、运行链路和 ResourcePath 说明见 [`docs/MACHINE_VISION_CAP_MVP.md`](docs/MACHINE_VISION_CAP_MVP.md)。

## 验证

```powershell
dotnet build src/IndustrialDAQ.UI/IndustrialDAQ.UI.csproj --no-restore
```

自动化测试项目位于 `tests/IndustrialDAQ.Tests`。当前覆盖权限继承与拒绝、报警事件广播、计算引擎、计算规则仓储、历史值类型恢复以及数据库连接档案。

## 开发原则

- 配置驱动，禁止写死现场资源。
- 功能模块化，资源树结构化。
- 权限动态化，规则支持热更新。
- 服务异步化，数据事件化。
- 关键修改和授权失败必须产生审计记录。

## License

MIT License
