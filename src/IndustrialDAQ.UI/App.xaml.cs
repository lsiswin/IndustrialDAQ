// File: App.xaml.cs  Module: UI (Composition Root)  Author: IndustrialDAQ Team
using IndustrialDAQ.Acquisition;
using IndustrialDAQ.Acquisition.Mocks;
using IndustrialDAQ.Alarm;
using IndustrialDAQ.Alarm.Center;
using IndustrialDAQ.Alarm.RuleBuilder;
using IndustrialDAQ.Alarm.RuleEngine;
using IndustrialDAQ.Alarm.StateMachine;
using IndustrialDAQ.Alarm.Notifications;
using IndustrialDAQ.Core;
using IndustrialDAQ.Core.Authorization;
using IndustrialDAQ.Core.Configuration;
using IndustrialDAQ.Core.Interfaces;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using IndustrialDAQ.Infrastructure;
using IndustrialDAQ.Infrastructure.Alarms;
using IndustrialDAQ.Infrastructure.Authorization;
using IndustrialDAQ.Infrastructure.ResourceTree;
using IndustrialDAQ.Infrastructure.Processing;
using IndustrialDAQ.Processing;
using IndustrialDAQ.Storage;
using IndustrialDAQ.Trend;
using IndustrialDAQ.UI.ViewModels;
using IndustrialDAQ.UI.Views;
using IndustrialDAQ.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prism.DryIoc;
using Prism.Ioc;
using Prism.Navigation.Regions;
using Serilog;
using Serilog.Events;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace IndustrialDAQ.UI;

public partial class App : PrismApplication
{
    private FileSystemWatcher? _configWatcher;
    private CancellationTokenSource? _debounceCts;
    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<AcquisitionChannel>();
        containerRegistry.RegisterSingleton<IDriverFactory, DriverFactory>();
        containerRegistry.RegisterSingleton<RealTimeStore>();
        containerRegistry.RegisterSingleton<AcquisitionHost>();
        containerRegistry.RegisterSingleton<HistoryWriter>();
        containerRegistry.RegisterSingleton<DataProcessor>();
        containerRegistry.RegisterSingleton<CalculationRuleRepository>();
        containerRegistry.RegisterSingleton<MainWindowViewModel>();

        // 报警系统服务（统一走新链路，AlarmManager 仅作为 UI 兼容门面）
        containerRegistry.RegisterSingleton<AlarmHistoryRepository>();
        containerRegistry.RegisterSingleton<AlarmManager>();

        // 趋势引擎服务
        containerRegistry.RegisterSingleton<TrendDataStore>();
        containerRegistry.RegisterSingleton<TrendEngine>();

        // 设备模板仓储
        containerRegistry.RegisterSingleton<DeviceTemplateRepository>();

        // Runtime resource tree. Later modules use this same tree for menus,
        // devices, tags, alarms, rules and inherited permissions.
        containerRegistry.RegisterSingleton<IResourceTreeRepository, ResourceTreeRepository>();
        containerRegistry.RegisterSingleton<IResourceTreeService, ResourceTreeService>();
        // 将 AcquisitionHost 实时设备树镜像为资源树（单一事实源，解决报警页下拉与现场设备不一致）
        containerRegistry.RegisterSingleton<ResourceTreeSynchronizer>();
        
        containerRegistry.RegisterSingleton<IUserRepository, UserRepository>();
        containerRegistry.RegisterSingleton<IAuthManager, AuthManager>();
        containerRegistry.RegisterSingleton<SecurityAuditService>();
        containerRegistry.RegisterSingleton<PermissionManagementService>();
        containerRegistry.RegisterSingleton<RuntimeSettingsService>();
        containerRegistry.RegisterSingleton<HistoricalDataRetentionService>();

        containerRegistry.RegisterSingleton<IAuthorizationRepository, AuthorizationRepository>();
        containerRegistry.RegisterSingleton<IAuthorizationService, AuthorizationService>();

        // Database-backed alarm definitions. RuleBuilder will consume these
        // definitions and build executable workflows in the next module.
        containerRegistry.RegisterSingleton<IAlarmDefinitionRepository, AlarmDefinitionRepository>();
        containerRegistry.RegisterSingleton<IAlarmDefinitionService, AlarmDefinitionService>();
        containerRegistry.RegisterSingleton<IAlarmRuleBuilder, AlarmRuleBuilder>();
        containerRegistry.RegisterSingleton<IAlarmRuleSignalBus, AlarmRuleSignalBus>();
        containerRegistry.RegisterSingleton<IRuleEngineService, RuleEngineService>();
        containerRegistry.RegisterSingleton<IAlarmStateTransitionBus, AlarmStateTransitionBus>();
        containerRegistry.RegisterSingleton<IAlarmStateMachineService, AlarmStateMachineService>();
        containerRegistry.RegisterSingleton<IAlarmCenterEventBus, AlarmCenterEventBus>();
        containerRegistry.RegisterSingleton<IAlarmCenter, AlarmCenter>();
        containerRegistry.RegisterSingleton<WebhookAlarmNotificationAdapter>();
        containerRegistry.RegisterSingleton<AlarmNotificationDispatcher>();

