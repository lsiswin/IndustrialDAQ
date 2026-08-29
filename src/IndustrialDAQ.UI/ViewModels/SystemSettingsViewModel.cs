// File: SystemSettingsViewModel.cs  Module: UI (ViewModels)  Author: IndustrialDAQ Team
using System.Collections.ObjectModel;
using IndustrialDAQ.Core.Models;
using Prism.Commands;
using Prism.Mvvm;
using System.Windows;
using System.Linq;
using IndustrialDAQ.UI.Services;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>
/// 系统设置 ViewModel — 管理设备配置、通信参数、日志设置。
/// </summary>
public class SystemSettingsViewModel : BindableBase
{
    private readonly IAuthManager _authManager;
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

    /// <summary>数据库路径。</summary>
    public string DatabasePath { get; set; } = "industrialdaq.db";

    /// <summary>历史数据保留天数。</summary>
    public int HistoryRetentionDays { get; set; } = 90;

    /// <summary>采集超时时间 (ms)。</summary>
    public int AcquisitionTimeoutMs { get; set; } = 3000;

    /// <summary>重试次数。</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>是否启用死区压缩。</summary>
    public bool EnableDeadband { get; set; } = true;

    private string _statusMessage = "就绪";
    /// <summary>操作状态消息。</summary>
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public ObservableCollection<string> AvailableThemes { get; } = new() { "DarkTheme", "LightTheme" };

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

    public SystemSettingsViewModel(AlarmRuleConfigViewModel alarmRuleConfig, UserAccessManagementViewModel userAccessManagement, CalculationRuleManagementViewModel calculationRuleManagement, IAuthManager authManager)
    {
        AlarmRuleConfig = alarmRuleConfig;
        UserAccessManagement = userAccessManagement;
        CalculationRuleManagement = calculationRuleManagement;
        _authManager = authManager;
        SaveCommand = new DelegateCommand(OnSave, () => CanModify);
        ResetCommand = new DelegateCommand(OnReset, () => CanModify);
        _authManager.CurrentUserChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(CanModify));
            SaveCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
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
    }

    private void OnSave()
    {
        StatusMessage = $"✓ 设置已保存 — {DateTime.Now:HH:mm:ss}";
    }

    private void OnReset()
    {
        StatusMessage = $"↺ 设置已重置为默认值 — {DateTime.Now:HH:mm:ss}";
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
