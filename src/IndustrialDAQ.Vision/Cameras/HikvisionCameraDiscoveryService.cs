using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Cameras;

/// <summary>通过海康 MVS SDK 扫描真实相机和 MVS 虚拟相机。</summary>
public sealed class HikvisionCameraDiscoveryService : IHikvisionCameraDiscoveryService
{
    public Task<IReadOnlyList<HikvisionCameraCandidate>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<HikvisionCameraCandidate>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HikvisionMvsRuntime.TryLoad(out var runtime, out var error))
                throw new InvalidOperationException(error);
            return runtime!.Enumerate().Select(item => item.Candidate).OrderByDescending(item => item.IsVirtual).ThenBy(item => item.DisplayName).ToArray();
        }, cancellationToken);
}
