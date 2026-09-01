using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using IndustrialDAQ.UI.Services;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Models;
using IndustrialDAQ.Vision.Runtime;
using Prism.Commands;
using Prism.Mvvm;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>通用视觉检测工作台，展示实时图像、任务配方、指标和检测历史。</summary>
public sealed class VisionInspectionViewModel : BindableBase, IDestructible
{
    private readonly IVisionConfigurationRepository _repository;
    private readonly VisionInspectionEngine _engine;
    private readonly VisionResultPublisher _publisher;
    private readonly IDialogService _dialogService;
    private readonly IAuthManager _authManager;
    private VisionTaskListItem? _selectedTask;
    private BitmapImage? _previewImage;
    private string _resultText = "等待检测";
    private string _resultColor = "#94A3B8";
    private string _detailText = "请选择或新增视觉任务";
    private string _frameInfo = "尚未接收图像";
    private long _totalCount;
    private long _ngCount;
    private bool _isPaused;

    public ObservableCollection<VisionTaskListItem> Tasks { get; } = [];
    public ObservableCollection<VisionHistoryItem> History { get; } = [];
    public ObservableCollection<VisionRecipeDisplayItem> CurrentRecipe { get; } = [];
    public bool CanModify => _authManager.CanModify;
    public bool CannotModify => !CanModify;
    public BitmapImage? PreviewImage { get => _previewImage; private set => SetProperty(ref _previewImage, value); }
    public string ResultText { get => _resultText; private set => SetProperty(ref _resultText, value); }
    public string ResultColor { get => _resultColor; private set => SetProperty(ref _resultColor, value); }
    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }
    public string FrameInfo { get => _frameInfo; private set => SetProperty(ref _frameInfo, value); }
    public long TotalCount { get => _totalCount; private set { SetProperty(ref _totalCount, value); RaiseMetrics(); } }
    public long NgCount { get => _ngCount; private set { SetProperty(ref _ngCount, value); RaiseMetrics(); } }
    public long OkCount => Math.Max(0, TotalCount - NgCount);
    public double PassRate => TotalCount == 0 ? 0 : OkCount * 100d / TotalCount;
    public string PassRateText => TotalCount == 0 ? "--" : $"{PassRate:F2}%";
    public bool IsPaused { get => _isPaused; private set { SetProperty(ref _isPaused, value); RaisePropertyChanged(nameof(PauseButtonText)); } }
    public string PauseButtonText => IsPaused ? "▶ 继续" : "Ⅱ 暂停";

    public VisionTaskListItem? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetProperty(ref _selectedTask, value)) return;
            RaiseTaskDetails();
            TriggerCommand.RaiseCanExecuteChanged();
            ConfigureCommand.RaiseCanExecuteChanged();
        }
    }

    public string CameraName => SelectedTask?.CameraName ?? "未配置相机";
    public string ImageDirectory => SelectedTask?.Camera.ImageDirectory ?? "—";
    public int FrameInterval => SelectedTask?.Camera.IntervalMilliseconds ?? 0;
    public bool LoopPlayback => SelectedTask?.Camera.Loop == true;
    public string RoiText => SelectedTask is null ? "—" : $"X {SelectedTask.Task.Roi.X:P0}  Y {SelectedTask.Task.Roi.Y:P0}  W {SelectedTask.Task.Roi.Width:P0}  H {SelectedTask.Task.Roi.Height:P0}";
    public double MatchThreshold => SelectedTask?.Task.MatchThreshold ?? 0;
    public string TemplatePath => SelectedTask?.Task.TemplateImagePath ?? "—";
    public string ResultResourcePath => SelectedTask is null ? "—" : $"Vision/{Segment(SelectedTask.CameraName)}/{Segment(SelectedTask.Task.Name)}/Result/Pass";
    public string AlarmRuleName => SelectedTask is null ? "—" : $"VISION_TASK_NG_{SelectedTask.Task.TaskId.ToUpperInvariant()}";

    public DelegateCommand AddTaskCommand { get; }
    public DelegateCommand ConfigureCommand { get; }
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand TriggerCommand { get; }
    public DelegateCommand StartCommand { get; }
    public DelegateCommand PauseCommand { get; }
    public DelegateCommand ResetCommand { get; }
    public DelegateCommand LoginToConfigureCommand { get; }

    public VisionInspectionViewModel(IVisionConfigurationRepository repository, VisionInspectionEngine engine,
        VisionResultPublisher publisher, IDialogService dialogService, IAuthManager authManager)
    {
        _repository = repository; _engine = engine; _publisher = publisher;
        _dialogService = dialogService; _authManager = authManager;
        AddTaskCommand = new DelegateCommand(() => OpenTaskDialog(null), () => CanModify);
        ConfigureCommand = new DelegateCommand(() => OpenTaskDialog(SelectedTask), () => CanModify && SelectedTask is not null);
        RefreshCommand = new DelegateCommand(async () => await LoadAsync());
        TriggerCommand = new DelegateCommand(async () => await TriggerAsync(), () => SelectedTask is not null);
        StartCommand = new DelegateCommand(async () => await StartAsync());
        PauseCommand = new DelegateCommand(TogglePause);
        ResetCommand = new DelegateCommand(ResetStatistics, () => CanModify);
        LoginToConfigureCommand = new DelegateCommand(OpenLogin, () => CannotModify);
        _engine.InspectionCompleted += OnInspectionCompleted;
        _authManager.CurrentUserChanged += OnCurrentUserChanged;
        _ = LoadAsync();
    }

    public void Destroy()
    {
        _engine.InspectionCompleted -= OnInspectionCompleted;
        _authManager.CurrentUserChanged -= OnCurrentUserChanged;
    }

    private async Task LoadAsync()
    {
        var selectedId = SelectedTask?.Task.TaskId;
        var cameras = await _repository.LoadCamerasAsync();
        var tasks = await _repository.LoadTasksAsync();
        Tasks.Clear();
        foreach (var task in tasks)
        {
            var camera = cameras.FirstOrDefault(item => item.CameraId == task.CameraId);
            Tasks.Add(new VisionTaskListItem(task, camera ?? new VisionCameraConfig { CameraId = task.CameraId, Name = task.CameraId }));
        }
        SelectedTask = Tasks.FirstOrDefault(item => item.Task.TaskId == selectedId) ?? Tasks.FirstOrDefault();
        DetailText = Tasks.Count == 0 ? "暂无任务，工程师可点击“新增视觉任务”创建检测配方" : $"已加载 {Tasks.Count} 个视觉任务";
    }

    private void OpenTaskDialog(VisionTaskListItem? item)
    {
        var parameters = new DialogParameters();
        if (item is not null) parameters.Add("TaskId", item.Task.TaskId);
        _dialogService.ShowDialog("VisionTaskDialog", parameters, result => { if (result.Result == ButtonResult.OK) _ = LoadAsync(); });
    }

    private void OpenLogin() => _dialogService.ShowDialog("LoginDialog", result =>
    {
        if (result.Result == ButtonResult.OK) DetailText = "登录成功，现在可以新增或配置视觉任务";
    });

    private async Task StartAsync()
    {
        _engine.Resume(); IsPaused = false; DetailText = "正在重新加载视觉任务...";
        await _engine.ReloadAsync(); DetailText = "视觉检测已启动";
    }

    private void TogglePause()
    {
        if (IsPaused) _engine.Resume(); else _engine.Pause();
        IsPaused = !IsPaused; DetailText = IsPaused ? "视觉检测已暂停" : "视觉检测已继续";
    }

    private void ResetStatistics()
    {
        if (SelectedTask is not null) _publisher.ResetStatistics(SelectedTask.Task.TaskId);
        TotalCount = 0; NgCount = 0; History.Clear();
        DetailText = "本次运行统计已清零，数据库历史记录未删除";
    }

    private async Task TriggerAsync()
    {
        if (SelectedTask is null) return;
        DetailText = "正在执行单帧检测...";
        if (await _engine.TriggerAsync(SelectedTask.Task.CameraId) is null)
            DetailText = "相机没有可用图片，请检查模拟图片目录";
    }

    private void OnInspectionCompleted(object? sender, VisionInspectionCompletedEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            PreviewImage = Decode(e.Frame.EncodedImage);
            ResultText = e.Result.IsPass ? "✓ OK · 检测合格" : "✕ NG · 检测不合格";
            ResultColor = e.Result.IsPass ? "#10B981" : "#EF4444";
            DetailText = $"匹配分数 {e.Result.MatchScore:F3} · 耗时 {e.Result.ProcessingTimeMilliseconds:F1} ms" +
                         (string.IsNullOrWhiteSpace(e.Result.FailureReason) ? string.Empty : $" · {e.Result.FailureReason}");
            FrameInfo = $"图像：{Path.GetFileName(e.Frame.SourcePath) ?? e.Frame.FrameId}   时间：{e.Frame.Timestamp.ToLocalTime():HH:mm:ss.fff}";
            TotalCount++; if (!e.Result.IsPass) NgCount++;
            History.Insert(0, new VisionHistoryItem(e.Result, Path.GetFileName(e.Frame.SourcePath) ?? e.Frame.FrameId));
            while (History.Count > 200) History.RemoveAt(History.Count - 1);
        });
    }

    private void OnCurrentUserChanged(object? sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(CanModify));
        RaisePropertyChanged(nameof(CannotModify));
        AddTaskCommand.RaiseCanExecuteChanged(); ConfigureCommand.RaiseCanExecuteChanged(); ResetCommand.RaiseCanExecuteChanged();
        LoginToConfigureCommand.RaiseCanExecuteChanged();
    }

    private void RaiseMetrics()
    {
        RaisePropertyChanged(nameof(OkCount)); RaisePropertyChanged(nameof(PassRate)); RaisePropertyChanged(nameof(PassRateText));
    }

    private void RaiseTaskDetails()
    {
        CurrentRecipe.Clear();
        if (SelectedTask is not null)
        {
            foreach (var item in SelectedTask.Task.Operators.OrderBy(item => item.Order))
                CurrentRecipe.Add(new VisionRecipeDisplayItem(item.Order + 1, item.DisplayName, ParameterSummary(item)));
        }
        RaisePropertyChanged(nameof(CameraName)); RaisePropertyChanged(nameof(ImageDirectory)); RaisePropertyChanged(nameof(FrameInterval));
        RaisePropertyChanged(nameof(LoopPlayback)); RaisePropertyChanged(nameof(RoiText)); RaisePropertyChanged(nameof(MatchThreshold));
        RaisePropertyChanged(nameof(TemplatePath)); RaisePropertyChanged(nameof(ResultResourcePath)); RaisePropertyChanged(nameof(AlarmRuleName));
    }

    private static string Segment(string value) => value.Trim().Replace('/', '-').Replace('\\', '-');
    private static string ParameterSummary(VisionOperatorDefinition item) => item.Parameters.Count == 0
        ? "使用默认配置"
        : string.Join("  ·  ", item.Parameters.Where(pair => !pair.Key.Contains("Path", StringComparison.OrdinalIgnoreCase)).Take(3).Select(pair => $"{pair.Key}={pair.Value}"));
    private static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
}

