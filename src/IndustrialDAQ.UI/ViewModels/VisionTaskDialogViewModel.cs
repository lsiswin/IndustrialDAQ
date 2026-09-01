using System.Collections.ObjectModel;
using System.Globalization;
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

/// <summary>通用视觉任务向导：选择相机后按顺序添加 OpenCV 算子并发布配方。</summary>
public sealed class VisionTaskDialogViewModel : BindableBase, IDialogAware
{
    private readonly IVisionConfigurationRepository _repository;
    private readonly VisionTemplateTeachingService _teachingService;
    private readonly IHikvisionCameraDiscoveryService _hikvisionDiscovery;
    private readonly IVisionInspectionAlgorithm _pipelineAlgorithm;
    private readonly VisionInspectionEngine _engine;
    private readonly IAlarmDefinitionRepository _alarmRepository;
    private readonly IRuleEngineService _ruleEngine;
    private readonly IAuthManager _authManager;
    private readonly SecurityAuditService _audit;
    private string _taskId = "vision-task-" + Guid.NewGuid().ToString("N")[..8];
    private string _cameraId = "vision-camera-" + Guid.NewGuid().ToString("N")[..8];
    private string _cameraName = "模拟视觉相机";
    private string _taskName = "瓶盖有无检测";
    private string _productCode = "VISION-001";
    private string _imageDirectory = string.Empty;
    private string _deviceSerialNumber = string.Empty;
    private string _deviceIpAddress = string.Empty;
    private VisionCameraSourceOption _selectedCameraSource = CameraSourceOptions.All[1];
    private HikvisionCameraCandidate? _selectedDiscoveredCamera;
    private VisionOperatorDescriptor? _selectedAvailableOperator;
    private VisionOperatorEditorItem? _selectedOperator;
    private int _intervalMilliseconds = 1000;
    private bool _loop = true;
    private bool _saveNgImage = true;
    private BitmapImage? _previewImage;
    private string _statusText = "选择相机后，按顺序添加视觉算子";

    public string Title => "配置视觉任务";
    public string CameraName { get => _cameraName; set => SetProperty(ref _cameraName, value); }
    public string TaskName { get => _taskName; set => SetProperty(ref _taskName, value); }
    public string ProductCode { get => _productCode; set => SetProperty(ref _productCode, value); }
    public string ImageDirectory { get => _imageDirectory; set => SetProperty(ref _imageDirectory, value); }
    public int IntervalMilliseconds { get => _intervalMilliseconds; set => SetProperty(ref _intervalMilliseconds, value); }
    public bool Loop { get => _loop; set => SetProperty(ref _loop, value); }
    public bool SaveNgImage { get => _saveNgImage; set => SetProperty(ref _saveNgImage, value); }
    public BitmapImage? PreviewImage { get => _previewImage; private set => SetProperty(ref _previewImage, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public IReadOnlyList<VisionCameraSourceOption> CameraSources { get; } = CameraSourceOptions.All;
    public IReadOnlyList<VisionOperatorDescriptor> AvailableOperators { get; } = VisionOperatorCatalog.Common;
    public ObservableCollection<HikvisionCameraCandidate> DiscoveredCameras { get; } = [];
    public ObservableCollection<VisionOperatorEditorItem> Operators { get; } = [];

    public VisionCameraSourceOption SelectedCameraSource
    {
        get => _selectedCameraSource;
        set
        {
            if (!SetProperty(ref _selectedCameraSource, value)) return;
            RaisePropertyChanged(nameof(UsesDirectorySource));
            RaisePropertyChanged(nameof(UsesHikvisionMvs));
            StatusText = value.DriverType == VisionCameraDriverTypes.HikvisionMvs
                ? "点击“自动扫描”，发现真实或 MVS 虚拟海康相机"
                : "选择图片目录，系统将按文件名顺序模拟相机取流";
        }
    }

    public HikvisionCameraCandidate? SelectedDiscoveredCamera
    {
        get => _selectedDiscoveredCamera;
        set
        {
            if (!SetProperty(ref _selectedDiscoveredCamera, value) || value is null) return;
            _deviceSerialNumber = value.SerialNumber;
            _deviceIpAddress = value.IpAddress;
            CameraName = value.DisplayName;
            StatusText = $"已选择{(value.IsVirtual ? "海康虚拟" : "海康")}相机：{value.DisplayText}";
        }
    }

    public VisionOperatorDescriptor? SelectedAvailableOperator
    {
        get => _selectedAvailableOperator;
        set { SetProperty(ref _selectedAvailableOperator, value); AddOperatorCommand.RaiseCanExecuteChanged(); }
    }

    public VisionOperatorEditorItem? SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            SetProperty(ref _selectedOperator, value);
            RemoveOperatorCommand.RaiseCanExecuteChanged();
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
        }
    }

