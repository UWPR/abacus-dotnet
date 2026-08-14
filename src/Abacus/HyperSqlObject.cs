using Microsoft.Data.Sqlite;

namespace Abacus;

/// <summary>
/// Ported from abacus/hyperSQLObject.java - the spectral-counting aggregation
/// engine. Builds up a chain of working tables (srcFileTags, combined,
/// protXML, prot2peps_*, protidSummary, results, ...) from RAWprotXML/pepXML
/// and writes the final protein-centric output file.
///
/// See CLAUDE.md "Translation conventions" for the HSQLDB-to-SQLite dialect
/// rules applied throughout (CREATE CACHED/MEMORY TABLE, ALTER...BEFORE,
/// CREATE FUNCTION, etc).
/// </summary>
public class HyperSqlObject
{
    // variables specific to the database queries
    protected string? CombinedFile;
    protected string? DecoyTag;

    protected double MaxIniProbTh = -1;
    protected double IniProbTh = -1;
    protected double MinCombinedFilePw = -1;
    protected double MinPw = -1;

    // fixed, not adjusted by the user
    protected const double WtTh = 0.9;

    // ---- small ADO.NET helpers to keep the SQL-heavy methods below readable ----

    protected static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Begins a transaction batching every subsequent statement on `conn` until
    /// `Commit()` (matching the Java original's `setAutoCommit(false); ...;
    /// executeBatch(); setAutoCommit(true)` pattern around its per-row loops -
    /// without this, SQLite auto-commits, and fsyncs, every single unbatched
    /// `ExecuteNonQuery()` individually, which is cheap for the default
    /// in-memory DB but catastrophically slow for `keepDB=true`'s on-disk one).
    /// Any `SqliteCommand` created via `conn.CreateCommand()` *after* the
    /// transaction begins auto-adopts it, but one created *before* does not -
    /// `SqliteCommand.Transaction` is captured at creation time, not resolved
    /// dynamically at execution time - so any command reused across the loop
    /// being batched (as opposed to a fresh one created per-row by `Exec`/
    /// `ExecScalar*`/`ExecuteReader`, which are always safe regardless of
    /// ordering) must be passed here to get `.Transaction` assigned explicitly.
    /// </summary>
    protected static SqliteTransaction BeginTransaction(SqliteConnection conn, params SqliteCommand[] cmds)
    {
        var tx = conn.BeginTransaction();
        foreach (var cmd in cmds) cmd.Transaction = tx;
        return tx;
    }

    protected static int ExecScalarInt(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? 0 : Convert.ToInt32(result);
    }

    protected static double ExecScalarDouble(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? 0.0 : Convert.ToDouble(result);
    }

    protected static string? ExecScalarString(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result is DBNull ? null : Convert.ToString(result);
    }

    /// <summary>Rounds to `digits` significant figures (replaces the original's locale-sensitive `String.format("%.Ng")` round-trip).</summary>
    protected static double RoundToSignificantFigures(double value, int digits)
    {
        if (value == 0) return 0;
        var scale = Math.Pow(10, digits - 1 - Math.Floor(Math.Log10(Math.Abs(value))));
        return Math.Round(value * scale) / scale;
    }

