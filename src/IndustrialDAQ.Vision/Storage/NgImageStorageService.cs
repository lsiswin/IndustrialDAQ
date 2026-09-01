using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.Vision.Storage;

/// <summary>按任务和日期保存 NG 原图，数据库只记录文件路径。</summary>
public sealed class NgImageStorageService
{
    private readonly string _rootDirectory;
    public NgImageStorageService(string rootDirectory) => _rootDirectory = Path.GetFullPath(rootDirectory);

    public async Task<string> SaveAsync(
        VisionFrame frame,
        VisionInspectionTask task,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(_rootDirectory, Sanitize(task.TaskId), frame.Timestamp.ToLocalTime().ToString("yyyyMMdd"));
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(frame.SourcePath) is { Length: > 1 } sourceExtension ? sourceExtension : ".jpg";
        var path = Path.Combine(directory, $"{frame.Timestamp:HHmmssfff}-{Sanitize(frame.FrameId)}{extension}");
        await File.WriteAllBytesAsync(path, frame.EncodedImage, cancellationToken);
        return path;
    }

    private static string Sanitize(string value) => string.Concat(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
}