public sealed record VisionRecipeDisplayItem(int Order, string Name, string Parameters);

public sealed class VisionTaskListItem(VisionInspectionTask task, VisionCameraConfig camera)
{
    public VisionInspectionTask Task { get; } = task;
    public VisionCameraConfig Camera { get; } = camera;
    public string Name => Task.Name;
    public string CameraName => Camera.Name;
    public string DisplayText => $"{Task.Name} · {Camera.Name}";
}

public sealed class VisionHistoryItem
{
    public DateTimeOffset Timestamp { get; }
    public string ImageName { get; }
    public string ResultText { get; }
    public string ResultColor { get; }
    public double Score { get; }
    public double Elapsed { get; }
    public string AlarmText { get; }
    public string? ImagePath { get; }
    public VisionHistoryItem(VisionInspectionResult result, string imageName)
    {
        Timestamp = result.Timestamp.ToLocalTime(); ImageName = imageName;
        ResultText = result.IsPass ? "OK · 合格" : "NG · 不合格";
        ResultColor = result.IsPass ? "#10B981" : "#EF4444";
        Score = result.MatchScore; Elapsed = result.ProcessingTimeMilliseconds;
        AlarmText = result.IsPass ? "—" : "已触发"; ImagePath = result.ImagePath;
    }
}