        // ViewModel 注册（支持构造函数注入）
        containerRegistry.Register<AlarmRecordViewModel>();
        containerRegistry.Register<TrendViewModel>();

        containerRegistry.RegisterForNavigation<DashboardView>();
        containerRegistry.RegisterForNavigation<ProductionMonitorView>();
        containerRegistry.RegisterForNavigation<DeviceDetailView>();
        containerRegistry.RegisterForNavigation<AlarmRecordView>();
        containerRegistry.RegisterForNavigation<TrendView>();
        containerRegistry.RegisterForNavigation<DeviceTemplateView>();
        containerRegistry.RegisterForNavigation<SystemSettingsView>();
        // 报警规则只通过“系统设置/报警规则”进入，避免出现多个配置入口。

        containerRegistry.RegisterDialogWindow<FramelessDialogWindow>();
        containerRegistry.RegisterDialog<WriteTagDialog, WriteTagDialogViewModel>();
        containerRegistry.RegisterDialog<CreateDeviceDialog, CreateDeviceDialogViewModel>();
        containerRegistry.RegisterDialog<AddDeviceTemplateDialog, AddDeviceTemplateDialogViewModel>();
        containerRegistry.RegisterDialog<LoginDialog, LoginDialogViewModel>();
        containerRegistry.RegisterDialog<RegisterDialog, RegisterDialogViewModel>();
    }

    protected override IContainerExtension CreateContainerExtension()
    {
        var extension = new DryIocContainerExtension();
        var services = new ServiceCollection();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()  // 全局 Information
            .MinimumLevel.Override("IndustrialDAQ", LogEventLevel.Debug)  // 项目代码保持 Debug
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Query", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.ChangeTracking", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Update", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Migrations", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Model", LogEventLevel.Warning)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        services.AddLogging(builder => {
            builder.AddSerilog();
        });

        // 数据库固定到程序目录，避免从 IDE、终端或快捷方式启动时生成多份相对路径数据库。
        var databasePath = Path.Combine(AppContext.BaseDirectory, "industrialdaq.db");
        services.AddDbContextFactory<DaqDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));

        extension.Populate(services);

        return extension;
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();

        // ── 初始化数据库 ──
        var dbFactory = Container.Resolve<IDbContextFactory<DaqDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            // 为已有数据库补建新增的表（EnsureCreated 不会为已有库添加新表）
            EnsureTemplateTablesExist(db);
            EnsureSecuritySchema(db);
        }

        // ── 初始化内置设备模板（首次运行时写入数据库）──
        var templateRepo = Container.Resolve<DeviceTemplateRepository>();
        _ = templateRepo.InitializeBuiltInTemplatesAsync();

        var authorizationService = Container.Resolve<IAuthorizationService>();
        _ = authorizationService.ReloadAsync(CancellationToken.None);

        // ── 注册所有协议驱动 ──
        var driverFactory = Container.Resolve<IDriverFactory>();

        driverFactory.RegisterDriver("Modbus", (config, ct) =>
        {
            var driver = new global::Drivers.Modbus.ModbusTcpDriver(config);
            return Task.FromResult<IProtocolDriver>(driver);
        });

        driverFactory.RegisterDriver("OpcUA", (config, ct) =>
        {
            var driver = new global::Drivers.OpcUA.OpcUaDriver(config);
            return Task.FromResult<IProtocolDriver>(driver);
        });

        driverFactory.RegisterDriver("S7", (config, ct) =>
        {
            var driver = new IndustrialDAQ.Drivers.S7.S7Driver(config);
            return Task.FromResult<IProtocolDriver>(driver);
        });

        driverFactory.RegisterDriver("Mock", (config, ct) =>
        {
            var driver = new MockProtocolDriver();
            return Task.FromResult<IProtocolDriver>(driver);
        });

        driverFactory.RegisterDriver("MQTT", (config, ct) =>
            Task.FromResult<IProtocolDriver>(new global::Drivers.Mqtt.MqttDriver(config)));

        // ── 启动采集宿主和历史写入器 ──
        var acquisitionHost = Container.Resolve<AcquisitionHost>();
        var historyWriter = Container.Resolve<HistoryWriter>();
        var dataProcessor = Container.Resolve<DataProcessor>();

        _ = acquisitionHost.StartAsync(CancellationToken.None);
        _ = historyWriter.StartAsync(CancellationToken.None);
        _ = dataProcessor.StartAsync(CancellationToken.None);
        _ = LoadCalculationRulesAsync(dataProcessor);

        // ── 启动报警系统 ──
        var alarmManager = Container.Resolve<AlarmManager>();
        var ruleEngineService = Container.Resolve<IRuleEngineService>();
        var alarmStateMachineService = Container.Resolve<IAlarmStateMachineService>();
        var alarmCenter = Container.Resolve<IAlarmCenter>();
        var notificationDispatcher = Container.Resolve<AlarmNotificationDispatcher>();
        var retentionService = Container.Resolve<HistoricalDataRetentionService>();
        _ = alarmManager.StartAsync(CancellationToken.None);
        _ = ruleEngineService.StartAsync(CancellationToken.None);
        _ = alarmStateMachineService.StartAsync(CancellationToken.None);
        _ = alarmCenter.StartAsync(CancellationToken.None);
        _ = notificationDispatcher.StartAsync(CancellationToken.None);
        _ = retentionService.StartAsync(CancellationToken.None);

        // ── 启动趋势引擎 ──
        var trendEngine = Container.Resolve<TrendEngine>();
        _ = trendEngine.StartAsync(CancellationToken.None);

        // ── 加载 JSON 配置并启动设备（内部完成设备启动后会注册报警规则+趋势Tag） ──
        _ = LoadAndStartDevicesAsync(acquisitionHost, historyWriter, alarmManager, trendEngine);

        // ── 导航到仪表板 ──
        var regionManager = Container.Resolve<IRegionManager>();
        regionManager.RequestNavigate("MainRegion", nameof(DashboardView));
    }

    private async Task LoadCalculationRulesAsync(DataProcessor processor)
    {
        try
        {
            var repository = Container.Resolve<CalculationRuleRepository>();
            processor.ReplaceRules(await repository.LoadAsync());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "计算规则加载失败");
        }
    }

    /// <summary>
    /// 为已有数据库补建模板相关表（EnsureCreated 不会为已有库添加新表）。
    /// </summary>
    private static void EnsureTemplateTablesExist(DaqDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            conn.Open();

        using var cmd = conn.CreateCommand();

        cmd.CommandText = @"
            -- 迁移：重建表以修正列定义
            DROP TABLE IF EXISTS data_point_templates;
            DROP TABLE IF EXISTS trend_templates;
            DROP TABLE IF EXISTS device_templates;

            CREATE TABLE IF NOT EXISTS device_templates (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TemplateId TEXT NOT NULL,
                Name TEXT NOT NULL,
                DriverType TEXT NOT NULL,
                IsBuiltIn INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_device_templates_TemplateId ON device_templates(TemplateId);
            CREATE INDEX IF NOT EXISTS IX_device_templates_IsBuiltIn ON device_templates(IsBuiltIn);

            CREATE TABLE IF NOT EXISTS data_point_templates (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DeviceTemplateId INTEGER NOT NULL,
                TemplateId TEXT NOT NULL,
                Name TEXT NOT NULL,
                DataType INTEGER NOT NULL,
                Unit TEXT NOT NULL DEFAULT '',
                AlarmTemplateId TEXT,
                TrendTemplateId TEXT,
                FOREIGN KEY (DeviceTemplateId) REFERENCES device_templates(Id)
            );
            CREATE INDEX IF NOT EXISTS IX_data_point_templates_DeviceTemplateId ON data_point_templates(DeviceTemplateId);
            CREATE INDEX IF NOT EXISTS IX_data_point_templates_TemplateId ON data_point_templates(TemplateId);

            CREATE TABLE IF NOT EXISTS alarm_templates (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TemplateId TEXT NOT NULL,
                Name TEXT NOT NULL,
                ApplicableDataType INTEGER NOT NULL,
                Unit TEXT NOT NULL DEFAULT '',
                HighThreshold REAL NOT NULL DEFAULT 0,
                HighHighThreshold REAL NOT NULL DEFAULT 0,
                LowThreshold REAL NOT NULL DEFAULT 0,
                LowLowThreshold REAL NOT NULL DEFAULT 0,
                Hysteresis REAL NOT NULL DEFAULT 0,
                Severity INTEGER NOT NULL DEFAULT 1,
                CooldownSeconds INTEGER NOT NULL DEFAULT 60,
                SupportedAlarmTypesJson TEXT NOT NULL DEFAULT '[]',
                IsBuiltIn INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_alarm_templates_TemplateId ON alarm_templates(TemplateId);
            CREATE INDEX IF NOT EXISTS IX_alarm_templates_IsBuiltIn ON alarm_templates(IsBuiltIn);

            CREATE TABLE IF NOT EXISTS alarm_definitions (
                Id TEXT PRIMARY KEY,
                RuleId TEXT NOT NULL,
                AlarmCode TEXT NOT NULL,
                ResourcePath TEXT,
                TargetResourcePath TEXT,
                TagId TEXT NOT NULL DEFAULT '',
                TagName TEXT NOT NULL DEFAULT '',
                AlarmType TEXT NOT NULL DEFAULT 'High',
                Operator TEXT NOT NULL DEFAULT 'GreaterThan',
                Threshold REAL NOT NULL DEFAULT 0,
                Deadband REAL NOT NULL DEFAULT 0,
                ConditionExpression TEXT NOT NULL,
                ConditionsJson TEXT NOT NULL DEFAULT '[]',
                ClearExpression TEXT,
                SuppressionExpression TEXT,
                ExpressionJoin TEXT NOT NULL DEFAULT 'And',
                DelayMs INTEGER NOT NULL DEFAULT 0,
                Hysteresis REAL NOT NULL DEFAULT 0,
                Severity TEXT NOT NULL DEFAULT 'Warning',
                Title TEXT NOT NULL DEFAULT '',
                MessageTemplate TEXT NOT NULL DEFAULT '',
                Source TEXT NOT NULL DEFAULT '',
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                AckPolicy TEXT NOT NULL DEFAULT 'Required',
                ClearPolicy TEXT NOT NULL DEFAULT 'AutoClearWhenConditionFalse',
                CooldownSeconds INTEGER NOT NULL DEFAULT 60,
                WorkflowType TEXT NOT NULL DEFAULT 'Expression',
                WorkflowKey TEXT NOT NULL DEFAULT 'default',
                MetadataJson TEXT,
                Version INTEGER NOT NULL DEFAULT 1,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_alarm_definitions_RuleId ON alarm_definitions(RuleId);
            CREATE INDEX IF NOT EXISTS IX_alarm_definitions_AlarmCode ON alarm_definitions(AlarmCode);
            CREATE INDEX IF NOT EXISTS IX_alarm_definitions_ResourcePath ON alarm_definitions(ResourcePath);
            CREATE INDEX IF NOT EXISTS IX_alarm_definitions_TargetResourcePath ON alarm_definitions(TargetResourcePath);
            CREATE INDEX IF NOT EXISTS IX_alarm_definitions_TagId ON alarm_definitions(TagId);
            CREATE INDEX IF NOT EXISTS IX_alarm_definitions_IsEnabled ON alarm_definitions(IsEnabled);
            CREATE INDEX IF NOT EXISTS IX_alarm_definitions_TargetResourcePath_AlarmCode ON alarm_definitions(TargetResourcePath, AlarmCode);

            CREATE TABLE IF NOT EXISTS trend_templates (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TemplateId TEXT NOT NULL,
                Name TEXT NOT NULL,
                Unit TEXT NOT NULL DEFAULT '',
                YMin REAL,
                YMax REAL,
                BufferCapacity INTEGER NOT NULL DEFAULT 3600,
                WindowSeconds INTEGER NOT NULL DEFAULT 300,
                LineColor TEXT NOT NULL DEFAULT '#3B82F6',
                ShowAlarmLines INTEGER NOT NULL DEFAULT 1,
                StrokeThickness REAL NOT NULL DEFAULT 2,
                ShowGeometry INTEGER NOT NULL DEFAULT 0,
                IsBuiltIn INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_trend_templates_TemplateId ON trend_templates(TemplateId);
            CREATE INDEX IF NOT EXISTS IX_trend_templates_IsBuiltIn ON trend_templates(IsBuiltIn);

            -- 运行时资源树表：存储工厂、产线、设备、标签等所有资源的层级关系
            CREATE TABLE IF NOT EXISTS resource_nodes (
                Id TEXT PRIMARY KEY,
                ParentId TEXT,
                ResourcePath TEXT NOT NULL,
                Name TEXT NOT NULL,
                DisplayName TEXT NOT NULL,
                ResourceType TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                MetadataJson TEXT,
                Version INTEGER NOT NULL DEFAULT 1,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_resource_nodes_ResourcePath ON resource_nodes(ResourcePath);
            CREATE INDEX IF NOT EXISTS IX_resource_nodes_ParentId ON resource_nodes(ParentId);
            CREATE INDEX IF NOT EXISTS IX_resource_nodes_ResourceType ON resource_nodes(ResourceType);
            CREATE INDEX IF NOT EXISTS IX_resource_nodes_IsEnabled ON resource_nodes(IsEnabled);
            CREATE INDEX IF NOT EXISTS IX_resource_nodes_ParentId_SortOrder ON resource_nodes(ParentId, SortOrder);

            CREATE TABLE IF NOT EXISTS permission_policies (
                Id TEXT PRIMARY KEY,
                SubjectType TEXT NOT NULL,
                SubjectId TEXT NOT NULL,
                ResourcePath TEXT NOT NULL,
                Action TEXT NOT NULL,
                Effect TEXT NOT NULL,
                Inherit INTEGER NOT NULL DEFAULT 1,
                Priority INTEGER NOT NULL DEFAULT 0,
                ConditionJson TEXT,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                Version INTEGER NOT NULL DEFAULT 1,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_permission_policies_SubjectId ON permission_policies(SubjectId);
            CREATE INDEX IF NOT EXISTS IX_permission_policies_SubjectType ON permission_policies(SubjectType);
            CREATE INDEX IF NOT EXISTS IX_permission_policies_ResourcePath ON permission_policies(ResourcePath);
            CREATE INDEX IF NOT EXISTS IX_permission_policies_Action ON permission_policies(Action);
            CREATE INDEX IF NOT EXISTS IX_permission_policies_IsEnabled ON permission_policies(IsEnabled);
            CREATE INDEX IF NOT EXISTS IX_permission_policies_SubjectType_SubjectId_Action ON permission_policies(SubjectType, SubjectId, Action);
            CREATE INDEX IF NOT EXISTS IX_permission_policies_ResourcePath_Action ON permission_policies(ResourcePath, Action);
            CREATE TABLE IF NOT EXISTS calculation_rules (
                RuleId TEXT PRIMARY KEY,
                Expression TEXT NOT NULL,
                InputTagNamesJson TEXT NOT NULL,
                TargetTagId TEXT NOT NULL,
                TargetTagName TEXT NOT NULL,
                TargetDataType TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                UpdatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_calculation_rules_Enabled ON calculation_rules(Enabled);
            CREATE INDEX IF NOT EXISTS IX_calculation_rules_TargetTagId ON calculation_rules(TargetTagId);
        ";
        cmd.ExecuteNonQuery();

        Log.Information("模板相关表已确保存在");
    }

    /// <summary>兼容升级已有现场数据库的账号安全字段和审计表。</summary>
    private static void EnsureSecuritySchema(DaqDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"CREATE TABLE IF NOT EXISTS security_users (id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL, real_name TEXT NOT NULL, created_at_utc TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1, failed_login_count INTEGER NOT NULL DEFAULT 0, locked_until_utc TEXT NULL, must_change_password INTEGER NOT NULL DEFAULT 0, last_login_at_utc TEXT NULL);
            CREATE TABLE IF NOT EXISTS security_roles (Id TEXT PRIMARY KEY, Name TEXT NOT NULL UNIQUE, Description TEXT NOT NULL, IsBuiltIn INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS security_user_roles (UserId TEXT NOT NULL, RoleId TEXT NOT NULL, PRIMARY KEY(UserId, RoleId));
            CREATE TABLE IF NOT EXISTS security_audits (Id INTEGER PRIMARY KEY AUTOINCREMENT, OccurredAtUtc TEXT NOT NULL, UserId TEXT NOT NULL, Username TEXT NOT NULL, Action TEXT NOT NULL, ResourcePath TEXT NOT NULL, Detail TEXT NOT NULL, Success INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_security_audits_OccurredAtUtc ON security_audits(OccurredAtUtc);
            CREATE INDEX IF NOT EXISTS IX_security_audits_UserId ON security_audits(UserId);
            CREATE INDEX IF NOT EXISTS IX_security_audits_Action ON security_audits(Action);
            INSERT OR IGNORE INTO security_roles VALUES ('role-guest','Guest','只读访客',1);
            INSERT OR IGNORE INTO security_roles VALUES ('role-operator','Operator','生产操作员',1);
            INSERT OR IGNORE INTO security_roles VALUES ('role-engineer','Engineer','工程维护人员',1);
            INSERT OR IGNORE INTO security_roles VALUES ('role-admin','Admin','系统安全管理员',1);
            INSERT OR IGNORE INTO permission_policies (Id,SubjectType,SubjectId,ResourcePath,Action,Effect,Inherit,Priority,IsEnabled,Version,CreatedAtUtc,UpdatedAtUtc) VALUES ('builtin-engineer-device-write','Role','Engineer','Devices','Write','Allow',1,100,1,1,datetime('now'),datetime('now'));
            INSERT OR IGNORE INTO permission_policies (Id,SubjectType,SubjectId,ResourcePath,Action,Effect,Inherit,Priority,IsEnabled,Version,CreatedAtUtc,UpdatedAtUtc) VALUES ('builtin-admin-device-all','Role','Admin','Devices','*','Allow',1,100,1,1,datetime('now'),datetime('now'));
            INSERT OR IGNORE INTO permission_policies (Id,SubjectType,SubjectId,ResourcePath,Action,Effect,Inherit,Priority,IsEnabled,Version,CreatedAtUtc,UpdatedAtUtc) VALUES ('builtin-admin-system-all','Role','Admin','System','*','Allow',1,100,1,1,datetime('now'),datetime('now'));";
        command.ExecuteNonQuery();

        using var admin = connection.CreateCommand();
        admin.CommandText = "INSERT OR IGNORE INTO security_users (id,username,password_hash,real_name,created_at_utc,is_active,must_change_password) VALUES ('admin-sys','admin',@hash,'系统管理员',@created,1,1); INSERT OR IGNORE INTO security_user_roles VALUES ('admin-sys','role-admin');";
        var hash = admin.CreateParameter(); hash.ParameterName = "@hash"; hash.Value = CreateInitialPasswordHash("Admin#2026"); admin.Parameters.Add(hash);
        var created = admin.CreateParameter(); created.ParameterName = "@created"; created.Value = DateTime.UtcNow.ToString("O"); admin.Parameters.Add(created);
        admin.ExecuteNonQuery();
    }

    private static string CreateInitialPasswordHash(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return $"PBKDF2${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 注册测试报警规则到新报警定义仓储，用于开发调试。
    /// TagId 匹配 production-line.json 中的实际配置。
    /// </summary>
    private void RegisterDynamicAlarmRules(AlarmManager alarmManager, AcquisitionHost host)
    {
        var allRules = new List<AlarmDefinition>();
        var devices = host.GetDevices();

        // ═══ 报警规则模板（按 TagName 匹配，与设备/驱动无关）═══
        static AlarmDefinition MakeRule(string tagId, string tagName, string ruleId, string code,
            AlarmType type, string condition, double? hysteresis,
            AlarmSeverity severity, string title, string template, string source, int cooldown) => new()
        {
            RuleId = ruleId, AlarmCode = code, TagId = tagId, TagName = tagName,
            AlarmType = type, ConditionExpression = condition,
            Hysteresis = hysteresis ?? 0.0, Severity = severity,
            Title = title, MessageTemplate = template, Source = source,
            CooldownSeconds = cooldown
        };

        var ruleTemplates = new[]
        {
            ("Line.EStop",              "estop",       "LINE_ESTOP_ACTIVE",       AlarmType.Bool,   "Value == true",   (double?)null, AlarmSeverity.Critical, "急停按钮已触发",     "产线急停按钮已按下，请立即检查！", 5),
            ("Line.AlarmActive",         "alarm-active","LINE_ALARM_ACTIVE",        AlarmType.Bool,   "Value == true",   (double?)null, AlarmSeverity.Critical, "产线设备报警",      "产线异常，请检查！",                5),
            ("Filling.ActualLevel",      "fill-high",   "FILL_LEVEL_HIGH",          AlarmType.High,   "Value >= 700",    30.0,         AlarmSeverity.Warning,  "灌装液位偏高",      "灌装液位 {Value} mL 达到警戒线", 60),
            ("Filling.ActualLevel",      "fill-highhigh","FILL_LEVEL_HIGH_HIGH",    AlarmType.HighHigh,"Value >= 800",    30.0,         AlarmSeverity.Critical,  "灌装液位超高",      "灌装液位 {Value} mL 超过高高限，有溢出风险！",60),
            ("Conveyor.ActualSpeed",     "speed-high",  "CONVEYOR_SPEED_HIGH",      AlarmType.High,   "Value > 25",      2.0,          AlarmSeverity.Warning,  "传送速度偏高",      "传送速度 {Value} m/min 超过 25 m/min",15),
            ("Conveyor.ActualSpeed",     "speed-low",   "CONVEYOR_SPEED_LOW",       AlarmType.Low,    "Value < 5",       2.0,          AlarmSeverity.Warning,  "传送速度偏低",      "传送速度 {Value} m/min 低于 5 m/min",15),
        };

        foreach (var device in devices)
        {
            foreach (var (tagName, ruleId, code, type, cond, hys, sev, title, tmpl, cd) in ruleTemplates)
            {
                var tag = device.Tags.FirstOrDefault(t => t.Name == tagName);
                if (tag == null) continue;
                var safe = string.IsNullOrEmpty(device.Name) ? "unknown" : new string(device.Name.TakeWhile(char.IsLetterOrDigit).ToArray());
                allRules.Add(MakeRule(tag.Id, tag.Name, $"alm-{safe}-{ruleId}", code, type, cond, hys, sev, title, tmpl, device.Name ?? "未知设备", cd));
            }
        }

        if (allRules.Count > 0)
        {
            alarmManager.RegisterRules([.. allRules]);
            Log.Information("已动态注册 {Count} 条报警规则（覆盖 {DeviceCount} 台设备）", allRules.Count, devices.Count);
        }
        else
        {
            Log.Warning("未加载任何设备，跳过报警规则注册");
        }
    }

    /// <summary>
    /// 注册趋势跟踪 Tag，并从新报警定义快照中生成趋势报警线。
    /// </summary>
    private void RegisterTrendTags(TrendEngine trendEngine, IAlarmDefinitionService definitionService, AcquisitionHost host)
    {
        // 从实际加载的设备中动态查找模拟量 Tag（Float32/Int32），不再硬编码 S7 的 TagId
        var devices = host.GetDevices();
        var analogTags = new List<(string TagId, string TagName)>();
        foreach (var device in devices)
        {
            foreach (var tag in device.Tags.Where(t => t.DataType is TagDataType.Float32 or TagDataType.Int32 or TagDataType.Float64 or TagDataType.Int64))
            {
                analogTags.Add((tag.Id, tag.Name));
            }
        }

        // 如果没找到任何模拟量，回退到已知 TagId（兼容旧配置）
        if (analogTags.Count == 0)
        {
            analogTags = [("tag-filling-actuallevel", "Filling.ActualLevel"), ("tag-conveyor-actualspeed", "Conveyor.ActualSpeed")];
        }

        var tagUnits = new Dictionary<string, string>
        {
            ["Filling.ActualLevel"] = "mL", ["Conveyor.ActualSpeed"] = "m/min",
            ["Line.TotalCount"] = "个"
        };
        var tagColors = new Dictionary<string, string>
        {
            ["Filling.ActualLevel"] = "#3B82F6", ["Conveyor.ActualSpeed"] = "#10B981",
            ["Line.TotalCount"] = "#F59E0B"
        };

        foreach (var (tagId, tagName) in analogTags)
        {
            var template = new TrendTemplate
            {
                TemplateId = $"trend-{tagId}",
                Name = tagName,
                Unit = tagUnits.GetValueOrDefault(tagName, ""),
                YMin = 0,
                YMax = tagName.Contains("Level", StringComparison.OrdinalIgnoreCase) ? 1000 :
                       tagName.Contains("Speed", StringComparison.OrdinalIgnoreCase) ? 50 : 100,
                BufferCapacity = 3600,
                WindowSeconds = 300,
                LineColor = tagColors.GetValueOrDefault(tagId, "#3B82F6"),
                ShowAlarmLines = true,
                StrokeThickness = 2,
                ShowGeometry = false
            };
            trendEngine.RegisterTag(tagId, template);
        }

        // 从新链路已加载的报警定义添加报警线
        var rules = definitionService.Current.Definitions;
        trendEngine.AddAlarmLinesFromRules(rules);
        Log.Information("已注册 {Count} 个趋势 Tag，{LineCount} 条报警线", analogTags.Count, trendEngine.AlarmLines.Count);
    }

    /// <summary>
    /// 从配置目录中所有 JSON 文件加载设备并启动采集。
    /// </summary>
    private async Task LoadAndStartDevicesAsync(AcquisitionHost host, HistoryWriter writer, AlarmManager alarmManager, TrendEngine trendEngine)
    {
        try
        {
            string configDir = FindConfigDirectory("config");
            if (!Directory.Exists(configDir))
            {
                Log.Warning("配置目录 {Path} 不存在，使用 Mock 设备", configDir);
                await StartMockDevices(host, writer);
            }
            else
            {
                var jsonFiles = Directory.GetFiles(configDir, "*.json");
                if (jsonFiles.Length == 0)
                {
                    Log.Warning("配置目录 {Path} 中无 JSON 文件，使用 Mock 设备", configDir);
                    await StartMockDevices(host, writer);
                }
                else
                {

                    int startedCount = 0;
                    foreach (var filePath in jsonFiles)
                    {
                        var settings = Container.Resolve<RuntimeSettingsService>();
                        var deviceConfigs = (await DeviceConfigurationLoader.LoadFromFileAsync(filePath)).Select(settings.ApplyTo).ToList();
                        foreach (var config in deviceConfigs)
                        {
                            var readableTags = config.Tags.ToList();
                            writer.RegisterTags(readableTags);

                            try
                            {
                                await host.StartDeviceAsync(config);
                                Log.Information("设备 {Name} [{DriverType}] 采集已启动 (来自 {File})",
                                    config.Name, config.DriverType, Path.GetFileName(filePath));
                                startedCount++;
                            }
                            catch (Exception ex)
                            {
                                Log.Error(ex, "设备 {Name} [{DriverType}] 启动失败: {Message}",
                                    config.Name, config.DriverType, ex.Message);
                            }
                        }
                    }

                    if (startedCount == 0)
                    {
                        Log.Warning("无设备成功启动，使用 Mock 设备");
                        await StartMockDevices(host, writer);
                    }

                    // 启动目录监听（监控所有 JSON 文件变更）
                    StartConfigurationWatcher(configDir, host, writer);
                }

                // 将实时设备树镜像到资源树，并刷新快照（报警页下拉框随即反映真实设备）
                await SyncResourceTreeAsync(host);

                // ── 设备就绪后动态注册报警规则（按实际设备的 TagId 生成）──
                RegisterDynamicAlarmRules(alarmManager, host);

                // ── 注册趋势跟踪 Tag（从实际设备中查找模拟量） ──
                RegisterTrendTags(trendEngine, Container.Resolve<IAlarmDefinitionService>(), host);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载配置文件失败，使用 Mock 设备");
            await StartMockDevices(host, writer);
            await SyncResourceTreeAsync(host);
            // Mock 设备也需注册报警规则和趋势 Tag
            RegisterDynamicAlarmRules(alarmManager, host);
            RegisterTrendTags(trendEngine, Container.Resolve<IAlarmDefinitionService>(), host);
        }
    }

    /// <summary>
    /// 将 AcquisitionHost 中当前运行的所有设备同步到资源树（resource_nodes），
    /// 并原子刷新内存快照，使报警规则页等设备/数据点下拉框与现场保持一致。
    /// 失败仅记录日志，不影响采集主流程。
    /// </summary>
    private async Task SyncResourceTreeAsync(AcquisitionHost host)
    {
        try
        {
            var synchronizer = Container.Resolve<ResourceTreeSynchronizer>();
            await synchronizer.SyncFromDevicesAsync(host.GetDevices());
            Log.Information("资源树已与采集宿主设备同步，共 {Count} 台设备", host.GetDevices().Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "资源树同步失败（不影响采集）");
        }
    }

    /// <summary>
    /// 向上查找配置目录（从 AppContext.BaseDirectory 开始）。
    /// </summary>
    private static string FindConfigDirectory(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir)!;
            if (dir is null) break;
        }
        return relativePath;
    }

    /// <summary>
    /// 回退方案：创建 2 台模拟设备用于 UI 调试。
    /// </summary>
    private async Task StartMockDevices(AcquisitionHost host, HistoryWriter writer)
    {
        var device1 = new DeviceConfig
        {
            Id = "device-001",
            Name = "反应釜 #1",
            DriverType = "Mock",
            IpAddress = "192.168.1.101",
            Port = 502,
            CycleTimeMs = 500,
            TimeoutMs = 3000,
            RetryCount = 3,
            Tags = new List<TagPoint>
            {
                new() { Id = "tag-001", Name = "Temp_Reactor_01",  Address = "40001", DataType = TagDataType.Float32 },
                new() { Id = "tag-002", Name = "Pressure_Reactor_01", Address = "40003", DataType = TagDataType.Float32 },
                new() { Id = "tag-003", Name = "Valve_Status_01",   Address = "00001", DataType = TagDataType.Bool }
            }
        };

        var device2 = new DeviceConfig
        {
            Id = "device-002",
            Name = "锅炉 #3",
            DriverType = "Mock",
            IpAddress = "192.168.1.102",
            Port = 502,
            CycleTimeMs = 1000,
            TimeoutMs = 3000,
            RetryCount = 3,
            Tags = new List<TagPoint>
            {
                new() { Id = "tag-004", Name = "Temp_Boiler_03", Address = "40001", DataType = TagDataType.Float32 },
                new() { Id = "tag-005", Name = "Flow_Boiler_03",  Address = "40003", DataType = TagDataType.Float32 }
            }
        };

        writer.RegisterTags(device1.Tags);
        writer.RegisterTags(device2.Tags);

        await host.StartDeviceAsync(device1);
        await host.StartDeviceAsync(device2);
    }


    private void StartConfigurationWatcher(string configDir, AcquisitionHost host, HistoryWriter writer)
    {
        if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir))
            return;

        // 监听整个目录的 *.json 变更（新增、修改、删除、重命名）
        _configWatcher = new FileSystemWatcher(configDir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };

        FileSystemEventHandler handler = (s, e) => OnConfigurationChanged(configDir, host, writer);
        RenamedEventHandler renamedHandler = (s, e) => OnConfigurationChanged(configDir, host, writer);

        _configWatcher.Changed += handler;
        _configWatcher.Created += handler;
        _configWatcher.Deleted += handler;
        _configWatcher.Renamed += renamedHandler;
    }

    private void OnConfigurationChanged(string configDir, AcquisitionHost host, HistoryWriter writer)
    {
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token); // 防抖 500ms
                if (!token.IsCancellationRequested)
                {
                    Log.Information("检测到配置目录变更，正在重新加载所有设备...");
                    await ReloadConfigurationAsync(configDir, host, writer);
                }
            }
            catch (TaskCanceledException) { /* 预期内取消 */ }
        }, token);
    }

    /// <summary>
    /// 重新加载配置目录中所有 JSON 文件，与当前运行设备做三路对比。
    /// </summary>
    private async Task ReloadConfigurationAsync(string configDir, AcquisitionHost host, HistoryWriter writer)
    {
        try
        {
            if (!Directory.Exists(configDir)) return;

            // 合并所有 JSON 文件中的设备配置
            var allNewConfigs = new List<DeviceConfig>();
            var jsonFiles = Directory.GetFiles(configDir, "*.json");
            foreach (var file in jsonFiles)
            {
                try
                {
                    var settings = Container.Resolve<RuntimeSettingsService>();
                    var configs = (await DeviceConfigurationLoader.LoadFromFileAsync(file)).Select(settings.ApplyTo);
                    allNewConfigs.AddRange(configs);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "跳过无法解析的配置文件: {File}", Path.GetFileName(file));
                }
            }

            var currentDevices = host.GetDevices();
            var newIds = allNewConfigs.Select(c => c.Id).ToHashSet();
            var currentIds = currentDevices.Select(c => c.Id).ToHashSet();

            // 1. 停止被删除的设备
            var toStop = currentIds.Except(newIds);
            foreach (var id in toStop)
            {
                var oldConfig = currentDevices.First(c => c.Id == id);
                writer.UnregisterTags(oldConfig.Tags.Select(t => t.Id));
                await host.StopDeviceAsync(id);
                Log.Information("动态移除并停止设备 {Name}", oldConfig.Name);
            }

            // 2. 启动新设备
            var toStart = newIds.Except(currentIds);
            foreach (var id in toStart)
            {
                var newConfig = allNewConfigs.First(c => c.Id == id);
                writer.RegisterTags(newConfig.Tags);
                try
                {
                    await host.StartDeviceAsync(newConfig);
                    Log.Information("动态添加并启动设备 {Name}", newConfig.Name);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "动态启动设备 {Name} 失败", newConfig.Name);
                }
            }

            // 3. 重载已存在且配置发生变化的设备
            var toCheck = currentIds.Intersect(newIds);
            foreach (var id in toCheck)
            {
                var newConfig = allNewConfigs.First(c => c.Id == id);
                var oldConfig = currentDevices.First(c => c.Id == id);

                var newJson = JsonSerializer.Serialize(newConfig);
                var oldJson = JsonSerializer.Serialize(oldConfig);

                if (newJson != oldJson)
                {
                    writer.UnregisterTags(oldConfig.Tags.Select(t => t.Id));
                    writer.RegisterTags(newConfig.Tags);
                    await host.ReloadDeviceAsync(newConfig);
                    Log.Information("检测到设备 {Name} 配置变更，已重载", newConfig.Name);
                }
            }

            // 通知 UI 刷新设备列表
            var eventAggregator = Container.Resolve<IEventAggregator>();
            eventAggregator.GetEvent<IndustrialDAQ.UI.Events.ConfigurationReloadedEvent>().Publish();

            // 设备已变更，重新将实时设备树镜像到资源树
            await SyncResourceTreeAsync(host);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "动态重载配置失败");
        }
    }
}
