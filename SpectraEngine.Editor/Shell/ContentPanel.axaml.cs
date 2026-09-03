using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;

namespace SpectraEngine.Editor.Shell;

/// <summary>
/// The content browser's view. Every gesture resolves to a call on
/// <see cref="ContentBrowserModel"/>, or to an intent the window answers.
/// </summary>
public partial class ContentPanel : UserControl
{
    // The movement that separates a click from a drag, and the press that
    // started it. Avalonia 12's DoDragDropAsync wants the PRESS args, while the
    // threshold is only crossed during a move - the same pair, for the same
    // reason, as the scene tree's row drag.
    private const double DragThresholdPixels = 4.0;

    private ContentEntry? _pressedEntry;
    private PointerPressedEventArgs? _pressEvent;
    private Point _pressPoint;
    private bool _dragInProgress;

    public ContentPanel()
    {
        InitializeComponent();

        // TUNNEL, and deliberately never claiming the event. The tiles carry
        // their own DoubleTapped for descending into a folder, and a bubbling
        // handler here would see the press only after the item had, which is
        // fine for a click and useless for a drag: the gesture has to be
        // recognised from the same press the tile is about to treat as a tap.
        AddHandler(PointerPressedEvent, OnTilePointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnTilePointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnTilePointerReleased, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Raised when the user activates a FILE (a folder is handled here, by
    /// descending into it).
    /// </summary>
    /// <remarks>
    /// An intent rather than an action, like every other panel in this shell:
    /// the panel knows what was double-clicked, and the window is the only
    /// thing that knows whether there is a session to insert it into.
    /// </remarks>
    public event Action<ContentEntry>? EntryActivated;

    private ContentBrowserModel? Model =>
        (DataContext as ShellModel)?.Content;

    private void OnUpClicked(object? sender, RoutedEventArgs e) => Model?.GoUp();

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => Model?.Refresh();

    private void OnEntryActivated(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ContentEntry entry })
            return;

        if (entry.IsFolder)
            Model?.Open(entry);
        else
            EntryActivated?.Invoke(entry);
    }

    // --- Drag source ---------------------------------------------------------

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedEntry = null;
        _pressEvent = null;

        // Left button only. Avalonia raises PointerPressed for every button, and
        // a right-press on its way to a context menu that opened a drag instead
        // would be a gesture nobody asked for.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (EntryFrom(e.Source) is not { IsFolder: false } entry)
            return;

        _pressedEntry = entry;
        _pressEvent = e;
        _pressPoint = e.GetPosition(this);
    }

    private void OnTilePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragInProgress || _pressedEntry is not { } entry || _pressEvent is not { } press)
            return;

        // The button is re-checked on every move rather than trusted from the
        // press. A gesture that ended somewhere this panel never saw - a capture
        // taken away, a release delivered elsewhere - would otherwise leave the
        // press state armed, and the next idle sweep of the pointer across a
        // tile would start a drag nobody asked for.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressedEntry = null;
            _pressEvent = null;
            return;
        }

        Point now = e.GetPosition(this);
        if (Math.Abs(now.X - _pressPoint.X) < DragThresholdPixels &&
            Math.Abs(now.Y - _pressPoint.Y) < DragThresholdPixels)
        {
            return;
        }

        // Refused HERE rather than at the drop, because a drag that cannot
        // possibly resolve should never start: the "no drop" cursor over every
        // surface in the window is a clearer answer than a payload the viewport
        // has to decline.
        if (Model is not { } model || !model.TryDescribe(entry, out ContentDragPayload? payload))
        {
            _pressedEntry = null;
            _pressEvent = null;
            return;
        }

        _ = StartDragAsync(press, payload);
    }

    private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressedEntry = null;
        _pressEvent = null;
    }

    private async System.Threading.Tasks.Task StartDragAsync(
        PointerPressedEventArgs trigger, ContentDragPayload payload)
    {
        _dragInProgress = true;
        _pressedEntry = null;
        _pressEvent = null;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(ContentDrag.Format, payload));

        try
        {
            // Copy rather than Move, and the distinction is not cosmetic: the
            // file stays exactly where it is and the scene gains a reference to
            // it, so a Move cursor would promise that dropping moved something
            // on disk.
            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Copy);
        }
        finally
        {
            _dragInProgress = false;
        }
    }

    // The entry a press landed on, found by walking up from whatever leaf the
    // template put under the pointer: the tile's picture, its name and its size
    // label are all separate controls sharing the row's DataContext.
    private static ContentEntry? EntryFrom(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is StyledElement { DataContext: ContentEntry entry })
                return entry;
        }

        return null;
    }
}
