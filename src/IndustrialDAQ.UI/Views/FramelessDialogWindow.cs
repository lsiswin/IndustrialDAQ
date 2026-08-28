using System.Windows;
using System.Windows.Media;

namespace IndustrialDAQ.UI.Views;

public partial class FramelessDialogWindow : Window, IDialogWindow
{
    public IDialogResult Result { get; set; } = new DialogResult();

    public FramelessDialogWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // 弹窗最大尺寸跟随当前工作区，低分辨率下由内容滚动而不是超出屏幕。
        MaxWidth = SystemParameters.WorkArea.Width * 0.92;
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        
        // Let Prism manage the content fully
    }
}
