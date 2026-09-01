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

/// <summary>通用视觉工作台，相机实时预览与检测配方相互独立。</summary>
public sealed class VisionInspectionViewModel : BindableBase, IDestructible
{
    private readonly IVisionConfigurationRepository _repository;
    private readonly VisionInspectionEngine _engine;
    private readonly VisionResultPublisher _publisher;
    private readonly IDialogService _dialogService;
    private readonly IAuthManager _authManager;
    private bool _isTaskEditorOpen;
    private readonly List<VisionTaskListItem> _allTasks = [];
    private VisionCameraListItem? _selectedCamera;
    private VisionTaskListItem? _selectedTask;
    private BitmapImage? _previewImage;
    private string _resultText = "等待相机画面";
    private string _resultColor = "#94A3B8";
    private string _detailText = "请先选择相机";
    private string _frameInfo = "尚未接收图像";
    private long _totalCount;
    private long _ngCount;
    private bool _isPaused;
    private bool _isPreviewPaused;
    private bool _isCameraConnected;

    public ObservableCollection<VisionCameraListItem> Cameras { get; } = [];
    public ObservableCollection<VisionTaskListItem> Tasks { get; } = [];
    public ObservableCollection<VisionHistoryItem> History { get; } = [];
    public ObservableCollection<VisionRecipeDisplayItem> CurrentRecipe { get; } = [];
    public bool CanModify => _authManager.CanModify;
    public bool CannotModify => !CanModify;
    public VisionTaskDialogViewModel TaskEditor { get; }
    public bool IsTaskEditorOpen
    {
        get => _isTaskEditorOpen;
        private set
        {
            if (!SetProperty(ref _isTaskEditorOpen, value)) return;
            RaisePropertyChanged(nameof(IsWorkbenchVisible));
        }
    }
    public bool IsWorkbenchVisible => !IsTaskEditorOpen;
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
    public bool IsPreviewPaused
    {
        get => _isPreviewPaused;
        private set
        {
            if (!SetProperty(ref _isPreviewPaused, value)) return;
            RaisePropertyChanged(nameof(PreviewPauseButtonText));
        }
    }
    public string PreviewPauseButtonText => IsPreviewPaused ? "▶ 恢复画面" : "Ⅱ 暂停画面";

