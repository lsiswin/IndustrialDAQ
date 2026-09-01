using IndustrialDAQ.Vision.Models;
using OpenCvSharp;

namespace IndustrialDAQ.Vision.Algorithms;

/// <summary>从合格样图裁剪 ROI，生成瓶盖检测模板。</summary>
public sealed class VisionTemplateTeachingService
{
    public async Task<string> CreateTemplateAsync(
        VisionFrame frame,
        VisionRoi roi,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = VisionImageProcessor.Decode(frame.EncodedImage);
        using var prepared = VisionImageProcessor.Prepare(source, roi);
        var fullPath = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var bytes = prepared.ImEncode(Path.GetExtension(fullPath) is ".jpg" or ".jpeg" ? ".jpg" : ".png");
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);
        return fullPath;
    }
}