    /// <summary>
    /// Maps every column of `tableName` to its declared SQL type (e.g. "VARCHAR(20)",
    /// "DECIMAL(8,6)", "INT"), for columns added via `ALTER TABLE ... ADD COLUMN &lt;type&gt;`
    /// (SQLite preserves that declaration even though `CREATE TABLE ... AS SELECT`
    /// columns get no type at all). Used by <see cref="FormatCell"/> to render a NULL
    /// cell the way Java's declared-type-dispatched `ResultSet.getInt/getDouble/getString`
    /// would have.
    /// </summary>
    protected static Dictionary<string, string> GetColumnTypes(SqliteConnection conn, string tableName)
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            types[name] = reader.IsDBNull(2) ? "" : reader.GetString(2);
        }
        return types;
    }

    /// <summary>
    /// Formats a single cell for the tab-delimited output writers, mirroring the
    /// Java `ResultSetMetaData.getColumnType(i)` three-way dispatch
    /// (INTEGER/VARCHAR/default-double). `roundDoubles` matches per-writer
    /// behavior: defaultResults/formatQspecOutput/customOutput round to 4
    /// decimals; peptideLevelResults does not (ported faithfully as found).
    /// </summary>
    protected static string FormatCell(SqliteDataReader reader, int i, bool roundDoubles = true, IReadOnlyDictionary<string, string>? columnTypes = null)
    {
        // Java's writer.append((String)null) for a NULL VARCHAR cell literally
        // writes the text "null"; NULL INTEGER cells print "0" and NULL DOUBLE
        // cells print "0.0" (via ResultSet.getInt/getDouble, which default to
        // 0/0.0 for SQL NULL per the JDBC spec). Reproducing that needs to know
        // each column's *declared* type, which SQLite only preserves for columns
        // added via `ALTER TABLE ... ADD COLUMN <type>` (the vast majority of
        // `results`/`geneResults` - see GetColumnTypes) - not for the handful of
        // base columns that come straight from a `CREATE TABLE ... AS SELECT`
        // (e.g. `defline`), which carry no declared type either in SQLite or in
        // the original HSQLDB schema Java read via getColumnType. For those,
        // and whenever no columnTypes map is supplied, "" remains the fallback.
        if (reader.IsDBNull(i))
        {
            if (columnTypes != null && columnTypes.TryGetValue(reader.GetName(i), out var declType) && declType.Length > 0)
            {
                var t = declType.ToUpperInvariant();
                if (t.StartsWith("VARCHAR") || t.StartsWith("CHAR") || t.StartsWith("TEXT")) return "null";
                if (t.StartsWith("INT")) return "0";
                if (t.StartsWith("DECIMAL") || t.StartsWith("DOUBLE") || t.StartsWith("FLOAT") || t.StartsWith("REAL")) return "0.0";
            }
            return "";
        }

        var fieldType = reader.GetFieldType(i);
        if (fieldType == typeof(long)) return reader.GetInt64(i).ToString();
        if (fieldType == typeof(string)) return reader.GetString(i);

        var d = reader.GetDouble(i);
        return roundDoubles ? Globals.RoundDbl(d, 4).ToString() : d.ToString();
    }

    public HyperSqlObject()
    {
    }

    public virtual void Initialize()
    {
        if (!Globals.ByPeptide)
        {
            CombinedFile = Globals.CombinedFile;
            DecoyTag = Globals.DecoyTag;
            MaxIniProbTh = Globals.MaxIniProbTh;
            MinCombinedFilePw = Globals.MinCombinedFilePw;
            MinPw = Globals.MinPw;
        }
        IniProbTh = Globals.IniProbTh;
    }

    public virtual void MakeSrcFileTable(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("Creating srcFileTags table\n");
        else Console.Error.Write("Creating srcFileTags table\n");

        if (Globals.PepTagHash.Count == 0) Globals.RecordPepXmlTags();

        Exec(conn, "DROP TABLE IF EXISTS srcFileTags");
        Exec(conn, """
            CREATE TABLE srcFileTags (
              srcFile VARCHAR(250),
              tag VARCHAR(250),
              fileType VARCHAR(20)
            )
            """);

        var n = 3; // 3 indexes get built, so preset N to 3
        n += Globals.ProtTagHash.Count;
        n += Globals.PepTagHash.Count;
        console?.MonitorBoxInit(n, "srcFileTags Table");

        var ctr = 0;
        using (var cmd = conn.CreateCommand())
        using (var tx = conn.BeginTransaction())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO srcFileTags VALUES (@srcFile, @tag, @type)";
            var pSrc = cmd.Parameters.Add("@srcFile", Microsoft.Data.Sqlite.SqliteType.Text);
            var pTag = cmd.Parameters.Add("@tag", Microsoft.Data.Sqlite.SqliteType.Text);
            var pType = cmd.Parameters.Add("@type", Microsoft.Data.Sqlite.SqliteType.Text);

            if (!Globals.ByPeptide)
            {
                ctr = 1;
                foreach (var (srcFile, value) in Globals.ProtTagHash)
                {
                    var tag = Globals.ReplaceAll(Globals.ReplaceAll(value, '.', '_'), '-', '_');
                    if (char.IsDigit(tag[0])) tag = "x" + tag;

                    pSrc.Value = srcFile.ToUpperInvariant();
                    pTag.Value = tag.ToUpperInvariant();
                    pType.Value = "prot";
                    cmd.ExecuteNonQuery();
                    ctr++;
                    console?.MonitorBoxUpdate(ctr);
                }

                if (console != null) console.Append("\n");
                else Console.Error.Write("\n");
            }

            foreach (var (srcFile, value) in Globals.PepTagHash)
            {
                var tag = Globals.ReplaceAll(Globals.ReplaceAll(value, '.', '_'), '-', '_');
                if (char.IsDigit(tag[0])) tag = "x" + tag;

                pSrc.Value = srcFile.ToUpperInvariant();
                pTag.Value = tag.ToUpperInvariant();
                pType.Value = "pep";
                cmd.ExecuteNonQuery();
                ctr++;
                console?.MonitorBoxUpdate(ctr);
            }
            tx.Commit();
        }

        Exec(conn, "CREATE INDEX sf_idx1 ON srcFileTags(srcFile)");
        console?.MonitorBoxUpdate(ctr++);
        Exec(conn, "CREATE INDEX sf_idx2 ON srcFileTags(tag)");
        console?.MonitorBoxUpdate(ctr++);
        Exec(conn, "CREATE INDEX sf_idx3 ON srcFileTags(fileType)");
        console?.MonitorBoxUpdate(ctr);
        console?.CloseMonitorBox();

        // save on memory
        Globals.ProtTagHash.Clear();
        Globals.PepTagHash.Clear();

        if (console != null) console.Append("\n");
        else Console.Error.Write("\n");
    }

    /// <summary>Creates the `combined` table from the COMBINED protXML file's staged rows.</summary>
    public virtual void MakeCombinedTable(SqliteConnection conn, IConsole? console)
    {
        var msg = $"Creating combined table from '{Globals.CombinedFile}'\n";
        if (console != null) console.Append(msg);
        else Console.Error.Write(msg);

        Exec(conn, "DROP TABLE IF EXISTS combined");
        Exec(conn, """
            CREATE TABLE combined (
              groupid INT,
              siblingGroup VARCHAR(5),
              Pw DECIMAL(8,6),
              localPw DECIMAL(8,6),
              protId VARCHAR(250),
              protLen INT DEFAULT 0,
              isFwd INT,
              modPeptide VARCHAR(250),
              charge INT,
              iniProb DECIMAL(8,6),
              wt DECIMAL(8,6),
              defline VARCHAR(1000)
            )
            """);

        // progress-monitor count
        var n = ExecScalarInt(conn, $"""
            SELECT COUNT(*) FROM RAWprotXML
            WHERE srcFile = '{CombinedFile!.ToUpperInvariant()}'
            AND Pw >= {MinPw}
            AND iniProb >= {IniProbTh}
            """);

        if (n == 0) // nothing in RAWprotXML for the COMBINED file
        {
            var err = "\nERROR:\n"
                + "Nothing in your COMBINED file met your input parameters.\n"
                + "Please adjust your Abacus parameters and try again.\n"
                + "Now quiting....\n";
            if (console != null) console.Append(err);
            else Console.Error.Write(err);
            Environment.Exit(-1);
        }

        console?.MonitorBoxInit(n, "COMBINED table");

        using (var cmd = conn.CreateCommand())
        using (var tx = BeginTransaction(conn, cmd))
        {
            cmd.CommandText = "INSERT INTO combined VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12)";
            for (var i = 1; i <= 12; i++) cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

            using var reader = ExecuteReader(conn, $"""
                SELECT groupid, siblingGroup, Pw, localPw, protId, isFwd,
                  modPeptide, charge, iniProb, wt, defline
                FROM RAWprotXML
                WHERE srcFile = '{CombinedFile.ToUpperInvariant()}'
                AND Pw >= {MinCombinedFilePw}
                AND iniProb >= {IniProbTh}
                GROUP BY groupid, siblingGroup, Pw, localPw, protId, isFwd,
                  modPeptide, charge, iniProb, wt, defline
                ORDER BY groupid, siblingGroup
                """);

            var iter = 1;
            while (reader.Read())
            {
                cmd.Parameters["@p1"].Value = reader.GetInt32(0);
                cmd.Parameters["@p2"].Value = reader.GetString(1);
                cmd.Parameters["@p3"].Value = reader.GetDouble(2);
                cmd.Parameters["@p4"].Value = reader.GetDouble(3);
                var protid = reader.GetString(4);
                cmd.Parameters["@p5"].Value = protid;

                var len = 0;
                if (!string.IsNullOrEmpty(Globals.FastaFile) && Globals.ProtLen.TryGetValue(protid, out var pl)) len = pl;
                cmd.Parameters["@p6"].Value = len;

                cmd.Parameters["@p7"].Value = reader.GetInt32(5);
                cmd.Parameters["@p8"].Value = reader.GetString(6);
                cmd.Parameters["@p9"].Value = reader.GetInt32(7);
                cmd.Parameters["@p10"].Value = reader.GetDouble(8);
                cmd.Parameters["@p11"].Value = reader.GetDouble(9);
                cmd.Parameters["@p12"].Value = reader.IsDBNull(10) ? DBNull.Value : reader.GetString(10);

                cmd.ExecuteNonQuery();
                console?.MonitorBoxUpdate(iter);
                iter++;
            }
            tx.Commit();
        }
        console?.CloseMonitorBox();

        Exec(conn, "CREATE INDEX com_idx1 ON combined(groupid, siblingGroup)");
        Exec(conn, "CREATE INDEX com_idx2 ON combined(protid)");
        Exec(conn, "CREATE INDEX com_idx3 ON combined(modPeptide, charge)");
        Exec(conn, "CREATE INDEX com_idx4 ON combined(modPeptide)");

        Exec(conn, $"DELETE FROM RAWprotXML WHERE srcFile = '{CombinedFile}'");

        // clean up combined file cases where maxLocalPw
        CurateOnMaxLocalPw(Globals.CombinedFile!, conn, console);
        RecalculatePeptideWts(conn, "combined", console);

        if (console != null) console.Append("\n\n");
        Console.Error.Write("\n\n");
    }

    /// <summary>Keeps only sibling groups that meet the combined file's localPw threshold.</summary>
    private void CurateOnMaxLocalPw(string xmlId, SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append($"  Curating {xmlId}");
        else Console.Error.Write($"  Curating {xmlId}");

        if (xmlId != CombinedFile) return;

        using var reader = ExecuteReader(conn, "SELECT groupid, MAX(localPw) as maxLocalPw FROM combined GROUP BY groupid");
        var rows = new List<(int gid, double maxLocalPw)>();
        while (reader.Read()) rows.Add((reader.GetInt32(0), reader.GetDouble(1)));
        reader.Close();

        using (var tx = conn.BeginTransaction())
        {
            foreach (var (gid, maxLocalPw) in rows)
            {
                if (maxLocalPw == 0) // all sibling groups have probability 0; keep only 'a'
                {
                    Exec(conn, $"DELETE FROM combined WHERE groupid = {gid} AND siblingGroup != 'a'");
                }
                else
                {
                    Exec(conn, $"DELETE FROM combined WHERE groupid = {gid} AND localPw < {Globals.MinCombinedFilePw}");
                }
            }
            tx.Commit();
        }
    }

    /// <summary>Creates the `protXML` table (all non-combined protXML files, flattened).</summary>
    public virtual void MakeProtXmlTable(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("Creating protXML table\n");
        else Console.Error.Write("Creating protXML table\n");

        Exec(conn, "DROP TABLE IF EXISTS protXML");
        Exec(conn, """
            CREATE TABLE protXML (
              tag VARCHAR(250),
              srcFile VARCHAR(250),
              groupid INT,
              siblingGroup VARCHAR(5),
              Pw DECIMAL(8,6),
              localPw DECIMAL(8,6),
              protId VARCHAR(250),
              isFwd INT,
              modPeptide VARCHAR(250),
              charge INT,
              iniProb DECIMAL(8,6),
              wt DECIMAL(8,6),
              defline VARCHAR(1000)
            )
            """);

        var n = ExecScalarInt(conn, $"""
            SELECT COUNT(*) FROM RAWprotXML
            WHERE Pw >= {MinPw}
            AND iniProb >= {IniProbTh}
            AND srcFile != '{CombinedFile}'
            """);
        console?.MonitorBoxInit(n, "protXML table");

        using (var cmd = conn.CreateCommand())
        using (var tx = BeginTransaction(conn, cmd))
        {
            cmd.CommandText = "INSERT INTO protXML VALUES (@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13)";
            for (var i = 1; i <= 13; i++) cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

            using var reader = ExecuteReader(conn, $"""
                SELECT srcFile, srcFile,
                  groupid, siblingGroup, Pw, localPw, protId, isFwd,
                  modPeptide, charge, iniProb, wt, defline
                FROM RAWprotXML
                WHERE Pw >= {MinPw}
                AND iniProb >= {IniProbTh}
                AND srcFile != '{CombinedFile!.ToUpperInvariant()}'
                GROUP BY srcFile, groupid, siblingGroup, Pw, localPw, protId,
                  isFwd, modPeptide, charge, iniProb, wt, defline
                ORDER BY srcFile, groupid, siblingGroup, protId, modPeptide
                """);

            var iter = 0;
            var msg = "  Populating protXML table...";
            while (reader.Read())
            {
                cmd.Parameters["@p1"].Value = reader.GetString(0);
                cmd.Parameters["@p2"].Value = reader.GetString(1);
                cmd.Parameters["@p3"].Value = reader.GetInt32(2);
                cmd.Parameters["@p4"].Value = reader.GetString(3);
                cmd.Parameters["@p5"].Value = reader.GetDouble(4);
                cmd.Parameters["@p6"].Value = reader.GetDouble(5);
                cmd.Parameters["@p7"].Value = reader.GetString(6);
                cmd.Parameters["@p8"].Value = reader.GetInt32(7);
                cmd.Parameters["@p9"].Value = reader.GetString(8);
                cmd.Parameters["@p10"].Value = reader.GetInt32(9);
                cmd.Parameters["@p11"].Value = reader.GetDouble(10);
                cmd.Parameters["@p12"].Value = reader.GetDouble(11);
                cmd.Parameters["@p13"].Value = reader.IsDBNull(12) ? DBNull.Value : reader.GetString(12);
                cmd.ExecuteNonQuery();

                if (console != null) console.MonitorBoxUpdate(iter);
                else Globals.CursorStatus(iter, msg);
                iter++;
            }
            tx.Commit();
        }
        if (console != null) console.CloseMonitorBox();
        else Console.Error.Write("\n");

        if (console != null) console.Append("  Indexing protXML table (This can take a while...)\n");
        else Console.Error.Write("  Indexing protXML table (This can take a while...)\n");

        console?.MonitorBoxInit(9, "Indexing protXML...");
        var idxIter = 0;
        Exec(conn, "CREATE INDEX protXML_idx1 ON protXML(tag)");
        console?.MonitorBoxUpdate(idxIter++);
        Exec(conn, "CREATE INDEX protXML_idx2 ON protXML(groupid, siblingGroup)");
        console?.MonitorBoxUpdate(idxIter++);
        Exec(conn, "CREATE INDEX protXML_idx3 ON protXML(modPeptide, charge)");
        console?.MonitorBoxUpdate(idxIter++);
        Exec(conn, "CREATE INDEX protXML_idx4 ON protXML(protid)");
        console?.MonitorBoxUpdate(idxIter++);
        Exec(conn, "CREATE INDEX protXML_idx5 ON protXML(srcFile)");
        console?.MonitorBoxUpdate(idxIter++);

        using (var reader = ExecuteReader(conn, "SELECT srcFile, tag FROM srcFileTags WHERE fileType = 'prot' GROUP BY srcFile, tag"))
        {
            var updates = new List<(string srcFile, string tag)>();
            while (reader.Read()) updates.Add((reader.GetString(0), reader.GetString(1)));
            reader.Close();
            foreach (var (srcFile, tag) in updates)
            {
                Exec(conn, $"UPDATE protXML SET tag = '{tag}' WHERE srcFile = '{srcFile}'");
            }
        }
        console?.MonitorBoxUpdate(idxIter++);

        Exec(conn, "DROP INDEX IF EXISTS protXML_idx9");
        console?.MonitorBoxUpdate(idxIter++);
        // Unlike HSQLDB, SQLite doesn't auto-drop indexes that reference a
        // column being removed - it leaves a dangling, broken index instead.
        // protXML_idx5 indexes srcFile, so it has to go first.
        Exec(conn, "DROP INDEX IF EXISTS protXML_idx5");
        Exec(conn, "ALTER TABLE protXML DROP COLUMN srcFile");
        console?.MonitorBoxUpdate(idxIter++);
        Exec(conn, "CREATE INDEX protXML_idx6 ON protXML(tag)");
        console?.MonitorBoxUpdate(idxIter);

        Exec(conn, "DROP TABLE IF EXISTS RAWprotXML");

        if (console != null)
        {
            console.CloseMonitorBox();
            console.Append("  Indexing of protXML completed\n");
        }
        else Console.Error.Write("  Indexing of protXML completed\n");

        // remove proteins without at least 1 peptide >= epiThreshold, if stricter than iniProbTH
        if (Globals.EpiThreshold > Globals.IniProbTh)
        {
            var epiMsg = $"  Applying Experimental Peptide Inclusion threshold (EPI >= {Globals.EpiThreshold})\n";
            if (console != null) console.Append(epiMsg);
            else Console.Error.Write(epiMsg);

            Exec(conn, """
                CREATE TABLE x_ AS
                SELECT tag AS tag, groupid AS groupid, siblingGroup AS siblingGroup, MAX(iniProb) AS maxIniProb
                FROM protXML
                GROUP BY tag, groupid, siblingGroup
                """);
            Exec(conn, "CREATE INDEX x_1 ON x_(tag)");
            Exec(conn, "CREATE INDEX x_2 ON x_(maxIniProb)");
            Exec(conn, "CREATE INDEX x_3 ON x_(tag, groupid, siblingGroup)");
            Exec(conn, "CREATE INDEX x_4 ON x_(groupid, siblingGroup)");

            using (var reader = ExecuteReader(conn, $"SELECT * FROM x_ WHERE maxIniProb < {Globals.EpiThreshold}"))
            {
                var toDelete = new List<(string tag, int gid, string sib)>();
                while (reader.Read()) toDelete.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2)));
                reader.Close();
                foreach (var (tag, gid, sib) in toDelete)
                {
                    Exec(conn, $"DELETE FROM protXML WHERE tag = '{tag}' AND groupid = {gid} AND siblingGroup = '{sib}'");
                }
            }

            Exec(conn, "DROP INDEX IF EXISTS x_4");
            Exec(conn, "DROP INDEX IF EXISTS x_3");
            Exec(conn, "DROP INDEX IF EXISTS x_2");
            Exec(conn, "DROP INDEX IF EXISTS x_1");
            Exec(conn, "DROP TABLE IF EXISTS x_");
        }

        if (Globals.RecalcPepWts)
        {
            // recalculate peptide weights for peptides shared only among isoforms within the same protein group
            using var reader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot'");
            var tags = new List<string>();
            while (reader.Read()) tags.Add(reader.GetString(0));
            reader.Close();
            foreach (var tag in tags)
            {
                RecalculatePeptideWts(conn, tag, console);
                RecalculateLocalPw(conn, tag, console);
            }
        }

        if (console == null) Console.Error.Write("\n");
        else console.Append("\n");
    }

    /// <summary>Recomputes localPw for highly degenerate protein groups where all sibling groups are interrelated isoforms.</summary>
    public virtual void RecalculateLocalPw(SqliteConnection conn, string tag, IConsole? console)
    {
        Exec(conn, "DROP TABLE IF EXISTS x_");
        Exec(conn, $"""
            CREATE TABLE x_ AS
            SELECT groupid AS groupid, Pw AS Pw, MAX(localPw) AS maxLocalPw
            FROM protXML
            WHERE tag = '{tag}'
            GROUP BY groupid, Pw
            """);
        Exec(conn, "CREATE INDEX x_idx1 ON x_ (groupid)");

        var n = ExecScalarInt(conn, "SELECT COUNT(DISTINCT groupid) FROM x_ WHERE Pw > 0 AND maxLocalPw = 0");

        if (n > 0)
        {
            using var reader = ExecuteReader(conn, "SELECT DISTINCT groupid FROM x_ WHERE Pw > 0 AND maxLocalPw = 0");
            var gids = new List<int>();
            while (reader.Read()) gids.Add(reader.GetInt32(0));
            reader.Close();

            using var cmd = conn.CreateCommand();
            using var tx = BeginTransaction(conn, cmd);
            cmd.CommandText = $"UPDATE protXML SET localPw = Pw WHERE tag = '{tag}' AND groupid = @gid";
            var p = cmd.Parameters.Add("@gid", SqliteType.Integer);
            foreach (var gid in gids)
            {
                p.Value = gid;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        Exec(conn, "DROP INDEX x_idx1");
        Exec(conn, "DROP TABLE x_");
    }

    /// <summary>
    /// Adjusts peptide weights in protXML/combined: peptides shared among isoforms
    /// should all get the same weight (1 / number of groups sharing them).
    /// </summary>
    public virtual void RecalculatePeptideWts(SqliteConnection conn, string tag, IConsole? console)
    {
        if (console != null) console.Append($"\n  Recalculating peptide weights for {tag}");
        else Console.Error.Write($"\n  Recalculating peptide weights for {tag}");

        var isCombined = tag == "combined";

        Exec(conn, "DROP TABLE IF EXISTS wt_");
        Exec(conn, isCombined
            ? "CREATE TABLE wt_ AS SELECT groupid AS groupid, modPeptide AS modPeptide FROM combined GROUP BY groupid, modPeptide ORDER BY groupid"
            : $"CREATE TABLE wt_ AS SELECT groupid AS groupid, modPeptide AS modPeptide FROM protXML WHERE tag = '{tag}' GROUP BY groupid, modPeptide ORDER BY groupid");

        Exec(conn, "CREATE INDEX wt_idx1 ON wt_ (groupid)");
        Exec(conn, "CREATE INDEX wt_idx2 ON wt_ (modPeptide)");

        var updateSql = isCombined
            ? "UPDATE combined SET wt = @wt WHERE modPeptide = @modPep"
            : $"UPDATE protXML SET wt = @wt WHERE tag = '{tag}' AND modPeptide = @modPep";

        using (var updateCmd = conn.CreateCommand())
        {
            updateCmd.CommandText = updateSql;
            var pWt = updateCmd.Parameters.Add("@wt", SqliteType.Real);
            var pModPep = updateCmd.Parameters.Add("@modPep", SqliteType.Text);

            using var reader = ExecuteReader(conn, "SELECT DISTINCT modPeptide FROM wt_");
            var modPeps = new List<string>();
            while (reader.Read()) modPeps.Add(reader.GetString(0));
            reader.Close();

            using var tx = BeginTransaction(conn, updateCmd);
            foreach (var modPep in modPeps)
            {
                var count = ExecScalarInt(conn, $"SELECT COUNT(DISTINCT groupid) FROM wt_ WHERE modPeptide = '{modPep.Replace("'", "''")}'");
                var newWt = RoundToSignificantFigures(1.0 / count, 3);

                pWt.Value = newWt;
                pModPep.Value = modPep;
                updateCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        Exec(conn, "DROP TABLE IF EXISTS wt_");
        Exec(conn, "DROP INDEX IF EXISTS wt_idx1");
        Exec(conn, "DROP INDEX IF EXISTS wt_idx2");
    }

    /// <summary>
    /// Creates a per-protein summary of the peptide data used across the
    /// combined table and each individual protXML tag's table.
    /// </summary>
    public virtual void MakeTempProt2PepTable(SqliteConnection conn, IConsole? console)
    {
        Exec(conn, "DROP TABLE IF EXISTS prot2peps_combined");
        Exec(conn, """
            CREATE TABLE prot2peps_combined (
              protid VARCHAR(100),
              modpeptide VARCHAR(250),
              charge INT,
              wt DECIMAL(8,6),
              iniProb DECIMAL(8,6),
              nspecs INT
            )
            """);

        var n = ExecScalarInt(conn, "SELECT COUNT(*) FROM combined");

        var msg = "  Mapping peptides to proteins (combined) ";
        if (console != null)
        {
            console.Append(msg + "\n");
            console.MonitorBoxInit(n + 2, "Combined file peptides...");
        }
        else Console.Error.WriteLine(msg);

        using (var cmd = conn.CreateCommand())
        using (var tx = BeginTransaction(conn, cmd))
        {
            cmd.CommandText = "INSERT INTO prot2peps_combined VALUES (@p1,@p2,@p3,@p4,@p5,@p6)";
            for (var i = 1; i <= 6; i++) cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

            using var reader = ExecuteReader(conn, """
                SELECT c.protid, c.modPeptide, c.charge, c.wt, c.iniProb,
                    COUNT(DISTINCT px.specId)
                FROM combined c, pepXML px
                WHERE c.modPeptide = px.modPeptide
                AND c.charge = px.charge
                GROUP BY c.protid, c.modPeptide, c.charge, c.wt, c.iniProb
                ORDER BY c.protid, c.modPeptide, c.charge
                """);

            var iter = 0;
            while (reader.Read())
            {
                cmd.Parameters["@p1"].Value = reader.GetString(0);
                cmd.Parameters["@p2"].Value = reader.GetString(1);
                cmd.Parameters["@p3"].Value = reader.GetInt32(2);
                cmd.Parameters["@p4"].Value = reader.GetDouble(3);
                cmd.Parameters["@p5"].Value = reader.GetDouble(4);
                cmd.Parameters["@p6"].Value = reader.GetInt32(5);
                cmd.ExecuteNonQuery();

                iter++;
                if (console != null) console.MonitorBoxUpdate(iter);
                else Globals.CursorStatus(iter, msg);
            }
            tx.Commit();
        }

        Exec(conn, "CREATE INDEX pt2pep_combined_idx1 ON prot2peps_combined(protid)");
        Exec(conn, "CREATE INDEX pt2pep_combined_idx2 ON prot2peps_combined(modPeptide, charge)");
        if (console != null) console.CloseMonitorBox();
        else Console.Error.Write("\n");

        // one table per individual experimental file, for speed
        using (var reader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot'"))
        {
            var tags = new List<string>();
            while (reader.Read()) tags.Add(reader.GetString(0));
            reader.Close();

            foreach (var tag in tags)
            {
                if (console != null) console.Append($"  Mapping peptides to proteins ({tag})\n");
                else Console.Error.Write($"  Mapping peptides to proteins ({tag})\n");

                Exec(conn, $"DROP TABLE IF EXISTS prot2peps_{tag}");
                Exec(conn, $"""
                    CREATE TABLE prot2peps_{tag} AS
                    SELECT pr.protid AS protid, pr.modPeptide AS modPeptide, pr.charge AS charge,
                      pr.wt AS wt, pr.iniProb AS iniProb,
                      COUNT(DISTINCT px.specId) AS nspecs
                    FROM protXML pr, pepXML px
                    WHERE pr.tag = '{tag}'
                    AND pr.tag = px.tag
                    AND px.modPeptide = pr.modPeptide
                    AND px.charge = pr.charge
                    GROUP BY pr.protid, pr.modPeptide, pr.charge, pr.wt, pr.iniProb
                    ORDER BY pr.protid, pr.modPeptide, pr.charge
                    """);

                Exec(conn, $"CREATE INDEX pt2pep_{tag}_idx1 ON prot2peps_{tag}(protid)");
                Exec(conn, $"CREATE INDEX pt2pep_{tag}_idx2 ON prot2peps_{tag}(modPeptide, charge)");
            }
        }

        if (console != null) console.Append("\n");
        else Console.Error.Write("\n");
    }

    /// <summary>Builds the protidSummary table: one representative protein ID per groupid/siblingGroup.</summary>
    public virtual void MakeProtidSummary(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("\nCreating protidSummary table\n");
        else Console.Error.Write("\nCreating protidSummary table\n");

        var msg = $"  Collecting list of all proteins identified in {CombinedFile}\n";
        if (console != null) console.Append(msg);
        else Console.Error.Write(msg);

        Exec(conn, """
            CREATE TABLE t1_ (
              groupid INT,
              siblingGroup VARCHAR(10),
              protid VARCHAR(100),
              numXML INT DEFAULT 0,
              maxPw DECIMAL(8,6)
            )
            """);

        using (var cmd = conn.CreateCommand())
        using (var tx = BeginTransaction(conn, cmd))
        {
            cmd.CommandText = "INSERT INTO t1_ (groupid, siblingGroup, protid) VALUES (@p1,@p2,@p3)";
            for (var i = 1; i <= 3; i++) cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

            using var reader = ExecuteReader(conn, $"""
                SELECT groupid, siblingGroup, protid
                FROM combined
                WHERE wt > {WtTh}
                AND Pw > {MinCombinedFilePw}
                GROUP BY groupid, siblingGroup, protid
                ORDER BY groupid, siblingGroup
                """);
            while (reader.Read())
            {
                cmd.Parameters["@p1"].Value = reader.GetInt32(0);
                cmd.Parameters["@p2"].Value = reader.GetString(1);
                cmd.Parameters["@p3"].Value = reader.GetString(2);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        Exec(conn, "CREATE INDEX t1_idx1 ON t1_(protid)");
        Exec(conn, "CREATE INDEX t1_idx2 ON t1_(groupid, siblingGroup)");

        var n = ExecScalarInt(conn, $"""
            SELECT COUNT(*) FROM (
              SELECT groupid, siblingGroup, protid
              FROM combined
              WHERE wt > {WtTh}
              AND Pw > {MinCombinedFilePw}
              GROUP BY groupid, siblingGroup, protid
            )
            """);
        console?.MonitorBoxInit(n, "Getting protein frequencies...");

        var freqMsg = "  Counting protein frequencies across independent files\n";
        if (console != null) console.Append(freqMsg);
        else Console.Error.Write(freqMsg);

        using (var reader = ExecuteReader(conn, "SELECT protid, COUNT(DISTINCT tag) AS f FROM protXML GROUP BY protid"))
        {
            var freqRows = new List<(string protid, int f)>();
            while (reader.Read()) freqRows.Add((reader.GetString(0), reader.GetInt32(1)));
            reader.Close();

            var iter = 0;
            using (var tx = conn.BeginTransaction())
            {
                foreach (var (protid, f) in freqRows)
                {
                    Exec(conn, $"UPDATE t1_ SET numXML = {f} WHERE protid = '{protid}'");
                    iter++;
                    if (console != null) console.MonitorBoxUpdate(iter);
                    else Globals.CursorStatus(iter, "  Getting protein frequencies ");
                }
                tx.Commit();
            }
        }
        if (console != null) console.CloseMonitorBox();
        else Console.Error.Write("\n");

        // removes proteins only identified in the COMBINED file
        Exec(conn, "DELETE FROM t1_ WHERE numXML = 0");

        var pwMsg = "  Recording best ProteinProphet scores for each protein\n";
        if (console != null) console.Append(pwMsg);
        else Console.Error.Write(pwMsg);

        console?.MonitorBoxInit(n, "Collecting protein probabilities...");
        using (var reader = ExecuteReader(conn, "SELECT protid, MAX(Pw) as mPw FROM protXML GROUP BY protid"))
        {
            var pwRows = new List<(string protid, double pw)>();
            while (reader.Read()) pwRows.Add((reader.GetString(0), reader.GetDouble(1)));
            reader.Close();

            var iter = 0;
            using (var tx = conn.BeginTransaction())
            {
                foreach (var (protid, pw) in pwRows)
                {
                    Exec(conn, $"UPDATE t1_ SET maxPw = {pw} WHERE protid = '{protid}'");
                    iter++;
                    if (console != null) console.MonitorBoxUpdate(iter);
                    else Globals.CursorStatus(iter, "  Collecting protein probabilities ");
                }
                tx.Commit();
            }
        }
        if (console != null) console.CloseMonitorBox();
        else Console.Error.Write("\n");

        Exec(conn, "CREATE TABLE t2_ (groupid INT, siblingGroup VARCHAR(5), numXML INT, maxPw DECIMAL(8,6))");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO t2_ VALUES (@p1,@p2,@p3,@p4)";
            for (var i = 1; i <= 4; i++) cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

            using var reader = ExecuteReader(conn, """
                SELECT groupid, siblingGroup, MAX(numXML), MAX(maxPw)
                FROM t1_
                GROUP BY groupid, siblingGroup
                ORDER BY groupid, siblingGroup
                """);
            console?.MonitorBoxInit(n, "Selecting candidate proteins...");
            var iter = 0;
            using var tx = BeginTransaction(conn, cmd);
            while (reader.Read())
            {
                cmd.Parameters["@p1"].Value = reader.GetInt32(0);
                cmd.Parameters["@p2"].Value = reader.GetString(1);
                cmd.Parameters["@p3"].Value = reader.GetInt32(2);
                cmd.Parameters["@p4"].Value = reader.GetDouble(3);
                cmd.ExecuteNonQuery();
                iter++;
                console?.MonitorBoxUpdate(iter);
            }
            tx.Commit();
        }
        console?.CloseMonitorBox();

        Exec(conn, "CREATE INDEX t2_idx1 ON t2_(groupid, siblingGroup)");

        Exec(conn, """
            CREATE TABLE t3_ (
              groupid INT,
              siblingGroup VARCHAR(10),
              protid VARCHAR(100),
              numXML INT,
              maxPw DECIMAL(8,6),
              numPepsTot INT DEFAULT 0,
              numPepsUniq INT DEFAULT 0,
              numSpecsTot INT DEFAULT 0,
              numSpecsUniq INT DEFAULT 0,
              maxIniProb DECIMAL(8,6),
              wt_maxIniProb DECIMAL(8,6),
              maxIniProbUniq DECIMAL(8,6)
            )
            """);

        if (console != null) console.Append("  Creating selection heuristics table (This could take a while)...\n");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO t3_ (groupid, siblingGroup, protid) VALUES (@p1,@p2,@p3)";
            for (var i = 1; i <= 3; i++) cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

            using var reader = ExecuteReader(conn, """
                SELECT groupid, siblingGroup, protid
                FROM t1_
                GROUP BY groupid, siblingGroup, protid
                ORDER BY groupid, siblingGroup, protid
                """);
            console?.MonitorBoxInit(n + 2, "Building Heuristics...");
            var iter = 0;
            using var tx = BeginTransaction(conn, cmd);
            while (reader.Read())
            {
                cmd.Parameters["@p1"].Value = reader.GetInt32(0);
                cmd.Parameters["@p2"].Value = reader.GetString(1);
                cmd.Parameters["@p3"].Value = reader.GetString(2);
                cmd.ExecuteNonQuery();
                iter++;
                console?.MonitorBoxUpdate(iter);
            }
            tx.Commit();
        }

        Exec(conn, "CREATE INDEX t3_idx1 ON t3_(groupid, siblingGroup)");
        Exec(conn, "CREATE INDEX t3_idx2 ON t3_(protid)");
        console?.CloseMonitorBox();

        var n2 = ExecScalarInt(conn, "SELECT COUNT(*) FROM t2_");

        using (var cmd1 = conn.CreateCommand())
        using (var cmd2 = conn.CreateCommand())
        {
            cmd1.CommandText = "UPDATE t3_ SET numXML = @numXML WHERE groupid = @gid AND siblingGroup = @sib";
            var p1n = cmd1.Parameters.Add("@numXML", SqliteType.Integer);
            var p1g = cmd1.Parameters.Add("@gid", SqliteType.Integer);
            var p1s = cmd1.Parameters.Add("@sib", SqliteType.Text);

            cmd2.CommandText = "UPDATE t3_ SET maxPw = @maxPw WHERE groupid = @gid AND siblingGroup = @sib";
            var p2m = cmd2.Parameters.Add("@maxPw", SqliteType.Real);
            var p2g = cmd2.Parameters.Add("@gid", SqliteType.Integer);
            var p2s = cmd2.Parameters.Add("@sib", SqliteType.Text);

            using var reader = ExecuteReader(conn, "SELECT * FROM t2_ ORDER BY groupid, siblingGroup");
            console?.MonitorBoxInit(n2, "Building Heuristics (1/2)...");
            var iter = 0;
            using var tx = BeginTransaction(conn, cmd1, cmd2);
            while (reader.Read())
            {
                var gid = reader.GetInt32(0);
                var sib = reader.GetString(1);
                var numXml = reader.GetInt32(2);
                var maxPw = reader.GetDouble(3);

                p1n.Value = numXml; p1g.Value = gid; p1s.Value = sib;
                cmd1.ExecuteNonQuery();

                p2m.Value = maxPw; p2g.Value = gid; p2s.Value = sib;
                cmd2.ExecuteNonQuery();

                iter++;
                console?.MonitorBoxUpdate(iter);
            }
            tx.Commit();
        }
        console?.CloseMonitorBox();

        var n3 = ExecScalarInt(conn, "SELECT COUNT(*) FROM t1_");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE t3_
                  SET numPepsTot = @numPepsTot,
                      numPepsUniq = @numPepsUniq,
                      numSpecsTot = @numSpecsTot,
                      numSpecsUniq = @numSpecsUniq,
                      maxIniProb = @maxIniProb,
                      wt_maxIniProb = @wtMaxIniProb,
                      maxIniProbUniq = @maxIniProbUniq
                WHERE protid = @protid
                """;
            var pNumPepsTot = cmd.Parameters.Add("@numPepsTot", SqliteType.Integer);
            var pNumPepsUniq = cmd.Parameters.Add("@numPepsUniq", SqliteType.Integer);
            var pNumSpecsTot = cmd.Parameters.Add("@numSpecsTot", SqliteType.Integer);
            var pNumSpecsUniq = cmd.Parameters.Add("@numSpecsUniq", SqliteType.Integer);
            var pMaxIniProb = cmd.Parameters.Add("@maxIniProb", SqliteType.Real);
            var pWtMaxIniProb = cmd.Parameters.Add("@wtMaxIniProb", SqliteType.Real);
            var pMaxIniProbUniq = cmd.Parameters.Add("@maxIniProbUniq", SqliteType.Real);
            var pProtid = cmd.Parameters.Add("@protid", SqliteType.Text);

            using var reader = ExecuteReader(conn, "SELECT protid, numXML, maxPw FROM t3_ WHERE numXML > 0 GROUP BY protid, numXML, maxPw ORDER BY protid");
            console?.MonitorBoxInit(n3, "Building Heuristics (2/2)...");
            var iter = 0;
            using var tx = BeginTransaction(conn, cmd);
            while (reader.Read())
            {
                var protid = reader.GetString(0);

                var numPepsTot = RetNumPeps(conn, CombinedFile!, protid, 0, IniProbTh);
                var numPepsUniq = RetNumPeps(conn, CombinedFile!, protid, WtTh, IniProbTh);
                var numSpecsTot = RetNumSpectra(conn, CombinedFile!, protid, 0, IniProbTh);
                var numSpecsUniq = RetNumSpectra(conn, CombinedFile!, protid, WtTh, IniProbTh);
                var maxIniProb = RetMaxIniProb(conn, CombinedFile!, protid, 0);
                var wtMaxIniProb = RetWtMaxIniProb(conn, CombinedFile!, protid, maxIniProb);
                var maxIniProbUniq = RetMaxIniProb(conn, CombinedFile!, protid, WtTh);

                pNumPepsTot.Value = numPepsTot;
                pNumPepsUniq.Value = numPepsUniq;
                pNumSpecsTot.Value = numSpecsTot;
                pNumSpecsUniq.Value = numSpecsUniq;
                pMaxIniProb.Value = maxIniProb;
                pWtMaxIniProb.Value = wtMaxIniProb;
                pMaxIniProbUniq.Value = maxIniProbUniq;
                pProtid.Value = protid;
                cmd.ExecuteNonQuery();

                iter++;
                if (console == null) Globals.CursorStatus(iter, msg);
                else console.MonitorBoxUpdate(iter);
            }
            tx.Commit();
        }
        console?.CloseMonitorBox();

        // clean up
        Exec(conn, "DROP INDEX t1_idx1");
        Exec(conn, "DROP INDEX t1_idx2");
        Exec(conn, "DROP TABLE t1_");
        Exec(conn, "DROP INDEX t2_idx1");
        Exec(conn, "DROP TABLE t2_");

        Exec(conn, "DROP TABLE IF EXISTS protidSummary");
        Exec(conn, """
            CREATE TABLE protidSummary (
              groupid INT,
              siblingGroup VARCHAR(10),
              repID VARCHAR(100),
              numXML INT,
              maxPw DECIMAL(8,6),
              numPepsTot INT DEFAULT 0,
              numPepsUniq INT DEFAULT 0,
              numSpecsTot INT DEFAULT 0,
              numSpecsUniq INT DEFAULT 0,
              maxIniProb DECIMAL(8,6),
              wt_maxIniProb DECIMAL(8,6),
              maxIniProbUniq DECIMAL(8,6)
            )
            """);

        if (console != null) console.Append("  Picking representative protids\n");
        else Console.Error.Write("\n  Picking representative protids\n");

        var n4 = ExecScalarInt(conn, "SELECT COUNT(*) FROM (SELECT DISTINCT groupid, siblingGroup FROM t3_)") + 2;

        using (var groupReader = ExecuteReader(conn, "SELECT groupid, siblingGroup FROM t3_ GROUP BY groupid, siblingGroup ORDER BY groupid, siblingGroup"))
        {
            var groups = new List<(int gid, string sib)>();
            while (groupReader.Read()) groups.Add((groupReader.GetInt32(0), groupReader.GetString(1)));
            groupReader.Close();

            if (console != null) console.MonitorBoxInit(n4, "  Loading protidSummary table...");

            var iter = 0;
            foreach (var (gid, sib) in groups)
            {
                using var reader2 = ExecuteReader(conn, $"""
                    SELECT protid, numXML, maxPw, numPepsTot, numPepsUniq,
                      numSpecsTot, numSpecsUniq, maxIniProb, wt_maxIniProb,
                      maxIniProbUniq
                    FROM t3_
                    WHERE groupid = {gid}
                    AND siblingGroup = '{sib}'
                    AND maxIniProb >= {MaxIniProbTh}
                    GROUP BY protid, numXML, maxPw, numPepsTot, numPepsUniq,
                      numSpecsTot, numSpecsUniq, maxIniProb, wt_maxIniProb,
                      maxIniProbUniq
                    ORDER BY numXML DESC, maxPw DESC, maxIniProb DESC,
                      maxIniProbUniq DESC, numPepsUniq DESC, numSpecsUniq DESC, protid ASC
                    LIMIT 1
                    """);
                while (reader2.Read())
                {
                    Exec(conn, "INSERT INTO protidSummary VALUES ("
                        + $"{gid}, '{sib}', '{reader2.GetString(0)}', {reader2.GetInt32(1)}, {reader2.GetDouble(2)}, "
                        + $"{reader2.GetInt32(3)}, {reader2.GetInt32(4)}, {reader2.GetInt32(5)}, {reader2.GetInt32(6)}, "
                        + $"{reader2.GetDouble(7)}, {reader2.GetDouble(8)}, {reader2.GetDouble(9)})");
                }
                iter++;

                if (console != null) console.MonitorBoxUpdate(iter);
                else Globals.CursorStatus(iter, "  Loading protidSummary table...");
            }
        }

        Exec(conn, "CREATE INDEX ps_idx1 ON protidSummary(groupid, siblingGroup)");
        Exec(conn, "CREATE INDEX ps_idx2 ON protidSummary(repID)");

        if (Globals.Gene2ProtFile != null)
        {
            Exec(conn, "ALTER TABLE protidSummary ADD COLUMN geneID VARCHAR(100)");
        }

        Exec(conn, "DROP INDEX t3_idx1");
        Exec(conn, "DROP INDEX t3_idx2");
        Exec(conn, "DROP TABLE t3_");

        if (console != null) console.CloseMonitorBox();
        if (console != null) console.Append("\n");
        else Console.Error.Write("\n");
    }

    public virtual int RetNumPeps(SqliteConnection conn, string tag, string pid, double wt, double iniProb)
    {
        if (tag == Globals.CombinedFile) tag = "combined";

        // The original materializes this count via a scratch table
        // (CREATE TABLE nptmp_ AS ...; SELECT COUNT(*); DROP TABLE). Ported as
        // a pure read-only subquery instead: this method is called from
        // inside other methods' open SqliteDataReader loops, and SQLite (unlike
        // HSQLDB) errors ("database table is locked") if DDL runs on a
        // connection while another statement on it hasn't been fully stepped
        // through/closed.
        return ExecScalarInt(conn, $"""
            SELECT COUNT(*) FROM (
              SELECT modPeptide, charge
              FROM prot2peps_{tag}
              WHERE protid = '{pid}'
              AND wt >= {wt}
              AND iniProb >= {iniProb}
              GROUP BY modPeptide, charge
            )
            """);
    }

    public virtual double RetMaxIniProb(SqliteConnection conn, string tag, string protid, double wt)
    {
        if (tag == Globals.CombinedFile) tag = "combined";
        return ExecScalarDouble(conn, $"SELECT MAX(iniProb) FROM prot2peps_{tag} WHERE protid = '{protid}' AND wt >= {wt}");
    }

    /// <summary>Returns the wt of the maxIniProb for a given groupid-siblingGroup.</summary>
    private double RetWtMaxIniProb(SqliteConnection conn, string tag, string protid, double maxIniProb)
    {
        if (tag == Globals.CombinedFile) tag = "combined";
        return ExecScalarDouble(conn, $"SELECT MAX(wt) FROM prot2peps_{tag} WHERE protid = '{protid}' AND iniProb = {maxIniProb}");
    }

    /// <summary>Returns the number of spectra assigned to a given groupid-siblingGroup.</summary>
    public virtual int RetNumSpectra(SqliteConnection conn, string tag, string protid, double wt, double iniProb)
    {
        if (tag == Globals.CombinedFile) tag = "combined";
        return ExecScalarInt(conn, $"SELECT SUM(nspecs) FROM prot2peps_{tag} WHERE protid = '{protid}' AND wt >= {wt}");
    }

    /// <summary>Loads the user's protid-to-geneSymbol mapping file into the gene2prot table. Returns true on error.</summary>
    public virtual bool MakeGeneTable(SqliteConnection conn, IConsole? console)
    {
        if (console != null)
        {
            if (!Globals.ByGene) console.Append("  ");
            console.Append("Mapping protein IDs to their Gene IDs\n\n");
        }
        else
        {
            if (!Globals.ByGene) Console.Error.Write("  ");
            Console.Error.Write("Mapping protein IDs to their Gene IDs\n\n");
        }

        Exec(conn, "DROP TABLE IF EXISTS gene2prot");
        Exec(conn, """
            CREATE TABLE gene2prot(
              geneid VARCHAR(250),
              protid VARCHAR(250),
              geneDefline VARCHAR(1000) DEFAULT 'No Gene Description'
            )
            """);
        Exec(conn, "CREATE INDEX p2g_idx1 ON gene2prot(protid)");
        Exec(conn, "CREATE INDEX p2g_idx2 ON gene2prot(geneid)");

        if (!File.Exists(Globals.Gene2ProtFile))
        {
            if (console == null)
            {
                var err = "\n\nERROR loading gene2prot map file\n"
                    + $"The file '{Globals.Gene2ProtFile}' doesn't exist!\n\n";
                Console.Error.Write(err);
                Environment.Exit(-1);
            }
            else
            {
                var err = $"\n\nI could not open '{Globals.Gene2ProtFile}'\n"
                    + "Please check your file paths and names then try again.\n";
                console.Append(err);
                return true;
            }
        }

        try
        {
            using var cmd = conn.CreateCommand();
            using var tx = BeginTransaction(conn, cmd);
            cmd.CommandText = "INSERT INTO gene2prot VALUES (@geneid, @protid, @defline)";
            var pGeneId = cmd.Parameters.Add("@geneid", SqliteType.Text);
            var pProtId = cmd.Parameters.Add("@protid", SqliteType.Text);
            var pDefline = cmd.Parameters.Add("@defline", SqliteType.Text);

            foreach (var rawLine in File.ReadLines(Globals.Gene2ProtFile!))
            {
                if (rawLine.StartsWith("#")) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(rawLine, @"^[^\w]*$")) continue; // blank line

                var ary = rawLine.Split('\t'); // [0]=geneid, [1]=protid, [2]=defline

                pGeneId.Value = ary.Length > 0 && !string.IsNullOrEmpty(ary[0]) ? ary[0] : DBNull.Value;
                pProtId.Value = ary.Length > 1 && !string.IsNullOrEmpty(ary[1]) ? ary[1] : DBNull.Value;

                if (ary.Length == 3)
                {
                    Globals.GenesHaveDescriptions = true;
                    var defline = ary[2].Length > 1000 ? Globals.ReplaceAll(ary[2].Substring(0, 990), '#', '_') : ary[2];
                    pDefline.Value = defline;
                }
                else
                {
                    pDefline.Value = "No Gene Description";
                }

                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception e)
        {
            var err = $"Error parsing '{Globals.Gene2ProtFile}'\n{e}\n\n";
            if (console != null)
            {
                console.Append(err);
                return true;
            }
            Console.Error.Write(err);
            Environment.Exit(-1);
        }

        return false;
    }

    /// <summary>Adds gene IDs to protidSummary; only called when the gene2prot table exists.</summary>
    public virtual void AppendGeneIds(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("  Appending gene IDs to protidSummary table\n");
        else Console.Error.Write("  Appending gene IDs to protidSummary table\n");

        Exec(conn, """
            UPDATE protidSummary SET geneID = (
              SELECT gn.geneid FROM gene2prot gn WHERE gn.protid = protidSummary.repID
            )
            """);
    }

    /// <summary>Creates the final results table by joining protidSummary against combined.</summary>
    public virtual bool MakeResultsTable(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("Creating results table\n");
        else Console.Error.Write("Creating results\n");

        try
        {
            Exec(conn, "DROP TABLE IF EXISTS results");

            var hasGene = Globals.Gene2ProtFile != null;

            var query = "CREATE TABLE results AS SELECT b.repID AS protid, ";
            if (hasGene) query += "b.geneid AS geneid, ";
            query += """
                c.isFwd AS isFwd,
                c.defline AS defline,
                b.numXML AS numXML,
                b.groupid AS ALL_groupid,
                b.siblingGroup AS ALL_siblingGroup,
                b.maxPw AS maxPw,
                c.Pw AS ALL_Pw,
                c.localPw AS ALL_localPw,
                b.maxIniProb AS maxIniProb,
                b.wt_maxIniProb AS wt_maxIniProb,
                b.maxIniProbUniq AS maxIniProbUniq,
                b.numPepsTot AS ALL_numPepsTot,
                b.numPepsUniq AS ALL_numPepsUniq,
                b.numSpecsTot AS ALL_numSpecsTot,
                b.numSpecsUniq AS ALL_numSpecsUniq
                FROM protidSummary AS b, combined AS c
                WHERE b.groupid = c.groupid
                AND b.siblingGroup = c.siblingGroup
                AND b.repID = c.protid
                GROUP BY
                b.repID, c.isFwd, c.defline, b.numXML, b.groupid, b.siblingGroup,
                b.maxPw, c.Pw, c.localPw, b.maxIniProb, b.wt_maxIniProb, b.maxIniProbUniq,
                b.numPepsTot, b.numPepsUniq, b.numSpecsTot, b.numSpecsUniq
                """;
            if (hasGene) query += ", b.geneid";
            query += " ORDER BY b.groupid ASC, b.siblingGroup ASC";

            Exec(conn, query);

            Exec(conn, "CREATE INDEX res_gid_idx ON results(ALL_groupid, ALL_siblingGroup)");
            Exec(conn, "CREATE INDEX res_pid_idx ON results(protid)");

            if (hasGene) Exec(conn, "UPDATE results SET geneid = 'DECOY' WHERE isFwd = 0");
            Exec(conn, "UPDATE results SET defline = 'DECOY PROTEIN' WHERE isFwd = 0");
        }
        catch (SqliteException)
        {
            return true;
        }

        return false;
    }

    /// <summary>Populates the results table's protLen column from Globals.ProtLen / the combined table.</summary>
    public virtual void AddProteinLengths(SqliteConnection conn, IConsole? console, int dataType)
    {
        console?.Append("  Appending protLen column\n");

        if (dataType == 0)
        {
            if (console != null) console.Append("  Appending protein lengths\n");
            else Console.Error.Write("  Appending protein lengths\n");

            Exec(conn, "ALTER TABLE results ADD COLUMN protLen INT");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE results SET protLen = @len WHERE protid = @protid";
            var pLen = cmd.Parameters.Add("@len", SqliteType.Integer);
            var pProtid = cmd.Parameters.Add("@protid", SqliteType.Text);

            using var reader = ExecuteReader(conn, "SELECT DISTINCT protid, protLen FROM COMBINED");
            var rows = new List<(string pid, int len)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt32(1)));
            reader.Close();

            using (var tx = BeginTransaction(conn, cmd))
            {
                foreach (var (pid, len) in rows)
                {
                    pLen.Value = len;
                    pProtid.Value = pid;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        else if (dataType == 1)
        {
            // v_results is a copy of results, so protLen already exists there
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE v_results SET protLen = @len WHERE protid = @protid";
            var pLen = cmd.Parameters.Add("@len", SqliteType.Integer);
            var pProtid = cmd.Parameters.Add("@protid", SqliteType.Text);

            if (console != null)
            {
                var n = ExecScalarInt(conn, "SELECT COUNT(DISTINCT protid) FROM results");
                console.MonitorBoxInit(n, "Appending additional protein lengths...");
            }

            using var reader = ExecuteReader(conn, "SELECT DISTINCT protid FROM v_results WHERE protid LIKE '%:::%'");
            var xs = new List<string>();
            while (reader.Read()) xs.Add(reader.GetString(0));
            reader.Close();

            var ctr = 0;
            using (var tx = BeginTransaction(conn, cmd))
            {
                foreach (var x in xs)
                {
                    var pid = x.Substring(x.LastIndexOf(':') + 1);
                    var protLen = ExecScalarInt(conn, $"SELECT protLen FROM COMBINED WHERE protid = '{pid}'");

                    pLen.Value = protLen;
                    pProtid.Value = x;
                    cmd.ExecuteNonQuery();

                    ctr++;
                    if (console != null) console.MonitorBoxUpdate(ctr);
                    else Globals.CursorStatus(ctr, "  Appending additional protein lengths...");
                }
                tx.Commit();
            }

            if (console != null) console.Append("\n");
            else Console.Error.Write("\n");
        }
    }

    /// <summary>Creates one wt9X_&lt;tag&gt; table per protXML tag: unique-peptide spectral counts per protein.</summary>
    public virtual void MakeWt9XgroupsTable(SqliteConnection conn)
    {
        using var reader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot'");
        var tags = new List<string>();
        while (reader.Read()) tags.Add(reader.GetString(0));
        reader.Close();

        using (var tx = conn.BeginTransaction())
        {
            foreach (var tag in tags)
            {
                Exec(conn, $"""
                    CREATE TABLE wt9X_{tag} AS
                    SELECT protid AS protid, SUM(nspecs) AS nspecsUniq
                    FROM prot2peps_{tag}
                    WHERE wt >= {WtTh}
                    AND iniProb >= {IniProbTh}
                    GROUP BY protid
                    """);
                Exec(conn, $"CREATE INDEX wt_idx1_{tag} ON wt9X_{tag}(protid)");
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Builds pepUsage_: for every peptide, how many proteins it's shared across
    /// (denom) vs. this one (numer), and the resulting adjusted spectral count.
    /// </summary>
    public virtual void MakePepUsageTable(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("\nCreating peptide usage table\n");
        else Console.Error.Write("\nCreating peptide usage table\n");

        Exec(conn, """
            CREATE TABLE pepUsage_ (
              tag VARCHAR(100),
              protid VARCHAR(100),
              modPeptide VARCHAR(250),
              charge INT,
              nspecs INT DEFAULT 0,
              numer INT DEFAULT 0,
              denom INT DEFAULT 0,
              alpha DECIMAL(8,6),
              adjSpecs INT DEFAULT 0
            )
            """);

        using (var reader1 = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot'"))
        {
            var tags = new List<string>();
            while (reader1.Read()) tags.Add(reader1.GetString(0));
            reader1.Close();

            using var cmd = conn.CreateCommand();
            using var tx = BeginTransaction(conn, cmd);
            cmd.CommandText = "INSERT INTO pepUsage_ (tag, protid, modPeptide, charge, nspecs, numer) VALUES (@p1,@p2,@p3,@p4,@p5,@p6)";
            for (var i = 1; i <= 6; i++) cmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

            foreach (var tag in tags)
            {
                var n = ExecScalarInt(conn, $"SELECT COUNT(*) FROM prot2peps_{tag}");

                if (console != null)
                {
                    console.MonitorBoxInit(n, $"Indexing Peptide Usage ({tag})...");
                    console.Append($"  Indexing Peptide Usage for: {tag}\n");
                }
                else Globals.CursorStatus(n, $"  Peptide usage index ({tag})... ");

                using var reader2 = ExecuteReader(conn, $"""
                    SELECT s.repid, a.modPeptide, a.charge, a.nspecs, b.nspecsUniq
                    FROM prot2peps_{tag} AS a, wt9X_{tag} AS b, protidSummary AS s
                    WHERE a.protid = b.protid
                    AND a.protid = s.repid
                    GROUP BY s.repid, a.modPeptide, a.charge, a.nspecs, b.nspecsUniq
                    """);

                var iter = 0;
                while (reader2.Read())
                {
                    cmd.Parameters["@p1"].Value = tag;
                    cmd.Parameters["@p2"].Value = reader2.GetString(0);
                    cmd.Parameters["@p3"].Value = reader2.GetString(1);
                    cmd.Parameters["@p4"].Value = reader2.GetInt32(2);
                    cmd.Parameters["@p5"].Value = reader2.GetInt32(3);
                    cmd.Parameters["@p6"].Value = reader2.GetInt32(4);
                    cmd.ExecuteNonQuery();

                    iter++;
                    if (console != null) console.MonitorBoxUpdate(iter);
                    else Globals.CursorStatus(iter, $"  Peptide usage index ({tag})... ");
                }

                Exec(conn, $"DROP INDEX IF EXISTS wt_idx1_{tag}");
                Exec(conn, $"DROP TABLE IF EXISTS wt9X_{tag}");

                if (console != null) console.CloseMonitorBox();
                else Console.Error.Write("\n");
            }
            tx.Commit();
        }

        if (console != null) console.Append("  Indexing pepUsage_ table\n");
        else Console.Error.Write("  Indexing pepUsage_ table\n");

        Exec(conn, "CREATE INDEX pu_idx1 ON pepUsage_(tag, protid)");
        Exec(conn, "CREATE INDEX pu_idx2 ON pepUsage_(tag, modPeptide, charge)");

        // Original used a custom SQL function (sumNumer); SQLite has no
        // CREATE FUNCTION, so this is a correlated subquery instead.
        Exec(conn, """
            UPDATE pepUsage_ SET denom = (
              SELECT SUM(p2.numer) FROM pepUsage_ AS p2
              WHERE p2.tag = pepUsage_.tag
              AND p2.modPeptide = pepUsage_.modPeptide
              AND p2.charge = pepUsage_.charge
            )
            """);

        if (console != null) console.Append("  Updating adjusted spectral counts\n");
        else Console.Error.Write("  Updating adjusted spectral counts\n");

        // Java computes alpha via "ROUND((CAST(numer AS DECIMAL(16,6)) / CAST(denom
        // AS DECIMAL(16,6))), 6)". This looks like round-to-6-decimals, but isn't:
        // verified directly against a real HSQLDB instance (extracted from
        // abacus.jar) that DECIMAL(16,6)/DECIMAL(16,6) division in HSQLDB computes
        // the quotient AT the operand scale (6) and TRUNCATES there - e.g. 1/6
        // comes out as 0.166666, not the correctly-rounded 0.166667 - so the
        // subsequent ROUND(...,6) is a no-op on an already-6-decimal value. (A
        // DOUBLE/DOUBLE division of the same operands does round correctly to
        // 0.166667, confirming this is specifically a DECIMAL-division quirk, not
        // a ROUND() quirk.) A prior version of this code used C#'s `decimal` with
        // proper round-half-up here, which is more mathematically correct than
        // Java but doesn't match it - matching Java's actual output requires
        // reproducing the truncation, not "fixing" it. This was root-caused by
        // diffing DEBUG-instrumented traces of this exact method from both the
        // real recompiled abacus.jar and this port against the same real data;
        // see CLAUDE.md. The second ROUND (nspecs*alpha, to 0 decimals) operates
        // on an exact product of two already-fixed-scale values, so it isn't
        // subject to the same issue and uses ordinary round-half-up (verified
        // against the same HSQLDB instance for exact .5 ties).
        using (var readCmd = conn.CreateCommand())
        {
            readCmd.CommandText = "SELECT rowid, numer, denom, nspecs FROM pepUsage_";
            var rows = new List<(long rowid, long numer, long denom, long nspecs)>();
            using (var rdr = readCmd.ExecuteReader())
            {
                while (rdr.Read()) rows.Add((rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3)));
            }

            using var writeCmd = conn.CreateCommand();
            using var tx = BeginTransaction(conn, writeCmd);
            writeCmd.CommandText = "UPDATE pepUsage_ SET alpha = @alpha, adjSpecs = @adjSpecs WHERE rowid = @rowid";
            var pAlpha = writeCmd.Parameters.Add("@alpha", SqliteType.Real);
            var pAdjSpecs = writeCmd.Parameters.Add("@adjSpecs", SqliteType.Integer);
            var pRowid = writeCmd.Parameters.Add("@rowid", SqliteType.Integer);

            foreach (var (rowid, numer, denom, nspecs) in rows)
            {
                var alpha = Math.Truncate((decimal)numer / denom * 1_000_000m) / 1_000_000m;
                var adjSpecs = (int)Math.Round(nspecs * alpha, 0, MidpointRounding.AwayFromZero);

                pAlpha.Value = alpha;
                pAdjSpecs.Value = adjSpecs;
                pRowid.Value = rowid;
                writeCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Appends per-experiment statistics for each repID from every individual protXML tag.</summary>
    public virtual void AppendIndividualExpts(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("\nRetrieving data from individual experiments\n");
        else Console.Error.Write("\nRetrieving data from individual experiments\n");

        using var reader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot' ORDER BY tag ASC");
        var tags = new List<string>();
        while (reader.Read()) tags.Add(reader.GetString(0).Trim());
        reader.Close();

        foreach (var tag in tags)
        {
            if (console != null) console.Append($"  Adding data from {tag}\n");
            else Console.Error.Write($"  Adding data from {tag}\n");

            AppendColumns(conn, tag);
            FillColumns(conn, tag);
            UpdateSpectralCounts(conn, tag);
        }
    }

    /// <summary>Adds empty per-experiment columns to the results table.</summary>
    private static void AppendColumns(SqliteConnection conn, string tag)
    {
        Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_groupid INT");
        Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_sibGroup VARCHAR(5)");
        Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_Pw DECIMAL(8,6)");
        Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_numPepsTot INT DEFAULT 0");
        Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_numPepsUniq INT DEFAULT 0");
        Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_numSpecsTot INT DEFAULT 0");
        Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_numSpecsUniq INT DEFAULT 0");
        Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_numSpecsAdj INT DEFAULT 0");
    }

    /// <summary>Fills in the per-experiment column values for `tag`.</summary>
    private void FillColumns(SqliteConnection conn, string tag)
    {
        // If the user required specific AA mods, some files may have no matching rows.
        var n = ExecScalarInt(conn, $"SELECT COUNT(*) FROM protXML where tag = '{tag}'");
        if (n <= 0) return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE results SET
              {tag}_groupid = @gid,
              {tag}_sibGroup = @sib,
              {tag}_Pw = @localPw,
              {tag}_numPepsTot = @numPepsTot,
              {tag}_numPepsUniq = @numPepsUniq,
              {tag}_numSpecsTot = @numSpecsTot,
              {tag}_numSpecsUniq = @numSpecsUniq
            WHERE protid = @protid
            """;
        var pGid = cmd.Parameters.Add("@gid", SqliteType.Integer);
        var pSib = cmd.Parameters.Add("@sib", SqliteType.Text);
        var pLocalPw = cmd.Parameters.Add("@localPw", SqliteType.Real);
        var pNumPepsTot = cmd.Parameters.Add("@numPepsTot", SqliteType.Integer);
        var pNumPepsUniq = cmd.Parameters.Add("@numPepsUniq", SqliteType.Integer);
        var pNumSpecsTot = cmd.Parameters.Add("@numSpecsTot", SqliteType.Integer);
        var pNumSpecsUniq = cmd.Parameters.Add("@numSpecsUniq", SqliteType.Integer);
        var pProtid = cmd.Parameters.Add("@protid", SqliteType.Text);

        using var reader = ExecuteReader(conn, $"""
            SELECT p.groupid, p.siblingGroup, p.localPw, p.protid
            FROM protXML AS p, results AS r
            WHERE p.tag = '{tag}'
            AND p.protid = r.protid
            GROUP BY p.groupid, p.siblingGroup, p.localPw, p.protid
            """);
        var rows = new List<(int gid, string sib, double localPw, string protid)>();
        while (reader.Read()) rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetDouble(2), reader.GetString(3)));
        reader.Close();

        using (var tx = BeginTransaction(conn, cmd))
        {
            foreach (var (gid, sib, localPw, protid) in rows)
            {
                pGid.Value = gid;
                pSib.Value = sib;
                pLocalPw.Value = localPw;
                pNumPepsTot.Value = RetNumPeps(conn, tag, protid, 0, IniProbTh);
                pNumPepsUniq.Value = RetNumPeps(conn, tag, protid, WtTh, IniProbTh);
                pNumSpecsTot.Value = RetNumSpectra(conn, tag, protid, 0, IniProbTh);
                pNumSpecsUniq.Value = RetNumSpectra(conn, tag, protid, WtTh, IniProbTh);
                pProtid.Value = protid;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Updates the results table with adjusted spectral counts for `tag`.</summary>
    public virtual void UpdateSpectralCounts(SqliteConnection conn, string tag)
    {
        Exec(conn, $"""
            CREATE TABLE adjSpecs_ AS
            SELECT tag AS tag, protid AS protid, SUM(adjSpecs) AS X
            FROM pepUsage_
            WHERE tag = '{tag}'
            GROUP BY tag, protid
            ORDER BY tag
            """);
        Exec(conn, "CREATE INDEX asp_idx2 ON adjSpecs_(protid)");

        Exec(conn, $"""
            UPDATE results
              SET {tag}_numSpecsAdj = (
                SELECT X FROM adjSpecs_
                WHERE adjSpecs_.tag = '{tag}'
                AND adjSpecs_.protid = results.protid
              )
            """);
        // The subquery above is unconditional (runs for every row, not just
        // matched protids), so any protein with zero adjusted-spectra rows
        // for this tag gets SET to SQL NULL here rather than being left at
        // its ADD-COLUMN default of 0. Java's rs.getDouble()/getInt() masks
        // this silently (JDBC returns 0 for a NULL column); SqliteDataReader
        // throws instead, so the NULL must be cleaned up explicitly here -
        // same pattern already used for pepUsage_.adjSpecs above and
        // genePepUsage_.numer/denom in HyperSqlObjectGene.cs.
        Exec(conn, $"UPDATE results SET {tag}_numSpecsAdj = 0 WHERE {tag}_numSpecsAdj IS NULL");

        Exec(conn, "DROP TABLE adjSpecs_");
    }

    /// <summary>Writes the final results (or v_results/geneResults) table to the output file.</summary>
    public virtual void DefaultResults(SqliteConnection conn, IConsole? console)
    {
        var outputFileName = Globals.OutputFilePath!;
        if (console != null) console.Append($"\nWriting results to: '{outputFileName}'\n");
        else Console.Error.Write($"\nWriting results to: '{outputFileName}'\n");

        var table = "results";
        if (Globals.MakeVerboseOutput) table = "v_results";
        if (Globals.ByGene) table = "geneResults";
        var query = Globals.ByGene ? $"SELECT * FROM {table} ORDER BY geneid" : $"SELECT * FROM {table} ORDER BY ALL_id";
        var columnTypes = GetColumnTypes(conn, table);

        using var writer = new StreamWriter(outputFileName);
        using var reader = ExecuteReader(conn, query);
        var numColumns = reader.FieldCount;

        for (var i = 0; i < numColumns - 1; i++) writer.Write(reader.GetName(i) + "\t");
        writer.Write(reader.GetName(numColumns - 1) + "\n");

        while (reader.Read())
        {
            for (var i = 0; i < numColumns; i++)
            {
                writer.Write(FormatCell(reader, i, columnTypes: columnTypes));
                writer.Write(i != numColumns - 1 ? "\t" : "\n");
            }
        }
    }

    /// <summary>Writes a QSpec-formatted output file (protein- or gene-centric spectral counts only).</summary>
    public virtual void FormatQspecOutput(SqliteConnection conn, IConsole? console)
    {
        var outputFileName = Globals.OutputFilePath!;
        if (console != null) console.Append($"\nWriting spectral counts for QSpec to file:\n\t'{outputFileName}'\n");
        else Console.Error.Write($"\nWriting spectral counts for QSpec to file:\n\t'{outputFileName}'\n");

        string table;
        string query;
        if (Globals.OutputFormat == Globals.ProtQspecFormat)
        {
            table = Globals.MakeVerboseOutput ? "v_results" : "results";
            query = $"SELECT * FROM {table} ORDER BY ALL_id";
        }
        else // GeneQspecFormat
        {
            table = "geneResults";
            query = "SELECT * FROM geneResults ORDER BY geneid";
        }
        var columnTypes = GetColumnTypes(conn, table);

        using var writer = new StreamWriter(outputFileName);
        using var reader = ExecuteReader(conn, query);
        var numColumns = reader.FieldCount;

        // only the nspecsAdj (and id/protLen) columns are wanted, in column order
        var colHdrs = new SortedDictionary<int, string>();
        for (var i = 0; i < numColumns; i++)
        {
            var c = reader.GetName(i);

            if (Globals.OutputFormat == Globals.GeneQspecFormat)
            {
                if (c.Equals("geneid", StringComparison.OrdinalIgnoreCase)) colHdrs[i] = "geneid";
                else if (c.Equals("avgProtLen", StringComparison.OrdinalIgnoreCase)) colHdrs[i] = "avgProtLen";
                else if (c.EndsWith("_NUMSPECSADJ", StringComparison.OrdinalIgnoreCase)) colHdrs[i] = c.Substring(0, c.IndexOf("_NUMSPECSADJ", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (c.Equals("protid", StringComparison.OrdinalIgnoreCase)) colHdrs[i] = "protid";
                else if (c.Equals("protLen", StringComparison.OrdinalIgnoreCase)) colHdrs[i] = "protLen";
                else if (c.EndsWith("_NUMSPECSADJ", StringComparison.OrdinalIgnoreCase)) colHdrs[i] = c.Substring(0, c.IndexOf("_NUMSPECSADJ", StringComparison.OrdinalIgnoreCase));
            }
        }

        var maxColIdx = colHdrs.Count > 0 ? colHdrs.Keys.Max() : -1;
        var headers = colHdrs.Values.ToList();
        for (var i = 0; i < headers.Count; i++)
        {
            writer.Write(headers[i]);
            if (i != headers.Count - 1) writer.Write("\t");
        }
        writer.Write("\n");

        while (reader.Read())
        {
            for (var i = 0; i < numColumns; i++)
            {
                if (!colHdrs.ContainsKey(i)) continue;
                writer.Write(FormatCell(reader, i, columnTypes: columnTypes));
                writer.Write(i != maxColIdx ? "\t" : "\n");
            }
        }
    }

    /// <summary>Writes a custom results file limited to the user-chosen columns (printC/printE).</summary>
    public virtual void CustomOutput(SqliteConnection conn, IConsole? console)
    {
        HashSet<string>? exptSet = null;
        if (Globals.PrintE.Count > 0)
        {
            exptSet = new HashSet<string>();
            using var tagReader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot'");
            while (tagReader.Read())
            {
                var tag = tagReader.GetString(0);
                foreach (var suffix in Globals.PrintE)
                {
                    exptSet.Add(tag.ToUpperInvariant() + suffix.ToUpperInvariant());
                }
            }
        }

        var baseQuery = "SELECT * FROM results LIMIT 1";
        if (Globals.MakeVerboseOutput) baseQuery = "SELECT * FROM v_results LIMIT 1";
        else if (Globals.ByGene) baseQuery = "SELECT * FROM geneResults LIMIT 1";

        var selectCols = new SortedDictionary<int, string>();
        using (var reader = ExecuteReader(conn, baseQuery))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var colName = reader.GetName(i);
                if (Globals.PrintC.Count > 0 && Globals.PrintC.Contains(colName)) selectCols[i] = colName;
                if (Globals.PrintE.Count > 0 && exptSet!.Contains(colName)) selectCols[i] = colName;
            }
        }

        if (selectCols.Count == 0)
        {
            if (console != null) console.Append("No columns have been selected for output\n");
            else Console.Error.Write("No columns have been selected for output\n");
            return;
        }

        if (console != null) console.Append("\nCustom output columns:\n");
        else Console.Error.Write("\nCustom output columns:\n");

        foreach (var v in selectCols.Values)
        {
            if (console != null) console.Append(v + "\n");
            else Console.Error.Write(v + "\n");
        }
        var queryBody = string.Join(", ", selectCols.Values);

        var outputFileName = Globals.OutputFilePath!;
        if (console != null) console.Append($"\nWriting results to: '{outputFileName}'\n");
        else Console.Error.Write($"\nWriting results to: '{outputFileName}'\n");

        var query = $"SELECT DISTINCT {queryBody} FROM results";
        if (Globals.MakeVerboseOutput) query = $"SELECT DISTINCT {queryBody} FROM v_results";
        else if (Globals.ByGene) query = $"SELECT DISTINCT {queryBody} FROM geneResults";

        using var writer = new StreamWriter(outputFileName);
        using var dataReader = ExecuteReader(conn, query);
        var numCols = dataReader.FieldCount;

        for (var i = 0; i < numCols - 1; i++) writer.Write(dataReader.GetName(i) + "\t");
        writer.Write(dataReader.GetName(numCols - 1) + "\n");

        while (dataReader.Read())
        {
            for (var i = 0; i < numCols; i++)
            {
                if (dataReader.IsDBNull(i))
                {
                    // no-op, matches Java's blank output for a NULL value here
                }
                else
                {
                    var fieldType = dataReader.GetFieldType(i);
                    if (fieldType == typeof(long)) writer.Write(dataReader.GetInt64(i));
                    else if (fieldType == typeof(string)) writer.Write(dataReader.GetString(i));
                    else writer.Write(Math.Round(dataReader.GetDouble(i), 4)); // formatter "#0.0000"
                }
                writer.Write(i != numCols - 1 ? "\t" : "\n");
            }
        }
    }

    /// <summary>Removes intermediate tables no longer needed once results have been written.</summary>
    public virtual void CleanUp(SqliteConnection conn)
    {
        using (var reader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags"))
        {
            var tags = new List<string>();
            while (reader.Read()) tags.Add(reader.GetString(0));
            reader.Close();

            using var tx = conn.BeginTransaction();
            foreach (var tag in tags)
            {
                Exec(conn, $"DROP INDEX IF EXISTS wt_idx1_{tag}");
                Exec(conn, $"DROP TABLE IF EXISTS wt9X_{tag}");
                Exec(conn, $"DROP INDEX IF EXISTS pt2peps_{tag}_idx1");
                Exec(conn, $"DROP INDEX IF EXISTS pt2peps_{tag}_idx2");
                Exec(conn, $"DROP TABLE IF EXISTS prot2peps_{tag}");
            }
            tx.Commit();
        }

        Exec(conn, "DROP INDEX IF EXISTS pt2peps_combined_idx1");
        Exec(conn, "DROP INDEX IF EXISTS pt2peps_combined_idx2");
        Exec(conn, "DROP TABLE IF EXISTS prot2peps_combined");

        if (Globals.ByGene)
        {
            Exec(conn, "DROP INDEX IF EXISTS g2pep_idx1");
            Exec(conn, "DROP INDEX IF EXISTS g2pep_idx2");
            Exec(conn, "DROP INDEX IF EXISTS g2pep_idx3");
            Exec(conn, "DROP INDEX IF EXISTS g2pep_idx4");
            Exec(conn, "DROP INDEX IF EXISTS g2pep_idx5");
            Exec(conn, "DROP INDEX IF EXISTS g2pep_idx6");
            Exec(conn, "DROP INDEX IF EXISTS g2pep_idx7");
            Exec(conn, "DROP TABLE IF EXISTS g2pep_");
            Exec(conn, "DROP TABLE IF EXISTS t1_");
        }
        else
        {
            Exec(conn, "DROP INDEX IF EXISTS pt2pep_idx1");
            Exec(conn, "DROP INDEX IF EXISTS pt2pep_idx2");
            Exec(conn, "DROP INDEX IF EXISTS pt2pep_idx3");
            Exec(conn, "DROP INDEX IF EXISTS pt2pep_idx4");
            Exec(conn, "DROP INDEX IF EXISTS pt2pep_idx5");
            Exec(conn, "DROP TABLE IF EXISTS t1_");
        }
    }

    /// <summary>Concatenates groupid/siblingGroup into a single ALL_id (and per-tag _id) field.</summary>
    public virtual void MergeIdFields(SqliteConnection conn)
    {
        if (Globals.ByGene) return;

        Exec(conn, "ALTER TABLE results ADD COLUMN ALL_id VARCHAR(20)");
        Exec(conn, "UPDATE results SET ALL_id = (ALL_groupid || '-' || ALL_siblingGroup)");
        // res_gid_idx (created in MakeResultsTable) indexes these two columns;
        // SQLite doesn't auto-drop indexes when a column they cover is removed.
        Exec(conn, "DROP INDEX IF EXISTS res_gid_idx");
        Exec(conn, "ALTER TABLE results DROP COLUMN ALL_groupid");
        Exec(conn, "ALTER TABLE results DROP COLUMN ALL_siblingGroup");

        using var reader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot'");
        var tags = new List<string>();
        while (reader.Read()) tags.Add(reader.GetString(0));
        reader.Close();

        using (var tx = conn.BeginTransaction())
        {
            foreach (var tag in tags)
            {
                Exec(conn, $"ALTER TABLE results ADD COLUMN {tag}_id VARCHAR(20)");
                Exec(conn, $"UPDATE results SET {tag}_id = ({tag}_groupid || '-' || {tag}_sibGroup)");
                Exec(conn, $"ALTER TABLE results DROP COLUMN {tag}_groupid");
                Exec(conn, $"ALTER TABLE results DROP COLUMN {tag}_sibGroup");
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// pepXML file names may not match protXML file names (e.g. one protXML built
    /// from several pepXML runs). Uses srcFileTags to append the matching tag to
    /// each pepXML row.
    /// </summary>
    public virtual void CorrectPepXmlTags(SqliteConnection conn)
    {
        Exec(conn, "ALTER TABLE pepXML ADD COLUMN tag VARCHAR(250)");
        Exec(conn, """
            UPDATE pepXML SET tag = (
              SELECT sf.tag FROM srcFileTags sf
              WHERE sf.fileType = 'pep'
              AND sf.srcFile = pepXML.srcFile
            )
            """);

        Exec(conn, "CREATE INDEX pepxml_idx1 ON pepXML(specId)");
        Exec(conn, "CREATE INDEX pepxml_idx2 ON pepXML(modPeptide)");
        Exec(conn, "CREATE INDEX pepxml_idx3 ON pepXML(tag, modPeptide, charge)");
        Exec(conn, "CREATE INDEX pepxml_idx4 ON pepXML(tag, specId)");
        Exec(conn, "CREATE INDEX pepxml_idx5 ON pepXML(tag, modPeptide)");
        Exec(conn, "CREATE INDEX pepxml_idx6 ON pepXML(modPeptide, charge)");
    }

    /// <summary>Generates protein-centric spectral count data in NSAF format.</summary>
    public virtual void GetNsafValuesProt(SqliteConnection conn, IConsole? console)
    {
        Exec(conn, "DROP TABLE IF EXISTS nsaf_p1");
        Exec(conn, "DROP TABLE IF EXISTS nsaf");

        var msg = "\nCreating NSAF values table (protein-centric)\n";
        if (console != null) console.Append(msg);
        else Console.Error.Write(msg);

        Exec(conn, "CREATE TABLE nsaf_p1 AS SELECT protid AS protid FROM results GROUP BY protid ORDER BY protid ASC");
        Exec(conn, "CREATE INDEX nsaf_p1_idx1 ON nsaf_p1(protid)");

        Exec(conn, "CREATE TABLE nsaf AS SELECT protid AS protid FROM results GROUP BY protid ORDER BY protid ASC");
        Exec(conn, "CREATE INDEX nsaf_idx1 ON nsaf(protid)");

        // Values are multiplied by a scaling factor (10^(digits+1)) purely to
        // avoid numeric underflow with very small NSAF values.
        var numProts = ExecScalarInt(conn, "SELECT COUNT(protid) FROM results WHERE isFwd = 1");
        var factor = numProts.ToString().Length + 1;
        var nsafFactor = Math.Pow(10, factor);
        Globals.NsafFactor = nsafFactor;

        var factorMsg = $"  NSAF_FACTOR = 10^{factor} = {nsafFactor}\n";
        if (console != null) console.Append(factorMsg);
        else Console.Error.Write(factorMsg);

        using var tagReader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot' ORDER BY tag ASC");
        var tags = new List<string>();
        while (tagReader.Read()) tags.Add(tagReader.GetString(0));
        tagReader.Close();

        foreach (var tag in tags)
        {
            Exec(conn, $"ALTER TABLE nsaf_p1 ADD COLUMN {tag}_specsTot DOUBLE DEFAULT 0");
            Exec(conn, $"ALTER TABLE nsaf_p1 ADD COLUMN {tag}_specsUniq DOUBLE DEFAULT 0");
            Exec(conn, $"ALTER TABLE nsaf_p1 ADD COLUMN {tag}_specsAdj DOUBLE DEFAULT 0");

            Exec(conn, $"ALTER TABLE nsaf ADD COLUMN {tag}_totNSAF DOUBLE DEFAULT 0");
            Exec(conn, $"ALTER TABLE nsaf ADD COLUMN {tag}_uniqNSAF DOUBLE DEFAULT 0");
            Exec(conn, $"ALTER TABLE nsaf ADD COLUMN {tag}_adjNSAF DOUBLE DEFAULT 0");

            using (var reader = ExecuteReader(conn, $"""
                SELECT protid, protLen,
                  {tag}_numSpecsTot,
                  {tag}_numSpecsUniq,
                  {tag}_numSpecsAdj
                FROM results
                ORDER BY protid
                """))
            {
                var lenRows = new List<(string protid, double tot, double uniq, double adj)>();
                while (reader.Read())
                {
                    var protLen = reader.GetDouble(1);
                    lenRows.Add((reader.GetString(0), reader.GetDouble(2) / protLen, reader.GetDouble(3) / protLen, reader.GetDouble(4) / protLen));
                }
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var (protid, tot, uniq, adj) in lenRows)
                    {
                        Exec(conn, $"""
                            UPDATE nsaf_p1
                              SET {tag}_specsTot = {tot},
                                  {tag}_specsUniq = {uniq},
                                  {tag}_specsAdj = {adj}
                            WHERE protid = '{protid}'
                            """);
                    }
                    tx.Commit();
                }
            }

            var totSum = ExecScalarDouble(conn, $"SELECT SUM({tag}_specsTot) FROM nsaf_p1");
            var uniqSum = ExecScalarDouble(conn, $"SELECT SUM({tag}_specsUniq) FROM nsaf_p1");
            var adjSum = ExecScalarDouble(conn, $"SELECT SUM({tag}_specsAdj) FROM nsaf_p1");

            using (var reader = ExecuteReader(conn, $"""
                SELECT protid, {tag}_specsTot, {tag}_specsUniq, {tag}_specsAdj
                FROM nsaf_p1
                GROUP BY protid, {tag}_specsTot, {tag}_specsUniq, {tag}_specsAdj
                ORDER BY protid ASC
                """))
            {
                var nsafRows = new List<(string protid, double t, double u, double a)>();
                while (reader.Read()) nsafRows.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var (protid, xT, xU, xA) in nsafRows)
                    {
                        var nsafT = totSum == 0 ? 0 : (xT / totSum) * nsafFactor;
                        var nsafU = uniqSum == 0 ? 0 : (xU / uniqSum) * nsafFactor;
                        var nsafA = adjSum == 0 ? 0 : (xA / adjSum) * nsafFactor;

                        Exec(conn, $"""
                            UPDATE nsaf
                              SET {tag}_totNSAF = {nsafT},
                                  {tag}_uniqNSAF = {nsafU},
                                  {tag}_adjNSAF = {nsafA}
                            WHERE protid = '{protid}'
                            """);
                    }
                    tx.Commit();
                }
            }
        }

        Exec(conn, "DROP INDEX nsaf_p1_idx1");
        Exec(conn, "DROP TABLE nsaf_p1");

        ReformatResults(conn, console);
    }

    /// <summary>Merges the nsaf table's per-tag NSAF columns into results (or geneResults).</summary>
    public virtual void ReformatResults(SqliteConnection conn, IConsole? console)
    {
        var msg = "\nAdding NSAF values to results table.\n";
        if (console != null) console.Append(msg);
        else Console.Error.Write(msg);

        using var reader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot' ORDER BY tag ASC");
        var tags = new List<string>();
        while (reader.Read()) tags.Add(reader.GetString(0));
        reader.Close();

        var table = Globals.ByGene ? "geneResults" : "results";
        var idField = Globals.ByGene ? "geneid" : "protid";
        var nsafIdField = Globals.ByGene ? "geneid" : "protid";

        using (var tx = conn.BeginTransaction())
        {
            foreach (var tag in tags)
            {
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN {tag}_totNSAF DOUBLE");
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN {tag}_uniqNSAF DOUBLE");
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN {tag}_adjNSAF DOUBLE");

                Exec(conn, $"""
                    UPDATE {table}
                      SET {tag}_totNSAF = (
                        SELECT {tag}_totNSAF FROM nsaf WHERE nsaf.{nsafIdField} = {table}.{idField}
                      )
                    """);
                Exec(conn, $"""
                    UPDATE {table}
                      SET {tag}_uniqNSAF = (
                        SELECT {tag}_uniqNSAF FROM nsaf WHERE nsaf.{nsafIdField} = {table}.{idField}
                      )
                    """);
                Exec(conn, $"""
                    UPDATE {table}
                      SET {tag}_adjNSAF = (
                        SELECT {tag}_adjNSAF FROM nsaf WHERE nsaf.{nsafIdField} = {table}.{idField}
                      )
                    """);
            }
            tx.Commit();
        }
    }

    /// <summary>Returns the gene ID for a protein ID, or "DECOY" for decoys.</summary>
    public virtual string GetGeneId(SqliteConnection conn, IConsole? console, string protid)
    {
        if (protid.StartsWith(DecoyTag!)) return "DECOY";
        return ExecScalarString(conn, $"SELECT geneid FROM gene2prot WHERE protid = '{protid}'") ?? "";
    }

    /// <summary>Returns the length of the given protein ID from Globals.ProtLen.</summary>
    public virtual int GetProtLen(string protid)
    {
        if (string.IsNullOrEmpty(Globals.FastaFile)) return 0;
        return Globals.ProtLen.TryGetValue(protid, out var len) ? len : 0;
    }

    /// <summary>
    /// Appends every non-representative protein ID from a COMBINED-file group to
    /// the final output as duplicate rows (repProtId:::otherProtId), for verbose mode.
    /// </summary>
    public virtual void AddExtraProteins(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("\nAppending additional protein identifiers to final output\n");
        else Console.Error.Write("\nAppending additional protein identifiers to final output\n");

        Exec(conn, "CREATE TABLE v_results AS SELECT * FROM results");
        Exec(conn, "CREATE INDEX vr_idx1 ON v_results(protid)");
        Exec(conn, "CREATE INDEX vr_idx2 ON v_results(ALL_id)");

        using var idReader = ExecuteReader(conn, "SELECT DISTINCT ALL_id, protId FROM results ORDER BY ALL_Id");
        var idRows = new List<(string allId, string repProtId)>();
        while (idReader.Read()) idRows.Add((idReader.GetString(0), idReader.GetString(1)));
        idReader.Close();

        // template row: same column layout as v_results, minus the first (protid) column
        List<string> colNames;
        using (var schemaReader = ExecuteReader(conn, "SELECT * FROM v_results LIMIT 1"))
        {
            colNames = Enumerable.Range(0, schemaReader.FieldCount).Select(schemaReader.GetName).ToList();
        }

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = $"INSERT INTO v_results VALUES (@p0, {string.Join(", ", Enumerable.Range(1, colNames.Count - 1).Select(i => $"@p{i}"))})";
        for (var i = 0; i < colNames.Count; i++) insertCmd.Parameters.Add(new SqliteParameter($"@p{i}", DBNull.Value));

        using var tx = BeginTransaction(conn, insertCmd);
        foreach (var (allId, repProtId) in idRows)
        {
            var parts = allId.Split('-');
            var allGroupId = parts[0];
            var allSib = parts[1];

            using var extraReader = ExecuteReader(conn, $"""
                SELECT DISTINCT protid, defline
                FROM combined
                WHERE groupid = {allGroupId}
                AND siblingGroup = '{allSib}'
                AND protId != '{repProtId}'
                """);
            var extras = new List<(string curId, string curDefline)>();
            while (extraReader.Read()) extras.Add((extraReader.GetString(0), extraReader.GetString(1)));
            extraReader.Close();

            foreach (var (curId, curDefline) in extras)
            {
                var geneId = Globals.Gene2ProtFile != null ? GetGeneId(conn, console, curId) : null;
                var protLen = GetProtLen(curId);

                using var templateReader = ExecuteReader(conn, $"SELECT * FROM v_results WHERE protId = '{repProtId}'");
                if (!templateReader.Read()) continue;

                insertCmd.Parameters["@p0"].Value = $"{repProtId}:::{curId}";
                for (var i = 1; i < colNames.Count; i++)
                {
                    if (colNames[i].Equals("PROTLEN", StringComparison.OrdinalIgnoreCase)) insertCmd.Parameters[$"@p{i}"].Value = protLen;
                    else if (colNames[i].Equals("DEFLINE", StringComparison.OrdinalIgnoreCase)) insertCmd.Parameters[$"@p{i}"].Value = curDefline;
                    else if (colNames[i].Equals("GENEID", StringComparison.OrdinalIgnoreCase)) insertCmd.Parameters[$"@p{i}"].Value = (object?)geneId ?? DBNull.Value;
                    else if (templateReader.IsDBNull(i)) insertCmd.Parameters[$"@p{i}"].Value = DBNull.Value;
                    else
                    {
                        var fieldType = templateReader.GetFieldType(i);
                        insertCmd.Parameters[$"@p{i}"].Value = fieldType == typeof(long) ? templateReader.GetInt64(i)
                            : fieldType == typeof(string) ? templateReader.GetString(i)
                            : templateReader.GetDouble(i);
                    }
                }

                try
                {
                    insertCmd.ExecuteNonQuery();
                }
                catch (SqliteException e)
                {
                    Console.Error.Write($"\nError caused by inserting extra protein row for {curId}\n\n");
                    Console.Error.WriteLine(e);
                    Environment.Exit(0);
                }
            }
        }
        tx.Commit();
    }

    /// <summary>Writes a peptide-level output file (one row per modPeptide/charge, columns per experiment).</summary>
    public virtual void PeptideLevelResults(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("\nWriting peptide-level summary file to disk.\n");
        else Console.Error.WriteLine("\nWriting peptide-level summary file to disk.\n");

        using var tagReader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags");
        var tags = new List<string>();
        while (tagReader.Read()) tags.Add(tagReader.GetString(0));
        tagReader.Close();

        var ddl = "CREATE TABLE pepResults (modPep VARCHAR(250), charge INT, "
            + string.Join(", ", tags.Select(t => $"{t}_maxProb DOUBLE DEFAULT 0, {t}_nspecs INT DEFAULT 0"))
            + ")";
        Exec(conn, ddl);

        using (var cmd = conn.CreateCommand())
        using (var tx = BeginTransaction(conn, cmd))
        {
            cmd.CommandText = "INSERT INTO pepResults (modPep, charge) VALUES (@modPep, @charge)";
            var pModPep = cmd.Parameters.Add("@modPep", SqliteType.Text);
            var pCharge = cmd.Parameters.Add("@charge", SqliteType.Integer);

            using var reader = ExecuteReader(conn, "SELECT DISTINCT modPeptide, charge FROM pepXML");
            while (reader.Read())
            {
                pModPep.Value = reader.GetString(0);
                pCharge.Value = reader.GetInt32(1);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        foreach (var tag in tags)
        {
            if (console != null) console.Append($"Adding {tag} peptides\n");
            else Console.Error.Write($"Adding {tag} peptides\n");

            using var reader = ExecuteReader(conn, $"""
                SELECT modPeptide, charge, MAX(iniProb) as maxProb, COUNT(DISTINCT specId) as nspecs
                FROM pepXML WHERE tag = '{tag}'
                GROUP BY modPeptide, charge
                """);
            var rows = new List<(string modPep, int z, double prob, int nspecs)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetDouble(2), reader.GetInt32(3)));
            reader.Close();

            using var tagTx = conn.BeginTransaction();
            foreach (var (modPep, z, prob, nspecs) in rows)
            {
                var escaped = modPep.Replace("'", "''");
                Exec(conn, $"UPDATE pepResults SET {tag}_nspecs = {nspecs} WHERE modPep = '{escaped}' AND charge = {z}");
                Exec(conn, $"UPDATE pepResults SET {tag}_maxProb = {prob} WHERE modPep = '{escaped}' AND charge = {z}");
            }
            tagTx.Commit();
        }

        using var writer = new StreamWriter(Globals.OutputFilePath!);
        using var outReader = ExecuteReader(conn, "SELECT * FROM pepResults");
        var ncols = outReader.FieldCount;

        for (var i = 0; i < ncols - 1; i++) writer.Write(outReader.GetName(i) + "\t");
        writer.Write(outReader.GetName(ncols - 1) + "\n");

        while (outReader.Read())
        {
            for (var i = 0; i < ncols; i++)
            {
                // peptideLevelResults does not round doubles (ported as found - see CLAUDE.md)
                writer.Write(FormatCell(outReader, i, roundDoubles: false));
                writer.Write(i != ncols - 1 ? "\t" : "\n");
            }
        }
    }

    protected static SqliteDataReader ExecuteReader(SqliteConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteReader();
    }
}
