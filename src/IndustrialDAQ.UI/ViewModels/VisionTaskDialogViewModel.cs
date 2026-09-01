using System.IO;
using System.Windows.Media.Imaging;
using IndustrialDAQ.Alarm.RuleEngine;
using IndustrialDAQ.Core.Models;
using IndustrialDAQ.Core.ResourceTree;
using IndustrialDAQ.UI.Services;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Algorithms;
using IndustrialDAQ.Vision.Models;
using IndustrialDAQ.Vision.Runtime;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Mvvm;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>瓶盖检测傻瓜式配置对话框：目录、ROI、阈值、教学、测试和发布。</summary>
public sealed class VisionTaskDialogViewModel : BindableBase, IDialogAware
{
    private readonly IVisionConfigurationRepository _repository;
    private readonly VisionTemplateTeachingService _teachingService;
    private readonly IVisionInspectionAlgorithm _algorithm;
    private readonly VisionInspectionEngine _engine;
    private readonly IAlarmDefinitionRepository _alarmRepository;
    private readonly IRuleEngineService _ruleEngine;
    private readonly IAuthManager _authManager;
    private readonly SecurityAuditService _audit;
    private string _taskId = "vision-cap-" + Guid.NewGuid().ToString("N")[..8];
    private string _cameraId = "vision-camera-" + Guid.NewGuid().ToString("N")[..8];
    private string _cameraName = "模拟瓶盖相机";
    private string _taskName = "瓶盖有无检测";
    private string _productCode = "CAP-MVP";
    private string _imageDirectory = string.Empty;
    private int _intervalMilliseconds = 1000;
    private bool _loop = true;
    private double _roiX = 0.25, _roiY = 0.25, _roiWidth = 0.5, _roiHeight = 0.5;
    private double _threshold = 0.80;
    private bool _saveNgImage = true;
    private string _templatePath = string.Empty;
    private BitmapImage? _previewImage;
    private string _statusText = "先选择包含合格与缺盖样图的目录";

