using System.Collections.ObjectModel;
using System.IO;
using IndustrialDAQ.UI.Services;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Models;
using IndustrialDAQ.Vision.Runtime;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Mvvm;

namespace IndustrialDAQ.UI.ViewModels;

/// <summary>独立相机配置，不要求预先创建视觉检测任务。</summary>
public sealed class VisionCameraDialogViewModel : BindableBase, IDialogAware
{
    private readonly IVisionConfigurationRepository _repository;
    private readonly IHikvisionCameraDiscoveryService _discovery;
    private readonly VisionInspectionEngine _engine;
    private string _cameraId = "vision-camera-" + Guid.NewGuid().ToString("N")[..8];
    private string _cameraName = "视觉相机";
    private string _imageDirectory = string.Empty;
    private string _serialNumber = string.Empty;
    private string _ipAddress = string.Empty;
    private int _intervalMilliseconds = 1000;
    private bool _loop = true;
    private VisionCameraSourceOption _selectedSource = CameraSourceOptions.All[1];
    private HikvisionCameraCandidate? _selectedDiscoveredCamera;
    private VisionCameraConfig? _selectedSavedCamera;
    private string _statusText = "配置相机后即可在工作台实时预览，无需先创建配方";

    public string Title => "相机配置";
    public IReadOnlyList<VisionCameraSourceOption> CameraSources { get; } = CameraSourceOptions.All;
    public ObservableCollection<VisionCameraConfig> SavedCameras { get; } = [];
    public ObservableCollection<HikvisionCameraCandidate> DiscoveredCameras { get; } = [];
    public string CameraName { get => _cameraName; set => SetProperty(ref _cameraName, value); }
    public string ImageDirectory { get => _imageDirectory; set => SetProperty(ref _imageDirectory, value); }
    public int IntervalMilliseconds { get => _intervalMilliseconds; set => SetProperty(ref _intervalMilliseconds, value); }
    public bool Loop { get => _loop; set => SetProperty(ref _loop, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public VisionCameraConfig? SelectedSavedCamera
    {
        get => _selectedSavedCamera;
        set
        {
            if (!SetProperty(ref _selectedSavedCamera, value) || value is null) return;
            ApplyCamera(value);
        }
    }
    public VisionCameraSourceOption SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            RaisePropertyChanged(nameof(UsesDirectory));
            RaisePropertyChanged(nameof(UsesMvs));
            StatusText = value.DriverType == VisionCameraDriverTypes.HikvisionMvs
                ? "点击“自动扫描”选择真实或 MVS 虚拟海康相机"
                : "选择含 jpg、png 或 bmp 图片的目录作为连续模拟图像源";
        }
    }
    public HikvisionCameraCandidate? SelectedDiscoveredCamera
    {
        get => _selectedDiscoveredCamera;
        set
        {
            if (!SetProperty(ref _selectedDiscoveredCamera, value) || value is null) return;
            _serialNumber = value.SerialNumber; _ipAddress = value.IpAddress; CameraName = value.DisplayName;
        }
    }
    public bool UsesDirectory => SelectedSource.DriverType != VisionCameraDriverTypes.HikvisionMvs;
    public bool UsesMvs => SelectedSource.DriverType == VisionCameraDriverTypes.HikvisionMvs;
    public DelegateCommand BrowseCommand { get; }
    public DelegateCommand ScanCommand { get; }
    public DelegateCommand NewCameraCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DialogCloseListener RequestClose { get; }

    public VisionCameraDialogViewModel(IVisionConfigurationRepository repository,
        IHikvisionCameraDiscoveryService discovery, VisionInspectionEngine engine)
    {
        _repository = repository; _discovery = discovery; _engine = engine;
        BrowseCommand = new DelegateCommand(Browse);
        ScanCommand = new DelegateCommand(async () => await ScanAsync());
        NewCameraCommand = new DelegateCommand(StartNewCamera);
        SaveCommand = new DelegateCommand(async () => await SaveAsync());
        CancelCommand = new DelegateCommand(() => RequestClose.Invoke(ButtonResult.Cancel));
    }