    public bool UsesDirectorySource => SelectedCameraSource.DriverType != VisionCameraDriverTypes.HikvisionMvs;
    public bool UsesHikvisionMvs => SelectedCameraSource.DriverType == VisionCameraDriverTypes.HikvisionMvs;
    public DelegateCommand BrowseCommand { get; }
    public DelegateCommand ScanHikvisionCommand { get; }
    public DelegateCommand AddOperatorCommand { get; }
    public DelegateCommand RemoveOperatorCommand { get; }
    public DelegateCommand MoveUpCommand { get; }
    public DelegateCommand MoveDownCommand { get; }
    public DelegateCommand TeachCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DialogCloseListener RequestClose { get; }

    public VisionTaskDialogViewModel(IVisionConfigurationRepository repository,
        VisionTemplateTeachingService teachingService, IHikvisionCameraDiscoveryService hikvisionDiscovery,
        IEnumerable<IVisionInspectionAlgorithm> algorithms, VisionInspectionEngine engine,
        IAlarmDefinitionRepository alarmRepository, IRuleEngineService ruleEngine,
        IAuthManager authManager, SecurityAuditService audit)
    {
        _repository = repository;
        _teachingService = teachingService;
        _hikvisionDiscovery = hikvisionDiscovery;
        _pipelineAlgorithm = algorithms.First(item => item.AlgorithmType == VisionOperatorPipelineAlgorithm.TypeName);
        _engine = engine;
        _alarmRepository = alarmRepository;
        _ruleEngine = ruleEngine;
        _authManager = authManager;
        _audit = audit;
        BrowseCommand = new DelegateCommand(Browse);
        ScanHikvisionCommand = new DelegateCommand(async () => await ScanHikvisionAsync());
        AddOperatorCommand = new DelegateCommand(AddOperator, () => SelectedAvailableOperator is not null);
        RemoveOperatorCommand = new DelegateCommand(RemoveOperator, () => SelectedOperator is not null);
        MoveUpCommand = new DelegateCommand(() => MoveOperator(-1), () => SelectedOperator is not null && Operators.IndexOf(SelectedOperator) > 0);
        MoveDownCommand = new DelegateCommand(() => MoveOperator(1), () => SelectedOperator is not null && Operators.IndexOf(SelectedOperator) < Operators.Count - 1);
        TeachCommand = new DelegateCommand(async () => await TeachAndTestAsync());
        SaveCommand = new DelegateCommand(async () => await SaveAsync());
        CancelCommand = new DelegateCommand(() => RequestClose.Invoke(ButtonResult.Cancel));
        LoadRecommendedRecipe();
    }

    public bool CanCloseDialog() => true;
    public void OnDialogClosed() { }

    public async void OnDialogOpened(IDialogParameters parameters)
    {
        if (!parameters.TryGetValue("TaskId", out string? taskId) || string.IsNullOrWhiteSpace(taskId)) return;
        var task = (await _repository.LoadTasksAsync()).FirstOrDefault(item => item.TaskId == taskId);
        if (task is null) return;
        var camera = (await _repository.LoadCamerasAsync()).FirstOrDefault(item => item.CameraId == task.CameraId);
        _taskId = task.TaskId;
        _cameraId = task.CameraId;
        TaskName = task.Name;
        ProductCode = task.ProductCode;
        SaveNgImage = task.SaveNgImage;
        LoadOperators(task);
        if (camera is not null)
        {
            CameraName = camera.Name;
            ImageDirectory = camera.ImageDirectory;
            _deviceSerialNumber = camera.DeviceSerialNumber;
            _deviceIpAddress = camera.DeviceIpAddress;
            SelectedCameraSource = CameraSources.FirstOrDefault(item => item.DriverType == camera.DriverType) ?? CameraSources[0];
            IntervalMilliseconds = camera.IntervalMilliseconds;
            Loop = camera.Loop;
        }
        LoadPreview();
        StatusText = "已加载当前任务配方，可调整算子参数和执行顺序";
    }

