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
using Prism.Commands;
using Prism.Mvvm;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>通用视觉任务向导：选择相机后按顺序添加 OpenCV 算子并发布配方。</summary>
public sealed class VisionTaskDialogViewModel : BindableBase
{
    private readonly IVisionConfigurationRepository _repository;
    private readonly VisionTemplateTeachingService _teachingService;
    private readonly IVisionInspectionAlgorithm _pipelineAlgorithm;
    private readonly VisionInspectionEngine _engine;
    private readonly IAlarmDefinitionRepository _alarmRepository;
    private readonly IRuleEngineService _ruleEngine;
    private readonly IAuthManager _authManager;
    private readonly SecurityAuditService _audit;
    private string _taskId = "vision-task-" + Guid.NewGuid().ToString("N")[..8];
    private string _taskName = "瓶盖有无检测";
    private string _productCode = "VISION-001";
    private VisionCameraConfig? _selectedCamera;
    private VisionOperatorDescriptor? _selectedAvailableOperator;
    private VisionOperatorEditorItem? _selectedOperator;
    private bool _saveNgImage = true;
    private bool _isRegionDrawing;
    private BitmapImage? _previewImage;
    private VisionFrame? _teachingFrame;
    private string _statusText = "选择相机后，按顺序添加视觉算子";
    private bool _isSaving;