    public bool CanCloseDialog() => true;
    public void OnDialogClosed() { }
    public async void OnDialogOpened(IDialogParameters parameters)
    {
        // 相机配置持久化在业务数据库中，打开弹窗时始终加载，后续无需重复添加。
        var cameras = await _repository.LoadCamerasAsync();
        SavedCameras.Clear();
        foreach (var item in cameras) SavedCameras.Add(item);
        parameters.TryGetValue("CameraId", out string? cameraId);
        SelectedSavedCamera = SavedCameras.FirstOrDefault(item => item.CameraId == cameraId) ?? SavedCameras.FirstOrDefault();
        if (SelectedSavedCamera is null) StartNewCamera();
    }

    private void ApplyCamera(VisionCameraConfig camera)
    {
        _cameraId = camera.CameraId; CameraName = camera.Name; ImageDirectory = camera.ImageDirectory;
        _serialNumber = camera.DeviceSerialNumber; _ipAddress = camera.DeviceIpAddress;
        IntervalMilliseconds = camera.IntervalMilliseconds; Loop = camera.Loop;
        SelectedSource = CameraSources.FirstOrDefault(item => item.DriverType == camera.DriverType) ?? CameraSources[0];
        StatusText = $"已加载相机“{camera.Name}”，修改后保存即可重新连接";
    }

    private void StartNewCamera()
    {
        _cameraId = "vision-camera-" + Guid.NewGuid().ToString("N")[..8];
        _selectedSavedCamera = null;
        RaisePropertyChanged(nameof(SelectedSavedCamera));
        CameraName = "视觉相机";
        ImageDirectory = string.Empty;
        _serialNumber = string.Empty;
        _ipAddress = string.Empty;
        IntervalMilliseconds = 1000;
        Loop = true;
        SelectedSource = CameraSourceOptions.All[1];
        DiscoveredCameras.Clear();
        SelectedDiscoveredCamera = null;
        StatusText = "正在创建新相机配置；保存后会进入已保存相机列表";
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择模拟相机图片目录", Multiselect = false };
        if (dialog.ShowDialog() != true) return;
        ImageDirectory = dialog.FolderName;
        StatusText = "已选择模拟图片目录，保存后将立即重新连接";
    }

    private async Task ScanAsync()
    {
        try
        {
            StatusText = "正在扫描海康相机...";
            var cameras = await _discovery.ScanAsync();
            DiscoveredCameras.Clear(); foreach (var camera in cameras) DiscoveredCameras.Add(camera);
            SelectedDiscoveredCamera = cameras.FirstOrDefault(item => item.IsVirtual) ?? cameras.FirstOrDefault();
            StatusText = cameras.Count == 0 ? "未发现海康相机" : $"发现 {cameras.Count} 台相机";
        }
        catch (Exception ex) { StatusText = "扫描失败：" + ex.Message; }
    }

    private async Task SaveAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CameraName)) throw new InvalidOperationException("请输入相机名称。");
            if (IntervalMilliseconds < 50) throw new InvalidOperationException("帧间隔不能小于 50 毫秒。");
            if (UsesDirectory)
            {
                if (!Directory.Exists(ImageDirectory)) throw new InvalidOperationException("请选择有效的模拟图片目录。");
                var hasImage = Directory.EnumerateFiles(ImageDirectory).Any(path =>
                    new[] { ".jpg", ".jpeg", ".png", ".bmp" }.Contains(Path.GetExtension(path).ToLowerInvariant()));
                if (!hasImage) throw new InvalidOperationException("模拟图片目录中没有 jpg、png 或 bmp 图片。");
            }
            if (UsesMvs && string.IsNullOrWhiteSpace(_serialNumber))
                throw new InvalidOperationException("请先自动扫描并选择一台海康相机。");

            StatusText = "正在保存配置并连接相机...";
            await _repository.UpsertCameraAsync(new VisionCameraConfig
            {
                CameraId = _cameraId, Name = CameraName.Trim(), DriverType = SelectedSource.DriverType,
                ImageDirectory = ImageDirectory.Trim(), DeviceSerialNumber = _serialNumber, DeviceIpAddress = _ipAddress,
                IntervalMilliseconds = IntervalMilliseconds, Loop = Loop, TriggerMode = VisionTriggerMode.Continuous, IsEnabled = true
            });
            await _engine.ReloadAsync();
            RequestClose.Invoke(new DialogParameters { { "CameraId", _cameraId } }, ButtonResult.OK);
        }
        catch (Exception ex) { StatusText = "保存失败：" + ex.Message; }
    }
}
