using System.Collections.Concurrent;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Cameras;
using IndustrialDAQ.Vision.Models;
using IndustrialDAQ.Vision.Storage;
using IndustrialDAQ.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IndustrialDAQ.Vision.Runtime;

/// <summary>配置驱动的视觉运行引擎，负责相机取图、算法执行、NG 存图和结果发布。</summary>
public sealed class VisionInspectionEngine : IHostedService
{
    private readonly IVisionConfigurationRepository _repository;
    private readonly IReadOnlyDictionary<string, IVisionInspectionAlgorithm> _algorithms;
    private readonly VisionResultPublisher _publisher;
    private readonly VisionResourceSynchronizer _resourceSynchronizer;
    private readonly NgImageStorageService _ngStorage;
    private readonly HistoryWriter _historyWriter;
    private readonly ILogger<VisionInspectionEngine> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IVisionCameraDriver> _drivers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private List<Task> _cameraLoops = [];
    private readonly HashSet<string> _registeredTagIds = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isPaused;

    public VisionInspectionEngine(
        IVisionConfigurationRepository repository,
        IEnumerable<IVisionInspectionAlgorithm> algorithms,
        VisionResultPublisher publisher,
        VisionResourceSynchronizer resourceSynchronizer,
        NgImageStorageService ngStorage,
        HistoryWriter historyWriter,
        ILogger<VisionInspectionEngine> logger)
    {
        _repository = repository;
        _algorithms = algorithms.ToDictionary(item => item.AlgorithmType, StringComparer.OrdinalIgnoreCase);
        _publisher = publisher;
        _resourceSynchronizer = resourceSynchronizer;
        _ngStorage = ngStorage;
        _historyWriter = historyWriter;
        _logger = logger;
    }

    public event EventHandler<VisionInspectionResult>? ResultProduced;
    public event EventHandler<VisionInspectionCompletedEventArgs>? InspectionCompleted;
    public bool IsPaused => _isPaused;

    public void Pause() => _isPaused = true;
    public void Resume() => _isPaused = false;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await ReloadAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        try { await Task.WhenAll(_cameraLoops).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
        foreach (var driver in _drivers.Values) await driver.DisposeAsync();
        _drivers.Clear();
        _cameraLoops.Clear();
        _cts?.Dispose();
        _cts = null;
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            if (_cameraLoops.Count > 0)
            {
                _cts?.Cancel();
                try { await Task.WhenAll(_cameraLoops); } catch (OperationCanceledException) { }
                foreach (var driver in _drivers.Values) await driver.DisposeAsync();
                _drivers.Clear();
                _cameraLoops.Clear();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }

            var cameras = await _repository.LoadCamerasAsync(cancellationToken);
            var tasks = await _repository.LoadTasksAsync(cancellationToken);
            await _resourceSynchronizer.SyncAsync(cameras, tasks, cancellationToken);
            _historyWriter.UnregisterTags(_registeredTagIds);
            _registeredTagIds.Clear();
            var visionTags = tasks.Where(item => item.IsEnabled).SelectMany(VisionTagCatalog.CreateTagPoints).ToArray();
            _historyWriter.RegisterTags(visionTags);
            foreach (var tag in visionTags) _registeredTagIds.Add(tag.Id);
            if (_cts is null) return;

            foreach (var camera in cameras.Where(item => item.IsEnabled))
            {
                var cameraTasks = tasks.Where(item => item.IsEnabled && item.CameraId == camera.CameraId).ToArray();
                if (cameraTasks.Length == 0) continue;
                var driver = CreateDriver(camera);
                _drivers[camera.CameraId] = driver;
                foreach (var task in cameraTasks) await _publisher.PublishStatusAsync(task, driver.IsConnected, true, cancellationToken);
                if (camera.TriggerMode == VisionTriggerMode.Continuous)
                    _cameraLoops.Add(Task.Run(() => RunCameraAsync(driver, cameraTasks, _cts.Token), _cts.Token));
            }
            _logger.LogInformation("视觉引擎已加载 {CameraCount} 台相机、{TaskCount} 个任务", cameras.Count, tasks.Count);
        }
        finally { _reloadLock.Release(); }
    }

    public async Task<VisionInspectionResult?> TriggerAsync(string cameraId, CancellationToken cancellationToken = default)
    {
        if (!_drivers.TryGetValue(cameraId, out var driver)) return null;
        var frame = await driver.TriggerAsync(cancellationToken);
        if (frame is null) return null;
        var tasks = (await _repository.LoadTasksAsync(cancellationToken)).Where(item => item.IsEnabled && item.CameraId == cameraId).ToArray();
        VisionInspectionResult? last = null;
        foreach (var task in tasks) last = await InspectAsync(frame, task, cancellationToken);
        return last;
    }

    private async Task RunCameraAsync(IVisionCameraDriver driver, IReadOnlyList<VisionInspectionTask> tasks, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in driver.CaptureAsync(cancellationToken))
            {
                if (_isPaused) continue;
                foreach (var task in tasks) await InspectAsync(frame, task, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "视觉相机 {CameraId} 运行失败", driver.CameraId);
            foreach (var task in tasks) await _publisher.PublishStatusAsync(task, false, false, CancellationToken.None);
        }
    }

    private async Task<VisionInspectionResult> InspectAsync(VisionFrame frame, VisionInspectionTask task, CancellationToken cancellationToken)
    {
        if (!_algorithms.TryGetValue(task.AlgorithmType, out var algorithm))
            throw new InvalidOperationException($"未注册视觉算法：{task.AlgorithmType}");
        var result = await algorithm.InspectAsync(frame, task, cancellationToken);
        if (!result.IsPass && task.SaveNgImage)
        {
            var imagePath = await _ngStorage.SaveAsync(frame, task, cancellationToken);
            result = CopyWithImagePath(result, imagePath);
        }
        await _repository.SaveResultAsync(result, cancellationToken);
        await _publisher.PublishResultAsync(task, result, cancellationToken);
        ResultProduced?.Invoke(this, result);
        InspectionCompleted?.Invoke(this, new VisionInspectionCompletedEventArgs(frame, result));
        return result;
    }

    private static IVisionCameraDriver CreateDriver(VisionCameraConfig camera) =>
        camera.DriverType switch
        {
            VisionCameraDriverTypes.Directory => new DirectoryCameraDriver(camera),
            VisionCameraDriverTypes.HikvisionSimulator => new DirectoryCameraDriver(camera),
            VisionCameraDriverTypes.HikvisionMvs => new HikvisionMvsCameraDriver(camera),
            _ => throw new NotSupportedException($"暂不支持视觉相机驱动：{camera.DriverType}")
        };

    private static VisionInspectionResult CopyWithImagePath(VisionInspectionResult source, string imagePath) => new()
    {
        RecordId = source.RecordId, TaskId = source.TaskId, CameraId = source.CameraId,
        FrameId = source.FrameId, Timestamp = source.Timestamp, IsPass = source.IsPass,
        CapPresent = source.CapPresent, MatchScore = source.MatchScore,
        ProcessingTimeMilliseconds = source.ProcessingTimeMilliseconds,
        ImagePath = imagePath, FailureReason = source.FailureReason
    };
}

/// <summary>携带原图和检测结果的 UI 通知，运行时持久化仍只保存结果与 NG 路径。</summary>
public sealed class VisionInspectionCompletedEventArgs : EventArgs
{
    public VisionFrame Frame { get; }
    public VisionInspectionResult Result { get; }
    public VisionInspectionCompletedEventArgs(VisionFrame frame, VisionInspectionResult result) =>
        (Frame, Result) = (frame, result);
}