    public string Title => "配置瓶盖有无检测";
    public string CameraName { get => _cameraName; set => SetProperty(ref _cameraName, value); }
    public string TaskName { get => _taskName; set => SetProperty(ref _taskName, value); }
    public string ProductCode { get => _productCode; set => SetProperty(ref _productCode, value); }
    public string ImageDirectory { get => _imageDirectory; set => SetProperty(ref _imageDirectory, value); }
    public int IntervalMilliseconds { get => _intervalMilliseconds; set => SetProperty(ref _intervalMilliseconds, value); }
    public bool Loop { get => _loop; set => SetProperty(ref _loop, value); }
    public double RoiX { get => _roiX; set => SetProperty(ref _roiX, value); }
    public double RoiY { get => _roiY; set => SetProperty(ref _roiY, value); }
    public double RoiWidth { get => _roiWidth; set => SetProperty(ref _roiWidth, value); }
    public double RoiHeight { get => _roiHeight; set => SetProperty(ref _roiHeight, value); }
    public double Threshold { get => _threshold; set { SetProperty(ref _threshold, value); RaisePropertyChanged(nameof(ThresholdText)); } }
    public string ThresholdText => Threshold.ToString("F2");
    public bool SaveNgImage { get => _saveNgImage; set => SetProperty(ref _saveNgImage, value); }
    public BitmapImage? PreviewImage { get => _previewImage; private set => SetProperty(ref _previewImage, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public DelegateCommand BrowseCommand { get; }
    public DelegateCommand TeachCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DialogCloseListener RequestClose { get; }

    public VisionTaskDialogViewModel(IVisionConfigurationRepository repository,
        VisionTemplateTeachingService teachingService, IVisionInspectionAlgorithm algorithm,
        VisionInspectionEngine engine, IAlarmDefinitionRepository alarmRepository,
        IRuleEngineService ruleEngine, IAuthManager authManager, SecurityAuditService audit)
    {
        _repository = repository; _teachingService = teachingService; _algorithm = algorithm;
        _engine = engine; _alarmRepository = alarmRepository; _ruleEngine = ruleEngine;
        _authManager = authManager; _audit = audit;
        BrowseCommand = new DelegateCommand(Browse);
        TeachCommand = new DelegateCommand(async () => await TeachAndTestAsync());
        SaveCommand = new DelegateCommand(async () => await SaveAsync());
        CancelCommand = new DelegateCommand(() => RequestClose.Invoke(ButtonResult.Cancel));
    }

    public bool CanCloseDialog() => true;
    public void OnDialogClosed() { }

    public async void OnDialogOpened(IDialogParameters parameters)
    {
        if (!parameters.TryGetValue("TaskId", out string? taskId) || string.IsNullOrWhiteSpace(taskId)) return;
        var task = (await _repository.LoadTasksAsync()).FirstOrDefault(item => item.TaskId == taskId);
        if (task is null) return;
        var camera = (await _repository.LoadCamerasAsync()).FirstOrDefault(item => item.CameraId == task.CameraId);
        _taskId = task.TaskId; _cameraId = task.CameraId;
        TaskName = task.Name; ProductCode = task.ProductCode; Threshold = task.MatchThreshold;
        RoiX = task.Roi.X; RoiY = task.Roi.Y; RoiWidth = task.Roi.Width; RoiHeight = task.Roi.Height;
        SaveNgImage = task.SaveNgImage; _templatePath = task.TemplateImagePath;
        if (camera is not null)
        {
            CameraName = camera.Name; ImageDirectory = camera.ImageDirectory;
            IntervalMilliseconds = camera.IntervalMilliseconds; Loop = camera.Loop;
        }
        LoadPreview(); StatusText = "已加载现有配置，可重新教学或直接发布";
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择瓶盖检测样图目录", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        ImageDirectory = dialog.FolderName; LoadPreview();
        StatusText = "目录已选择，请确认首张图片为有瓶盖合格样图，然后点击“教学并测试”";
    }

    private async Task TeachAndTestAsync()
    {
        try
        {
            var sample = FirstImage();
            if (sample is null) throw new InvalidOperationException("目录中没有 jpg、png 或 bmp 图片。");
            var roi = CurrentRoi();
            var frame = new VisionFrame("teach-" + Guid.NewGuid().ToString("N"), _cameraId, await File.ReadAllBytesAsync(sample), DateTimeOffset.UtcNow, sample);
            _templatePath = await _teachingService.CreateTemplateAsync(frame, roi,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndustrialDAQ", "Vision", "Templates", _taskId, "cap-template.png"));
            var result = await _algorithm.InspectAsync(frame, BuildTask());
            PreviewImage = Decode(frame.EncodedImage);
            StatusText = result.IsPass
                ? $"✓ 教学成功，样图匹配分数 {result.MatchScore:F3}，可以发布"
                : $"✗ 教学测试未通过：{result.FailureReason}";
        }
        catch (Exception ex) { StatusText = "教学失败：" + ex.Message; }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (!_authManager.CanModify) throw new UnauthorizedAccessException("当前账号无权修改视觉配置。");
            if (!File.Exists(_templatePath)) throw new InvalidOperationException("请先执行模板教学。");
            var camera = BuildCamera(); var task = BuildTask();
            await _repository.UpsertCameraAsync(camera); await _repository.UpsertTaskAsync(task);
            await _engine.ReloadAsync();
            await ProvisionAlarmAsync(camera, task);
            await _audit.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username,
                "VisionTaskSaved", ResultPath(camera, task), $"Threshold={task.MatchThreshold};Roi={task.Roi}", true);
            RequestClose.Invoke(new DialogParameters { { "TaskId", task.TaskId } }, ButtonResult.OK);
        }
        catch (Exception ex) { StatusText = "发布失败：" + ex.Message; }
    }

    private async Task ProvisionAlarmAsync(VisionCameraConfig camera, VisionInspectionTask task)
    {
        var target = new ResourcePath(ResultPath(camera, task));
        var code = $"VISION_CAP_MISSING_{task.TaskId.ToUpperInvariant()}";
        await _alarmRepository.UpsertAsync(new AlarmDefinition
        {
            Id = "alarm-" + task.TaskId, RuleId = "vision-cap-missing-" + task.TaskId,
            AlarmCode = code, ResourcePath = new ResourcePath($"{target.Value}/Alarm/{code}"),
            TargetResourcePath = target, TagId = VisionTagCatalog.CapPresent(task.TaskId),
            TagName = "Result.CapPresent", AlarmType = AlarmType.Bool,
            ConditionExpression = "Value == false", ClearExpression = "Value == true",
            Severity = AlarmSeverity.Warning, Title = "瓶盖缺失",
            MessageTemplate = $"产品 {task.ProductCode} 未检测到瓶盖，请检查 NG 图片。",
            Source = camera.Name, IsEnabled = true, DelayMs = 0, CooldownSeconds = 5,
            MetadataJson = "{\"origin\":\"vision-task\"}"
        });
        await _ruleEngine.ReloadAsync();
    }

    private VisionCameraConfig BuildCamera() => new()
    {
        CameraId = _cameraId, Name = CameraName.Trim(), DriverType = "Directory",
        ImageDirectory = ImageDirectory.Trim(), IntervalMilliseconds = IntervalMilliseconds,
        Loop = Loop, TriggerMode = VisionTriggerMode.Continuous, IsEnabled = true
    };

    private VisionInspectionTask BuildTask() => new()
    {
        TaskId = _taskId, CameraId = _cameraId, Name = TaskName.Trim(), ProductCode = ProductCode.Trim(),
        Roi = CurrentRoi(), MatchThreshold = Threshold, TemplateImagePath = _templatePath,
        SaveNgImage = SaveNgImage, IsEnabled = true, Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    private VisionRoi CurrentRoi() => new(RoiX, RoiY, RoiWidth, RoiHeight);
    private string ResultPath(VisionCameraConfig camera, VisionInspectionTask task) =>
        $"Vision/{Segment(camera.Name)}/{Segment(task.Name)}/Result/CapPresent";
    private static string Segment(string value) => value.Trim().Replace('/', '-').Replace('\\', '-');

    private void LoadPreview()
    {
        var path = FirstImage();
        if (path is null) return;
        PreviewImage = Decode(File.ReadAllBytes(path));
    }

    private string? FirstImage() => Directory.Exists(ImageDirectory)
        ? Directory.EnumerateFiles(ImageDirectory).Where(path => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant())).OrderBy(path => path).FirstOrDefault()
        : null;

    private static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}
