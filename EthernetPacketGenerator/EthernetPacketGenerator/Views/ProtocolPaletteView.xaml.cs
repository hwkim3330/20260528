using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.ViewModels;

namespace EthernetPacketGenerator.Views;

public partial class ProtocolPaletteView : UserControl
{
    private Point?               _mouseDownPos;
    private ProtocolPaletteItem? _pendingItem;
    private bool                 _isDragging;
    private ProtocolPaletteItem? _dragItem;
    private DragAdornerWindow?   _adorner;

    private static readonly Point CenterOffset =
        new(DragAdornerWindow.ChipW / 2, DragAdornerWindow.ChipH / 2);

    public event EventHandler<ProtocolType>? ProtocolAddRequested;

    public ProtocolPaletteView()
    {
        InitializeComponent();
    }

    // ── hit test ─────────────────────────────────────────────────────────────

    private ProtocolPaletteItem? HitTestItem(MouseEventArgs e)
    {
        var fe = e.OriginalSource as FrameworkElement;
        while (fe != null)
        {
            if (fe.DataContext is ProtocolPaletteItem item) return item;
            fe = VisualTreeHelper.GetParent(fe) as FrameworkElement;
        }
        return null;
    }

    // ── mouse events ──────────────────────────────────────────────────────────

    private void Palette_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pendingItem  = HitTestItem(e);
        _mouseDownPos = _pendingItem != null ? e.GetPosition(this) : null;
        _isDragging   = false;

        if (_pendingItem != null)
            PaletteList.CaptureMouse();
    }

    private void Palette_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging && (_mouseDownPos == null || _pendingItem == null)) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }

        var pos = e.GetPosition(this);
        if (!_isDragging)
        {
            bool over =
                Math.Abs(pos.X - _mouseDownPos!.Value.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(pos.Y - _mouseDownPos!.Value.Y) > SystemParameters.MinimumVerticalDragDistance;
            if (!over) return;

            _isDragging   = true;
            _dragItem     = _pendingItem;
            _pendingItem  = null;
            _mouseDownPos = null;

            var tempBlock = PacketItem.CreateBlock(_dragItem!.ProtocolType);
            _adorner = DragAdornerWindow.Create(tempBlock);
            _adorner.PlaceAtCursor(CenterOffset);
            _adorner.Show();
        }

        _adorner?.PlaceAtCursor(CenterOffset);

        // Tell the builder where the cursor is (screen DIP → the builder converts internally)
        var curDip = DragAdornerWindow.CursorDip();
        BlockBuilderView.ActiveDropTarget?.PaletteDragOver(_dragItem!.ProtocolType, curDip);

        e.Handled = true;
    }

    private void Palette_MouseUp(object sender, MouseButtonEventArgs e)
    {
        PaletteList.ReleaseMouseCapture();

        if (_isDragging && _dragItem != null)
        {
            var curDip = DragAdornerWindow.CursorDip();
            BlockBuilderView.ActiveDropTarget?.PaletteDrop(_dragItem.ProtocolType, curDip);
        }
        else if (_pendingItem != null && !_isDragging)
        {
            ProtocolAddRequested?.Invoke(this, _pendingItem.ProtocolType);
        }

        FinishDrag();
    }

    private void CancelDrag()
    {
        PaletteList.ReleaseMouseCapture();
        BlockBuilderView.ActiveDropTarget?.PaletteDragLeave();
        _mouseDownPos = null;
        _pendingItem  = null;
        _isDragging   = false;
        _dragItem     = null;
        CloseAdorner();
    }

    private void FinishDrag()
    {
        BlockBuilderView.ActiveDropTarget?.PaletteDragLeave();
        _isDragging   = false;
        _dragItem     = null;
        _pendingItem  = null;
        _mouseDownPos = null;
        CloseAdorner();
    }

    private void CloseAdorner() { _adorner?.Close(); _adorner = null; }
}
