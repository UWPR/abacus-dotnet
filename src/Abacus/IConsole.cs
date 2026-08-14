namespace Abacus;

/// <summary>
/// Stand-in for the original Java GUI's abacus_textArea console.
/// When null, ported code falls back to Console.Error, matching the
/// original's `console == null` -> System.err behavior.
/// </summary>
public interface IConsole
{
    void Append(string text);

    // Progress-monitor calls used by hyperSQLObject's longer-running queries.
    // Always unreachable in the current CLI-only build (console is always
    // null), kept for when the GUI console is ported.
    void MonitorBoxInit(int total, string label);
    void MonitorBoxUpdate(int current);
    void CloseMonitorBox();
}
