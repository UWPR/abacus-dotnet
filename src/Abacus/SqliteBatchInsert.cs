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
    private readonly Dictionary<int, object?> current = new();
    private readonly List<Dictionary<int, object?>> queued = new();

    public SqliteBatchInsert(SqliteConnection connection, string insertSql, int parameterCount)
    {
        this.connection = connection;
        this.insertSql = insertSql;
        this.parameterCount = parameterCount;
    }

    public void SetString(int index, string? value) => current[index] = value;
    public void SetInt(int index, int value) => current[index] = value;
    public void SetDouble(int index, double value) => current[index] = value;

    public void AddBatch()
    {
        queued.Add(new Dictionary<int, object?>(current));
    }

    /// <summary>Equivalent to `prep.executeBatch()`: replays all queued rows in one transaction.</summary>
    public void ExecuteBatch()
    {
        if (queued.Count == 0) return;

        using var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = insertSql;

        for (var i = 1; i <= parameterCount; i++)
        {
            cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));
        }

        foreach (var row in queued)
        {
            for (var i = 1; i <= parameterCount; i++)
            {
                cmd.Parameters[$"@p{i}"].Value = row.TryGetValue(i, out var v) && v != null ? v : DBNull.Value;
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
