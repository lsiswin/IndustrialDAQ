using IndustrialDAQ.Vision.Algorithms;
using IndustrialDAQ.Vision.Models;
using OpenCvSharp;

namespace IndustrialDAQ.Tests;

public sealed class VisionOperatorPipelineTests
{
    [Fact]
    public async Task TemplateRecipe_ExecutesOpenCvOperatorsInOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), "industrialdaq-recipe-" + Guid.NewGuid().ToString("N"));
        var templatePath = Path.Combine(root, "template.png");
        var passBytes = CreatePartImage(true);
        var ngBytes = CreatePartImage(false);
        var roi = new VisionRoi(0.2, 0.2, 0.6, 0.6);
        await new VisionTemplateTeachingService().CreateTemplateAsync(
            new VisionFrame("teach", "camera", passBytes, DateTimeOffset.UtcNow), roi, templatePath);
        var roiOperator = VisionOperatorCatalog.Find("RoiCrop").CreateDefault(0);
        roiOperator.Parameters["X"] = "0.2"; roiOperator.Parameters["Y"] = "0.2";
        roiOperator.Parameters["Width"] = "0.6"; roiOperator.Parameters["Height"] = "0.6";
        var templateOperator = VisionOperatorCatalog.Find("TemplateMatch").CreateDefault(1);
        templateOperator.Parameters["TemplatePath"] = templatePath;
        templateOperator.Parameters["MinScore"] = "0.92";
        var task = new VisionInspectionTask
        {
            TaskId = "recipe", CameraId = "camera", Name = "顺序配方",
            AlgorithmType = VisionOperatorPipelineAlgorithm.TypeName,
            Operators = [roiOperator, templateOperator]
        };
        var algorithm = new VisionOperatorPipelineAlgorithm();

        var pass = await algorithm.InspectAsync(new VisionFrame("pass", "camera", passBytes, DateTimeOffset.UtcNow), task);
        var ng = await algorithm.InspectAsync(new VisionFrame("ng", "camera", ngBytes, DateTimeOffset.UtcNow), task);

        Assert.True(pass.IsPass);
        Assert.False(ng.IsPass, $"NG score={ng.MatchScore:F6}; pass score={pass.MatchScore:F6}");
        Assert.True(pass.MatchScore > ng.MatchScore);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(10, false)]
    public async Task BrightnessOperator_UsesOpenCvMean(int grayValue, bool expectedPass)
    {
        using var image = new Mat(new Size(80, 80), MatType.CV_8UC1, new Scalar(grayValue));
        var task = new VisionInspectionTask
        {
            TaskId = "brightness", CameraId = "camera", Name = "亮度检测",
            AlgorithmType = VisionOperatorPipelineAlgorithm.TypeName,
            Operators = [VisionOperatorCatalog.Find("Brightness").CreateDefault(0)]
        };

        var result = await new VisionOperatorPipelineAlgorithm().InspectAsync(
            new VisionFrame("frame", "camera", image.ImEncode(".png"), DateTimeOffset.UtcNow), task);

        Assert.Equal(expectedPass, result.IsPass);
    }

    private static byte[] CreatePartImage(bool hasFeature)
    {
        using var image = new Mat(new Size(200, 200), MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(55, 55, 90, 110), new Scalar(70, 70, 70), -1);
        if (hasFeature)
        {
            Cv2.Circle(image, new Point(100, 80), 32, new Scalar(230, 230, 230), -1);
            Cv2.Line(image, new Point(75, 80), new Point(125, 80), new Scalar(110, 110, 110), 5);
        }
        return image.ImEncode(".png");
    }
}