    private void LoadRecommendedRecipe()
    {
        // 默认配方覆盖典型瓶盖有无检测，用户仍可逐个增删算子。
        Operators.Clear();
        foreach (var type in new[] { "RoiCrop", "Grayscale", "GaussianBlur", "TemplateMatch" })
            Operators.Add(new VisionOperatorEditorItem(VisionOperatorCatalog.Find(type).CreateDefault(Operators.Count)));
        SelectedAvailableOperator = AvailableOperators[0];
        SelectedOperator = Operators[0];
    }

    private void LoadOperators(VisionInspectionTask task)
    {
        Operators.Clear();
        var definitions = task.Operators.Count > 0 ? task.Operators : LegacyRecipe(task);
        foreach (var definition in definitions.OrderBy(item => item.Order)) Operators.Add(new VisionOperatorEditorItem(definition));
        SelectedOperator = Operators.FirstOrDefault();
    }

    private static IReadOnlyList<VisionOperatorDefinition> LegacyRecipe(VisionInspectionTask task)
    {
        var roi = VisionOperatorCatalog.Find("RoiCrop").CreateDefault(0);
        roi.Parameters["X"] = task.Roi.X.ToString(CultureInfo.InvariantCulture);
        roi.Parameters["Y"] = task.Roi.Y.ToString(CultureInfo.InvariantCulture);
        roi.Parameters["Width"] = task.Roi.Width.ToString(CultureInfo.InvariantCulture);
        roi.Parameters["Height"] = task.Roi.Height.ToString(CultureInfo.InvariantCulture);
        var template = VisionOperatorCatalog.Find("TemplateMatch").CreateDefault(3);
        template.Parameters["TemplatePath"] = task.TemplateImagePath;
        template.Parameters["MinScore"] = task.MatchThreshold.ToString(CultureInfo.InvariantCulture);
        return [roi, VisionOperatorCatalog.Find("Grayscale").CreateDefault(1), VisionOperatorCatalog.Find("GaussianBlur").CreateDefault(2), template];
    }

    private void AddOperator()
    {
        if (SelectedAvailableOperator is null) return;
        var item = new VisionOperatorEditorItem(SelectedAvailableOperator.CreateDefault(Operators.Count));
        Operators.Add(item);
        SelectedOperator = item;
        StatusText = $"已添加“{item.DisplayName}”，可修改默认参数";
    }

    private void RemoveOperator()
    {
        if (SelectedOperator is null) return;
        var index = Operators.IndexOf(SelectedOperator);
        Operators.Remove(SelectedOperator);
        NormalizeOrder();
        SelectedOperator = Operators.Count == 0 ? null : Operators[Math.Min(index, Operators.Count - 1)];
    }

    private void MoveOperator(int offset)
    {
        if (SelectedOperator is null) return;
        var oldIndex = Operators.IndexOf(SelectedOperator);
        var newIndex = oldIndex + offset;
        if (newIndex < 0 || newIndex >= Operators.Count) return;
        Operators.Move(oldIndex, newIndex);
        NormalizeOrder();
    }

    private void NormalizeOrder()
    {
        for (var index = 0; index < Operators.Count; index++) Operators[index].Order = index + 1;
        RemoveOperatorCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择视觉检测样图目录", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        ImageDirectory = dialog.FolderName;
        LoadPreview();
        StatusText = "目录已选择，可采集模板并测试当前配方";
    }

    private async Task ScanHikvisionAsync()
    {
        try
        {
            StatusText = "正在调用海康 MVS SDK 扫描 GigE、USB 和虚拟相机...";
            var cameras = await _hikvisionDiscovery.ScanAsync();
            DiscoveredCameras.Clear();
            foreach (var camera in cameras) DiscoveredCameras.Add(camera);
            SelectedDiscoveredCamera = cameras.FirstOrDefault(item => item.IsVirtual) ?? cameras.FirstOrDefault();
            StatusText = cameras.Count == 0 ? "未发现海康相机，请在 MVS 中启动模拟相机后重试。" : $"扫描完成，共发现 {cameras.Count} 台相机。";
        }
        catch (Exception ex) { StatusText = "扫描失败：" + ex.Message; }
    }

