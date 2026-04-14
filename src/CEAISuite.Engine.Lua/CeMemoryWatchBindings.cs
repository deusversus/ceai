using System.Collections.Concurrent;
using CEAISuite.Engine.Abstractions;
using MoonSharp.Interpreter;

namespace CEAISuite.Engine.Lua;

/// <summary>
/// Reactive memory monitoring via createMemoryWatch(). Polls a target address
/// on a configurable interval and fires an OnChange callback when the value changes.
/// Thread-safe: timer callbacks execute via <see cref="MoonSharpLuaEngine.ExecuteGuarded"/>.
/// </summary>
internal sealed class CeMemoryWatchBindings : IDisposable
{
    private sealed class WatchState : IDisposable
    {
        public required string Id;
        public nuint Address;
        public string DataType = "4 Bytes";
        public int IntervalMs = 100;
        public string? LastValue;
        public volatile bool IsRunning;
        public Timer? Timer;
        private volatile bool _inCallback;

        /// <summary>Whether a callback is currently executing (prevents re-entrant execution).</summary>
        public bool InCallback { get => _inCallback; set => _inCallback = value; }

        public void Dispose()
        {
            IsRunning = false;
            Timer?.Dispose();
            Timer = null;
        }
    }

    private readonly ConcurrentDictionary<string, WatchState> _watches = new();
    private readonly ConcurrentDictionary<string, DynValue> _callbacks = new();
    private int _nextWatchId;
    private MoonSharpLuaEngine? _engine;
    private IEngineFacade? _facade;
    private IAutoAssemblerEngine? _aa;

    /// <summary>Maximum number of concurrent watches allowed.</summary>
    private const int MaxWatches = 50;

    /// <summary>Minimum poll interval in milliseconds.</summary>
    private const int MinIntervalMs = 10;

    public void Register(Script script, MoonSharpLuaEngine engine,
        IEngineFacade facade, IAutoAssemblerEngine? aa)
    {
        _engine = engine;
        _facade = facade;
        _aa = aa;

        // createMemoryWatch(address, [dataType]) → watch proxy table
        script.Globals["createMemoryWatch"] = (Func<DynValue, DynValue, DynValue>)((addrArg, typeArg) =>
        {
            if (_watches.Count >= MaxWatches)
                throw new ScriptRuntimeException($"Maximum watch limit ({MaxWatches}) reached");

            var watchId = $"watch_{Interlocked.Increment(ref _nextWatchId)}";
            var pid = LuaBindingHelpers.RequireProcess(engine);
            var addr = LuaBindingHelpers.ResolveAddressArg(addrArg, pid, facade, aa);
            var dataType = typeArg.IsNil() ? "4 Bytes" : typeArg.String;

            var watch = new WatchState
            {
                Id = watchId,
                Address = addr,
                DataType = dataType,
                IntervalMs = 100,
                IsRunning = false
            };
            _watches[watchId] = watch;

            // Build proxy table with methods
            var table = new Table(script);
            table["_id"] = watchId;
            table["_type"] = "memorywatch";

            table["start"] = (Action)(() => StartWatch(script, watch));
            table["stop"] = (Action)(() => StopWatch(watch));
            table["destroy"] = (Action)(() => DestroyWatch(watchId));
            table["getLastValue"] = (Func<DynValue>)(() =>
                watch.LastValue is not null ? DynValue.NewString(watch.LastValue) : DynValue.Nil);

            // Properties via CePropertyProxy
            var props = CePropertyProxy.CreatePropertyMap();
            props["Interval"] = CePropertyProxy.ReadWrite(
                () => DynValue.NewNumber(watch.IntervalMs),
                v =>
                {
                    watch.IntervalMs = Math.Max(CePropertyProxy.ToInt(v), MinIntervalMs);
                    // Update running timer if active
                    watch.Timer?.Change(watch.IntervalMs, watch.IntervalMs);
                });
            props["Address"] = CePropertyProxy.ReadWrite(
                () => DynValue.NewNumber((double)(ulong)watch.Address),
                v =>
                {
                    var currentPid = engine.CurrentProcessId;
                    if (currentPid.HasValue)
                        watch.Address = LuaBindingHelpers.ResolveAddressArg(v, currentPid.Value, facade, aa);
                });
            props["DataType"] = CePropertyProxy.ReadWrite(
                () => DynValue.NewString(watch.DataType),
                v => watch.DataType = v.String);

            var events = CePropertyProxy.CreateEventSet();
            events.Add("OnChange");

            CePropertyProxy.ApplyProxy(script, table, props, events, _callbacks, $"{watchId}:");

            return DynValue.NewTable(table);
        });

        // destroyAllWatches() — cleanup helper
        script.Globals["destroyAllWatches"] = (Action)(() =>
        {
            foreach (var (_, watch) in _watches)
                watch.Dispose();
            _watches.Clear();

            // Remove all watch-related callbacks
            foreach (var key in _callbacks.Keys.Where(k => k.StartsWith("watch_", StringComparison.Ordinal)).ToList())
                _callbacks.TryRemove(key, out _);
        });
    }

