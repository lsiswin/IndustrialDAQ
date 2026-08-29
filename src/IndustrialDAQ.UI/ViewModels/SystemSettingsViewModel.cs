// File: SystemSettingsViewModel.cs  Module: UI (ViewModels)  Author: IndustrialDAQ Team
using System.Collections.ObjectModel;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Infrastructure;
using Prism.Commands;
using Prism.Mvvm;
using System.Windows;
using System.Linq;
using System.IO;
using IndustrialDAQ.UI.Services;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>
/// 系统设置 ViewModel — 管理设备配置、通信参数、日志设置。
/// </summary>
public class SystemSettingsViewModel : BindableBase
{
    private readonly IAuthManager _authManager;
    private readonly RuntimeSettingsService _settingsService;
    private readonly SecurityAuditService _auditService;
    public bool CanModify => _authManager.CanModify;
    /// <summary>报警规则子页面 ViewModel，显式绑定以避免继承系统设置的 DataContext。</summary>
    public AlarmRuleConfigViewModel AlarmRuleConfig { get; }
    public UserAccessManagementViewModel UserAccessManagement { get; }
    public CalculationRuleManagementViewModel CalculationRuleManagement { get; }

    /// <summary>设置分类集合。</summary>
    public ObservableCollection<SettingsCategory> Categories { get; } = new();

