using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IndustrialDAQ.UI.ViewModels;
using IndustrialDAQ.Vision.Models;

namespace IndustrialDAQ.UI.Views;

public partial class VisionTaskDialog : UserControl
{
    private Point? _selectionStart;
    private bool _hasDragDelta;
    private VisionTaskDialogViewModel? _viewModel;

    public VisionTaskDialog()
    {
        InitializeComponent();
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
        if (args.PropertyName == nameof(VisionTaskDialogViewModel.IsRegionDrawing) && _viewModel?.IsRegionDrawing != true)
            Dispatcher.BeginInvoke(ResetSelectionGesture);
    }

    private void OnRegionDragStarted(object sender, DragStartedEventArgs args)
    {
        if (_viewModel?.IsRegionDrawing != true) return;
        if (PreviewImageControl.Source is not BitmapSource)
        {
            _viewModel.ReportSelectionIssue("尚未取得相机图像，无法框选；请先确认相机已连接");
            return;
        }
        var point = ToNormalizedPoint(Mouse.GetPosition(ImageSelectionHost));
        if (point is null)
        {
            _viewModel.ReportSelectionIssue("请在实际教学图范围内开始框选");
            return;
        }

        // 若已经保留了第一次单击，则本次按下直接确定终点。
        if (_selectionStart is not null)
        {
            ApplyRegion(_selectionStart.Value, point.Value);
            ResetSelectionGesture();
            _viewModel.CompleteRegionSelection();
            return;
        }

        _selectionStart = point;
        _hasDragDelta = false;
        _viewModel.ReportSelectionIssue("已确定框选起点：可按住拖动，或松开后再次单击终点");
        ShowDragStart(point.Value);
    }

    private void OnRegionDragDelta(object sender, DragDeltaEventArgs args)
    {
        if (_selectionStart is null) return;
        var end = ToNormalizedPoint(Mouse.GetPosition(ImageSelectionHost), clampToImage: true);
        if (end is null) return;
        _hasDragDelta |= Math.Abs(_selectionStart.Value.X - end.Value.X) >= 0.005 || Math.Abs(_selectionStart.Value.Y - end.Value.Y) >= 0.005;
        if (_hasDragDelta) ApplyRegion(_selectionStart.Value, end.Value);
    }

    private void OnRegionDragCompleted(object sender, DragCompletedEventArgs args)
    {
        if (_selectionStart is null) return;
        var end = ToNormalizedPoint(Mouse.GetPosition(ImageSelectionHost), clampToImage: true);
        if (_hasDragDelta && end is not null)
        {
            ApplyRegion(_selectionStart.Value, end.Value);
            ResetSelectionGesture();
            _viewModel?.CompleteRegionSelection();
        }
        else
        {
            _viewModel?.ReportSelectionIssue("起点已保留，请在图像中再次单击区域终点");
        }
    }

    private void ApplyRegion(Point start, Point end)
    {
        if (_viewModel is null) return;
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(start.X - end.X);
        var height = Math.Abs(start.Y - end.Y);
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

    private void ResetSelectionGesture()
    {
        _selectionStart = null;
        _hasDragDelta = false;
        if (SelectionThumb.IsMouseCaptured) SelectionThumb.ReleaseMouseCapture();
        DragStartMarker.Visibility = Visibility.Collapsed;
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
