using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Commands;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Editing.Undo;

/// <summary>
/// The editor's bounded edit history over one <see cref="Scene"/>: a linear
/// undo/redo timeline of <see cref="IEditorCommand"/>s, with an explicit
/// transaction concept so a whole gesture becomes one entry.
/// </summary>
/// <remarks>
/// <b>Transactions are what make a gizmo drag usable.</b> A drag pushes a
/// command per frame; without a transaction that would be sixty undo entries
/// for one gesture. So a tool calls <see cref="BeginTransaction"/> on grab,
/// records freely while dragging (the stack coalesces same-node,
/// same-kind edits into the open transaction — see
/// <see cref="ICoalescingCommand"/>), and calls <see cref="CommitTransaction"/>
/// on release, which lands exactly one history entry. Cancelling instead rolls
/// the scene back to the pre-grab state and lands nothing.
/// <para>
/// <b>Redo is invalidated by any new command.</b> Undoing three steps and then
/// making an edit drops the three redoable entries — the timeline is linear,
/// not a tree.
/// </para>
/// <para>
/// <b>History is bounded</b> by <see cref="Capacity"/>: pushing onto a full
/// history evicts the oldest entry, which then becomes permanently
/// un-undoable. The entries live in a ring buffer, so eviction moves no memory
/// and steady-state pushes allocate nothing.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the <see cref="Scene"/> it edits
/// — every command mutates the scene graph. <b>Re-entrancy:</b>
/// <see cref="Changed"/> handlers must not push, undo, redo, or open a
/// transaction from inside the event; observe and defer, the same rule as the
/// scene's own change events.
/// </para>
/// </remarks>
public sealed class UndoStack
{
    /// <summary>The default history bound, in entries.</summary>
    public const int DefaultCapacity = 256;

    // Ring buffer of retained entries. _head is the slot of the OLDEST entry;
    // logical index i lives at (_head + i) % Capacity. Entries [0.._cursor) are
    // applied to the scene (undoable), entries [_cursor.._count) have been
    // undone and are redoable. Evicting the oldest entry is a head bump, not a
    // memmove — which is why the bound costs nothing per push.
    private readonly IEditorCommand?[] _entries;
    private int _head;
    private int _count;
    private int _cursor;

    // The open transaction's recorded commands, or an unused empty list when no
    // transaction is open. Retains its capacity across gestures so a drag of N
    // nodes allocates only on the first gesture that wide.
    private readonly List<IEditorCommand> _transaction = [];
    private string? _transactionName;

    /// <summary>
    /// Creates an empty history for <paramref name="scene"/>.
    /// </summary>
    /// <param name="scene">The scene every command in this history edits.</param>
    /// <param name="capacity">Maximum retained entries; must be at least 1.</param>
    public UndoStack(Scene scene, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Scene = scene;
        _entries = new IEditorCommand?[capacity];
    }

    /// <summary>The scene this history edits.</summary>
    public Scene Scene { get; }

    /// <summary>The maximum number of retained entries.</summary>
    public int Capacity => _entries.Length;

    /// <summary>
    /// Retained entries, undoable and redoable together. Never exceeds
    /// <see cref="Capacity"/>.
    /// </summary>
    public int Count => _count;

    /// <summary>How many entries are currently undoable.</summary>
    public int UndoCount => _cursor;

    /// <summary>How many entries are currently redoable.</summary>
    public int RedoCount => _count - _cursor;

    /// <summary>
    /// True when <see cref="Undo"/> would do something. False while a
    /// transaction is open: undoing in the middle of a gesture would interleave
    /// with the edit in progress, so tools must commit or cancel first.
    /// </summary>
    public bool CanUndo => !IsTransactionOpen && _cursor > 0;

    /// <summary>
    /// True when <see cref="Redo"/> would do something. False while a
    /// transaction is open, for the same reason as <see cref="CanUndo"/>.
    /// </summary>
    public bool CanRedo => !IsTransactionOpen && _cursor < _count;

