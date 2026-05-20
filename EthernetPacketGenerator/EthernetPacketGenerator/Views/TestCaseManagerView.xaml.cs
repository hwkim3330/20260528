using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator.Views;

public partial class TestCaseManagerView : UserControl
{
    public TestCaseManagerView() => InitializeComponent();

    private void Triangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TestCaseGroup group)
            group.IsExpanded = !group.IsExpanded;
        e.Handled = true;
    }

    private void SelectAllTc_Checked(object sender, RoutedEventArgs e)
        => SetAllTcChecked(true);

    private void SelectAllTc_Unchecked(object sender, RoutedEventArgs e)
        => SetAllTcChecked(false);

    private void SetAllTcChecked(bool value)
    {
        if (DataContext is TestCaseManagerViewModel vm)
            foreach (var tc in vm.Groups.SelectMany(g => g.TestCases))
                tc.IsChecked = value;
        CommandManager.InvalidateRequerySuggested();
    }

    // ── TestSequence 리스트 드래그 & 드랍 ────────────────────────────────────
    private TestCaseEntry? _pendingDragTc;
    private Point           _seqMouseDownPos;
    private bool            _seqDragging;
    private TestCaseEntry?  _seqDragItem;
    private int             _seqInsertIdx = -1;

    private void SeqList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var lvi = GetSeqListViewItem(e.GetPosition(SeqList));
        if (lvi?.DataContext is not TestCaseEntry tc) return;

        // 클릭: TC 선택 명령 실행 (TEST SEQUENCE → 이전 결과 복원)
        if (DataContext is TestCaseManagerViewModel vm)
            vm.SelectTcFromSequenceCommand.Execute(tc);

        _pendingDragTc   = tc;
        _seqMouseDownPos = e.GetPosition(this);
        _seqDragging     = false;
    }

    private void SeqList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingDragTc == null && !_seqDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { SeqCancelDrag(); return; }

        if (!_seqDragging)
        {
            var pos = e.GetPosition(this);
            bool over =
                Math.Abs(pos.X - _seqMouseDownPos.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _seqMouseDownPos.Y) > SystemParameters.MinimumVerticalDragDistance;
            if (!over) return;

            _seqDragging  = true;
            _seqDragItem  = _pendingDragTc;
            _pendingDragTc = null;
            SeqList.CaptureMouse();
        }

        var ptInList = e.GetPosition(SeqList);
        if (new Rect(0, 0, SeqList.ActualWidth, SeqList.ActualHeight).Contains(ptInList))
        {
            _seqInsertIdx = SeqCalcInsertIdx(ptInList);
            SeqShowIndicator(_seqInsertIdx);
        }
        else
        {
            _seqInsertIdx = -1;
            SeqHideIndicator();
        }
        e.Handled = true;
    }

    private void SeqList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        SeqList.ReleaseMouseCapture();
        if (!_seqDragging || _seqDragItem == null) { SeqResetDrag(); return; }

        var ptInList = e.GetPosition(SeqList);
        if (!new Rect(0, 0, SeqList.ActualWidth, SeqList.ActualHeight).Contains(ptInList))
            _seqInsertIdx = -1;

        SeqApplyDrop();
        SeqResetDrag();
        e.Handled = true;
    }

    private void SeqList_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_seqDragging) { _seqInsertIdx = -1; SeqHideIndicator(); }
    }

    private void SeqApplyDrop()
    {
        if (DataContext is not TestCaseManagerViewModel vm) return;
        if (_seqDragItem == null) return;
        var seq = vm.TestSequence;
        int from = seq.IndexOf(_seqDragItem);
        if (from < 0 || _seqInsertIdx < 0) return;
        int dest = _seqInsertIdx > from ? _seqInsertIdx - 1 : _seqInsertIdx;
        if (dest != from && dest >= 0 && dest < seq.Count)
            seq.Move(from, dest);
    }

    private void SeqCancelDrag()
    {
        SeqList.ReleaseMouseCapture();
        SeqResetDrag();
    }

    private void SeqResetDrag()
    {
        _pendingDragTc = null;
        _seqDragging   = false;
        _seqDragItem   = null;
        _seqInsertIdx  = -1;
        SeqHideIndicator();
    }

    // ── Drop indicator ────────────────────────────────────────────────────────
    private void SeqShowIndicator(int insertIdx)
    {
        double y = SeqGetIndicatorY(insertIdx);
        Canvas.SetTop(SeqDropLine, y);
        SeqDropCanvas.Visibility = Visibility.Visible;
    }

    private void SeqHideIndicator() => SeqDropCanvas.Visibility = Visibility.Collapsed;

    private double SeqGetIndicatorY(int insertIdx)
    {
        var items = GetSeqVisibleItems();
        if (items.Count == 0) return 0;
        if (insertIdx <= 0)
        {
            try { return items[0].TransformToAncestor(SeqDropCanvas).TransformBounds(new Rect(items[0].RenderSize)).Top; }
            catch { return 0; }
        }
        int t = Math.Min(insertIdx - 1, items.Count - 1);
        try { return items[t].TransformToAncestor(SeqDropCanvas).TransformBounds(new Rect(items[t].RenderSize)).Bottom; }
        catch { return 0; }
    }

    private int SeqCalcInsertIdx(Point ptInList)
    {
        if (DataContext is not TestCaseManagerViewModel vm) return 0;
        var items = GetSeqVisibleItems();
        for (int i = 0; i < items.Count; i++)
        {
            try
            {
                var b = items[i].TransformToAncestor(SeqList).TransformBounds(new Rect(items[i].RenderSize));
                if (ptInList.Y < b.Top + b.Height / 2) return i;
            }
            catch { }
        }
        return vm.TestSequence.Count;
    }

    private List<ListViewItem> GetSeqVisibleItems()
    {
        var result = new List<ListViewItem>();
        var panel  = FindVisualDescendant<VirtualizingStackPanel>(SeqList);
        if (panel == null) return result;
        int count = VisualTreeHelper.GetChildrenCount(panel);
        for (int i = 0; i < count; i++)
            if (VisualTreeHelper.GetChild(panel, i) is ListViewItem lvi) result.Add(lvi);
        return result;
    }

    private ListViewItem? GetSeqListViewItem(Point point)
    {
        var hit = VisualTreeHelper.HitTest(SeqList, point);
        if (hit == null) return null;
        var dep = hit.VisualHit as DependencyObject;
        while (dep != null)
        {
            if (dep is ListViewItem lvi) return lvi;
            dep = VisualTreeHelper.GetParent(dep);
        }
        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject element) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T t) return t;
            var found = FindVisualDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}
