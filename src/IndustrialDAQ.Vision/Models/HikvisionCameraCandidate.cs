namespace IndustrialDAQ.Vision.Models;

/// <summary>海康 MVS 自动扫描得到的相机候选项。</summary>
public sealed record HikvisionCameraCandidate(
    string SerialNumber,
    string DisplayName,
    string ModelName,
    string IpAddress,
    string TransportLayer,
    bool IsVirtual)
{
    public string DisplayText => string.IsNullOrWhiteSpace(IpAddress)
        ? $"{DisplayName} · {SerialNumber} · {TransportLayer}"
        : $"{DisplayName} · {IpAddress} · {SerialNumber}";
}