    private void StartWatch(Script script, WatchState watch)
    {
        if (watch.IsRunning) return;
        watch.IsRunning = true;

        // Read initial value
        watch.LastValue = ReadCurrentValue(watch);

        watch.Timer = new Timer(_ =>
        {
            if (!watch.IsRunning || watch.InCallback) return;

            var currentValue = ReadCurrentValue(watch);
            if (currentValue == watch.LastValue) return;

            var oldValue = watch.LastValue;
            watch.LastValue = currentValue;

            // Fire OnChange callback
            var cbKey = $"{watch.Id}:onchange";
            if (_callbacks.TryGetValue(cbKey, out var cb) && cb.Type == DataType.Function)
            {
                if (_engine is not null)
                {
                    watch.InCallback = true;
                    try
                    {
                        _engine.ExecuteGuarded(() =>
                        {
                            try
                            {
                                script.Call(cb,
                                    DynValue.NewString(oldValue ?? ""),
                                    DynValue.NewString(currentValue ?? ""));
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                            {
                                _engine.RaiseOutput(
                                    $"[MemoryWatch {watch.Id}] OnChange callback error: {ex.Message}");
                            }
                        });
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[MemoryWatch] ExecuteGuarded failed: {ex.Message}");
                    }
                    finally
                    {
                        watch.InCallback = false;
                    }
                }
            }
        }, null, watch.IntervalMs, watch.IntervalMs);
    }

    private string? ReadCurrentValue(WatchState watch)
    {
        try
        {
            var pid = _engine?.CurrentProcessId;
            if (pid is null || _facade is null) return null;

            var dataType = MapDataType(watch.DataType);
            var result = _facade.ReadValueAsync(pid.Value, watch.Address, dataType)
                .GetAwaiter().GetResult();
            return result.DisplayValue;
        }
        catch (OutOfMemoryException) { throw; }
        catch (StackOverflowException) { throw; }
        catch { return null; }
    }

    private static MemoryDataType MapDataType(string ceType) => ceType.ToLowerInvariant() switch
    {
        "byte" => MemoryDataType.Byte,
        "2 bytes" => MemoryDataType.Int16,
        "4 bytes" => MemoryDataType.Int32,
        "8 bytes" => MemoryDataType.Int64,
        "float" => MemoryDataType.Float,
        "double" => MemoryDataType.Double,
        _ => MemoryDataType.Int32
    };

    private static void StopWatch(WatchState watch)
    {
        watch.IsRunning = false;
        watch.Timer?.Dispose();
        watch.Timer = null;
    }

    private void DestroyWatch(string watchId)
    {
        if (_watches.TryRemove(watchId, out var watch))
            watch.Dispose();

        // Remove associated callbacks
        foreach (var key in _callbacks.Keys.Where(k => k.StartsWith(watchId + ":", StringComparison.Ordinal)).ToList())
            _callbacks.TryRemove(key, out _);
    }

    /// <summary>Stop and dispose all watches. Called on engine Reset/Dispose.</summary>
    public void Dispose()
    {
        foreach (var (_, watch) in _watches)
            watch.Dispose();
        _watches.Clear();
        _callbacks.Clear();
    }
}
