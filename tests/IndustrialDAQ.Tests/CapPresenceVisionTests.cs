using IndustrialDAQ.Vision.Algorithms;
using IndustrialDAQ.Vision.Cameras;
using IndustrialDAQ.Vision.Models;
using OpenCvSharp;

namespace IndustrialDAQ.Tests;

public sealed class CapPresenceVisionTests
{
    [Fact]
    public async Task TaughtTemplate_DistinguishesCapFromMissingCap()
    {
        var root = Path.Combine(Path.GetTempPath(), "industrialdaq-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var capBytes = CreateBottleImage(hasCap: true);
        var missingBytes = CreateBottleImage(hasCap: false);
        var templatePath = Path.Combine(root, "template.png");
        var teacher = new VisionTemplateTeachingService();
        var roi = new VisionRoi(0.25, 0.25, 0.5, 0.5);
        await teacher.CreateTemplateAsync(new VisionFrame("teach", "camera", capBytes, DateTimeOffset.UtcNow), roi, templatePath);
        var task = new VisionInspectionTask
        {
            TaskId = "cap-task", CameraId = "camera", Name = "瓶盖有无",
            Roi = roi, MatchThreshold = 0.75, TemplateImagePath = templatePath
        };
        var algorithm = new CapPresenceTemplateAlgorithm();

        var pass = await algorithm.InspectAsync(new VisionFrame("pass", "camera", capBytes, DateTimeOffset.UtcNow), task);
        var ng = await algorithm.InspectAsync(new VisionFrame("ng", "camera", missingBytes, DateTimeOffset.UtcNow), task);

        Assert.True(pass.CapPresent);
        Assert.True(pass.IsPass);
        Assert.False(ng.CapPresent);
        Assert.False(ng.IsPass);
        Assert.True(pass.MatchScore > ng.MatchScore);
    }

    [Fact]
    public async Task DirectoryCamera_ReadsSupportedImagesInFileNameOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "industrialdaq-camera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "02.png"), CreateBottleImage(false));
        await File.WriteAllBytesAsync(Path.Combine(root, "01.png"), CreateBottleImage(true));
        await File.WriteAllTextAsync(Path.Combine(root, "ignore.txt"), "not an image");
        await using var camera = new DirectoryCameraDriver(new VisionCameraConfig
        {
            CameraId = "camera", Name = "目录相机", ImageDirectory = root,
            TriggerMode = VisionTriggerMode.Software, Loop = false
        });

        var first = await camera.TriggerAsync();
        var second = await camera.TriggerAsync();

        Assert.EndsWith("01.png", first!.SourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("02.png", second!.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CreateBottleImage(bool hasCap)
    {
        using var image = new Mat(new Size(200, 200), MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(65, 70, 70, 120), new Scalar(70, 70, 70), -1);
        if (hasCap)
        {
            Cv2.Circle(image, new Point(100, 65), 35, new Scalar(230, 230, 230), -1);
            Cv2.Circle(image, new Point(100, 65), 24, new Scalar(120, 120, 120), 4);
        }
        return image.ImEncode(".png");
    }
}