    public VisionCameraListItem? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (!SetProperty(ref _selectedCamera, value)) return;
            IsPreviewPaused = false;
            FilterTasksForCamera();
            IsCameraConnected = value is not null && _engine.IsCameraConnected(value.Camera.CameraId);
            RaiseCameraDetails();
            RefreshCommands();
            DetailText = value is null ? "请先选择相机" : IsCameraConnected
                ? (SelectedTask is null ? "相机已连接，请创建或选择检测配方" : "相机与检测配方已就绪")
                : "相机未连接，请检查相机配置或图片目录";
        }
    }

    public VisionTaskListItem? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetProperty(ref _selectedTask, value)) return;
            RaiseTaskDetails();
            RefreshCommands();
            if (value is null && SelectedCamera is not null)
                DetailText = "当前相机没有检测配方，请点击“新增视觉任务”创建";
        }
    }

    public bool IsCameraConnected
    {
        get => _isCameraConnected;
        private set
        {
            if (!SetProperty(ref _isCameraConnected, value)) return;
            RaisePropertyChanged(nameof(CameraStatusText));
            RaisePropertyChanged(nameof(CameraStatusColor));
            RaisePropertyChanged(nameof(CanUseCamera));
            RaisePropertyChanged(nameof(CanRunDetection));
            RefreshCommands();
        }
    }

    public bool CanUseCamera => SelectedCamera is not null && IsCameraConnected;
    // 相机连接后即可控制取流；没有配方时只做实时预览，不执行检测算法。
    public bool CanRunDetection => CanUseCamera;
    public bool HasNoRecipe => SelectedTask is null;
    public string CameraStatusText => SelectedCamera is null ? "未选择相机" : IsCameraConnected ? $"{SelectedCamera.Name} · 已连接" : $"{SelectedCamera.Name} · 未连接";
    public string CameraStatusColor => IsCameraConnected ? "#10B981" : "#EF4444";
    public string CameraName => SelectedCamera?.Name ?? "未配置相机";
    public string CameraDriver => SelectedCamera?.Camera.DriverType ?? "—";
    public string ImageDirectory => SelectedCamera?.Camera.ImageDirectory ?? "—";
    public int FrameInterval => SelectedCamera?.Camera.IntervalMilliseconds ?? 0;
    public bool LoopPlayback => SelectedCamera?.Camera.Loop == true;
    public string ResultResourcePath => SelectedTask is null ? "—" : $"Vision/{Segment(CameraName)}/{Segment(SelectedTask.Task.Name)}/Result/Pass";
    public string AlarmRuleName => SelectedTask is null ? "—" : $"VISION_TASK_NG_{SelectedTask.Task.TaskId.ToUpperInvariant()}";

    public DelegateCommand AddTaskCommand { get; }
    public DelegateCommand ConfigureCameraCommand { get; }
    public DelegateCommand ConfigureCommand { get; }
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand TriggerCommand { get; }
    public DelegateCommand StartCommand { get; }
    public DelegateCommand PauseCommand { get; }
    public DelegateCommand PreviewPauseCommand { get; }
    public DelegateCommand ResetCommand { get; }
    public DelegateCommand LoginToConfigureCommand { get; }

    public VisionInspectionViewModel(IVisionConfigurationRepository repository, VisionInspectionEngine engine,
        VisionResultPublisher publisher, IDialogService dialogService, IAuthManager authManager,
        VisionTaskDialogViewModel taskEditor)
    {
        _repository = repository; _engine = engine; _publisher = publisher;
        _dialogService = dialogService; _authManager = authManager;
        TaskEditor = taskEditor;
        TaskEditor.EditorClosed += OnTaskEditorClosed;
        AddTaskCommand = new DelegateCommand(() => _ = OpenTaskEditorAsync(null), () => CanModify);
        ConfigureCameraCommand = new DelegateCommand(OpenCameraDialog, () => CanModify);
        ConfigureCommand = new DelegateCommand(() => _ = OpenTaskEditorAsync(SelectedTask), () => CanModify && SelectedTask is not null);
        RefreshCommand = new DelegateCommand(async () => await ReloadRuntimeAsync());
        TriggerCommand = new DelegateCommand(async () => await TriggerAsync(), () => CanUseCamera);
        StartCommand = new DelegateCommand(async () => await StartAsync(), () => CanRunDetection);
        PauseCommand = new DelegateCommand(TogglePause, () => CanRunDetection);
        PreviewPauseCommand = new DelegateCommand(TogglePreviewPause, () => CanUseCamera);
        ResetCommand = new DelegateCommand(ResetStatistics, () => CanModify && SelectedTask is not null);
        LoginToConfigureCommand = new DelegateCommand(OpenLogin, () => CannotModify);
        _engine.InspectionCompleted += OnInspectionCompleted;
        _engine.FrameReceived += OnFrameReceived;
        _engine.CameraStatusChanged += OnCameraStatusChanged;
        _authManager.CurrentUserChanged += OnCurrentUserChanged;
        _ = LoadAsync();
    }

    public void Destroy()
    {
        _engine.InspectionCompleted -= OnInspectionCompleted;
        _engine.FrameReceived -= OnFrameReceived;
        _engine.CameraStatusChanged -= OnCameraStatusChanged;
        _authManager.CurrentUserChanged -= OnCurrentUserChanged;
        TaskEditor.EditorClosed -= OnTaskEditorClosed;
    }

    private async Task LoadAsync()
    {
        var selectedCameraId = SelectedCamera?.Camera.CameraId;
        var selectedTaskId = SelectedTask?.Task.TaskId;
        var cameras = await _repository.LoadCamerasAsync();
        var tasks = await _repository.LoadTasksAsync();
        Cameras.Clear();
        foreach (var camera in cameras) Cameras.Add(new VisionCameraListItem(camera));
        _allTasks.Clear();
        foreach (var task in tasks)
        {
            var camera = cameras.FirstOrDefault(item => item.CameraId == task.CameraId)
                         ?? new VisionCameraConfig { CameraId = task.CameraId, Name = task.CameraId };
            _allTasks.Add(new VisionTaskListItem(task, camera));
        }
        SelectedCamera = Cameras.FirstOrDefault(item => item.Camera.CameraId == selectedCameraId) ?? Cameras.FirstOrDefault();
        if (selectedTaskId is not null) SelectedTask = Tasks.FirstOrDefault(item => item.Task.TaskId == selectedTaskId) ?? SelectedTask;
        if (Cameras.Count == 0) DetailText = "尚未配置相机，请先点击“相机配置”新增并连接相机";
    }

    private void FilterTasksForCamera()
    {
        var previousTaskId = SelectedTask?.Task.TaskId;
        Tasks.Clear();
        if (SelectedCamera is not null)
        {
            foreach (var item in _allTasks.Where(item => item.Task.CameraId == SelectedCamera.Camera.CameraId)) Tasks.Add(item);
        }
        SelectedTask = Tasks.FirstOrDefault(item => item.Task.TaskId == previousTaskId) ?? Tasks.FirstOrDefault();
        RaisePropertyChanged(nameof(HasNoRecipe));
    }

    private async Task ReloadRuntimeAsync()
    {
        DetailText = "正在重新加载相机和视觉任务...";
        await _engine.ReloadAsync();
        await LoadAsync();
    }

    private async Task OpenTaskEditorAsync(VisionTaskListItem? item)
    {
        IsTaskEditorOpen = true;
        await TaskEditor.OpenAsync(item?.Task.TaskId);
    }

    private async void OnTaskEditorClosed(object? sender, VisionTaskEditorClosedEventArgs args)
    {
        IsTaskEditorOpen = false;
        if (args.Saved) await ReloadRuntimeAsync();
    }

    private void OpenCameraDialog()
    {
        var parameters = new DialogParameters();
        if (SelectedCamera is not null) parameters.Add("CameraId", SelectedCamera.Camera.CameraId);
        _dialogService.ShowDialog("VisionCameraDialog", parameters, result => { if (result.Result == ButtonResult.OK) _ = ReloadRuntimeAsync(); });
    }

    private void OpenLogin() => _dialogService.ShowDialog("LoginDialog", result =>
    {
        if (result.Result == ButtonResult.OK) DetailText = "登录成功，现在可以新增或配置视觉任务";
    });

    private async Task StartAsync()
    {
        if (!CanRunDetection) { DetailText = "请先选择并连接相机"; return; }
        _engine.Resume(); IsPaused = false; DetailText = "正在启动视觉检测...";
        await _engine.ReloadAsync();
        IsCameraConnected = SelectedCamera is not null && _engine.IsCameraConnected(SelectedCamera.Camera.CameraId);
        DetailText = IsCameraConnected
            ? SelectedTask is null ? "实时预览已启动；创建任务配方后将自动执行检测" : "视觉检测已启动"
            : "相机连接失败，请检查相机配置";
    }

    private void TogglePause()
    {
        if (!CanRunDetection) { DetailText = "请先选择并连接相机"; return; }
        if (IsPaused) _engine.Resume(); else _engine.Pause();
        IsPaused = !IsPaused; DetailText = IsPaused ? "检测算法已暂停，实时预览继续" : "视觉检测已继续";
    }

    private void TogglePreviewPause()
    {
        if (!CanUseCamera) { DetailText = "请先选择并连接相机"; return; }
        // 只冻结 UI 画面，后台取流、检测、报警和历史记录仍继续运行。
        IsPreviewPaused = !IsPreviewPaused;
        DetailText = IsPreviewPaused ? "实时预览画面已冻结，后台检测继续运行" : "实时预览画面已恢复";
    }

    private void ResetStatistics()
    {
        if (SelectedTask is not null) _publisher.ResetStatistics(SelectedTask.Task.TaskId);
        TotalCount = 0; NgCount = 0; History.Clear();
        DetailText = "本次运行统计已清零，数据库历史记录未删除";
    }

    private async Task TriggerAsync()
    {
        if (!CanUseCamera || SelectedCamera is null) { DetailText = "请先连接并选择相机"; return; }
        DetailText = SelectedTask is null ? "正在采集单帧预览..." : "正在执行单帧检测...";
        if (SelectedTask is null)
        {
            if (await _engine.TriggerFrameAsync(SelectedCamera.Camera.CameraId) is null) DetailText = "相机未返回图像";
        }
        else if (await _engine.TriggerAsync(SelectedCamera.Camera.CameraId) is null) DetailText = "相机未返回图像，请检查连接";
    }

    private void OnFrameReceived(object? sender, VisionFrameReceivedEventArgs args)
    {
        if (SelectedCamera?.Camera.CameraId != args.Frame.CameraId || IsPreviewPaused) return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            PreviewImage = Decode(args.Frame.EncodedImage);
            FrameInfo = $"图像：{Path.GetFileName(args.Frame.SourcePath) ?? args.Frame.FrameId}   时间：{args.Frame.Timestamp.ToLocalTime():HH:mm:ss.fff}";
            if (SelectedTask is null)
            {
                ResultText = "实时预览"; ResultColor = "#06B6D4";
                DetailText = "相机实时画面正常；创建配方后可执行检测";
            }
        });
    }

    private void OnInspectionCompleted(object? sender, VisionInspectionCompletedEventArgs args)
    {
        if (SelectedTask?.Task.TaskId != args.Result.TaskId) return;
        Application.Current?.Dispatcher.Invoke(() =>
        {
            ResultText = args.Result.IsPass ? "✓ OK · 检测合格" : "✕ NG · 检测不合格";
            ResultColor = args.Result.IsPass ? "#10B981" : "#EF4444";
            DetailText = $"匹配分数 {args.Result.MatchScore:F3} · 耗时 {args.Result.ProcessingTimeMilliseconds:F1} ms" +
                         (string.IsNullOrWhiteSpace(args.Result.FailureReason) ? string.Empty : $" · {args.Result.FailureReason}");
            TotalCount++; if (!args.Result.IsPass) NgCount++;
            History.Insert(0, new VisionHistoryItem(args.Result, Path.GetFileName(args.Frame.SourcePath) ?? args.Frame.FrameId));
            while (History.Count > 200) History.RemoveAt(History.Count - 1);
        });
    }

    private void OnCameraStatusChanged(object? sender, VisionCameraStatusEventArgs args)
    {
        if (SelectedCamera?.Camera.CameraId != args.CameraId) return;
        Application.Current?.Dispatcher.Invoke(() => IsCameraConnected = args.IsConnected);
    }

    private void OnCurrentUserChanged(object? sender, EventArgs args)
    {
        RaisePropertyChanged(nameof(CanModify)); RaisePropertyChanged(nameof(CannotModify));
        RefreshCommands(); LoginToConfigureCommand.RaiseCanExecuteChanged();
    }

    private void RefreshCommands()
    {
        AddTaskCommand.RaiseCanExecuteChanged(); ConfigureCameraCommand.RaiseCanExecuteChanged(); ConfigureCommand.RaiseCanExecuteChanged();
        TriggerCommand.RaiseCanExecuteChanged(); StartCommand.RaiseCanExecuteChanged(); PauseCommand.RaiseCanExecuteChanged();
        PreviewPauseCommand.RaiseCanExecuteChanged();
        ResetCommand.RaiseCanExecuteChanged();
    }

    private void RaiseMetrics()
    {
        RaisePropertyChanged(nameof(OkCount));
        RaisePropertyChanged(nameof(PassRate));
        RaisePropertyChanged(nameof(PassRateText));
    }

    private void RaiseCameraDetails()
    {
        RaisePropertyChanged(nameof(CameraName)); RaisePropertyChanged(nameof(CameraDriver));
        RaisePropertyChanged(nameof(ImageDirectory)); RaisePropertyChanged(nameof(FrameInterval));
        RaisePropertyChanged(nameof(LoopPlayback)); RaisePropertyChanged(nameof(CameraStatusText)); RaisePropertyChanged(nameof(CameraStatusColor));
    }

    private void RaiseTaskDetails()
    {
        CurrentRecipe.Clear();
        if (SelectedTask is not null)
        {
            foreach (var item in SelectedTask.Task.Operators.OrderBy(item => item.Order))
                CurrentRecipe.Add(new VisionRecipeDisplayItem(item.Order + 1, item.DisplayName, ParameterSummary(item)));
        }
        RaisePropertyChanged(nameof(HasNoRecipe)); RaisePropertyChanged(nameof(ResultResourcePath)); RaisePropertyChanged(nameof(AlarmRuleName));
    }

    private static string Segment(string value) => value.Trim().Replace('/', '-').Replace('\\', '-');
    private static string ParameterSummary(VisionOperatorDefinition item) => item.Parameters.Count == 0
        ? "使用默认配置"
        : string.Join("  ·  ", item.Parameters.Where(pair => !pair.Key.Contains("Path", StringComparison.OrdinalIgnoreCase)).Take(3).Select(pair => $"{pair.Key}={pair.Value}"));

    private static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}

public sealed record VisionRecipeDisplayItem(int Order, string Name, string Parameters);
public sealed class VisionCameraListItem(VisionCameraConfig camera)
{
    public VisionCameraConfig Camera { get; } = camera;
    public string Name => Camera.Name;
    public string DisplayText => $"{Camera.Name} · {Camera.DriverType}";
}

public sealed class VisionTaskListItem(VisionInspectionTask task, VisionCameraConfig camera)
{
    public VisionInspectionTask Task { get; } = task;
    public VisionCameraConfig Camera { get; } = camera;
    public string Name => Task.Name;
    public string CameraName => Camera.Name;
    public string DisplayText => Task.Name;
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
