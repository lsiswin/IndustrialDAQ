// File: TrendView.xaml.cs  Module: UI (Views)  Author: IndustrialDAQ Team
using System.Windows.Controls;
using System.Windows.Input;
using IndustrialDAQ.UI.ViewModels;
using Prism.Ioc;

namespace IndustrialDAQ.UI.Views;

public partial class TrendView : UserControl
{
    public TrendView()
    {
        InitializeComponent();
    }

    /// <summary>让十字线跟随鼠标，辅助比较同一时刻的多条趋势曲线。</summary>
    private void OnTrendChartMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(TrendChartHost);

        CrosshairVertical.X1 = position.X;
        CrosshairVertical.X2 = position.X;
        CrosshairVertical.Y1 = 0;
        CrosshairVertical.Y2 = TrendChartHost.ActualHeight;

        CrosshairHorizontal.X1 = 0;
        CrosshairHorizontal.X2 = TrendChartHost.ActualWidth;
        CrosshairHorizontal.Y1 = position.Y;
        CrosshairHorizontal.Y2 = position.Y;

        CrosshairVertical.Visibility = System.Windows.Visibility.Visible;
        CrosshairHorizontal.Visibility = System.Windows.Visibility.Visible;
    }

    /// <summary>鼠标离开图表后隐藏十字线，避免遮挡静态查看。</summary>
    private void OnTrendChartMouseLeave(object sender, MouseEventArgs e)
    {
        CrosshairVertical.Visibility = System.Windows.Visibility.Collapsed;
        CrosshairHorizontal.Visibility = System.Windows.Visibility.Collapsed;
    }
}
