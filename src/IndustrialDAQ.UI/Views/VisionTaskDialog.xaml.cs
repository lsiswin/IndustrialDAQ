using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IndustrialDAQ.UI.ViewModels;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.UI.Views;

public partial class VisionTaskDialog : UserControl
{
    private Point? _dragStart;
    private VisionTaskDialogViewModel? _viewModel;

    public VisionTaskDialog()
    {
        InitializeComponent();
        // Prism DialogWindow 可能提前处理鼠标事件；handledEventsToo=true 保证框选画布仍能收到输入。
        ImageSelectionHost.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnImageMouseDown), true);
        ImageSelectionHost.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnImageMouseMove), true);
        ImageSelectionHost.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnImageMouseUp), true);
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += (_, _) => { if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged; };
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        // Prism Dialog 会复用视图实例，重新显示时必须恢复订阅，否则框选状态与覆盖框不会刷新。
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        UpdateRegionOverlays();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = args.NewValue as VisionTaskDialogViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateRegionOverlays();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(VisionTaskDialogViewModel.SelectedOperator) or nameof(VisionTaskDialogViewModel.PreviewImage))
            Dispatcher.BeginInvoke(UpdateRegionOverlays);
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left) return;
        if (_viewModel?.IsRegionDrawing != true) return;
        if (PreviewImageControl.Source is not BitmapSource)
        {
            _viewModel.ReportSelectionIssue("尚未取得相机图像，无法框选；请先确认相机已连接");
            return;
        }
        var point = ToNormalizedPoint(args.GetPosition(ImageSelectionHost));
        if (point is null)
        {
            _viewModel.ReportSelectionIssue("请在实际图像范围内按下鼠标左键并拖动");
            return;
        }
        _dragStart = point;
        _viewModel.ReportSelectionIssue("已确定框选起点，请按住左键拖动到区域终点");
        ShowDragStart(point.Value);
        ImageSelectionHost.CaptureMouse();
        args.Handled = true;
    }

    private void OnImageMouseMove(object sender, MouseEventArgs args)
    {
        if (_dragStart is null || args.LeftButton != MouseButtonState.Pressed) return;
        UpdateDraggedRegion(args.GetPosition(ImageSelectionHost));
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (args.ChangedButton != MouseButton.Left) return;
        if (_dragStart is null) return;
        UpdateDraggedRegion(args.GetPosition(ImageSelectionHost));
        _dragStart = null;
        DragStartMarker.Visibility = Visibility.Collapsed;
        ImageSelectionHost.ReleaseMouseCapture();
        _viewModel?.CompleteRegionSelection();
        args.Handled = true;
    }

    private void UpdateDraggedRegion(Point hostPoint)
    {
        var end = ToNormalizedPoint(hostPoint, clampToImage: true);
        if (_dragStart is null || end is null || _viewModel is null) return;
        var left = Math.Min(_dragStart.Value.X, end.Value.X);
        var top = Math.Min(_dragStart.Value.Y, end.Value.Y);
        var width = Math.Abs(_dragStart.Value.X - end.Value.X);
        var height = Math.Abs(_dragStart.Value.Y - end.Value.Y);
        if (width < 0.005 || height < 0.005) return;
        _viewModel.SetSelectedRegion(new VisionRoi(left, top, width, height));
        UpdateRegionOverlays();
    }

    private Point? ToNormalizedPoint(Point point, bool clampToImage = false)
    {
        var bounds = GetRenderedImageBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0 || (!clampToImage && !bounds.Contains(point))) return null;
        return new Point(
            Math.Clamp((point.X - bounds.Left) / bounds.Width, 0, 1),
            Math.Clamp((point.Y - bounds.Top) / bounds.Height, 0, 1));
    }

    private Rect GetRenderedImageBounds()
    {
        if (PreviewImageControl.Source is not BitmapSource source || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            return Rect.Empty;
        var hostWidth = ImageSelectionHost.ActualWidth;
        var hostHeight = ImageSelectionHost.ActualHeight;
        var scale = Math.Min(hostWidth / source.PixelWidth, hostHeight / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        return new Rect((hostWidth - width) / 2, (hostHeight - height) / 2, width, height);
    }

    private void OnImageHostSizeChanged(object sender, SizeChangedEventArgs args) => UpdateRegionOverlays();

    private void ShowDragStart(Point normalizedPoint)
    {
        var bounds = GetRenderedImageBounds();
        if (bounds.IsEmpty) return;
        DragStartMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(DragStartMarker, bounds.Left + normalizedPoint.X * bounds.Width - DragStartMarker.Width / 2);
        Canvas.SetTop(DragStartMarker, bounds.Top + normalizedPoint.Y * bounds.Height - DragStartMarker.Height / 2);
    }

    private void UpdateRegionOverlays()
    {
        if (_viewModel is null || PreviewImageControl.Source is not BitmapSource)
        {
            RoiRegion.Visibility = Visibility.Collapsed;
            TemplateRegion.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateRectangle(RoiRegion, _viewModel.GetRegion("RoiCrop"), _viewModel.SelectedOperator?.OperatorType == "RoiCrop");
        UpdateRectangle(TemplateRegion, _viewModel.GetRegion("TemplateMatch"), _viewModel.SelectedOperator?.OperatorType == "TemplateMatch");
    }

    private void UpdateRectangle(Rectangle rectangle, VisionRoi? region, bool selected)
    {
        var bounds = GetRenderedImageBounds();
        if (region is null || !region.IsValid || bounds.IsEmpty)
        {
            rectangle.Visibility = Visibility.Collapsed;
            return;
        }
        rectangle.Visibility = Visibility.Visible;
        rectangle.StrokeThickness = selected ? 3 : 2;
        Canvas.SetLeft(rectangle, bounds.Left + region.X * bounds.Width);
        Canvas.SetTop(rectangle, bounds.Top + region.Y * bounds.Height);
        rectangle.Width = region.Width * bounds.Width;
        rectangle.Height = region.Height * bounds.Height;
    }
}