    private SettingsCategory? _selectedCategory;
    /// <summary>当前选中的设置分类。</summary>
    public SettingsCategory? SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }

    /// <summary>保存设置命令。</summary>
    public DelegateCommand SaveCommand { get; }

    /// <summary>重置设置命令。</summary>
    public DelegateCommand ResetCommand { get; }

    public ObservableCollection<DatabaseProfileEditor> DatabaseProfiles { get; } = new();
    public ObservableCollection<string> AvailableDatabaseProviders { get; } = new() { "SQLite", "PostgreSQL" };
    public DelegateCommand AddDatabaseCommand { get; }
    public DelegateCommand RemoveDatabaseCommand { get; }
    public DelegateCommand TestDatabaseCommand { get; }

    private DatabaseProfileEditor? _selectedDatabaseProfile;
    public DatabaseProfileEditor? SelectedDatabaseProfile
    {
        get => _selectedDatabaseProfile;
        set
        {
            if (SetProperty(ref _selectedDatabaseProfile, value))
            {
                RaisePropertyChanged(nameof(DatabasePath));
                RemoveDatabaseCommand.RaiseCanExecuteChanged();
                TestDatabaseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private DatabaseProfileEditor? _startupDatabaseProfile;
    public DatabaseProfileEditor? StartupDatabaseProfile
    {
        get => _startupDatabaseProfile;
        set => SetProperty(ref _startupDatabaseProfile, value);
    }

    /// <summary>当前选中数据库的安全连接摘要，不显示密码。</summary>
    public string DatabasePath => SelectedDatabaseProfile is null ? "未选择" : DatabaseProfileConnection.Describe(SelectedDatabaseProfile.ToProfile());

    private string _databaseStatus = "数据库切换将在下次启动时生效";
    public string DatabaseStatus { get => _databaseStatus; set => SetProperty(ref _databaseStatus, value); }

    /// <summary>历史数据保留天数。</summary>
    private int _historyRetentionDays = 90;
    public int HistoryRetentionDays { get => _historyRetentionDays; set => SetProperty(ref _historyRetentionDays, value); }

    /// <summary>采集超时时间 (ms)。</summary>
    private int _acquisitionTimeoutMs = 3000;
    public int AcquisitionTimeoutMs { get => _acquisitionTimeoutMs; set => SetProperty(ref _acquisitionTimeoutMs, value); }

    /// <summary>重试次数。</summary>
    private int _retryCount = 3;
    public int RetryCount { get => _retryCount; set => SetProperty(ref _retryCount, value); }

    /// <summary>是否启用死区压缩。</summary>
    private bool _enableDeadband = true;
    public bool EnableDeadband { get => _enableDeadband; set => SetProperty(ref _enableDeadband, value); }

    private string _statusMessage = "就绪";
    /// <summary>操作状态消息。</summary>
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public ObservableCollection<string> AvailableThemes { get; } = new() { "DarkTheme", "LightTheme" };
    public ObservableCollection<string> AvailableLogLevels { get; } = new() { "Debug", "Information", "Warning", "Error" };
    private string _selectedLogLevel = "Information";
    public string SelectedLogLevel { get => _selectedLogLevel; set => SetProperty(ref _selectedLogLevel, value); }
    public string StorageProvider => Environment.GetEnvironmentVariable("INDUSTRIALDAQ_STORAGE_PROVIDER") ?? "SQLite";
    public string NotificationConfiguration => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INDUSTRIALDAQ_ALARM_WEBHOOKS")) ? "未配置 INDUSTRIALDAQ_ALARM_WEBHOOKS" : "Webhook 外部通知已启用";

    private string _selectedTheme = "DarkTheme";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                ApplyTheme(value);
            }
        }
    }

    public SystemSettingsViewModel(AlarmRuleConfigViewModel alarmRuleConfig, UserAccessManagementViewModel userAccessManagement, CalculationRuleManagementViewModel calculationRuleManagement, IAuthManager authManager, RuntimeSettingsService settingsService, SecurityAuditService auditService)
    {
        AlarmRuleConfig = alarmRuleConfig;
        UserAccessManagement = userAccessManagement;
        CalculationRuleManagement = calculationRuleManagement;
        _authManager = authManager;
        _settingsService = settingsService;
        _auditService = auditService;
        SaveCommand = new DelegateCommand(OnSave, () => CanModify);
        ResetCommand = new DelegateCommand(OnReset, () => CanModify);
        AddDatabaseCommand = new DelegateCommand(AddDatabase, () => CanModify);
        RemoveDatabaseCommand = new DelegateCommand(RemoveDatabase, () => CanModify && SelectedDatabaseProfile is not null && DatabaseProfiles.Count > 1);
        TestDatabaseCommand = new DelegateCommand(TestDatabase, () => CanModify && SelectedDatabaseProfile is not null);
        _authManager.CurrentUserChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(CanModify));
            SaveCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
            AddDatabaseCommand.RaiseCanExecuteChanged();
            RemoveDatabaseCommand.RaiseCanExecuteChanged();
            TestDatabaseCommand.RaiseCanExecuteChanged();
        };

        // 初始化设置分类
        Categories.Add(new SettingsCategory("设备管理", "管理采集设备和驱动配置", "⚙"));
        Categories.Add(new SettingsCategory("通信设置", "Modbus / OPC UA 通信参数", "📡"));
        Categories.Add(new SettingsCategory("存储管理", "历史数据存储和保留策略", "💾"));
        Categories.Add(new SettingsCategory("报警规则", "报警阈值和通知配置", "🔔"));
        Categories.Add(new SettingsCategory("计算规则", "虚拟量和数据加工表达式", "∑"));
        Categories.Add(new SettingsCategory("日志设置", "Serilog 日志级别和输出目标", "📋"));
        Categories.Add(new SettingsCategory("系统参数", "采集周期、重试、死区等全局参数", "🖥"));
        Categories.Add(new SettingsCategory("用户与权限", "账号、角色和访问状态", "🔐"));

        SelectedCategory = Categories[0];
        LoadSettings(_settingsService.Current);
    }

    private async void OnSave()
    {
        try
        {
            var settings = BuildSettings();
            await _settingsService.SaveAsync(settings);
            await _auditService.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "RuntimeSettingsChanged", "System/Settings", $"Timeout={settings.AcquisitionTimeoutMs};Retry={settings.RetryCount};Deadband={settings.EnableDeadband};Retention={settings.HistoryRetentionDays};Theme={settings.Theme}", true);
            StatusMessage = $"✓ 设置已持久化；采集参数将在设备热重载后生效 — {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { StatusMessage = "✗ 保存失败：" + ex.Message; }
    }

    private async void OnReset()
    {
        await _settingsService.ResetAsync();
        LoadSettings(_settingsService.Current);
        StatusMessage = $"↺ 设置已持久化重置为默认值 — {DateTime.Now:HH:mm:ss}";
    }

    private RuntimeSettings BuildSettings() => new()
    {
        AcquisitionTimeoutMs = AcquisitionTimeoutMs, RetryCount = RetryCount, EnableDeadband = EnableDeadband,
        HistoryRetentionDays = HistoryRetentionDays, Theme = SelectedTheme, LogLevel = SelectedLogLevel,
        ActiveDatabaseProfileId = StartupDatabaseProfile?.Id ?? DatabaseProfiles.First().Id,
        DatabaseProfiles = DatabaseProfiles.Select(editor => editor.ToProfile()).ToArray()
    };

    private void LoadSettings(RuntimeSettings settings)
    {
        AcquisitionTimeoutMs = settings.AcquisitionTimeoutMs; RetryCount = settings.RetryCount;
        EnableDeadband = settings.EnableDeadband; HistoryRetentionDays = settings.HistoryRetentionDays; SelectedTheme = settings.Theme; SelectedLogLevel = settings.LogLevel;
        DatabaseProfiles.Clear();
        foreach (var profile in settings.DatabaseProfiles) DatabaseProfiles.Add(new DatabaseProfileEditor(profile));
        StartupDatabaseProfile = DatabaseProfiles.FirstOrDefault(profile => profile.Id == settings.ActiveDatabaseProfileId) ?? DatabaseProfiles.First();
        SelectedDatabaseProfile = StartupDatabaseProfile;
        RemoveDatabaseCommand.RaiseCanExecuteChanged();
    }

    private void AddDatabase()
    {
        var editor = new DatabaseProfileEditor(new DatabaseProfile
        {
            Name = $"PostgreSQL {DatabaseProfiles.Count}",
            Provider = "PostgreSQL",
            PasswordEnvironmentVariable = "INDUSTRIALDAQ_POSTGRES_PASSWORD"
        });
        DatabaseProfiles.Add(editor);
        SelectedDatabaseProfile = editor;
        DatabaseStatus = "已添加数据库配置，请填写参数并测试连接";
    }

    private void RemoveDatabase()
    {
        if (SelectedDatabaseProfile is null || DatabaseProfiles.Count <= 1) return;
        var removed = SelectedDatabaseProfile;
        DatabaseProfiles.Remove(removed);
        if (ReferenceEquals(StartupDatabaseProfile, removed)) StartupDatabaseProfile = DatabaseProfiles.First();
        SelectedDatabaseProfile = DatabaseProfiles.FirstOrDefault();
        DatabaseStatus = "数据库配置已从编辑列表移除，保存后生效";
    }

    private async void TestDatabase()
    {
        if (SelectedDatabaseProfile is null) return;
        var profile = SelectedDatabaseProfile.ToProfile();
        try
        {
            DatabaseStatus = $"正在测试 {profile.Name}...";
            await DatabaseProfileConnection.TestAsync(profile);
            DatabaseStatus = $"✓ {profile.Name} 连接成功";
            await _auditService.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "DatabaseConnectionTest", "System/Settings/Storage", DatabaseProfileConnection.Describe(profile), true);
        }
        catch (Exception ex)
        {
            DatabaseStatus = $"✗ {profile.Name} 连接失败：{ex.Message}";
            await _auditService.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username, "DatabaseConnectionTest", "System/Settings/Storage", DatabaseProfileConnection.Describe(profile), false);
        }
    }

    private void ApplyTheme(string themeName)
    {
        var app = Application.Current;
        if (app == null) return;

        try
        {
            var resources = app.Resources.MergedDictionaries;
            
            // 找到并移除现有的主题字典
            var existingTheme = resources.FirstOrDefault(d => d.Source != null && d.Source.ToString().Contains("Theme.xaml"));
            if (existingTheme != null)
            {
                resources.Remove(existingTheme);
            }

            // 使用与 App.xaml 一致的相对路径添加新主题
            resources.Add(new ResourceDictionary
            {
                Source = new Uri($"Themes/{themeName}.xaml", UriKind.Relative)
            });
            
            StatusMessage = $"✓ 主题已切换至 {themeName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"✗ 主题切换失败: {ex.Message}";
        }
    }
}

