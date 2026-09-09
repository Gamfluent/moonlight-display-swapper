using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DisplaySwitcher.Gui.ViewModels;

namespace DisplaySwitcher.Gui.Controls;

/// <summary>
/// The monitor arrangement surface, modelled on the Windows display settings page: each enabled
/// display is a numbered rectangle drawn to scale, and dragging one snaps its edges to its
/// neighbours so the desktop stays a single connected area.
/// </summary>
public sealed class ArrangementCanvas : Canvas
{
    private const double EdgePadding = 24;
    private const double SnapPixels = 14;

    private readonly Dictionary<MonitorViewModel, Border> _tiles = new();

    private MonitorViewModel? _dragTarget;
    private Point _dragStart;
    private int _dragOriginX;
    private int _dragOriginY;
    private double _scale = 0.1;
    private double _offsetX;
    private double _offsetY;

    public ArrangementCanvas()
    {
        ClipToBounds = true;
        Background = Brush("CanvasBackground", Colors.WhiteSmoke);
        MouseLeftButtonDown += OnCanvasPressed;
    }

    public IReadOnlyList<MonitorViewModel> Monitors { get; private set; } = Array.Empty<MonitorViewModel>();

    public MonitorViewModel? Selected { get; private set; }

    public event EventHandler<MonitorViewModel?>? SelectionChanged;
    public event EventHandler? LayoutChanged;

    public void SetMonitors(IReadOnlyList<MonitorViewModel> monitors)
    {
        Monitors = monitors;
        Rebuild();
    }

    public void Select(MonitorViewModel? monitor)
    {
        Selected = monitor;
        Rebuild();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Rebuild();
    }

    public void Rebuild()
    {
        Children.Clear();
        _tiles.Clear();

        var active = Monitors.Where(m => m.IsActive && m.Width > 0 && m.Height > 0).ToList();

        if (active.Count == 0 || ActualWidth < 10 || ActualHeight < 10)
        {
            if (ActualWidth > 10)
            {
                Children.Add(EmptyHint());
            }

            return;
        }

        var left = active.Min(m => m.PositionX);
        var top = active.Min(m => m.PositionY);
        var right = active.Max(m => m.PositionX + (int)m.Width);
        var bottom = active.Max(m => m.PositionY + (int)m.Height);

        var spanX = Math.Max(1, right - left);
        var spanY = Math.Max(1, bottom - top);

        _scale = Math.Min((ActualWidth - EdgePadding * 2) / spanX, (ActualHeight - EdgePadding * 2) / spanY);
        _offsetX = (ActualWidth - spanX * _scale) / 2 - left * _scale;
        _offsetY = (ActualHeight - spanY * _scale) / 2 - top * _scale;

        foreach (var monitor in active)
        {
            var tile = CreateTile(monitor);
            _tiles[monitor] = tile;
            SetLeft(tile, monitor.PositionX * _scale + _offsetX);
            SetTop(tile, monitor.PositionY * _scale + _offsetY);
            Children.Add(tile);
        }
    }

    private UIElement EmptyHint()
    {
        var text = new TextBlock
        {
            Text = "No displays are enabled for this profile.\nTick \u201cUse this display\u201d below to add one.",
            TextAlignment = TextAlignment.Center,
            Foreground = Brush("TextSubtle", Colors.Gray),
            FontSize = 13
        };

        text.Measure(new Size(ActualWidth, ActualHeight));
        SetLeft(text, Math.Max(0, (ActualWidth - text.DesiredSize.Width) / 2));
        SetTop(text, Math.Max(0, (ActualHeight - text.DesiredSize.Height) / 2));
        return text;
    }

