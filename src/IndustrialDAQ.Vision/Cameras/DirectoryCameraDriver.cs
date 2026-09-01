using System.Runtime.CompilerServices;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Cameras;

/// <summary>按文件名顺序播放图片的模拟相机，用于无硬件环境和回归测试。</summary>
public sealed class DirectoryCameraDriver : IVisionCameraDriver
{
    private static readonly HashSet<string> Extensions = [".jpg", ".jpeg", ".png", ".bmp"];
    private readonly VisionCameraConfig _config;
    private readonly SemaphoreSlim _triggerLock = new(1, 1);
    private int _nextIndex;

    public DirectoryCameraDriver(VisionCameraConfig config) => _config = config;
    public string CameraId => _config.CameraId;
    public bool IsConnected => LoadImagePaths().Count > 0;

    public async IAsyncEnumerable<VisionFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        do
        {
            var paths = LoadImagePaths();
            if (paths.Count == 0) yield break;

            while (_nextIndex < paths.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = await ReadFrameAsync(paths[_nextIndex++], cancellationToken);
                yield return frame;
                await Task.Delay(Math.Max(50, _config.IntervalMilliseconds), cancellationToken);
            }

            if (_config.Loop) _nextIndex = 0;
        } while (_config.Loop && !cancellationToken.IsCancellationRequested);
    }

    public async ValueTask<VisionFrame?> TriggerAsync(CancellationToken cancellationToken = default)
    {
        await _triggerLock.WaitAsync(cancellationToken);
        try
        {
            var paths = LoadImagePaths();
            if (paths.Count == 0) return null;
            if (_nextIndex >= paths.Count) _nextIndex = _config.Loop ? 0 : paths.Count - 1;
            return await ReadFrameAsync(paths[_nextIndex++], cancellationToken);
        }
        finally { _triggerLock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _triggerLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<string> LoadImagePaths()
    {
        if (string.IsNullOrWhiteSpace(_config.ImageDirectory) || !Directory.Exists(_config.ImageDirectory)) return [];
        return Directory.EnumerateFiles(_config.ImageDirectory)
            .Where(path => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private VisionFrame CreateFrame(string path, byte[] bytes) => new(
        $"{CameraId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}",
        CameraId, bytes, DateTimeOffset.UtcNow, path);

    private async Task<VisionFrame> ReadFrameAsync(string path, CancellationToken cancellationToken) =>
        CreateFrame(path, await File.ReadAllBytesAsync(path, cancellationToken));
}
