using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace ReBackup.Shared.Wpf.Controls;

/// <summary>A move in a list: the item at <see cref="From"/> ends up at <see cref="To"/> (indexes after the move).</summary>
public readonly record struct ListMove(int From, int To);

/// <summary>Where an item container of a list lies, vertically, in the list's coordinates.</summary>
public readonly record struct ItemSpan(int Index, double Top, double Height)
{
    public double Bottom => Top + Height;
}

/// <summary>
/// Drag &amp; drop reordering for a vertical list (ListBox or any ItemsControl): set <c>ListReorder.MoveCommand</c> on
/// the list and dragging an item shows an insertion line in the accent colour (<c>Brush.Accent</c>, followed on theme
/// changes); dropping it executes the command with a <see cref="ListMove"/> when it can execute. The command moves the
/// item in the bound collection; the behavior changes nothing itself. Drags that start on a button are left alone.
/// After any move of the selected (or focused) item, by drop, menu or key, the item is scrolled into view and its
/// container gets the keyboard focus back (a virtualizing panel makes a new container for a moved item, which loses
/// the focus), so Alt+Up / Alt+Down can be pressed again and again.
/// </summary>
public static class ListReorder
{
    /// <summary>The resource key of the insertion line's brush.</summary>
    public const string LineBrushKey = "Brush.Accent";

    private const string DataFormat = "ReBackup.Shared.Wpf.ListReorder";

    /// <summary>Distance from the top or bottom edge within which a drag scrolls the list.</summary>
    private const double ScrollEdge = 24;

    public static readonly DependencyProperty MoveCommandProperty = DependencyProperty.RegisterAttached(
        "MoveCommand", typeof(ICommand), typeof(ListReorder), new PropertyMetadata(null, OnMoveCommandChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(DragState), typeof(ListReorder), new PropertyMetadata(null));

    public static ICommand? GetMoveCommand(DependencyObject element) => (ICommand?)element.GetValue(MoveCommandProperty);

    public static void SetMoveCommand(DependencyObject element, ICommand? value) => element.SetValue(MoveCommandProperty, value);

    /// <summary>
    /// The gap a drop at <paramref name="y"/> goes to: 0 is above the first item, <paramref name="count"/> below the
    /// last. An item's upper half means the gap above it. <paramref name="items"/> are the realized items by index.
    /// </summary>
    public static int InsertionIndex(IReadOnlyList<ItemSpan> items, double y, int count)
    {
        foreach (var item in items)
        {
            if (y < item.Top + item.Height / 2)
                return item.Index;
        }
        return items.Count == 0 ? count : Math.Min(items[^1].Index + 1, count);
    }

    /// <summary>
    /// The index the item at <paramref name="fromIndex"/> gets when dropped into gap <paramref name="insertionIndex"/>;
    /// null when that is no move (the gaps right above and below the item) or an index is out of range.
    /// </summary>
    public static int? DropIndex(int fromIndex, int insertionIndex, int count)
    {
        if (fromIndex < 0 || fromIndex >= count || insertionIndex < 0 || insertionIndex > count)
            return null;
        var to = insertionIndex > fromIndex ? insertionIndex - 1 : insertionIndex;
        return to == fromIndex ? null : to;
    }

    /// <summary>
    /// The height of the insertion line for gap <paramref name="insertionIndex"/>: the middle between two items, the
    /// top edge of the first or the bottom edge of the last.
    /// </summary>
    public static double InsertionLineY(IReadOnlyList<ItemSpan> items, int insertionIndex)
    {
        if (items.Count == 0)
            return 0;
        ItemSpan? above = null;
        ItemSpan? below = null;
        foreach (var item in items)
        {
            if (item.Index == insertionIndex - 1)
                above = item;
            else if (item.Index == insertionIndex)
                below = item;
        }
        return (above, below) switch
        {
            ({ } a, { } b) => (a.Bottom + b.Top) / 2,
            (null, { } b) => b.Top,
            ({ } a, null) => a.Bottom,
            _ => insertionIndex <= items[0].Index ? items[0].Top : items[^1].Bottom,
        };
    }

    private static void OnMoveCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
            return;

        list.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
        list.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
        list.PreviewMouseMove -= OnPreviewMouseMove;
        list.DragOver -= OnDragOver;
        list.DragLeave -= OnDragLeave;
        list.Drop -= OnDrop;
        if (StateOf(list) is { } old)
            ((INotifyCollectionChanged)list.Items).CollectionChanged -= old.OnItemsChanged;

        if (e.NewValue is null)
        {
            list.ClearValue(UIElement.AllowDropProperty);
            list.ClearValue(StateProperty);
            return;
        }

        var state = new DragState(list);
        list.SetValue(StateProperty, state);
        ((INotifyCollectionChanged)list.Items).CollectionChanged += state.OnItemsChanged;
        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        list.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
        list.PreviewMouseMove += OnPreviewMouseMove;
        list.DragOver += OnDragOver;
        list.DragLeave += OnDragLeave;
        list.Drop += OnDrop;
    }

