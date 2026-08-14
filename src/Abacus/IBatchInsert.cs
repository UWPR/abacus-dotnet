namespace Abacus;

/// <summary>
/// Mirrors the subset of java.sql.PreparedStatement (setString/setInt/setDouble
/// + addBatch) that pepXML.java and protXML.java use to stage rows for
/// insertion into the HyperSQL database. Parameter indices are 1-based to
/// match the original JDBC call sites exactly, minimizing transcription
/// errors when porting the files that call these methods.
///
/// The concrete implementation (backed by an ADO.NET/SQLite command, added
/// when hyperSQLObject.java is ported) decides how "batches" are actually
/// flushed to the database.
/// </summary>
public interface IBatchInsert
{
    void SetString(int index, string? value);
    void SetInt(int index, int value);
    void SetDouble(int index, double value);
    void AddBatch();
}
