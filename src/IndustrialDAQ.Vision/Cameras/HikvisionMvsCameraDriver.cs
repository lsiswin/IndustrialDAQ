using System.Reflection;
using System.Runtime.CompilerServices;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Cameras;

/// <summary>使用运行时加载的海康 MVS SDK 连接真实或虚拟相机并输出 JPEG 图像帧。</summary>
public sealed class HikvisionMvsCameraDriver : IVisionCameraDriver
{
    private readonly VisionCameraConfig _config;
    private readonly SemaphoreSlim _grabLock = new(1, 1);
    private HikvisionMvsRuntime? _runtime;
    private object? _device;
    private object? _streamGrabber;

    public HikvisionMvsCameraDriver(VisionCameraConfig config) => _config = config;
    public string CameraId => _config.CameraId;
    public bool IsConnected => ReadBoolean(_device, "IsConnected");

    public async IAsyncEnumerable<VisionFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await TriggerAsync(cancellationToken);
            if (frame is not null) yield return frame;
            await Task.Delay(Math.Max(50, _config.IntervalMilliseconds), cancellationToken);
        }
    }

    public async ValueTask<VisionFrame?> TriggerAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        await _grabLock.WaitAsync(cancellationToken);
        try { return await Task.Run(GrabFrame, cancellationToken); }
        finally { _grabLock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        try { Invoke(_streamGrabber, "StopGrabbing"); } catch { }
        try { Invoke(_device, "Close"); } catch { }
        _streamGrabber = null;
        _device = null;
        _grabLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_config.DeviceSerialNumber))
            throw new InvalidOperationException("海康相机配置缺少序列号，请重新自动扫描并选择设备。");
        if (!HikvisionMvsRuntime.TryLoad(out _runtime, out var error)) throw new InvalidOperationException(error);
        _device = _runtime!.CreateDeviceBySerialNumber(_config.DeviceSerialNumber);
        EnsureSuccess(Invoke(_device, "Open"), "打开相机");
        _streamGrabber = ReadProperty(_device, "StreamGrabber");
        EnsureSuccess(Invoke(_streamGrabber, "StartGrabbing"), "启动取流");
        return Task.CompletedTask;
    }

    private VisionFrame? GrabFrame()
    {
        var getBuffer = _streamGrabber!.GetType().GetMethod("GetImageBuffer")!;
        object?[] arguments = [(uint)Math.Max(500, _config.IntervalMilliseconds * 2), null];
        var code = Convert.ToInt32(getBuffer.Invoke(_streamGrabber, arguments));
        if (code != 0 || arguments[1] is null) return null;
        var frameOut = arguments[1]!;
        try
        {
            var image = ReadProperty(frameOut, "Image");
            var imageSize = Convert.ToUInt64(ReadProperty(image, "ImageSize"));
            var buffer = new byte[Math.Max(1024 * 1024, checked((int)Math.Min(int.MaxValue, imageSize * 4 + 65536)))];
            var formatInfo = Activator.CreateInstance(_runtime!.RequireType("MvCameraControl.ImageFormatInfo"))!;
            formatInfo.GetType().GetField("FormatType")!.SetValue(formatInfo, _runtime.EnumValue("MvCameraControl.ImageFormatType", "Jpeg"));
            formatInfo.GetType().GetField("JpegQuality")!.SetValue(formatInfo, (uint)90);
            var saver = ReadProperty(_device!, "ImageSaver");
            var saveMethod = saver.GetType().GetMethods().Single(method =>
                method.Name == "SaveImageToBuffer" &&
                method.GetParameters()[0].ParameterType == typeof(byte[]) &&
                method.GetParameters()[1].ParameterType.GetElementType() == typeof(uint));
            object?[] saveArguments = [buffer, (uint)0, image, formatInfo, _runtime.EnumValue("MvCameraControl.CFAMethod", "Fast")];
            EnsureSuccess(saveMethod.Invoke(saver, saveArguments), "转换相机图像");
            var length = checked((int)Convert.ToUInt32(saveArguments[1]));
            return new VisionFrame($"{CameraId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}", CameraId,
                buffer[..length], DateTimeOffset.UtcNow, $"hikvision://{_config.DeviceSerialNumber}");
        }
        finally { Invoke(_streamGrabber, "FreeImageBuffer", frameOut); }
    }

    private static object ReadProperty(object source, string name) =>
        source.GetType().GetProperty(name)?.GetValue(source) ?? throw new MissingMemberException(source.GetType().Name, name);
    private static bool ReadBoolean(object? source, string name) => source is not null && ReadProperty(source, name) is true;
    private static object? Invoke(object? source, string name, params object?[] arguments) =>
        source?.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, arguments.Select(item => item?.GetType() ?? typeof(object)).ToArray(), null)?.Invoke(source, arguments)
        ?? source?.GetType().GetMethods().FirstOrDefault(method => method.Name == name && method.GetParameters().Length == arguments.Length)?.Invoke(source, arguments);
    private static void EnsureSuccess(object? result, string action)
    {
        var code = Convert.ToInt32(result);
        if (code != 0) throw new InvalidOperationException($"海康 MVS {action}失败，错误码 0x{code:X8}。");
    }
}
