using IndustrialDAQ.Vision.Models;
using OpenCvSharp;

namespace IndustrialDAQ.Vision.Algorithms;

/// <summary>视觉图像公共预处理，统一 ROI 换算和灰度归一化。</summary>
internal static class VisionImageProcessor
{
    public static Mat Decode(byte[] encodedImage)
    {
        var image = Cv2.ImDecode(encodedImage, ImreadModes.Color);
        if (image.Empty()) throw new InvalidOperationException("图像解码失败或文件格式不受支持。");
        return image;
    }

    public static Rect ToPixelRect(VisionRoi roi, Size size)
    {
        if (!roi.IsValid) throw new InvalidOperationException("检测 ROI 超出图像范围。");
        var x = Math.Clamp((int)Math.Round(roi.X * size.Width), 0, size.Width - 1);
        var y = Math.Clamp((int)Math.Round(roi.Y * size.Height), 0, size.Height - 1);
        var width = Math.Clamp((int)Math.Round(roi.Width * size.Width), 1, size.Width - x);
        var height = Math.Clamp((int)Math.Round(roi.Height * size.Height), 1, size.Height - y);
        return new Rect(x, y, width, height);
    }

    public static Mat Prepare(Mat image, VisionRoi roi)
    {
        using var cropped = new Mat(image, ToPixelRect(roi, image.Size()));
        var gray = new Mat();
        Cv2.CvtColor(cropped, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);
        Cv2.EqualizeHist(gray, gray);
        return gray;
    }
}
