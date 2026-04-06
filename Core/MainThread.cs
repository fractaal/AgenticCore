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
	private static bool _pumpConnected;

	// Drain budget per frame
	private const int MaxActionsPerTick = 64;
	private const double MaxMillisPerTick = 2.0;

	// Monitoring
	private static int _totalEnqueued;
	private static int _totalDrained;
	private static int _peakQueueDepth;
	private static double _nextLogTimeSec;
	private const double LogIntervalSec = 10.0;

	/// <summary>
	/// Capture SynchronizationContext.Current and connect the queue drain to SceneTree.ProcessFrame.
	/// Safe to call multiple times; first successful setup wins.
	/// </summary>
	public static void TryInitFromCurrentThread() {
		if (_ctx == null) {
			var current = SynchronizationContext.Current;
			if (current != null) Interlocked.CompareExchange(ref _ctx, current, null);
		}

		if (!_pumpConnected) {
			var tree = Engine.GetMainLoop() as SceneTree;
			if (tree != null) {
				tree.ProcessFrame += DrainQueue;
				_pumpConnected = true;
				GD.Print("[MainThread] Pump connected via SceneTree.ProcessFrame signal.");
			}
		}
	}

	/// <summary>
	/// Post an action via SynchronizationContext immediately (no coalescing).
	/// </summary>
	public static void Post(Action action) {
		if (action == null) return;
		var ctx = _ctx;
		if (ctx != null) { ctx.Post(_ => action(), null); return; }
		action();
	}

	/// <summary>
	/// Enqueue an action to be executed on the next frame by the ProcessFrame drain.
	/// Coalesces many background events into one or a few per-frame batches.
	/// </summary>
	public static void Enqueue(Action action) {
		if (action == null) return;

		// If pump isn't connected yet, fall back to Post() so we don't silently drop work.
		if (!_pumpConnected) {
			Post(action);
			return;
		}

		_queue.Enqueue(action);
		Interlocked.Increment(ref _totalEnqueued);
	}

	private static void DrainQueue() {
		int processed = 0;
		var startUsec = Time.GetTicksUsec();
		while (processed < MaxActionsPerTick && _queue.TryDequeue(out var action)) {
			try {
				action?.Invoke();
			} catch (Exception e) {
				GD.PrintErr($"[MainThread] Pump callback error: {e.Message}\n{e.StackTrace}");
			}
			processed++;
			var elapsedMs = (Time.GetTicksUsec() - startUsec) / 1000.0;
			if (elapsedMs >= MaxMillisPerTick) break;
		}
		_totalDrained += processed;

		// Track peak queue depth (items remaining after drain)
		int remaining = _queue.Count;
		if (remaining > _peakQueueDepth) _peakQueueDepth = remaining;

		// Periodic health log
		double now = Time.GetTicksUsec() / 1_000_000.0;
		if (now >= _nextLogTimeSec) {
			GD.Print($"[MainThread] pump stats — enqueued: {_totalEnqueued}, drained: {_totalDrained}, " +
				$"backlog: {remaining}, peak backlog: {_peakQueueDepth}, " +
				$"this frame drained: {processed}, took: {(Time.GetTicksUsec() - startUsec) / 1000.0:F1}ms");
			_peakQueueDepth = 0;
			_nextLogTimeSec = now + LogIntervalSec;
		}
	}
}