    private static DragState? StateOf(ItemsControl list) => list.GetValue(StateProperty) as DragState;

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ItemsControl list || StateOf(list) is not { } state)
            return;
        state.FromIndex = -1;
        if (e.OriginalSource is not DependencyObject source || Ancestor<ButtonBase>(source, list) is not null)
            return;
        if (Ancestor<FrameworkElement>(source, list, element => list.ItemContainerGenerator.IndexFromContainer(element) >= 0)
            is not { } container)
            return;
        state.FromIndex = list.ItemContainerGenerator.IndexFromContainer(container);
        state.Start = e.GetPosition(list);
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ItemsControl list && StateOf(list) is { } state)
            state.FromIndex = -1;
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ItemsControl list || StateOf(list) is not { FromIndex: >= 0 } state)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            state.FromIndex = -1;
            return;
        }

        var offset = e.GetPosition(list) - state.Start;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var from = state.FromIndex;
        state.FromIndex = -1;
        try
        {
            DragDrop.DoDragDrop(list, new DataObject(DataFormat, new DragToken(list, from)), DragDropEffects.Move);
        }
        finally
        {
            HideLine(list, state);
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl list || StateOf(list) is not { } state || TokenOf(e, list) is not { } token)
            return;

        e.Handled = true;
        AutoScroll(list, e.GetPosition(list).Y);
        var spans = Spans(list, out var left, out var right);
        var count = list.Items.Count;
        var insertion = InsertionIndex(spans, e.GetPosition(list).Y, count);
        if (DropIndex(token.FromIndex, insertion, count) is null)
        {
            e.Effects = DragDropEffects.None;
            HideLine(list, state);
            return;
        }

        e.Effects = DragDropEffects.Move;
        ShowLine(list, state, InsertionLineY(spans, insertion), left, right);
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl list || StateOf(list) is not { } state)
            return;
        // Raised as well when the pointer moves from one item to the next; only leaving the list hides the line.
        var position = e.GetPosition(list);
        if (position.X < 0 || position.Y < 0 || position.X >= list.ActualWidth || position.Y >= list.ActualHeight)
            HideLine(list, state);
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl list || StateOf(list) is not { } state || TokenOf(e, list) is not { } token)
            return;

        e.Handled = true;
        HideLine(list, state);
        var count = list.Items.Count;
        var insertion = InsertionIndex(Spans(list, out _, out _), e.GetPosition(list).Y, count);
        if (DropIndex(token.FromIndex, insertion, count) is not { } to)
            return;

        var move = new ListMove(token.FromIndex, to);
        if (GetMoveCommand(list) is { } command && command.CanExecute(move))
            command.Execute(move);
    }

    /// <summary>Only drags this list started are taken.</summary>
    private static DragToken? TokenOf(DragEventArgs e, ItemsControl list)
    {
        var token = e.Data.GetDataPresent(DataFormat) ? e.Data.GetData(DataFormat) as DragToken : null;
        if (token is not null && ReferenceEquals(token.List, list))
            return token;
        e.Effects = DragDropEffects.None;
        return null;
    }

    /// <summary>The realized, visible item containers in index order, and their common horizontal extent.</summary>
    private static List<ItemSpan> Spans(ItemsControl list, out double left, out double right)
    {
        var spans = new List<ItemSpan>();
        left = 0;
        right = list.ActualWidth;
        var first = true;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement { IsVisible: true } container ||
                !list.IsAncestorOf(container))
                continue;
            var origin = container.TransformToAncestor(list).Transform(new Point(0, 0));
            spans.Add(new ItemSpan(i, origin.Y, container.ActualHeight));
            if (first)
            {
                left = origin.X;
                right = origin.X + container.ActualWidth;
                first = false;
            }
        }
        return spans;
    }

    private static void AutoScroll(ItemsControl list, double y)
    {
        if (Descendant<ScrollViewer>(list) is not { } viewer)
            return;
        if (y < ScrollEdge)
            viewer.LineUp();
        else if (y > list.ActualHeight - ScrollEdge)
            viewer.LineDown();
    }

    private static void ShowLine(ItemsControl list, DragState state, double y, double left, double right)
    {
        if (state.Adorner is null)
        {
            if (AdornerLayer.GetAdornerLayer(list) is not { } layer)
                return;
            state.Adorner = new InsertionAdorner(list);
            layer.Add(state.Adorner);
        }
        state.Adorner.Place(y, left, right);
    }

    private static void HideLine(ItemsControl list, DragState state)
    {
        if (state.Adorner is null)
            return;
        AdornerLayer.GetAdornerLayer(list)?.Remove(state.Adorner);
        state.Adorner = null;
    }

    private static T? Ancestor<T>(DependencyObject start, DependencyObject stop, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        for (var current = start; current is not null && !ReferenceEquals(current, stop); current = Parent(current))
        {
            if (current is T found && (match is null || match(found)))
                return found;
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject element) =>
        element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
                return found;
            if (Descendant<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private sealed record DragToken(ItemsControl List, int FromIndex);

    /// <summary>
    /// The item moved: when it was the selected one or its container had the focus, it is scrolled into view and
    /// focused again once the list has made its new container.
    /// </summary>
    private static void OnItemMoved(ItemsControl list, object? item)
    {
        if (item is null)
            return;
        var container = list.ItemContainerGenerator.ContainerFromItem(item) as UIElement;
        var hadFocus = container?.IsKeyboardFocusWithin == true || list.IsKeyboardFocusWithin;
        var selected = list is Selector selector && Equals(selector.SelectedItem, item);
        if (!selected && container?.IsKeyboardFocusWithin != true)
            return;

        list.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!list.Items.Contains(item))
                return;
            if (list is ListBox listBox)
                listBox.ScrollIntoView(item);
            list.UpdateLayout();
            // Only when the focus was in the list or went nowhere: a move must not take the focus from elsewhere.
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (!hadFocus && focused is not null && !(focused is Visual visual && list.IsAncestorOf(visual)))
                return;
            (list.ItemContainerGenerator.ContainerFromItem(item) as UIElement)?.Focus();
        });
    }

    private sealed class DragState(ItemsControl list)
    {
        public void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Move && e.OldItems is { Count: 1 } moved)
                OnItemMoved(list, moved[0]);
        }

        public int FromIndex { get; set; } = -1;
        public Point Start { get; set; }
        public InsertionAdorner? Adorner { get; set; }
    }

    /// <summary>The insertion line: a 2 px accent line with round dots at both ends.</summary>
    private sealed class InsertionAdorner : Adorner
    {
        private static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
            "LineBrush", typeof(Brush), typeof(InsertionAdorner),
            new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        private double _y;
        private double _left;
        private double _right;

        public InsertionAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
            SetResourceReference(LineBrushProperty, LineBrushKey);
        }

        public void Place(double y, double left, double right)
        {
            if (_y == y && _left == left && _right == right)
                return;
            (_y, _left, _right) = (y, left, right);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var element = (FrameworkElement)AdornedElement;
            if (_y < 0 || _y > element.ActualHeight)
                return;   // scrolled out of view
            var brush = (Brush)GetValue(LineBrushProperty);
            const double dot = 3;
            var left = _left + dot;
            var right = Math.Max(left, _right - dot);
            drawingContext.DrawLine(new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                new Point(left, _y), new Point(right, _y));
            drawingContext.DrawEllipse(brush, null, new Point(left, _y), dot, dot);
            drawingContext.DrawEllipse(brush, null, new Point(right, _y), dot, dot);
        }
    }
}