    private Border CreateTile(MonitorViewModel monitor)
    {
        var isSelected = ReferenceEquals(monitor, Selected);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(new TextBlock
        {
            Text = monitor.Number.ToString(CultureInfo.InvariantCulture),
            FontSize = Math.Clamp(monitor.Height * _scale / 3.0, 14, 46),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var caption = new TextBlock
        {
            Text = monitor.Label,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(40, monitor.Width * _scale - 12)
        };
        stack.Children.Add(caption);

        if (monitor.IsPrimary)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Main display",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }

        var border = new Border
        {
            Width = Math.Max(24, monitor.Width * _scale),
            Height = Math.Max(18, monitor.Height * _scale),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(isSelected
                ? Color.FromRgb(0x1A, 0x6E, 0xC2)
                : Color.FromRgb(0x4C, 0x4A, 0x48)),
            BorderBrush = isSelected ? Brush("Accent", Colors.DodgerBlue) : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(isSelected ? 3 : 1),
            Cursor = Cursors.SizeAll,
            Tag = monitor,
            Child = stack,
            ToolTip = $"{monitor.Label}\n{monitor.Width} \u00d7 {monitor.Height} at {monitor.SelectedRefreshHz} Hz\n{monitor.DevicePath}"
        };

        border.MouseLeftButtonDown += OnTilePressed;
        border.MouseMove += OnTileMoved;
        border.MouseLeftButtonUp += OnTileReleased;

        return border;
    }

    private void OnCanvasPressed(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is ArrangementCanvas)
        {
            Selected = null;
            SelectionChanged?.Invoke(this, null);
            Rebuild();
        }
    }

    private void OnTilePressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: MonitorViewModel monitor } border)
        {
            return;
        }

        Selected = monitor;
        SelectionChanged?.Invoke(this, monitor);

        _dragTarget = monitor;
        _dragStart = e.GetPosition(this);
        _dragOriginX = monitor.PositionX;
        _dragOriginY = monitor.PositionY;

        border.CaptureMouse();
        Rebuild();
        e.Handled = true;
    }

    private void OnTileMoved(object sender, MouseEventArgs e)
    {
        if (_dragTarget is null || e.LeftButton != MouseButtonState.Pressed || sender is not Border border)
        {
            return;
        }

        var position = e.GetPosition(this);
        var deltaX = (int)Math.Round((position.X - _dragStart.X) / _scale);
        var deltaY = (int)Math.Round((position.Y - _dragStart.Y) / _scale);

        _dragTarget.PositionX = _dragOriginX + deltaX;
        _dragTarget.PositionY = _dragOriginY + deltaY;

        ApplySnapping(_dragTarget);

        if (_tiles.TryGetValue(_dragTarget, out var tile))
        {
            SetLeft(tile, _dragTarget.PositionX * _scale + _offsetX);
            SetTop(tile, _dragTarget.PositionY * _scale + _offsetY);
        }

        e.Handled = true;
    }

    private void OnTileReleased(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            border.ReleaseMouseCapture();
        }

        if (_dragTarget is null)
        {
            return;
        }

        _dragTarget = null;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        Rebuild();
        e.Handled = true;
    }

    /// <summary>
    /// Pulls the dragged display onto its neighbours' edges. Windows rejects a layout with a gap
    /// between displays, so snapping is what keeps a hand-arranged layout applicable.
    /// </summary>
    private void ApplySnapping(MonitorViewModel dragged)
    {
        var threshold = SnapPixels / _scale;

        var draggedLeft = dragged.PositionX;
        var draggedTop = dragged.PositionY;
        var draggedRight = dragged.PositionX + (int)dragged.Width;
        var draggedBottom = dragged.PositionY + (int)dragged.Height;

        double bestDx = 0;
        double bestDy = 0;
        var bestXDistance = threshold;
        var bestYDistance = threshold;

        foreach (var other in Monitors.Where(m => m.IsActive && !ReferenceEquals(m, dragged)))
        {
            var otherLeft = other.PositionX;
            var otherTop = other.PositionY;
            var otherRight = other.PositionX + (int)other.Width;
            var otherBottom = other.PositionY + (int)other.Height;

            // Butt the vertical edges together, or line up the left/right edges.
            foreach (var (candidate, target) in new[]
                     {
                         (draggedLeft, otherRight),
                         (draggedRight, otherLeft),
                         (draggedLeft, otherLeft),
                         (draggedRight, otherRight)
                     })
            {
                var distance = Math.Abs(candidate - target);
                if (distance < bestXDistance)
                {
                    bestXDistance = distance;
                    bestDx = target - candidate;
                }
            }

            foreach (var (candidate, target) in new[]
                     {
                         (draggedTop, otherBottom),
                         (draggedBottom, otherTop),
                         (draggedTop, otherTop),
                         (draggedBottom, otherBottom)
                     })
            {
                var distance = Math.Abs(candidate - target);
                if (distance < bestYDistance)
                {
                    bestYDistance = distance;
                    bestDy = target - candidate;
                }
            }
        }

        dragged.PositionX += (int)Math.Round(bestDx);
        dragged.PositionY += (int)Math.Round(bestDy);
    }

    private static SolidColorBrush Brush(string resourceKey, Color fallback)
    {
        if (Application.Current?.TryFindResource(resourceKey) is SolidColorBrush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }
}
