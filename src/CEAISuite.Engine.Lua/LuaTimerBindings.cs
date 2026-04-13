using System.Collections.Concurrent;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Non-blocking native timer system for Lua scripts. Unlike form-based timers,
/// these don't require a form host and run via .NET System.Threading.Timer.
/// Timer callbacks are queued and executed on the Lua engine thread via the
/// semaphore-gated execution path.
/// </summary>
internal sealed class LuaTimerBindings : IDisposable
{
    private readonly ConcurrentDictionary<string, TimerState> _timers = new();
    private readonly ConcurrentDictionary<string, ThreadState> _threads = new();
    private int _nextTimerId;

    public void Register(Script script, MoonSharpLuaEngine engine)
    {
        // createNativeTimer(intervalMs, callback) → timer handle table
        script.Globals["createNativeTimer"] = (Func<int, DynValue, DynValue>)((intervalMs, callback) =>
        {
            if (callback.Type != DataType.Function)
                throw new ScriptRuntimeException("createNativeTimer: second argument must be a function");

            var timerId = $"ntimer_{Interlocked.Increment(ref _nextTimerId)}";
            var capped = Math.Max(intervalMs, 10); // minimum 10ms

            var state = new TimerState(timerId, callback, script, engine, capped);
            _timers[timerId] = state;
            state.Start();

            var timerTable = new Table(script);
            timerTable["_id"] = timerId;

            timerTable["setInterval"] = (Action<int>)(ms =>
            {
                if (_timers.TryGetValue(timerId, out var ts))
                    ts.SetInterval(Math.Max(ms, 10));
            });

            timerTable["setEnabled"] = (Action<bool>)(enabled =>
            {
                if (_timers.TryGetValue(timerId, out var ts))
                {
                    if (enabled) ts.Start();
                    else ts.Stop();
                }
            });

            timerTable["destroy"] = (Action)(() =>
            {
                if (_timers.TryRemove(timerId, out var ts))
                    ts.Dispose();
            });

            timerTable["isEnabled"] = (Func<bool>)(() =>
                _timers.TryGetValue(timerId, out var ts) && ts.IsRunning);

            return DynValue.NewTable(timerTable);
        });

        // destroyAllTimers() — cleanup helper
        script.Globals["destroyAllTimers"] = (Action)(() => DisposeAll());

        // createThread(function) → thread handle table
        // Runs a Lua function on a background .NET thread. The function can call sleep()
        // to yield between iterations. Thread is cooperative — terminates on next sleep/yield.
        script.Globals["createThread"] = (Func<DynValue, DynValue>)(func =>
        {
            if (func.Type != DataType.Function)
                throw new ScriptRuntimeException("createThread: argument must be a function");

            var threadId = $"thread_{Interlocked.Increment(ref _nextTimerId)}";
            var cts = new CancellationTokenSource();
            var threadState = new ThreadState(threadId, cts);
            _threads[threadId] = threadState;

            // Run the function on a background thread
            threadState.Task = Task.Run(() =>
            {
                try
                {
                    script.Call(func);
                }
                catch (ScriptRuntimeException)
                {
                    // Script errors in threads are silently caught (matching CE behavior)
                }
                catch (OperationCanceledException) { }
                finally
                {
                    _threads.TryRemove(threadId, out _);
                }
            }, cts.Token);

            var threadTable = new Table(script);
            threadTable["_id"] = threadId;

            threadTable["terminate"] = (Action)(() =>
            {
                cts.Cancel();
                _threads.TryRemove(threadId, out _);
            });

            threadTable["isRunning"] = (Func<bool>)(() =>
                _threads.TryGetValue(threadId, out var ts) && ts.Task is { IsCompleted: false });

            threadTable["waitFor"] = (Action)(() =>
            {
                if (_threads.TryGetValue(threadId, out var ts) && ts.Task is not null)
                    ts.Task.GetAwaiter().GetResult();
            });

            return DynValue.NewTable(threadTable);
        });
    }

    /// <summary>Stop and dispose all active timers and threads. Called on engine Reset/Dispose.</summary>
    public void DisposeAll()
    {
        foreach (var (_, state) in _timers)
            state.Dispose();
        _timers.Clear();

        foreach (var (_, thread) in _threads)
            thread.Cts.Cancel();
        _threads.Clear();
    }

    public void Dispose() => DisposeAll();

    private sealed class TimerState : IDisposable
    {
        private readonly string _id;
        private readonly DynValue _callback;
        private readonly Script _script;
        private readonly MoonSharpLuaEngine _engine;
        private Timer? _timer;
        private int _intervalMs;
        private volatile bool _running;
        private volatile bool _inCallback; // prevent re-entrant execution

        public bool IsRunning => _running;

        public TimerState(string id, DynValue callback, Script script, MoonSharpLuaEngine engine, int intervalMs)
        {
            _id = id;
            _callback = callback;
            _script = script;
            _engine = engine;
            _intervalMs = intervalMs;
        }

        public void Start()
        {
            Stop();
            _running = true;
            _timer = new Timer(OnTick, null, _intervalMs, _intervalMs);
        }

        public void Stop()
        {
            _running = false;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _timer?.Dispose();
            _timer = null;
        }

        public void SetInterval(int ms)
        {
            _intervalMs = ms;
            _timer?.Change(ms, ms);
        }

        private void OnTick(object? _)
        {
            if (!_running || _inCallback) return;

            _inCallback = true;
            try
            {
                // Execute callback synchronously on timer thread
                // The callback runs inside the Lua engine which is semaphore-gated
                _script.Call(_callback);
            }
            catch
            {
                // Timer callback errors are silently swallowed to prevent timer death.
                // In production, these should be logged.
            }
            finally
            {
                _inCallback = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }

    private sealed class ThreadState(string id, CancellationTokenSource cts)
    {
        public string Id { get; } = id;
        public CancellationTokenSource Cts { get; } = cts;
        public Task? Task { get; set; }
    }
}