    public string Title => "配置视觉任务";
    public string TaskName { get => _taskName; set => SetProperty(ref _taskName, value); }
    public string ProductCode { get => _productCode; set => SetProperty(ref _productCode, value); }
    public bool SaveNgImage { get => _saveNgImage; set => SetProperty(ref _saveNgImage, value); }
    public BitmapImage? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (!SetProperty(ref _previewImage, value)) return;
            RaisePropertyChanged(nameof(HasPreviewImage));
            RaisePropertyChanged(nameof(HasNoPreviewImage));
            RaisePropertyChanged(nameof(CanStartRegionSelection));
            RaisePropertyChanged(nameof(SelectionInstruction));
            StartRegionSelectionCommand.RaiseCanExecuteChanged();
        }
    }
    public bool HasPreviewImage => PreviewImage is not null;
    public bool HasNoPreviewImage => !HasPreviewImage;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value)) return;
            RaisePropertyChanged(nameof(SaveButtonText));
            SaveCommand.RaiseCanExecuteChanged();
        }
    }
    public string SaveButtonText => IsSaving ? "正在发布..." : "发布视觉任务";
    public IReadOnlyList<VisionOperatorDescriptor> AvailableOperators { get; } = VisionOperatorCatalog.Common;
    public ObservableCollection<VisionCameraConfig> ExistingCameras { get; } = [];
    public ObservableCollection<VisionOperatorEditorItem> Operators { get; } = [];

    public VisionCameraConfig? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (!SetProperty(ref _selectedCamera, value)) return;
            _teachingFrame = null;
            PreviewImage = null;
            RefreshTeachingFrameCommand.RaiseCanExecuteChanged();
            _ = LoadPreviewAsync();
            StatusText = value is null
                ? "没有可用相机，请先返回工作台完成相机配置"
                : $"已选择相机“{value.Name}”，可继续配置视觉算子";
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
            RaisePropertyChanged(nameof(CanDrawRegion));
            RaisePropertyChanged(nameof(CanStartRegionSelection));
            RaisePropertyChanged(nameof(SelectionInstruction));
            IsRegionDrawing = false;
            StartRegionSelectionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool CanDrawRegion => SelectedOperator?.OperatorType is "RoiCrop" or "TemplateMatch";
    public bool CanStartRegionSelection => CanDrawRegion && HasPreviewImage;
    public bool IsRegionDrawing
    {
        get => _isRegionDrawing;
        private set
        {
            if (!SetProperty(ref _isRegionDrawing, value)) return;
            RaisePropertyChanged(nameof(RegionSelectionButtonText));
            RaisePropertyChanged(nameof(SelectionInstruction));
        }
    }
    public string RegionSelectionButtonText => IsRegionDrawing ? "✕ 取消框选" : "▣ 开始框选";
    public string SelectionInstruction => SelectedOperator?.OperatorType switch
    {
        _ when !HasPreviewImage => "请先点击左侧“获取相机快照”，取得教学图后再框选",
        "RoiCrop" when IsRegionDrawing => "框选模式：按住左键拖动，或依次点击起点和终点（蓝框）",
        "TemplateMatch" when IsRegionDrawing => "框选模式：按住左键拖动，或依次点击起点和终点（橙框）",
        "RoiCrop" => "点击右侧“开始框选”，再到左侧图像选择 ROI 搜索区域",
        "TemplateMatch" => "点击右侧“开始框选”，再到左侧图像选择模板教学区域",
        _ => "当前算子使用右侧标量参数，不需要图像框选"
    };

    public DelegateCommand AddOperatorCommand { get; }
    public DelegateCommand RemoveOperatorCommand { get; }
    public DelegateCommand MoveUpCommand { get; }
    public DelegateCommand MoveDownCommand { get; }
    public DelegateCommand StartRegionSelectionCommand { get; }
    public DelegateCommand RefreshTeachingFrameCommand { get; }
    public DelegateCommand TeachCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public event EventHandler<VisionTaskEditorClosedEventArgs>? EditorClosed;

    public VisionTaskDialogViewModel(IVisionConfigurationRepository repository,
        VisionTemplateTeachingService teachingService,
        IEnumerable<IVisionInspectionAlgorithm> algorithms, VisionInspectionEngine engine,
        IAlarmDefinitionRepository alarmRepository, IRuleEngineService ruleEngine,
        IAuthManager authManager, SecurityAuditService audit)
    {
        _repository = repository;
        _teachingService = teachingService;
        _pipelineAlgorithm = algorithms.First(item => item.AlgorithmType == VisionOperatorPipelineAlgorithm.TypeName);
        _engine = engine;
        _alarmRepository = alarmRepository;
        _ruleEngine = ruleEngine;
        _authManager = authManager;
        _audit = audit;
        AddOperatorCommand = new DelegateCommand(AddOperator, () => SelectedAvailableOperator is not null);
        RemoveOperatorCommand = new DelegateCommand(RemoveOperator, () => SelectedOperator is not null);
        // 移动按钮只依赖是否选中算子，边界由 MoveOperator 处理，避免 Collection.Move 后命令状态卡死。
        MoveUpCommand = new DelegateCommand(() => MoveOperator(-1), () => SelectedOperator is not null);
        MoveDownCommand = new DelegateCommand(() => MoveOperator(1), () => SelectedOperator is not null);
        StartRegionSelectionCommand = new DelegateCommand(BeginRegionSelection, () => CanStartRegionSelection);
        RefreshTeachingFrameCommand = new DelegateCommand(async () => await LoadPreviewAsync(true), () => SelectedCamera is not null);
        TeachCommand = new DelegateCommand(async () => await TeachAndTestAsync());
        SaveCommand = new DelegateCommand(async () => await SaveAsync(), () => !IsSaving);
        CancelCommand = new DelegateCommand(() => EditorClosed?.Invoke(this, new VisionTaskEditorClosedEventArgs(false, null)));
        LoadRecommendedRecipe();
    }

    public async Task OpenAsync(string? taskId)
    {
        // 页面内编辑器每次打开都恢复独立状态，避免上一次配方和框选手势残留。
        IsRegionDrawing = false;
        _teachingFrame = null;
        PreviewImage = null;
        _taskId = "vision-task-" + Guid.NewGuid().ToString("N")[..8];
        TaskName = "瓶盖有无检测";
        ProductCode = "VISION-001";
        SaveNgImage = true;
        LoadRecommendedRecipe();

        // 视觉任务只引用已保存的相机配置，避免在配方页面重复维护连接参数。
        var cameras = await _repository.LoadCamerasAsync();
        SelectedCamera = null;
        ExistingCameras.Clear();
        foreach (var camera in cameras.Where(item => item.IsEnabled)) ExistingCameras.Add(camera);

        if (string.IsNullOrWhiteSpace(taskId))
        {
            SelectedCamera = ExistingCameras.FirstOrDefault();
            if (SelectedCamera is null) StatusText = "尚未配置相机，请返回视觉工作台并点击“相机配置”";
            return;
        }

        var task = (await _repository.LoadTasksAsync()).FirstOrDefault(item => item.TaskId == taskId);
        if (task is null) return;
        _taskId = task.TaskId;
        TaskName = task.Name;
        ProductCode = task.ProductCode;
        SaveNgImage = task.SaveNgImage;
        LoadOperators(task);
        SelectedCamera = ExistingCameras.FirstOrDefault(item => item.CameraId == task.CameraId);
        StatusText = "已加载当前任务配方，可调整算子参数和执行顺序";
    }

    private void LoadRecommendedRecipe()
    {
        // 默认配方覆盖典型瓶盖有无检测，用户仍可逐个增删算子。
        Operators.Clear();
        foreach (var type in new[] { "RoiCrop", "TemplateMatch" })
            Operators.Add(new VisionOperatorEditorItem(VisionOperatorCatalog.Find(type).CreateDefault(Operators.Count), false));
        SelectedAvailableOperator = AvailableOperators[0];
        SelectedOperator = Operators[0];
    }

    private void LoadOperators(VisionInspectionTask task)
    {
        Operators.Clear();
        var definitions = task.Operators.Count > 0 ? task.Operators : LegacyRecipe(task);
        // 每次进入编辑页都先隐藏历史区域框，只有本次主动框选后才显示，避免误认为已进入框选状态。
        foreach (var definition in definitions.OrderBy(item => item.Order)) Operators.Add(new VisionOperatorEditorItem(definition, false));
        SelectedOperator = Operators.FirstOrDefault();
    }

    private static IReadOnlyList<VisionOperatorDefinition> LegacyRecipe(VisionInspectionTask task)
    {
        var roi = VisionOperatorCatalog.Find("RoiCrop").CreateDefault(0);
        roi.Parameters["X"] = task.Roi.X.ToString(CultureInfo.InvariantCulture);
        roi.Parameters["Y"] = task.Roi.Y.ToString(CultureInfo.InvariantCulture);
        roi.Parameters["Width"] = task.Roi.Width.ToString(CultureInfo.InvariantCulture);
        roi.Parameters["Height"] = task.Roi.Height.ToString(CultureInfo.InvariantCulture);
        var template = VisionOperatorCatalog.Find("TemplateMatch").CreateDefault(1);
        template.Parameters["X"] = task.Roi.X.ToString(CultureInfo.InvariantCulture);
        template.Parameters["Y"] = task.Roi.Y.ToString(CultureInfo.InvariantCulture);
        template.Parameters["Width"] = task.Roi.Width.ToString(CultureInfo.InvariantCulture);
        template.Parameters["Height"] = task.Roi.Height.ToString(CultureInfo.InvariantCulture);
        template.Parameters["TemplatePath"] = task.TemplateImagePath;
        template.Parameters["MinScore"] = task.MatchThreshold.ToString(CultureInfo.InvariantCulture);
        return [roi, template];
    }

    private void AddOperator()
    {
        if (SelectedAvailableOperator is null) return;
        var item = new VisionOperatorEditorItem(SelectedAvailableOperator.CreateDefault(Operators.Count), false);
        Operators.Add(item);
        SelectedOperator = item;
        StatusText = $"已添加“{item.DisplayName}”，可修改默认参数";
    }

    private void BeginRegionSelection()
    {
        if (!CanDrawRegion) return;
        if (IsRegionDrawing)
        {
            IsRegionDrawing = false;
            StatusText = "已取消框选";
            return;
        }
        IsRegionDrawing = true;
        StatusText = SelectedOperator?.OperatorType == "RoiCrop"
            ? "请在左侧图片上拖动鼠标框选 ROI 搜索区域"
            : "请在左侧图片上拖动鼠标框选模板特征区域";
    }

    public void CompleteRegionSelection() => IsRegionDrawing = false;

    public void ReportSelectionIssue(string message) => StatusText = message;

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

    private async Task TeachAndTestAsync()
    {
        try
        {
            if (Operators.Count == 0) throw new InvalidOperationException("请至少添加一个视觉算子。");
            var frame = await CaptureTeachingFrameAsync();
            var templateOperator = Operators.FirstOrDefault(item => item.OperatorType == "TemplateMatch");
            if (templateOperator is not null)
            {
                ValidateTemplateRegion();
                var templatePath = await _teachingService.CreateTemplateAsync(frame, TemplateRoi(),
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
        if (IsSaving) return;
        IsSaving = true;
        StatusText = "正在校验配方并发布，请稍候...";
        try
        {
            if (!_authManager.CanModify) throw new UnauthorizedAccessException("当前账号无权修改视觉配置。");
            if (Operators.Count == 0) throw new InvalidOperationException("请至少添加一个视觉算子。");
            var template = Operators.FirstOrDefault(item => item.OperatorType == "TemplateMatch");
            if (template is not null)
            {
                ValidateTemplateRegion();
                if (!File.Exists(template.ParameterValue("TemplatePath")))
                    throw new InvalidOperationException("配方包含模板匹配，请先点击“采集模板并测试”。");
            }
            var camera = RequireSelectedCamera();
            var task = BuildTask();
            await _repository.UpsertTaskAsync(task);
            await _engine.ReloadAsync();
            await ProvisionAlarmAsync(camera, task);
            await _audit.RecordAsync(_authManager.CurrentUser.Id, _authManager.CurrentUser.Username,
                "VisionTaskSaved", ResultPath(camera, task), $"Operators={task.Operators.Count};Product={task.ProductCode}", true);
            EditorClosed?.Invoke(this, new VisionTaskEditorClosedEventArgs(true, task.TaskId));
        }
        catch (Exception ex) { StatusText = "发布失败：" + ex.GetBaseException().Message; }
        finally { IsSaving = false; }
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

    private VisionInspectionTask BuildTask()
    {
        var camera = RequireSelectedCamera();
        var definitions = Operators.Select((item, index) => item.ToDefinition(index)).ToArray();
        var template = definitions.FirstOrDefault(item => item.OperatorType == "TemplateMatch");
        var threshold = template is null ? 0.8 : Parse(template.Parameters.GetValueOrDefault("MinScore"), 0.8);
        return new VisionInspectionTask
        {
            TaskId = _taskId, CameraId = camera.CameraId, Name = TaskName.Trim(), ProductCode = ProductCode.Trim(),
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

    public VisionRoi? GetRegion(string operatorType)
    {
        var item = Operators.FirstOrDefault(operatorItem => operatorItem.OperatorType == operatorType);
        // 新增算子的全图默认值仅供算法兜底，用户实际框选前不在教学图上绘制区域框。
        return item is null || !item.HasSelectedRegion ? null : RegionFrom(item);
    }

    public void SetSelectedRegion(VisionRoi region)
    {
        if (!CanDrawRegion || SelectedOperator is null || !region.IsValid) return;
        SelectedOperator.SetParameter("X", region.X.ToString("0.####", CultureInfo.InvariantCulture));
        SelectedOperator.SetParameter("Y", region.Y.ToString("0.####", CultureInfo.InvariantCulture));
        SelectedOperator.SetParameter("Width", region.Width.ToString("0.####", CultureInfo.InvariantCulture));
        SelectedOperator.SetParameter("Height", region.Height.ToString("0.####", CultureInfo.InvariantCulture));
        SelectedOperator.HasSelectedRegion = true;
        // 模板区域改变后旧模板立即失效，必须重新教学才能发布。
        if (SelectedOperator.OperatorType == "TemplateMatch") SelectedOperator.SetParameter("TemplatePath", string.Empty);
        StatusText = SelectedOperator.OperatorType == "RoiCrop" ? "ROI 搜索区域已更新" : "模板教学区域已更新";
    }

    private VisionRoi TemplateRoi()
    {
        var item = Operators.FirstOrDefault(operatorItem => operatorItem.OperatorType == "TemplateMatch");
        return item is null ? RecipeRoi() : RegionFrom(item);
    }

    private static VisionRoi RegionFrom(VisionOperatorEditorItem item) => new(
        Parse(item.ParameterValue("X"), 0), Parse(item.ParameterValue("Y"), 0),
        Parse(item.ParameterValue("Width"), 1), Parse(item.ParameterValue("Height"), 1));

    private void ValidateTemplateRegion()
    {
        var search = RecipeRoi();
        var template = TemplateRoi();
        if (!template.IsValid) throw new InvalidOperationException("模板框选区域无效，请重新框选。");
        if (Operators.Any(item => item.OperatorType == "RoiCrop") &&
            (template.X < search.X || template.Y < search.Y ||
             template.X + template.Width > search.X + search.Width ||
             template.Y + template.Height > search.Y + search.Height))
            throw new InvalidOperationException("模板区域必须位于 ROI 搜索区域内部。");
    }

    private string ResultPath(VisionCameraConfig camera, VisionInspectionTask task) => $"Vision/{Segment(camera.Name)}/{Segment(task.Name)}/Result/Pass";
    private static string Segment(string value) => value.Trim().Replace('/', '-').Replace('\\', '-');
    private static double Parse(string? value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private async Task LoadPreviewAsync(bool forceRefresh = false)
    {
        var configuredCamera = SelectedCamera;
        if (configuredCamera is null) { _teachingFrame = null; PreviewImage = null; return; }
        try
        {
            var path = FirstImage(configuredCamera);
            if (path is not null)
            {
                var bytes = await File.ReadAllBytesAsync(path);
                _teachingFrame = new VisionFrame("teach-" + Guid.NewGuid().ToString("N"), configuredCamera.CameraId,
                    bytes, DateTimeOffset.UtcNow, path);
                PreviewImage = Decode(bytes);
                StatusText = "教学图已加载，可选择 ROI 或模板匹配算子开始框选";
                return;
            }

            // 硬件相机优先复用运行引擎最近帧，避免配方向导创建第二驱动抢占海康相机。
            var frame = forceRefresh ? null : _engine.GetLatestFrame(configuredCamera.CameraId);
            frame ??= await _engine.TriggerFrameAsync(configuredCamera.CameraId);
            _teachingFrame = frame;
            PreviewImage = frame is null ? null : Decode(frame.EncodedImage);
            StatusText = frame is null ? "相机尚未返回图像，请确认连接状态后重试" : "已获取相机教学快照，可以开始框选";
        }
        catch (Exception ex)
        {
            _teachingFrame = null;
            PreviewImage = null;
            StatusText = FriendlyCameraError(ex);
        }
    }

    private async Task<VisionFrame> CaptureTeachingFrameAsync()
    {
        var configuredCamera = RequireSelectedCamera();
        if (_teachingFrame is not null && _teachingFrame.CameraId == configuredCamera.CameraId) return _teachingFrame;
        if (configuredCamera.DriverType != VisionCameraDriverTypes.HikvisionMvs)
        {
            var sample = FirstImage(configuredCamera) ?? throw new InvalidOperationException("已选相机目录中没有 jpg、png 或 bmp 图片。");
            return new VisionFrame("teach-" + Guid.NewGuid().ToString("N"), configuredCamera.CameraId, await File.ReadAllBytesAsync(sample), DateTimeOffset.UtcNow, sample);
        }
        return _engine.GetLatestFrame(configuredCamera.CameraId)
               ?? await _engine.TriggerFrameAsync(configuredCamera.CameraId)
               ?? throw new InvalidOperationException("海康相机未返回图像，请先获取教学快照。");
    }

    private static string FriendlyCameraError(Exception exception) => exception.GetBaseException().Message.Contains("0x80000203", StringComparison.OrdinalIgnoreCase)
        ? "海康相机当前被 MVS 客户端或其他程序占用（0x80000203）。请先在 MVS 中停止取流并关闭设备，再点击“获取相机快照”。"
        : "读取相机教学图失败：" + exception.GetBaseException().Message;

    private VisionCameraConfig RequireSelectedCamera() => SelectedCamera
        ?? throw new InvalidOperationException("请先选择一个已配置相机；若列表为空，请返回工作台新增相机。");

    private static string? FirstImage(VisionCameraConfig? camera) => camera is not null && Directory.Exists(camera.ImageDirectory)
        ? Directory.EnumerateFiles(camera.ImageDirectory).Where(path => new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant())).OrderBy(path => path).FirstOrDefault()
        : null;

    private static BitmapImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
}

/// <summary>页面内视觉配方编辑器关闭通知，父工作台据此恢复显示并按需重载运行时。</summary>
public sealed class VisionTaskEditorClosedEventArgs(bool saved, string? taskId) : EventArgs
{
    public bool Saved { get; } = saved;
    public string? TaskId { get; } = taskId;
}

/// <summary>可编辑算子项，将动态参数映射到通用配方模型。</summary>
public sealed class VisionOperatorEditorItem : BindableBase
{
    private int _order;
    private bool _hasSelectedRegion;
    public string OperatorId { get; }
    public string OperatorType { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public string Description { get; }
    public ObservableCollection<VisionOperatorParameterEditor> Parameters { get; } = [];
    public IEnumerable<VisionOperatorParameterEditor> EditableParameters => Parameters.Where(item => !IsGraphicalParameter(item.Name));
    public IEnumerable<VisionOperatorParameterEditor> DetailedParameters => Parameters;
    public int Order { get => _order; set => SetProperty(ref _order, value); }
    public bool HasSelectedRegion { get => _hasSelectedRegion; set => SetProperty(ref _hasSelectedRegion, value); }

    public VisionOperatorEditorItem(VisionOperatorDefinition definition, bool hasSelectedRegion)
    {
        var descriptor = VisionOperatorCatalog.Find(definition.OperatorType);
        OperatorId = definition.OperatorId;
        OperatorType = definition.OperatorType;
        DisplayName = string.IsNullOrWhiteSpace(definition.DisplayName) ? descriptor.DisplayName : definition.DisplayName;
        Category = descriptor.Category;
        Description = descriptor.Description;
        Order = definition.Order + 1;
        HasSelectedRegion = hasSelectedRegion && definition.OperatorType is "RoiCrop" or "TemplateMatch";
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
    private static bool IsGraphicalParameter(string name) => name is "X" or "Y" or "Width" or "Height" or "TemplatePath";
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
