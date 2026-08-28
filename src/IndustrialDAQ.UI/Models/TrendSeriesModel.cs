// File: TrendSeriesModel.cs  Module: UI (Models)  Author: IndustrialDAQ Team
using System.Collections.ObjectModel;
using Prism.Mvvm;

namespace IndustrialDAQ.UI.Models;

/// <summary>
/// 趋势 Tag 选择项 — 用于 UI 绑定 Tag 的勾选状态。
/// </summary>
public class TrendTagItem : BindableBase
{
    /// <summary>所属设备 ID。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>所属设备名称。</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>数据点在资源树中的完整路径。</summary>
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>Tag ID。</summary>
    public string TagId { get; init; } = string.Empty;

    /// <summary>Tag 名称。</summary>
    public string TagName { get; init; } = string.Empty;

    /// <summary>工程单位。</summary>
    public string Unit { get; init; } = string.Empty;

    private bool _isSelected;
    /// <summary>是否选中显示趋势。</summary>
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    /// <summary>曲线颜色（十六进制）。</summary>
    public string Color { get; init; } = "#3B82F6";

    private bool _isAlarmActive;
    /// <summary>该数据点当前是否存在活跃报警。</summary>
    public bool IsAlarmActive
    {
        get => _isAlarmActive;
        set
        {
            if (SetProperty(ref _isAlarmActive, value))
                RaisePropertyChanged(nameof(DisplayColor));
        }
    }

    private string _alarmDescription = string.Empty;
    /// <summary>当前活跃报警描述，供图例和悬浮提示显示。</summary>
    public string AlarmDescription
    {
        get => _alarmDescription;
        set => SetProperty(ref _alarmDescription, value);
    }

    /// <summary>报警时使用红色特殊标注，否则使用趋势配置颜色。</summary>
    public string DisplayColor => IsAlarmActive ? "#EF4444" : Color;
}

/// <summary>
/// 趋势设备选择项 — 对应资源树中的 Device 节点，并持有该设备下可绘制的数值数据点。
/// </summary>
public class TrendDeviceItem : BindableBase
{
    /// <summary>设备 ID。</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>设备显示名称。</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>设备在资源树中的完整路径。</summary>
    public string ResourcePath { get; init; } = string.Empty;

    /// <summary>该设备下允许加入趋势图的数值数据点。</summary>
    public ObservableCollection<TrendTagItem> Tags { get; } = [];

    /// <summary>下拉列表显示文本。</summary>
    public string DisplayText => $"{DeviceName} ({Tags.Count})";
}
