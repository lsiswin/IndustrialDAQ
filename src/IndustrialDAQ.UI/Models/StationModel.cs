// File: StationModel.cs  Module: UI (Models)  Author: IndustrialDAQ Team
using Prism.Mvvm;
using System.Collections.ObjectModel;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.Kernel.Sketches;
using SkiaSharp;

namespace IndustrialDAQ.UI.Models;

/// <summary>
/// 工位状态枚举
/// </summary>
public enum StationStatus
{
    Running,
    Standby,
    Fault,
    NotStarted
}

/// <summary>
/// 生产线工位模型
/// </summary>
public class StationModel : BindableBase
{
    /// <summary>工位语义图标，不再由界面统一显示齿轮占位。</summary>
    public string Icon { get; init; } = "⚙";
    private string _stationId = string.Empty;
    public string StationId
    {
        get => _stationId;
        set => SetProperty(ref _stationId, value);
    }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private StationStatus _status;
    public StationStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                RaisePropertyChanged(nameof(StatusColor));
                RaisePropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => Status switch
    {
        StationStatus.Running => "运行",
        StationStatus.Standby => "待机",
        StationStatus.Fault => "故障",
        StationStatus.NotStarted => "未开始",
        _ => "未知"
    };

    public string StatusColor => Status switch
    {
        StationStatus.Running => "#10B981", // Success Green
        StationStatus.Standby => "#F59E0B", // Warning Yellow
        StationStatus.Fault => "#EF4444",   // Danger Red
        StationStatus.NotStarted => "#6B7280", // Gray
        _ => "#6B7280"
    };

    private string _primaryStatName = string.Empty;
    public string PrimaryStatName
    {
        get => _primaryStatName;
        set => SetProperty(ref _primaryStatName, value);
    }

    private string _primaryStatValue = string.Empty;
    public string PrimaryStatValue
    {
        get => _primaryStatValue;
        set => SetProperty(ref _primaryStatValue, value);
    }

    private int _plcCount;
    public int PlcCount
    {
        get => _plcCount;
        set => SetProperty(ref _plcCount, value);
    }

    private bool _isLast;
    public bool IsLast
    {
        get => _isLast;
        set => SetProperty(ref _isLast, value);
    }

    // LiveCharts2 的微型图表数据
    public ObservableCollection<ISeries> SparklineSeries { get; set; } = new();

    public Axis[] SparklineXAxes { get; set; } = new Axis[] { new Axis { IsVisible = false } };
    public Axis[] SparklineYAxes { get; set; } = new Axis[] { new Axis { IsVisible = false } };


    public StationModel()
    {
    }
}
