using System.Diagnostics;
using System.Globalization;
using IndustrialDAQ.Vision.Abstractions;
using IndustrialDAQ.Vision.Models;
using OpenCvSharp;

namespace IndustrialDAQ.Vision.Algorithms;

/// <summary>按配方顺序逐个执行视觉算子，任一判定算子失败都会使最终任务判为 NG。</summary>
public sealed class VisionOperatorPipelineAlgorithm : IVisionInspectionAlgorithm
{
    public const string TypeName = "OperatorPipeline";
    public string AlgorithmType => TypeName;

    public ValueTask<VisionInspectionResult> InspectAsync(
        VisionFrame frame, VisionInspectionTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var failures = new List<string>();
        var score = 1d;
        try
        {
            using var source = VisionImageProcessor.Decode(frame.EncodedImage);
            using var context = new PipelineImageContext(source.Clone());
            foreach (var definition in task.Operators.Where(item => item.IsEnabled).OrderBy(item => item.Order))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Execute(definition, context, failures, ref score);
            }
            var passed = failures.Count == 0;
            return ValueTask.FromResult(Result(frame, task, passed, score, stopwatch.Elapsed.TotalMilliseconds, string.Join("；", failures)));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(Result(frame, task, false, 0, stopwatch.Elapsed.TotalMilliseconds, ex.Message));
        }
    }

    private static void Execute(
        VisionOperatorDefinition definition, PipelineImageContext context, List<string> failures, ref double score)
    {
        switch (definition.OperatorType)
        {
            case "RoiCrop": Crop(definition, context); break;
            case "Grayscale": context.Replace(ToGray(context.Image)); break;
            case "GaussianBlur": Blur(definition, context); break;
            case "BinaryThreshold": Threshold(definition, context); break;
            case "TemplateMatch": TemplateMatch(definition, context, failures, ref score); break;
            case "BlobCount": BlobCount(definition, context, failures); break;
            case "EdgeDensity": EdgeDensity(definition, context, failures); break;
            case "Brightness": Brightness(definition, context, failures); break;
            default: throw new InvalidOperationException($"未注册视觉算子：{definition.OperatorType}");
        }
    }

    private static void Crop(VisionOperatorDefinition definition, PipelineImageContext context)
    {
        var roi = new VisionRoi(D(definition, "X"), D(definition, "Y"), D(definition, "Width"), D(definition, "Height"));
        context.Replace(new Mat(context.Image, VisionImageProcessor.ToPixelRect(roi, context.Image.Size())).Clone());
    }

    private static void Blur(VisionOperatorDefinition definition, PipelineImageContext context)
    {
        var size = Math.Max(1, I(definition, "KernelSize"));
        if (size % 2 == 0) size++;
        var output = new Mat(); Cv2.GaussianBlur(context.Image, output, new Size(size, size), 0); context.Replace(output);
    }

    private static void Threshold(VisionOperatorDefinition definition, PipelineImageContext context)
    {
        using var gray = ToGray(context.Image);
        var output = new Mat();
        Cv2.Threshold(gray, output, D(definition, "Threshold"), 255,
            B(definition, "Invert") ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary);
        context.Replace(output);
    }

    private static void TemplateMatch(
        VisionOperatorDefinition definition, PipelineImageContext context, List<string> failures, ref double score)
    {
        var path = S(definition, "TemplatePath");
        if (!File.Exists(path)) throw new FileNotFoundException("模板匹配算子尚未采集模板。", path);
        using var search = ToGray(context.Image);
        Cv2.GaussianBlur(search, search, new Size(5, 5), 0); Cv2.EqualizeHist(search, search);
        using var template = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (template.Empty()) throw new InvalidOperationException("模板图片读取失败。");
        using var resized = ResizeTemplate(template, search.Size());
        using var response = new Mat(); Cv2.MatchTemplate(search, resized, response, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(response, out _, out score, out _, out _); score = double.IsFinite(score) ? Math.Clamp(score, 0, 1) : 0;
        var minimum = D(definition, "MinScore");
        if (score < minimum) failures.Add($"模板匹配 {score:F3} < {minimum:F3}");
    }

    private static void BlobCount(VisionOperatorDefinition definition, PipelineImageContext context, List<string> failures)
    {
        using var binary = EnsureBinary(context.Image);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);
        // OpenCV 的第 0 行是背景，仅统计满足面积范围的前景连通域。
        var count = 0; var minArea = D(definition, "MinArea"); var maxArea = D(definition, "MaxArea");
        var rows = stats.Rows;
        for (var row = 1; row < rows; row++)
        {
            var area = stats.At<int>(row, (int)ConnectedComponentsTypes.Area);
            if (area >= minArea && area <= maxArea) count++;
        }
        var minimum = I(definition, "MinCount"); var maximum = I(definition, "MaxCount");
        if (count < minimum || count > maximum) failures.Add($"斑点数量 {count} 不在 {minimum}~{maximum}");
    }

    private static void EdgeDensity(VisionOperatorDefinition definition, PipelineImageContext context, List<string> failures)
    {
        using var gray = ToGray(context.Image); using var edges = new Mat();
        Cv2.Canny(gray, edges, D(definition, "CannyLow"), D(definition, "CannyHigh"));
        var density = Cv2.CountNonZero(edges) / (double)(edges.Rows * edges.Cols);
        var minimum = D(definition, "MinDensity"); var maximum = D(definition, "MaxDensity");
        if (density < minimum || density > maximum) failures.Add($"边缘密度 {density:F3} 不在 {minimum:F3}~{maximum:F3}");
    }

    private static void Brightness(VisionOperatorDefinition definition, PipelineImageContext context, List<string> failures)
    {
        using var gray = ToGray(context.Image); var mean = Cv2.Mean(gray).Val0;
        var minimum = D(definition, "MinMean"); var maximum = D(definition, "MaxMean");
        if (mean < minimum || mean > maximum) failures.Add($"平均亮度 {mean:F1} 不在 {minimum:F1}~{maximum:F1}");
    }

    private static Mat ToGray(Mat image)
    {
        if (image.Channels() == 1) return image.Clone();
        var gray = new Mat(); Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY); return gray;
    }

    private static Mat EnsureBinary(Mat image)
    {
        using var gray = ToGray(image); var binary = new Mat(); Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu); return binary;
    }

    private static Mat ResizeTemplate(Mat template, Size searchSize)
    {
        if (template.Width <= searchSize.Width && template.Height <= searchSize.Height) return template.Clone();
        var scale = Math.Min((double)searchSize.Width / template.Width, (double)searchSize.Height / template.Height);
        var resized = new Mat(); Cv2.Resize(template, resized, new Size(Math.Max(1, (int)(template.Width * scale)), Math.Max(1, (int)(template.Height * scale)))); return resized;
    }

    private static string S(VisionOperatorDefinition item, string name) =>
        item.Parameters.TryGetValue(name, out var value) ? value : VisionOperatorCatalog.Find(item.OperatorType).Parameters.First(p => p.Name == name).DefaultValue;
    private static double D(VisionOperatorDefinition item, string name) =>
        double.TryParse(S(item, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidOperationException($"{item.DisplayName}.{name} 参数无效。");
    private static int I(VisionOperatorDefinition item, string name) =>
        int.TryParse(S(item, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidOperationException($"{item.DisplayName}.{name} 参数无效。");
    private static bool B(VisionOperatorDefinition item, string name) =>
        bool.TryParse(S(item, name), out var value) && value;

    private static VisionInspectionResult Result(VisionFrame frame, VisionInspectionTask task, bool pass, double score, double elapsed, string reason) => new()
    {
        TaskId = task.TaskId, CameraId = frame.CameraId, FrameId = frame.FrameId, Timestamp = frame.Timestamp,
        IsPass = pass, CapPresent = pass, MatchScore = score, ProcessingTimeMilliseconds = elapsed, FailureReason = reason
    };

    private sealed class PipelineImageContext(Mat image) : IDisposable
    {
        public Mat Image { get; private set; } = image;
        public void Replace(Mat image) { Image.Dispose(); Image = image; }
        public void Dispose() => Image.Dispose();
    }
}