    /// <summary>True while a transaction is open.</summary>
    public bool IsTransactionOpen => _transactionName is not null;

    /// <summary>
    /// The open transaction's label, or null when none is open. Becomes the
    /// history entry's <see cref="IEditorCommand.Name"/> on commit of a
    /// multi-command transaction.
    /// </summary>
    public string? OpenTransactionName => _transactionName;

    /// <summary>
    /// The name of the entry <see cref="Undo"/> would revert, or null when
    /// <see cref="CanUndo"/> is false — for an "Undo &lt;name&gt;" menu item.
    /// </summary>
    public string? UndoName => CanUndo ? EntryAt(_cursor - 1).Name : null;

    /// <summary>
    /// The name of the entry <see cref="Redo"/> would re-apply, or null when
    /// <see cref="CanRedo"/> is false.
    /// </summary>
    public string? RedoName => CanRedo ? EntryAt(_cursor).Name : null;

    /// <summary>
    /// Raised after any change to what the history offers: a recorded entry, an
    /// undo, a redo, a clear, and each transaction boundary (opening one flips
    /// <see cref="CanUndo"/> to false, so UI bound to it must hear about it).
    /// Recording <em>into</em> an open transaction raises nothing — the history
    /// itself has not changed yet.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Applies <paramref name="command"/> to the scene and records it. The
    /// entry point for edits the tool has not already performed.
    /// </summary>
    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do(Scene);
        Record(command);
    }

    /// <summary>
    /// Records a command whose effect the tool has <em>already</em> applied to
    /// the scene — the normal case for a live gizmo drag, which moves the node
    /// itself each frame and only wants the history entry. The command is not
    /// executed; its after-state must already match the scene.
    /// </summary>
    public void Record(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (IsTransactionOpen)
        {
            // Coalesce into the gesture: offer the newcomer to the entries
            // already recorded, newest first. The scan is bounded by the number
            // of DISTINCT targets in the gesture (one entry per node), not by
            // the number of frames, precisely because absorbing succeeds.
            for (int i = _transaction.Count - 1; i >= 0; i--)
            {
                if (_transaction[i] is ICoalescingCommand coalescing && coalescing.TryAbsorb(command))
                    return;
            }

            _transaction.Add(command);
            return; // No Changed: nothing lands in history until commit.
        }

        PushEntry(command);
        Changed?.Invoke();
    }

    /// <summary>
    /// Opens a transaction: every command recorded until
    /// <see cref="CommitTransaction"/> collapses into one history entry.
    /// Transactions do not nest — opening one while another is open throws.
    /// </summary>
    public void BeginTransaction(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (IsTransactionOpen)
        {
            throw new InvalidOperationException(
                $"An edit transaction ('{_transactionName}') is already open; transactions do not nest.");
        }

        _transactionName = name;
        // CanUndo/CanRedo just went false — UI bound to them must follow.
        Changed?.Invoke();
    }

    /// <summary>
    /// Closes the open transaction and lands its commands as a single history
    /// entry: the command itself when the transaction recorded exactly one, a
    /// <see cref="CompositeCommand"/> when it recorded several, and nothing at
    /// all when it recorded none (a click that turned out not to move
    /// anything). Throws when no transaction is open.
    /// </summary>
    public void CommitTransaction()
    {
        RequireOpenTransaction();

        string name = _transactionName!;
        _transactionName = null;

        if (_transaction.Count == 1)
        {
            // A one-command gesture needs no wrapper — and keeping the command
            // itself preserves its own name for the undo menu.
            PushEntry(_transaction[0]);
        }
        else if (_transaction.Count > 1)
        {
            PushEntry(new CompositeCommand(name, _transaction));
        }

        _transaction.Clear();
        // Always raised, even for an empty transaction: CanUndo/CanRedo were
        // forced false while it was open and are now live again.
        Changed?.Invoke();
    }

    /// <summary>
    /// Closes the open transaction, rolling the scene back to its pre-gesture
    /// state (the recorded commands are rolled back in reverse) and discarding
    /// them — nothing lands in history. This is the Escape key during a gizmo
    /// drag. Throws when no transaction is open.
    /// </summary>
    /// <remarks>
    /// The roll-back reaches <em>every</em> node the gesture touched, including
    /// one that has left the scene since — see
    /// <see cref="IEditorCommand.RollBack"/>. Anything less would be a silent
    /// hole in the contract, because a cancel keeps nothing that could restore
    /// such a node later.
    /// </remarks>
    public void CancelTransaction()
    {
        RequireOpenTransaction();

        // Reverse order, exactly like CompositeCommand.Undo: the discarded
        // commands may depend on each other.
        //
        // RollBack, not Undo: these commands are about to be thrown away, so
        // this is the only chance any of them will ever get. Undo's
        // missing-target rule — no-op when the node is not in the scene — is
        // right for replaying history behind an undone delete and wrong here,
        // where a node that left the scene mid-gesture would be stranded at a
        // mid-drag value with nothing left to restore it (see
        // IEditorCommand.RollBack).
        for (int i = _transaction.Count - 1; i >= 0; i--)
            _transaction[i].RollBack(Scene);

        _transaction.Clear();
        _transactionName = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Reverts the most recent undoable entry and returns true; returns false
    /// when <see cref="CanUndo"/> is false.
    /// </summary>
    public bool Undo()
    {
        if (!CanUndo)
            return false;

        _cursor--;
        EntryAt(_cursor).Undo(Scene);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Re-applies the most recently undone entry and returns true; returns
    /// false when <see cref="CanRedo"/> is false.
    /// </summary>
    public bool Redo()
    {
        if (!CanRedo)
            return false;

        EntryAt(_cursor).Do(Scene);
        _cursor++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Drops the whole history without touching the scene — for a document
    /// load or a "clear history" action. A no-op (and silent) on an already
    /// empty history. Throws when a transaction is open; commit or cancel it
    /// first.
    /// </summary>
    public void Clear()
    {
        if (IsTransactionOpen)
        {
            throw new InvalidOperationException(
                $"Cannot clear the undo history while the transaction '{_transactionName}' is open.");
        }

        if (_count == 0)
            return;

        // Null the slots so a dropped history stops rooting its commands (and
        // through them, captured values) for the lifetime of the stack.
        for (int i = 0; i < _count; i++)
            _entries[RingIndex(i)] = null;

        _head = 0;
        _count = 0;
        _cursor = 0;
        Changed?.Invoke();
    }

    // Appends one entry at the cursor, dropping anything redoable behind it and
    // evicting the oldest entry when the ring is full.
    private void PushEntry(IEditorCommand command)
    {
        // A new command makes the redoable tail unreachable: the timeline is
        // linear, so those entries are gone, not branched.
        for (int i = _cursor; i < _count; i++)
            _entries[RingIndex(i)] = null;
        _count = _cursor;

        if (_count == Capacity)
        {
            // Full: the oldest entry falls off the back and can never be undone
            // again. Head bump, no copying.
            _entries[_head] = null;
            _head = _head + 1 == Capacity ? 0 : _head + 1;
            _count--;
            _cursor--;
        }

        _entries[RingIndex(_count)] = command;
        _count++;
        _cursor = _count;
    }

    // Logical index -> ring slot. _head + i stays below 2*Capacity because
    // i < Capacity, so a subtract beats a modulo.
    private int RingIndex(int logicalIndex)
    {
        int index = _head + logicalIndex;
        return index >= Capacity ? index - Capacity : index;
    }

    // Non-null by construction: slots inside [0.._count) always hold a command.
    private IEditorCommand EntryAt(int logicalIndex) => _entries[RingIndex(logicalIndex)]!;

    private void RequireOpenTransaction()
    {
        if (!IsTransactionOpen)
            throw new InvalidOperationException("No edit transaction is open.");
    }
}
