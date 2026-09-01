namespace IndustrialDAQ.Vision.Models;

/// <summary>视觉配方中的单个顺序算子，参数使用字符串保存以便 UI 和数据库动态扩展。</summary>
public sealed class VisionOperatorDefinition
{
    public string OperatorId { get; init; } = Guid.NewGuid().ToString("N");
    public string OperatorType { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Order { get; init; }
    public bool IsEnabled { get; init; } = true;
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>算子库中的参数定义，提供默认值、说明和编辑顺序。</summary>
public sealed record VisionOperatorParameterDescriptor(string Name, string DisplayName, string DefaultValue, string Help);

/// <summary>算子库描述，用于傻瓜式逐个添加。</summary>
public sealed record VisionOperatorDescriptor(
    string OperatorType,
    string DisplayName,
    string Category,
    string Description,
    IReadOnlyList<VisionOperatorParameterDescriptor> Parameters)
{
    public VisionOperatorDefinition CreateDefault(int order) => new()
    {
        OperatorType = OperatorType, DisplayName = DisplayName, Order = order,
        Parameters = Parameters.ToDictionary(item => item.Name, item => item.DefaultValue, StringComparer.OrdinalIgnoreCase)
    };
}

/// <summary>首期常用视觉算子目录，后续算法插件只需追加描述和执行器。</summary>
public static class VisionOperatorCatalog
{
    public static IReadOnlyList<VisionOperatorDescriptor> Common { get; } =
    [
        new("RoiCrop", "ROI 区域", "图像预处理", "裁剪检测区域，坐标使用 0 到 1 的比例。",
        [P("X", "左边", "0.25", "ROI 左上角 X 比例"), P("Y", "顶部", "0.25", "ROI 左上角 Y 比例"), P("Width", "宽度", "0.50", "ROI 宽度比例"), P("Height", "高度", "0.50", "ROI 高度比例")]),
        new("Grayscale", "灰度转换", "图像预处理", "将彩色图像转换为灰度图。", []),
        new("GaussianBlur", "高斯平滑", "图像预处理", "抑制噪声，核大小必须为正奇数。",
        [P("KernelSize", "核大小", "5", "常用值 3、5、7")]),
        new("BinaryThreshold", "二值化", "图像分割", "按灰度阈值分离前景和背景。",
        [P("Threshold", "灰度阈值", "128", "范围 0 到 255"), P("Invert", "黑白反转", "false", "true 时反转前景背景")]),
        new("TemplateMatch", "模板匹配", "定位与检测", "将当前图像与教学模板比较。",
        [P("TemplatePath", "模板路径", "", "通过“采集模板”自动生成"), P("MinScore", "最低分数", "0.80", "范围 0 到 1")]),
        new("BlobCount", "斑点计数", "缺陷检测", "统计二值图中的连通区域数量和面积。",
        [P("MinArea", "最小面积", "100", "忽略更小噪点"), P("MaxArea", "最大面积", "100000", "忽略过大区域"), P("MinCount", "最少数量", "1", "合格数量下限"), P("MaxCount", "最多数量", "1", "合格数量上限")]),
        new("EdgeDensity", "边缘密度", "缺陷检测", "使用 Canny 边缘占比判断轮廓是否完整。",
        [P("CannyLow", "低阈值", "50", "Canny 低阈值"), P("CannyHigh", "高阈值", "150", "Canny 高阈值"), P("MinDensity", "最小密度", "0.02", "合格边缘占比下限"), P("MaxDensity", "最大密度", "0.50", "合格边缘占比上限")]),
        new("Brightness", "亮度判定", "质量判定", "检查平均灰度，识别过暗或过曝。",
        [P("MinMean", "最低亮度", "40", "平均灰度下限"), P("MaxMean", "最高亮度", "220", "平均灰度上限")])
    ];

    public static VisionOperatorDescriptor Find(string operatorType) =>
        Common.First(item => string.Equals(item.OperatorType, operatorType, StringComparison.OrdinalIgnoreCase));

    private static VisionOperatorParameterDescriptor P(string name, string displayName, string value, string help) =>
        new(name, displayName, value, help);
}
