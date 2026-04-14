namespace CEAISuite.Engine.Abstractions;

/// <summary>
/// Host interface for reactive data bindings between Lua form elements and memory records.
/// The host fires RefreshCycleCompleted on each address table refresh cycle.
/// </summary>
public interface ILuaDataBindingHost
{
    /// <summary>Fired after each address table refresh cycle. Bindings check for value changes.</summary>
    event Action? RefreshCycleCompleted;
}
