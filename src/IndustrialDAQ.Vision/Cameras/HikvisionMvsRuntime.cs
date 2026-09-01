using System.Collections;
using System.Reflection;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Cameras;

/// <summary>按需加载海康 MVS SDK，避免将厂商安装路径固化为编译依赖。</summary>
internal sealed class HikvisionMvsRuntime
{
    private const int SupportedTransportLayers = 1 | 4 | 16 | 32;
    private readonly Assembly _assembly;

    private HikvisionMvsRuntime(Assembly assembly)
    {
        _assembly = assembly;
        InvokeStatic("MvCameraControl.SDKSystem", "Initialize");
    }

    public static bool TryLoad(out HikvisionMvsRuntime? runtime, out string error)
    {
        runtime = null;
        foreach (var path in CandidateSdkPaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                runtime = new HikvisionMvsRuntime(Assembly.LoadFrom(path));
                error = string.Empty;
                return true;
            }
            catch (Exception ex) { error = $"海康 MVS SDK 加载失败：{ex.GetBaseException().Message}"; return false; }
        }
        error = "未找到 MvCameraControl.Net.dll，请安装海康 MVS 客户端或设置 HIKVISION_MVS_SDK_PATH。";
        return false;
    }

    public IReadOnlyList<(object DeviceInfo, HikvisionCameraCandidate Candidate)> Enumerate()
    {
        var layerType = Enum.ToObject(RequireType("MvCameraControl.DeviceTLayerType"), SupportedTransportLayers);
        var method = RequireType("MvCameraControl.DeviceEnumerator").GetMethod("EnumDevices", BindingFlags.Public | BindingFlags.Static)
                     ?? throw new MissingMethodException("DeviceEnumerator.EnumDevices");
        object?[] arguments = [layerType, null];
        var result = Convert.ToInt32(method.Invoke(null, arguments));
        if (result != 0) throw new InvalidOperationException($"海康相机扫描失败，错误码 0x{result:X8}。");

        var devices = new List<(object, HikvisionCameraCandidate)>();
        if (arguments[1] is not IEnumerable deviceInfos) return devices;
        foreach (var deviceInfo in deviceInfos)
        {
            if (deviceInfo is null) continue;
            var serial = ReadString(deviceInfo, "SerialNumber");
            var model = ReadString(deviceInfo, "ModelName");
            var userName = ReadString(deviceInfo, "UserDefinedName");
            var transport = ReadString(deviceInfo, "TLayerType");
            var ipAddress = TryReadIpAddress(deviceInfo);
            var isVirtual = ReadBoolean(deviceInfo, "VirtualDevice") || transport.Contains("Vir", StringComparison.OrdinalIgnoreCase);
            var displayName = string.IsNullOrWhiteSpace(userName) ? model : userName;
            devices.Add((deviceInfo, new HikvisionCameraCandidate(serial, displayName, model, ipAddress, transport, isVirtual)));
        }
        return devices;
    }

    public object CreateDeviceBySerialNumber(string serialNumber)
    {
        var selected = Enumerate().FirstOrDefault(item =>
            string.Equals(item.Candidate.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));
        if (selected.DeviceInfo is null) throw new InvalidOperationException($"未扫描到海康相机：{serialNumber}");
        var factory = RequireType("MvCameraControl.DeviceFactory");
        return factory.GetMethod("CreateDevice", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [selected.DeviceInfo])
               ?? throw new InvalidOperationException("海康 MVS 创建设备失败。");
    }

    public Type RequireType(string name) => _assembly.GetType(name, true)!;
    public object EnumValue(string typeName, string value) => Enum.Parse(RequireType(typeName), value);

    private void InvokeStatic(string typeName, string methodName) =>
        RequireType(typeName).GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);

    private static IEnumerable<string> CandidateSdkPaths()
    {
        var explicitPath = Environment.GetEnvironmentVariable("HIKVISION_MVS_SDK_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath)) yield return explicitPath;
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"D:\Program Files" };
        foreach (var root in roots.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "MVS", "Development", "DotNet", "AnyCpu", "netstandard2.0", "MvCameraControl.Net.dll");
            yield return Path.Combine(root, "MVS", "Development", "DotNet", "win64", "netstandard2.0", "MvCameraControl.Net.dll");
        }
    }

    private static string ReadString(object source, string property) =>
        source.GetType().GetProperty(property)?.GetValue(source)?.ToString() ?? string.Empty;
    private static bool ReadBoolean(object source, string property) =>
        source.GetType().GetProperty(property)?.GetValue(source) as bool? ?? false;
    private static string TryReadIpAddress(object source)
    {
        var value = source.GetType().GetProperty("CurrentIp")?.GetValue(source);
        if (value is not uint ip || ip == 0) return string.Empty;
        return $"{(ip >> 24) & 255}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";
    }
}
