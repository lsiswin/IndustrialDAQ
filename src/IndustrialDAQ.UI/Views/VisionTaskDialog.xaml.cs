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
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => UpdateRegionOverlays();
        Unloaded += (_, _) => { if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged; };
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
        if (args.PropertyName == nameof(VisionTaskDialogViewModel.IsRegionDrawing))
            Dispatcher.BeginInvoke(UpdateSelectionCursor);
    }

    private void OnImageMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (_viewModel?.IsRegionDrawing != true || PreviewImageControl.Source is not BitmapSource) return;
        var point = ToNormalizedPoint(args.GetPosition(ImageSelectionHost));
        if (point is null) return;
        _dragStart = point;
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
        if (_dragStart is null) return;
        UpdateDraggedRegion(args.GetPosition(ImageSelectionHost));
        _dragStart = null;
        ImageSelectionHost.ReleaseMouseCapture();
        _viewModel?.CompleteRegionSelection();
        UpdateSelectionCursor();
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

    private void UpdateSelectionCursor() =>
        ImageSelectionHost.Cursor = _viewModel?.IsRegionDrawing == true ? Cursors.Cross : Cursors.Arrow;

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