/// <summary>数据库档案的可编辑 UI 模型，转换后再进入持久化设置。</summary>
public sealed class DatabaseProfileEditor : BindableBase
{
    public string Id { get; }
    private string _name;
    private string _provider;
    private string _sqlitePath;
    private string _host;
    private int _port;
    private string _database;
    private string _username;
    private string _passwordEnvironmentVariable;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Provider { get => _provider; set => SetProperty(ref _provider, value); }
    public string SqlitePath { get => _sqlitePath; set => SetProperty(ref _sqlitePath, value); }
    public string Host { get => _host; set => SetProperty(ref _host, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public string Database { get => _database; set => SetProperty(ref _database, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string PasswordEnvironmentVariable { get => _passwordEnvironmentVariable; set => SetProperty(ref _passwordEnvironmentVariable, value); }

    public DatabaseProfileEditor(DatabaseProfile profile)
    {
        Id = profile.Id; _name = profile.Name; _provider = profile.Provider; _sqlitePath = profile.SqlitePath;
        _host = profile.Host; _port = profile.Port; _database = profile.Database; _username = profile.Username;
        _passwordEnvironmentVariable = profile.PasswordEnvironmentVariable;
    }

    public DatabaseProfile ToProfile() => new()
    {
        Id = Id, Name = Name.Trim(), Provider = Provider, SqlitePath = SqlitePath.Trim(), Host = Host.Trim(), Port = Port,
        Database = Database.Trim(), Username = Username.Trim(), PasswordEnvironmentVariable = PasswordEnvironmentVariable.Trim()
    };
}

/// <summary>
/// 设置分类显示模型。
/// </summary>
public class SettingsCategory : BindableBase
{
    /// <summary>分类名称。</summary>
    public string Name { get; }

    /// <summary>分类描述。</summary>
    public string Description { get; }

    /// <summary>分类图标。</summary>
    public string Icon { get; }

    private bool _isSelected;
    /// <summary>是否被选中。</summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public SettingsCategory(string name, string description, string icon)
    {
        Name = name;
        Description = description;
        Icon = icon;
    }
}
