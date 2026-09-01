using IndustrialDAQ.Vision.Cameras;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Tests;

/// <summary>验证已安装的海康 MVS SDK 可以被动态加载并执行设备枚举。</summary>
public sealed class HikvisionCameraDiscoveryTests
{
    [Fact]
    public async Task InstalledMvsSdk_CanEnumerateRealAndVirtualDevices()
    {
        var sdkPath = @"D:\Program Files\MVS\Development\DotNet\AnyCpu\netstandard2.0\MvCameraControl.Net.dll";
        if (!File.Exists(sdkPath)) return;

        Environment.SetEnvironmentVariable("HIKVISION_MVS_SDK_PATH", sdkPath);
        var cameras = await new HikvisionCameraDiscoveryService().ScanAsync();

        Assert.NotNull(cameras);
        Assert.All(cameras, camera => Assert.False(string.IsNullOrWhiteSpace(camera.SerialNumber)));
    }

    [Fact]
    public async Task RunningMvsVirtualCamera_CanProduceJpegFrame()
    {
        var sdkPath = @"D:\Program Files\MVS\Development\DotNet\AnyCpu\netstandard2.0\MvCameraControl.Net.dll";
        if (!File.Exists(sdkPath)) return;
        Environment.SetEnvironmentVariable("HIKVISION_MVS_SDK_PATH", sdkPath);
        var candidate = (await new HikvisionCameraDiscoveryService().ScanAsync()).FirstOrDefault(item => item.IsVirtual);
        if (candidate is null) return;

        await using var camera = new HikvisionMvsCameraDriver(new VisionCameraConfig
        {
            CameraId = "hikvision-virtual-test", Name = candidate.DisplayName,
            DriverType = VisionCameraDriverTypes.HikvisionMvs,
            DeviceSerialNumber = candidate.SerialNumber, DeviceIpAddress = candidate.IpAddress,
            TriggerMode = VisionTriggerMode.Software, IntervalMilliseconds = 1000
        });
        var frame = await camera.TriggerAsync();

        Assert.NotNull(frame);
        Assert.True(frame.EncodedImage.Length > 100);
        Assert.Equal(0xFF, frame.EncodedImage[0]);
        Assert.Equal(0xD8, frame.EncodedImage[1]);
    }
}
