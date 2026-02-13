using System;
using System.Collections.Concurrent;
using System.Threading;
using Godot;

/// <summary>
/// Main-thread utilities and a lightweight on-idle dispatcher to coalesce callbacks.
/// Call TryInitFromCurrentThread() once on the Godot main thread (e.g., during startup).
/// </summary>
public static partial class MainThread {
    private static SynchronizationContext _ctx;
    private static ConcurrentQueue<Action> _queue = new();
    private static Pump _pump; // drains _queue on idle

    /// <summary>
    /// Capture SynchronizationContext.Current and ensure a pump exists on the scene tree.
    /// Safe to call multiple times; first non-null capture wins.
    /// </summary>
    public static void TryInitFromCurrentThread() {
        if (_ctx == null) {
            var current = SynchronizationContext.Current;
            if (current != null) Interlocked.CompareExchange(ref _ctx, current, null);
        }

        // Ensure a pump node exists (main thread only)
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null && _pump == null) {
            var p = new Pump();
            _pump = p;
            tree.Root.AddChild(p);
        }
    }

    /// <summary>
    /// Post an action via SynchronizationContext immediately (no coalescing).
    /// </summary>
    public static void Post(Action action) {
        if (action == null) return;
        var ctx = _ctx;
        if (ctx != null) { ctx.Post(_ => action(), null); return; }
        // Fallback: no captured context; execute inline (best effort)
        action();
    }

    /// <summary>
    /// Enqueue an action to be executed on the next idle ticks by the pump.
    /// Coalesces many background events into one or a few per-frame batches.
    /// Guarantees delivery even if the pump isn't ready yet by falling back.
    /// </summary>
    public static void Enqueue(Action action) {
        if (action == null) return;
        // If no SynchronizationContext yet, we cannot marshal or create a pump safely.
        // Run inline as a last-resort to avoid dropping work.
        if (_ctx == null && _pump == null) { action(); return; }

        _queue.Enqueue(action);

        // If pump not created yet, ask main thread to create it.
        if (_pump == null && _ctx != null) {
            _ctx.Post(_ => EnsurePumpOnMainThread(), null);
        }
    }

    private static void EnsurePumpOnMainThread() {
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null && _pump == null) {
            var p = new Pump();
            _pump = p;
            tree.Root.AddChild(p);
        }
    }

    private sealed partial class Pump : Node {
        [Export] public int MaxActionsPerTick { get; set; } = 64; // cap per frame
        [Export] public double MaxMillisPerTick { get; set; } = 2.0; // soft time budget

        public override void _Process(double delta) {
            // Drain with simple action-count and time budgets
            int processed = 0;
            var startUsec = Time.GetTicksUsec();
            while (processed < MaxActionsPerTick && _queue.TryDequeue(out var act)) {
                try { act?.Invoke(); } catch (Exception e) { GD.PrintErr($"[MainThreadPump] Callback error: {e.Message}\n{e.StackTrace}"); }
                processed++;
                var elapsedMs = (Time.GetTicksUsec() - startUsec) / 1000.0;
                if (elapsedMs >= MaxMillisPerTick) break;
            }
        }
    }
}

