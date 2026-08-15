using Microsoft.Data.Sqlite;

namespace Abacus;

/// <summary>
/// Concrete IBatchInsert backed by a SQLite parameterized INSERT.
///
/// JDBC's PreparedStatement.addBatch() only *stages* a parameter set;
/// nothing hits the database until executeBatch() runs, ideally wrapped in
/// setAutoCommit(false)/true so the whole batch is one transaction. SQLite
/// has no equivalent "batch of parameter sets" API, so this mirrors the same
/// two-phase contract explicitly: AddBatch() queues a snapshot of the
/// currently-set parameter values, and ExecuteBatch() (called by the same
/// abacus.java code paths that called prep.executeBatch()) opens one
/// transaction and replays every queued row through it.
/// </summary>
public sealed class SqliteBatchInsert : IBatchInsert, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly string insertSql;
    private readonly int parameterCount;

    // 1-based, index 0 unused - matches the Set*(index, ...) call sites'
    // 1-based JDBC-style parameter indices exactly, so no +/-1 translation
    // is needed anywhere. Array-backed instead of the original
    // Dictionary<int, object?>: this is on the hottest path in the whole
    // port (called once per peptide row while parsing every protXML/pepXML
    // file - millions of times across a real run), and a dictionary clone
    // (hash + bucket allocation) per row was measurably expensive purely as
    // allocation/GC pressure, not I/O. Both current call sites
    // (ProtXml.WriteToDb, PepXml's insert path) always set every index
    // 1..parameterCount before calling AddBatch(), but current is explicitly
    // cleared after each snapshot anyway so a caller that *doesn't* would
    // still see the original "unset index -> DBNull" behavior instead of a
    // stale value bleeding through from the previous row.
    private readonly object?[] current;
    private readonly List<object?[]> queued = new();

    public SqliteBatchInsert(SqliteConnection connection, string insertSql, int parameterCount)
    {
        this.connection = connection;
        this.insertSql = insertSql;
        this.parameterCount = parameterCount;
        current = new object?[parameterCount + 1];
    }

    public void SetString(int index, string? value) => current[index] = value;
    public void SetInt(int index, int value) => current[index] = value;
    public void SetDouble(int index, double value) => current[index] = value;

    public void AddBatch()
    {
        queued.Add((object?[])current.Clone());
        Array.Clear(current);
    }

    /// <summary>Equivalent to `prep.executeBatch()`: replays all queued rows in one transaction.</summary>
    public void ExecuteBatch()
    {
        if (queued.Count == 0) return;

        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = insertSql;

        // Capture parameter *references* once instead of looking each one up
        // by name (`cmd.Parameters[$"@p{i}"]`, allocating a fresh string and
        // doing a name-based lookup) inside the per-row loop below.
        var parameters = new SqliteParameter[parameterCount + 1];
        for (var i = 1; i <= parameterCount; i++)
        {
            parameters[i] = new SqliteParameter($"@p{i}", DBNull.Value);
            cmd.Parameters.Add(parameters[i]);
        }

        foreach (var row in queued)
        {
            for (var i = 1; i <= parameterCount; i++)
            {
                parameters[i].Value = row[i] ?? DBNull.Value;
            }
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        queued.Clear();
    }

    /// <summary>Equivalent to `prep.clearBatch()`.</summary>
    public void ClearBatch() => queued.Clear();

    public void Dispose()
    {
    }
}
