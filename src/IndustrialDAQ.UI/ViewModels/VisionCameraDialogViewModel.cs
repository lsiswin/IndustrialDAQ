using System.Collections.ObjectModel;
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
    private string _statusText = "配置相机后即可在工作台实时预览，无需先创建配方";

    public string Title => "相机配置";
    public IReadOnlyList<VisionCameraSourceOption> CameraSources { get; } = CameraSourceOptions.All;
    public ObservableCollection<HikvisionCameraCandidate> DiscoveredCameras { get; } = [];
    public string CameraName { get => _cameraName; set => SetProperty(ref _cameraName, value); }
    public string ImageDirectory { get => _imageDirectory; set => SetProperty(ref _imageDirectory, value); }
    public int IntervalMilliseconds { get => _intervalMilliseconds; set => SetProperty(ref _intervalMilliseconds, value); }
    public bool Loop { get => _loop; set => SetProperty(ref _loop, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public VisionCameraSourceOption SelectedSource
    {
        get => _selectedSource;
        set { if (SetProperty(ref _selectedSource, value)) { RaisePropertyChanged(nameof(UsesDirectory)); RaisePropertyChanged(nameof(UsesMvs)); } }
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
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DialogCloseListener RequestClose { get; }

    public VisionCameraDialogViewModel(IVisionConfigurationRepository repository,
        IHikvisionCameraDiscoveryService discovery, VisionInspectionEngine engine)
    {
        _repository = repository; _discovery = discovery; _engine = engine;
        BrowseCommand = new DelegateCommand(Browse);
        ScanCommand = new DelegateCommand(async () => await ScanAsync());
        SaveCommand = new DelegateCommand(async () => await SaveAsync());
        CancelCommand = new DelegateCommand(() => RequestClose.Invoke(ButtonResult.Cancel));
    }

    public bool CanCloseDialog() => true;
    public void OnDialogClosed() { }
    public async void OnDialogOpened(IDialogParameters parameters)
    {
        if (!parameters.TryGetValue("CameraId", out string? cameraId) || string.IsNullOrWhiteSpace(cameraId)) return;
        var camera = (await _repository.LoadCamerasAsync()).FirstOrDefault(item => item.CameraId == cameraId);
        if (camera is null) return;
        _cameraId = camera.CameraId; CameraName = camera.Name; ImageDirectory = camera.ImageDirectory;
        _serialNumber = camera.DeviceSerialNumber; _ipAddress = camera.DeviceIpAddress;
        IntervalMilliseconds = camera.IntervalMilliseconds; Loop = camera.Loop;
        SelectedSource = CameraSources.FirstOrDefault(item => item.DriverType == camera.DriverType) ?? CameraSources[0];
        StatusText = "已加载相机配置，保存后运行时会自动重新连接";
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "选择模拟相机图片目录", Multiselect = false };
        if (dialog.ShowDialog() == true) ImageDirectory = dialog.FolderName;
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