    private async Task TeachAndTestAsync()
    {
        try
        {
            if (Operators.Count == 0) throw new InvalidOperationException("请至少添加一个视觉算子。");
            var frame = await CaptureTeachingFrameAsync();
            var templateOperator = Operators.FirstOrDefault(item => item.OperatorType == "TemplateMatch");
            if (templateOperator is not null)
            {
                var templatePath = await _teachingService.CreateTemplateAsync(frame, RecipeRoi(),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IndustrialDAQ", "Vision", "Templates", _taskId, "template.png"));
                templateOperator.SetParameter("TemplatePath", templatePath);
            }
            var result = await _pipelineAlgorithm.InspectAsync(frame, BuildTask());
            PreviewImage = Decode(frame.EncodedImage);
            StatusText = result.IsPass ? $"✓ 当前配方测试通过，得分 {result.MatchScore:F3}" : $"✗ 当前配方测试未通过：{result.FailureReason}";
        }
        catch (Exception ex) { StatusText = "配方测试失败：" + ex.Message; }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (!_authManager.CanModify) throw new UnauthorizedAccessException("当前账号无权修改视觉配置。");
            if (Operators.Count == 0) throw new InvalidOperationException("请至少添加一个视觉算子。");
            var template = Operators.FirstOrDefault(item => item.OperatorType == "TemplateMatch");
            if (template is not null && !File.Exists(template.ParameterValue("TemplatePath")))
                throw new InvalidOperationException("配方包含模板匹配，请先点击“采集模板并测试”。");
            var camera = BuildCamera();
            var task = BuildTask();
            await _repository.UpsertCameraAsync(camera);
            await _repository.UpsertTaskAsync(task);
            await _engine.ReloadAsync();
            await ProvisionAlarmAsync(camera, task);
            await _audit.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username,
                "VisionTaskSaved", ResultPath(camera, task), $"Operators={task.Operators.Count};Product={task.ProductCode}", true);
            RequestClose.Invoke(new DialogParameters { { "TaskId", task.TaskId } }, ButtonResult.OK);
        }
        catch (Exception ex) { StatusText = "发布失败：" + ex.Message; }
    }

    private async Task ProvisionAlarmAsync(VisionCameraConfig camera, VisionInspectionTask task)
    {
        var target = new ResourcePath(ResultPath(camera, task));
        var code = $"VISION_TASK_NG_{task.TaskId.ToUpperInvariant()}";
        await _alarmRepository.UpsertAsync(new AlarmDefinition
        {
            Id = "alarm-" + task.TaskId, RuleId = "vision-task-ng-" + task.TaskId,
            AlarmCode = code, ResourcePath = new ResourcePath($"{target.Value}/Alarm/{code}"),
            TargetResourcePath = target, TagId = VisionTagCatalog.Pass(task.TaskId), TagName = "Result.Pass",
            AlarmType = AlarmType.Bool, ConditionExpression = "Value == false", ClearExpression = "Value == true",
            Severity = AlarmSeverity.Warning, Title = "视觉任务 NG",
            MessageTemplate = $"产品 {task.ProductCode} 的视觉任务“{task.Name}”检测不合格，请检查 NG 图片。",
            Source = camera.Name, IsEnabled = true, DelayMs = 0, CooldownSeconds = 5,
            MetadataJson = "{\"origin\":\"vision-operator-recipe\"}"
        });
        await _ruleEngine.ReloadAsync();
    }

    private VisionCameraConfig BuildCamera() => new()
    {
        CameraId = _cameraId, Name = CameraName.Trim(), DriverType = SelectedCameraSource.DriverType,
        ImageDirectory = ImageDirectory.Trim(), IntervalMilliseconds = IntervalMilliseconds,
        DeviceSerialNumber = _deviceSerialNumber, DeviceIpAddress = _deviceIpAddress,
        Loop = Loop, TriggerMode = VisionTriggerMode.Continuous, IsEnabled = true
    };

    private VisionInspectionTask BuildTask()
    {
        var definitions = Operators.Select((item, index) => item.ToDefinition(index)).ToArray();
        var template = definitions.FirstOrDefault(item => item.OperatorType == "TemplateMatch");
        var threshold = template is null ? 0.8 : Parse(template.Parameters.GetValueOrDefault("MinScore"), 0.8);
        return new VisionInspectionTask
        {
            TaskId = _taskId, CameraId = _cameraId, Name = TaskName.Trim(), ProductCode = ProductCode.Trim(),
            AlgorithmType = VisionOperatorPipelineAlgorithm.TypeName, Operators = definitions,
            Roi = RecipeRoi(), MatchThreshold = threshold,
            TemplateImagePath = template?.Parameters.GetValueOrDefault("TemplatePath") ?? string.Empty,
            SaveNgImage = SaveNgImage, IsEnabled = true, Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    private VisionRoi RecipeRoi()
    {
        var roi = Operators.FirstOrDefault(item => item.OperatorType == "RoiCrop");
        return roi is null ? VisionRoi.FullFrame : new VisionRoi(
            Parse(roi.ParameterValue("X"), 0), Parse(roi.ParameterValue("Y"), 0),
            Parse(roi.ParameterValue("Width"), 1), Parse(roi.ParameterValue("Height"), 1));
    }

    private string ResultPath(VisionCameraConfig camera, VisionInspectionTask task) => $"Vision/{Segment(camera.Name)}/{Segment(task.Name)}/Result/Pass";
    private static string Segment(string value) => value.Trim().Replace('/', '-').Replace('\\', '-');
    private static double Parse(string? value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private void LoadPreview()
    {
        var path = FirstImage();
        if (path is not null) PreviewImage = Decode(File.ReadAllBytes(path));
    }

    private async Task<VisionFrame> CaptureTeachingFrameAsync()
    {
        if (UsesDirectorySource)
        {
            var sample = FirstImage() ?? throw new InvalidOperationException("目录中没有 jpg、png 或 bmp 图片。");
            return new VisionFrame("teach-" + Guid.NewGuid().ToString("N"), _cameraId, await File.ReadAllBytesAsync(sample), DateTimeOffset.UtcNow, sample);
        }
        await using var camera = new IndustrialDAQ.Vision.Cameras.HikvisionMvsCameraDriver(BuildCamera());
        return await camera.TriggerAsync() ?? throw new InvalidOperationException("海康相机未返回图像，请检查 MVS 模拟相机是否正在取流。");
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

/// <summary>可编辑算子项，将动态参数映射到通用配方模型。</summary>
public sealed class VisionOperatorEditorItem : BindableBase
{
    private int _order;
    public string OperatorId { get; }
    public string OperatorType { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public string Description { get; }
    public ObservableCollection<VisionOperatorParameterEditor> Parameters { get; } = [];
    public int Order { get => _order; set => SetProperty(ref _order, value); }

    public VisionOperatorEditorItem(VisionOperatorDefinition definition)
    {
        var descriptor = VisionOperatorCatalog.Find(definition.OperatorType);
        OperatorId = definition.OperatorId;
        OperatorType = definition.OperatorType;
        DisplayName = string.IsNullOrWhiteSpace(definition.DisplayName) ? descriptor.DisplayName : definition.DisplayName;
        Category = descriptor.Category;
        Description = descriptor.Description;
        Order = definition.Order + 1;
        foreach (var parameter in descriptor.Parameters)
            Parameters.Add(new VisionOperatorParameterEditor(parameter.Name, parameter.DisplayName,
                definition.Parameters.GetValueOrDefault(parameter.Name, parameter.DefaultValue), parameter.Help));
    }

    public string ParameterValue(string name) => Parameters.FirstOrDefault(item => item.Name == name)?.Value ?? string.Empty;
    public void SetParameter(string name, string value)
    {
        var parameter = Parameters.FirstOrDefault(item => item.Name == name);
        if (parameter is not null) parameter.Value = value;
    }
    public VisionOperatorDefinition ToDefinition(int order) => new()
    {
        OperatorId = OperatorId, OperatorType = OperatorType, DisplayName = DisplayName, Order = order, IsEnabled = true,
        Parameters = Parameters.ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase)
    };
}

public sealed class VisionOperatorParameterEditor(string name, string displayName, string value, string help) : BindableBase
{
    private string _value = value;
    public string Name { get; } = name;
    public string DisplayName { get; } = displayName;
    public string Help { get; } = help;
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

/// <summary>相机来源选项，UI 仅展示名称，运行时使用稳定驱动标识。</summary>
public sealed record VisionCameraSourceOption(string DriverType, string DisplayName);

public static class CameraSourceOptions
{
    public static IReadOnlyList<VisionCameraSourceOption> All { get; } =
    [
        new(VisionCameraDriverTypes.Directory, "普通图片目录模拟相机"),
        new(VisionCameraDriverTypes.HikvisionSimulator, "海康图片目录模拟相机"),
        new(VisionCameraDriverTypes.HikvisionMvs, "海康 MVS 相机（真实 / 虚拟）")
    ];
}
