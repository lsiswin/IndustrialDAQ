using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Abstractions;

/// <summary>发现本机海康 MVS 可访问的 GigE、USB 与虚拟相机。</summary>
public interface IHikvisionCameraDiscoveryService
{
    Task<IReadOnlyList<HikvisionCameraCandidate>> ScanAsync(CancellationToken cancellationToken = default);
}
