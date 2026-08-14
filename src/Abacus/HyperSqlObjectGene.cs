using Microsoft.Data.Sqlite;

namespace Abacus;

/// <summary>
/// Ported from abacus/hyperSQLObject_gene.java - gene-centric variant of the
/// aggregation engine. Builds geneCombined/geneXML/geneidSummary/geneResults
/// on top of the protein-centric tables HyperSqlObject already built
/// (combined, protXML, gene2prot), then reuses the base class's shared
/// output writers (formatQspecOutput/defaultResults/cleanUp) and
/// ReformatResults (which itself branches on Globals.ByGene).
/// </summary>
public class HyperSqlObjectGene : HyperSqlObject
{
    /// <summary>Creates a gene-centric combined table from the protein-centric `combined` table.</summary>
    public virtual void MakeGeneCombined(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("Creating gene-centric combined table (this can take a while)...\n");
        else Console.Error.Write("Creating gene-centric combined table (this can take a while)...\n");

        Exec(conn, """
            CREATE TABLE geneCombined AS
            SELECT gn.geneid AS geneid, c.isFwd AS isFwd, c.modPeptide AS modPeptide
            FROM combined c, gene2prot gn
            WHERE c.protid = gn.protid
            AND c.isFwd = 1
            GROUP BY gn.geneid, c.isFwd, c.modPeptide
            ORDER BY gn.geneid ASC
            """);

        // BEFORE isn't supported by SQLite; columns always append at the end (see CLAUDE.md)
        Exec(conn, "ALTER TABLE geneCombined ADD COLUMN max_local_Pw DECIMAL(8,6)");
        Exec(conn, "ALTER TABLE geneCombined ADD COLUMN maxPw DECIMAL(8,6)");
        Exec(conn, "ALTER TABLE geneCombined ADD COLUMN iniProb DECIMAL(8,6)");

        Exec(conn, "CREATE INDEX gc_idx1 ON geneCombined(geneid)");
        Exec(conn, "CREATE INDEX gc_idx2 ON geneCombined(modPeptide)");

        if (console != null) console.Append("  Updating maxPw\n");
        else Console.Error.Write("  Updating maxPw\n");

        Exec(conn, "CREATE TABLE t1_ (geneid VARCHAR(100), maxPw DECIMAL(8,6), max_localPw DECIMAL(8,6))");
        Exec(conn, """
            INSERT INTO t1_
            SELECT gn.geneid, MAX(c.Pw), MAX(c.localPw)
            FROM gene2prot gn, combined c
            WHERE gn.protid = c.protid
            AND c.isFwd = 1
            GROUP BY gn.geneid
            """);
        Exec(conn, "CREATE INDEX t1_idx1 ON t1_(geneid)");

        // SQLite supports row-value UPDATE SET (a,b) = (subquery) since 3.15.
        Exec(conn, """
            UPDATE geneCombined
              SET (maxPw, max_local_Pw) = (
                SELECT maxPw, max_localPw FROM t1_ WHERE t1_.geneid = geneCombined.geneid
              )
            """);

        if (console != null) console.Append("  Updating Peptide Probabilities\n");
        else Console.Error.Write("  Updating Peptide Probabilities\n");

        Exec(conn, "CREATE TABLE t2_ (geneid VARCHAR(200), modPeptide VARCHAR(250), iniProb DECIMAL(8,6))");
        Exec(conn, """
            INSERT INTO t2_
            SELECT gc.geneid, gc.modPeptide, MAX(px.iniProb)
            FROM geneCombined gc, pepXML px
            WHERE gc.modPeptide = px.modPeptide
            GROUP BY gc.geneid, gc.modPeptide
            ORDER BY gc.geneid
            """);
        Exec(conn, "CREATE INDEX t2_idx1 ON t2_(geneid)");
        Exec(conn, "CREATE INDEX t2_idx2 ON t2_(modPeptide)");
        Exec(conn, "CREATE INDEX t2_idx3 ON t2_(geneid, modPeptide)");

        Exec(conn, """
            UPDATE geneCombined
              SET iniProb = (
                SELECT x.iniProb FROM t2_ x
                WHERE x.geneid = geneCombined.geneid
                AND x.modPeptide = geneCombined.modPeptide
              )
            """);

        // Decoy proteins have no genes by definition, so insert them separately.
        if (console != null) console.Append("  Accounting for decoy protein matches (if any)\n");
        else Console.Error.Write("  Accounting for decoy protein matches (if any)\n");

        Exec(conn, """
            INSERT INTO geneCombined
            SELECT 'decoy-' || c.groupid, c.isFwd,
              MAX(c.Pw), MAX(c.localPw), c.modPeptide, MAX(c.iniProb)
            FROM combined c
            WHERE c.isFwd = 0
            GROUP BY c.groupid, c.isFwd, c.modPeptide
            """);

        // record how many protein groups matched each geneid
        Exec(conn, "ALTER TABLE geneCombined ADD COLUMN numGroups INT DEFAULT 1");

        if (console != null) console.Append("  Calculating gene id usage\n");
        else Console.Error.Write("  Calculating gene id usage\n");

        Exec(conn, "CREATE TABLE t3_ (geneid VARCHAR(100), freq INT)");
        Exec(conn, """
            INSERT INTO t3_
            SELECT gn.geneid, COUNT(DISTINCT c.groupid)
            FROM combined c, gene2prot gn
            WHERE c.protid = gn.protid
            GROUP BY gn.geneid
            ORDER BY gn.geneid
            """);
        Exec(conn, "CREATE INDEX t3_idx1 ON t3_(geneid)");

        Exec(conn, """
            UPDATE geneCombined
              SET numGroups = (SELECT x.freq FROM t3_ x WHERE x.geneid = geneCombined.geneid)
            """);

        // recompute peptide weights to be gene-centric instead of protein-centric
        Exec(conn, "ALTER TABLE geneCombined ADD COLUMN wt DECIMAL(8,6) DEFAULT 0");

        if (console != null) console.Append("  Adjusting peptide weights on gene basis\n");
        else Console.Error.Write("  Adjusting peptide weights on gene basis\n");

        using (var modPepReader = ExecuteReader(conn, "SELECT DISTINCT modPeptide FROM geneCombined"))
        {
            var modPeps = new List<string>();
            while (modPepReader.Read()) modPeps.Add(modPepReader.GetString(0));
            modPepReader.Close();

            using var cmd = conn.CreateCommand();
            using var tx = BeginTransaction(conn, cmd);
            cmd.CommandText = "UPDATE geneCombined SET wt = @wt WHERE modPeptide = @modPep";
            var pWt = cmd.Parameters.Add("@wt", SqliteType.Real);
            var pModPep = cmd.Parameters.Add("@modPep", SqliteType.Text);

            foreach (var modPep in modPeps)
            {
                var count = ExecScalarInt(conn, $"SELECT COUNT(DISTINCT geneid) FROM geneCombined WHERE modPeptide = '{modPep.Replace("'", "''")}'");
                var n = Math.Round(1.0 / count, 4, MidpointRounding.ToEven);

                pWt.Value = n;
                pModPep.Value = modPep;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        Exec(conn, "DROP INDEX t1_idx1");
        Exec(conn, "DROP TABLE t1_");
        Exec(conn, "DROP INDEX t2_idx1");
        Exec(conn, "DROP INDEX t2_idx2");
        Exec(conn, "DROP INDEX t2_idx3");
        Exec(conn, "DROP TABLE t2_");
        Exec(conn, "DROP INDEX t3_idx1");
        Exec(conn, "DROP TABLE t3_");
    }

    /// <summary>Creates geneXML from the per-experiment protXML table, mapped through gene2prot.</summary>
    public virtual void MakeGeneXml(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("\nCreating geneXML table\n");
        else Console.Error.Write("\nCreating geneXML table\n");

        Exec(conn, """
            CREATE TABLE geneXML (
              tag VARCHAR(250),
              geneid VARCHAR(100),
              isFwd INT,
              maxPw DECIMAL(8,6),
              max_localPw DECIMAL(8,6),
              modPeptide VARCHAR(250),
              iniProb DECIMAL(8,6)
            )
            """);

        Exec(conn, $"""
            INSERT INTO geneXML
            SELECT r.tag, gn.geneid, r.isFwd, MAX(r.Pw), MAX(r.localPw),
              r.modPeptide, MAX(px.iniProb)
            FROM protXML r, gene2prot gn, pepXML px
            WHERE r.protId = gn.protid
            AND r.isFwd = 1
            AND r.tag = px.tag
            AND r.modPeptide = px.modPeptide
            AND px.iniProb >= {IniProbTh}
            AND r.iniProb >= {IniProbTh}
            GROUP BY r.tag, gn.geneid, r.isFwd, r.modPeptide
            HAVING max(r.localPw) > {MinPw}
            """);

        // decoy matches
        Exec(conn, $"""
            INSERT INTO geneXML
            SELECT r.tag, 'decoy-' || r.groupid, 0, MAX(r.Pw),
              MAX(r.localPw), r.modPeptide, MAX(r.iniProb)
            FROM protXML r
            WHERE r.isFwd = 0
            AND r.iniProb >= {IniProbTh}
            GROUP BY r.tag, r.groupid, r.modPeptide
            """);

        Exec(conn, "ALTER TABLE geneXML ADD COLUMN wt DECIMAL(8,6) DEFAULT 0 NOT NULL");

        Exec(conn, "CREATE INDEX gx_idx1 ON geneXML(tag, geneid)");
        Exec(conn, "CREATE INDEX gx_idx3 ON geneXML(modPeptide)");
        Exec(conn, "CREATE INDEX gx_idx4 ON geneXML(tag)");
        Exec(conn, "CREATE INDEX gx_idx5 ON geneXML(geneid)");

        // remove genes without at least 1 peptide >= epiThreshold, if stricter than iniProbTH
        if (Globals.EpiThreshold > Globals.IniProbTh)
        {
            Exec(conn, """
                CREATE TABLE x_ AS
                SELECT tag AS tag, geneid AS geneid, MAX(iniProb) AS maxIniProb
                FROM geneXML
                GROUP BY tag, geneid
                """);
            Exec(conn, "CREATE INDEX x_1 ON x_(tag, geneid)");
            Exec(conn, "CREATE INDEX x_2 ON x_(maxIniProb)");

            using (var reader = ExecuteReader(conn, $"SELECT * FROM x_ WHERE maxIniProb < {Globals.EpiThreshold}"))
            {
                var toDelete = new List<(string tag, string gid)>();
                while (reader.Read()) toDelete.Add((reader.GetString(0), reader.GetString(1)));
                reader.Close();

                using var tx = conn.BeginTransaction();
                foreach (var (tag, gid) in toDelete)
                {
                    Exec(conn, $"DELETE FROM geneXML WHERE tag = '{tag}' AND geneid = '{gid}'");
                }
                tx.Commit();
            }

            Exec(conn, "DROP INDEX IF EXISTS x_2");
            Exec(conn, "DROP INDEX IF EXISTS x_1");
            Exec(conn, "DROP TABLE IF EXISTS x_");
        }
    }

    /// <summary>Adjusts peptide weights on a gene basis (1 / number of genes a peptide maps to).</summary>
    public virtual void AdjustGenePeptideWt(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("  Adjusting peptide weights on gene basis\n");
        else Console.Error.Write("  Adjusting peptide weights on gene basis\n");

        using var tagReader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot'");
        var tags = new List<string>();
        while (tagReader.Read()) tags.Add(tagReader.GetString(0));
        tagReader.Close();

        console?.MonitorBoxInit(tags.Count, "Computing peptide weights.");

        var ctr = 1;
        using (var tx = conn.BeginTransaction())
        {
            foreach (var tag in tags)
            {
                Exec(conn, $"""
                    CREATE TABLE gpwt_{tag} AS
                    SELECT modPeptide AS modPeptide, COUNT(DISTINCT geneid) AS numGenes
                    FROM geneXML
                    WHERE tag = '{tag}'
                    GROUP BY modPeptide
                    """);
                Exec(conn, $"CREATE INDEX gpwt_{tag}_idx1 ON gpwt_{tag}(modPeptide)");
                Exec(conn, $"ALTER TABLE gpwt_{tag} ADD COLUMN wt DECIMAL(8,6) DEFAULT 0 NOT NULL");
                Exec(conn, $"UPDATE gpwt_{tag} SET wt = ROUND((1.0 / CAST(numGenes AS REAL)), 4)");

                // Original does this via a per-modPeptide row-by-row SELECT+UPDATE
                // loop (its own commented-out attempt at a single correlated-subquery
                // UPDATE was apparently abandoned mid-development). A single
                // correlated-subquery UPDATE is equivalent and simpler/faster.
                Exec(conn, $"""
                    UPDATE geneXML
                      SET wt = (SELECT wt FROM gpwt_{tag} WHERE gpwt_{tag}.modPeptide = geneXML.modPeptide)
                    WHERE tag = '{tag}'
                    """);

                if (console != null)
                {
                    console.MonitorBoxUpdate(ctr);
                    console.Append($"  {tag}\n");
                }
                ctr++;
            }
            tx.Commit();
        }

        if (console != null)
        {
            console.CloseMonitorBox();
            console.Append("\n");
        }
        else Console.Error.Write("\n");
    }

    /// <summary>Creates the geneidSummary table: per-gene rollup of geneCombined + spectral/peptide counts.</summary>
    public virtual void MakeGeneidSummary(SqliteConnection conn, IConsole? console)
    {
        var msg = "Creating geneidSummary table (this can take a while)...";
        if (console != null) console.Append(msg + "\n");

        Exec(conn, """
            CREATE TABLE geneidSummary (
              geneid VARCHAR(100),
              isFwd INT,
              maxPw DECIMAL(8,6),
              max_localPw DECIMAL(8,6),
              maxIniProb DECIMAL(8,6),
              numGroups INT
            )
            """);

        Exec(conn, """
            INSERT INTO geneidSummary
            SELECT geneid, isFwd, maxPw, max_local_Pw, MAX(iniProb), numGroups
            FROM geneCombined
            GROUP BY geneid, isFwd, maxPw, max_local_Pw, numGroups
            ORDER BY geneid
            """);

        Exec(conn, "CREATE INDEX geneSum_idx1 ON geneidSummary(geneid)");

        Exec(conn, "ALTER TABLE geneidSummary ADD COLUMN numXML INT");
        Exec(conn, "ALTER TABLE geneidSummary ADD COLUMN numSpecsTot INT DEFAULT 0");
        Exec(conn, "ALTER TABLE geneidSummary ADD COLUMN numSpecsUniq INT DEFAULT 0");
        Exec(conn, "ALTER TABLE geneidSummary ADD COLUMN numPepsTot INT DEFAULT 0");
        Exec(conn, "ALTER TABLE geneidSummary ADD COLUMN numPepsUniq INT DEFAULT 0");

        using var reader = ExecuteReader(conn, "SELECT geneid FROM geneidSummary");
        var geneids = new List<string>();
        while (reader.Read()) geneids.Add(reader.GetString(0));
        reader.Close();

        var iter = 0;
        using (var tx = conn.BeginTransaction())
        {
            foreach (var geneid in geneids)
            {
                var numXml = ExecScalarInt(conn, $"SELECT COUNT(DISTINCT tag) FROM geneXML WHERE geneid = '{geneid}'");
                Exec(conn, $"UPDATE geneidSummary SET numXML = {numXml} WHERE geneid = '{geneid}'");

                var nspecsTot = GetNumSpecsGc(geneid, CombinedFile!, conn, 0.0);
                var nspecsUniq = GetNumSpecsGc(geneid, CombinedFile!, conn, WtTh);
                Exec(conn, $"UPDATE geneidSummary SET numSpecsTot = {nspecsTot}, numSpecsUniq = {nspecsUniq} WHERE geneid = '{geneid}'");

                var npepsTot = GetNumPepsGc(geneid, CombinedFile!, conn, 0.0);
                var npepsUniq = GetNumPepsGc(geneid, CombinedFile!, conn, WtTh);
                Exec(conn, $"UPDATE geneidSummary SET numPepsTot = {npepsTot}, numPepsUniq = {npepsUniq} WHERE geneid = '{geneid}'");

                if (console == null) Globals.CursorStatus(iter, msg);
                iter++;
            }
            tx.Commit();
        }

        if (console != null) console.Append("\n");
        else Console.Error.Write("\n");
    }

    /// <summary>Returns the number of distinct peptides assigned to a given geneid.</summary>
    public virtual int GetNumPepsGc(string geneid, string sft, SqliteConnection conn, double wt)
    {
        var tag = sft == Globals.CombinedFile ? "COMBINED" : sft;
        return ExecScalarInt(conn, $"""
            SELECT COUNT(*) FROM (
              SELECT DISTINCT modPeptide
              FROM g2pep_
              WHERE tag = '{tag}'
              AND geneid = '{geneid}'
              AND wt >= {wt}
            )
            """);
    }

    /// <summary>Returns the number of spectra assigned to a given geneid.</summary>
    public virtual int GetNumSpecsGc(string geneid, string sft, SqliteConnection conn, double wt)
    {
        var tag = sft == Globals.CombinedFile ? "COMBINED" : sft;
        return ExecScalarInt(conn, $"SELECT SUM(nspec) FROM g2pep_ WHERE tag = '{tag}' AND geneid = '{geneid}' AND wt >= {wt}");
    }

    /// <summary>Creates the gene-centric results table.</summary>
    public virtual void MakeGeneResults(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("Creating gene-centric results table\n");
        else Console.Error.Write("Creating gene-centric results table\n");

        Exec(conn, $"""
            CREATE TABLE geneResults AS
            SELECT geneid AS geneid, isFwd AS isFwd, numXML AS numXML, numGroups AS numGroups,
              maxPw AS maxPw, max_localPw AS max_localPw,
              maxIniProb AS maxIniProb, numSpecsTot AS ALL_numSpecsTot, numSpecsUniq AS ALL_numSpecsUniq,
              numPepsTot AS ALL_numPepsTot, numPepsUniq AS ALL_numPepsUniq
            FROM geneidSummary
            WHERE maxIniProb > {MaxIniProbTh}
            AND numXML > 0
            GROUP BY geneid, isFwd, numXML, numGroups, maxPw, max_localPw,
              maxIniProb, ALL_numSpecsTot, ALL_numSpecsUniq, ALL_numPepsTot, ALL_numPepsUniq
            ORDER BY geneid ASC
            """);

        Exec(conn, "CREATE INDEX gr_idx1 ON geneResults(geneid)");
        Exec(conn, "ALTER TABLE geneResults ADD COLUMN numProts INT DEFAULT 0");
        Exec(conn, "ALTER TABLE geneResults ADD COLUMN avgProtLen INT DEFAULT 0");

        using var reader = ExecuteReader(conn, "SELECT geneid FROM geneResults");
        var geneids = new List<string>();
        while (reader.Read()) geneids.Add(reader.GetString(0));
        reader.Close();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE geneResults SET numProts = @numProts, avgProtLen = @avgProtLen WHERE geneid = @geneid";
        var pNumProts = cmd.Parameters.Add("@numProts", SqliteType.Integer);
        var pAvgProtLen = cmd.Parameters.Add("@avgProtLen", SqliteType.Integer);
        var pGeneid = cmd.Parameters.Add("@geneid", SqliteType.Text);

        using (var tx = BeginTransaction(conn, cmd))
        {
            foreach (var geneid in geneids)
            {
                var numProts = GetNumProts(conn, geneid);
                var avgProtLen = string.IsNullOrEmpty(Globals.FastaFile) ? 0 : GetAvgProtLen(conn, geneid);

                pNumProts.Value = numProts;
                pAvgProtLen.Value = avgProtLen;
                pGeneid.Value = geneid;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        if (console != null) console.Append("\n");
        else Console.Error.Write("\n");
    }

    /// <summary>Number of proteins associated with a geneid (or, for a decoy pseudo-gene, its protein group).</summary>
    private static int GetNumProts(SqliteConnection conn, string geneid)
    {
        if (geneid.StartsWith("decoy-"))
        {
            var groupid = int.Parse(geneid.Substring(6));
            return ExecScalarInt(conn, $"SELECT COUNT(DISTINCT protid) FROM combined WHERE groupid = {groupid}");
        }
        return ExecScalarInt(conn, $"SELECT COUNT(DISTINCT protid) FROM gene2prot WHERE geneid = '{geneid}'");
    }

    /// <summary>Average length of all proteins derived from a geneid (or decoy pseudo-gene's group).</summary>
    private static int GetAvgProtLen(SqliteConnection conn, string geneid)
    {
        List<string> protids;
        if (geneid.StartsWith("decoy-"))
        {
            var groupid = int.Parse(geneid.Substring(6));
            using var reader = ExecuteReader(conn, $"SELECT DISTINCT protid FROM combined WHERE groupid = {groupid}");
            protids = new List<string>();
            while (reader.Read()) protids.Add(reader.GetString(0));
        }
        else
        {
            using var reader = ExecuteReader(conn, $"SELECT DISTINCT protid FROM gene2prot WHERE geneid = '{geneid}'");
            protids = new List<string>();
            while (reader.Read()) protids.Add(reader.GetString(0));
        }

        var n = 0;
        var sumLen = 0;
        foreach (var protid in protids)
        {
            if (Globals.ProtLen.TryGetValue(protid, out var len))
            {
                n++;
                sumLen += len;
            }
        }

        // Matches Java's Math.round(float) = floor(x + 0.5f); n == 0 there
        // divides to NaN/Infinity and Math.round(NaN) is 0, so this mirrors
        // that (rather than throwing on the C# integer divide-by-zero).
        if (n == 0) return 0;
        var avg = (float)sumLen / n;
        return (int)Math.Floor(avg + 0.5f);
    }

    /// <summary>Creates a gene-centric peptide usage table (mirrors MakePepUsageTable, keyed by geneid instead of protid).</summary>
    public virtual void MakeGenePepUsageTable(SqliteConnection conn, IConsole? console)
    {
        var msg = "Creating gene-centric peptide usage table (this could take a while)...";
        if (console != null) console.Append(msg + "\n");

        Exec(conn, """
            CREATE TABLE genePepUsage_ (
              tag VARCHAR(250),
              geneid VARCHAR(100),
              modPeptide VARCHAR(250),
              nspecs INT,
              wt DECIMAL(8,6)
            )
            """);

        Exec(conn, """
            INSERT INTO genePepUsage_
            SELECT gr.tag, r.geneid, gr.modPeptide,
              COUNT(DISTINCT px.specId), gr.wt
            FROM geneXML gr, geneResults r, pepXML px
            WHERE gr.tag = px.tag
            AND gr.geneid = r.geneid
            AND gr.modPeptide = px.modPeptide
            GROUP BY gr.tag, r.geneid, gr.modPeptide, gr.wt
            ORDER BY gr.tag, r.geneid
            """);

        Exec(conn, "ALTER TABLE genePepUsage_ ADD COLUMN numer INT DEFAULT 0");
        Exec(conn, "ALTER TABLE genePepUsage_ ADD COLUMN denom INT DEFAULT 0");
        Exec(conn, "ALTER TABLE genePepUsage_ ADD COLUMN alpha DECIMAL(8,6) DEFAULT 0");
        Exec(conn, "ALTER TABLE genePepUsage_ ADD COLUMN adjSpecs INT DEFAULT 0");

        Exec(conn, "CREATE INDEX gpu_idx1 ON genePepUsage_(geneid)");
        Exec(conn, "CREATE INDEX gpu_idx2 ON genePepUsage_(tag)");
        Exec(conn, "CREATE INDEX gpu_idx3 ON genePepUsage_(tag,geneid)");
        Exec(conn, "CREATE INDEX gpu_idx5 ON genePepUsage_(modPeptide)");
        Exec(conn, "CREATE INDEX gpu_idx6 ON genePepUsage_(tag,modPeptide)");
        Exec(conn, "CREATE INDEX gpu_idx7 ON genePepUsage_(tag,geneid,modPeptide)");

        Exec(conn, $"""
            CREATE TABLE gWts_ AS
            SELECT tag AS tag, geneid AS geneid, SUM(nspecs) AS nspecsUniq
            FROM genePepUsage_
            WHERE wt > {WtTh}
            GROUP BY tag, geneid
            ORDER BY tag, geneid
            """);
        Exec(conn, "CREATE INDEX gw_idx1 ON gWts_(tag, geneid)");

        Exec(conn, """
            UPDATE genePepUsage_
              SET numer = (
                SELECT gWts_.nspecsUniq FROM gWts_
                WHERE gWts_.tag = genePepUsage_.tag
                AND gWts_.geneid = genePepUsage_.geneid
              )
            """);
        Exec(conn, "UPDATE genePepUsage_ SET numer = 0 WHERE numer IS NULL");

        // Original computes `denom` per (tag, modPeptide) by summing `numer`
        // across every geneid sharing that peptide, via a row-by-row loop; a
        // single correlated-subquery UPDATE is equivalent.
        Exec(conn, """
            UPDATE genePepUsage_
              SET denom = (
                SELECT SUM(p2.numer) FROM genePepUsage_ AS p2
                WHERE p2.tag = genePepUsage_.tag
                AND p2.modPeptide = genePepUsage_.modPeptide
              )
            """);

        // prevents division by zero
        Exec(conn, "UPDATE genePepUsage_ SET denom = 1 WHERE denom IS NULL");
        Exec(conn, "UPDATE genePepUsage_ SET denom = 1 WHERE denom = 0");

        // Java computes alpha as "CAST(numer AS DECIMAL(16,6)) / CAST(denom AS
        // DECIMAL(16,6))" with no ROUND() at all - same shape as
        // HyperSqlObject.MakePepUsageTable's alpha (see the comment there for
        // the full explanation, verified against a real HSQLDB instance): HSQLDB
        // truncates a DECIMAL(16,6)/DECIMAL(16,6) division to 6 decimal places
        // rather than rounding, so this has to be reproduced with an explicit
        // truncation rather than SQLite's REAL (double, which rounds) division.
        using (var readCmd = conn.CreateCommand())
        {
            readCmd.CommandText = "SELECT rowid, numer, denom, nspecs FROM genePepUsage_";
            var rows = new List<(long rowid, long numer, long denom, long nspecs)>();
            using (var rdr = readCmd.ExecuteReader())
            {
                while (rdr.Read()) rows.Add((rdr.GetInt64(0), rdr.GetInt64(1), rdr.GetInt64(2), rdr.GetInt64(3)));
            }

            using var writeCmd = conn.CreateCommand();
            using var tx = BeginTransaction(conn, writeCmd);
            writeCmd.CommandText = "UPDATE genePepUsage_ SET alpha = @alpha, adjSpecs = @adjSpecs WHERE rowid = @rowid";
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

        Exec(conn, "DROP INDEX IF EXISTS gw_idx1");
        Exec(conn, "DROP TABLE IF EXISTS gWts_");

        if (console != null) console.Append("\n\n");
        else Console.Error.Write("\n\n");
    }

    /// <summary>Appends per-experiment statistics to the gene-centric results table.</summary>
    public virtual void AppendIndividualExptsGc(SqliteConnection conn, IConsole? console)
    {
        if (console != null) console.Append("Appending individual experiment results\n");
        else Console.Error.Write("Appending individual experiment results\n");

        using (var tagReader = ExecuteReader(conn, "SELECT DISTINCT tag FROM srcFileTags WHERE fileType = 'prot' ORDER BY tag ASC"))
        {
            var tags = new List<string>();
            while (tagReader.Read()) tags.Add(tagReader.GetString(0));
            tagReader.Close();

            foreach (var tag in tags)
            {
                var msg = $"  Adding columns for {tag}";
                if (console != null) console.Append(msg);

                Exec(conn, $"ALTER TABLE geneResults ADD COLUMN {tag}_maxPw DECIMAL(8,6) DEFAULT 0");
                Exec(conn, $"ALTER TABLE geneResults ADD COLUMN {tag}_max_localPw DECIMAL(8,6) DEFAULT 0");
                Exec(conn, $"ALTER TABLE geneResults ADD COLUMN {tag}_maxIniProb DECIMAL(8,6) DEFAULT 0");
                Exec(conn, $"ALTER TABLE geneResults ADD COLUMN {tag}_numSpecsTot INT DEFAULT 0");
                Exec(conn, $"ALTER TABLE geneResults ADD COLUMN {tag}_numSpecsUniq INT DEFAULT 0");
                Exec(conn, $"ALTER TABLE geneResults ADD COLUMN {tag}_numSpecsAdj INT DEFAULT 0");
                Exec(conn, $"ALTER TABLE geneResults ADD COLUMN {tag}_numPepsTot INT DEFAULT 0");
                Exec(conn, $"ALTER TABLE geneResults ADD COLUMN {tag}_numPepsUniq INT DEFAULT 0");

                using var reader = ExecuteReader(conn, $"""
                    SELECT geneid, maxPw, max_localPw, MAX(iniProb)
                    FROM geneXML
                    WHERE tag = '{tag}'
                    GROUP BY geneid, maxPw, max_localPw
                    """);
                var rows = new List<(string geneid, double maxPw, double maxLocalPw, double maxIniProb)>();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
                reader.Close();

                var iter = 0;
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var (geneid, maxPw, maxLocalPw, maxIniProb) in rows)
                    {
                        Exec(conn, $"""
                            UPDATE geneResults
                              SET ({tag}_maxPw, {tag}_max_localPw, {tag}_maxIniProb) = ({maxPw}, {maxLocalPw}, {maxIniProb})
                            WHERE geneid = '{geneid}'
                            """);

                        var nsT = GetNumSpecsGc(geneid, tag, conn, 0);
                        var nsU = GetNumSpecsGc(geneid, tag, conn, WtTh);
                        var npT = GetNumPepsGc(geneid, tag, conn, 0);
                        var npU = GetNumPepsGc(geneid, tag, conn, WtTh);

                        Exec(conn, $"""
                            UPDATE geneResults
                              SET ({tag}_numSpecsTot, {tag}_numSpecsUniq, {tag}_numPepsTot, {tag}_numPepsUniq) = ({nsT}, {nsU}, {npT}, {npU})
                            WHERE geneid = '{geneid}'
                            """);

                        if (console == null) Globals.CursorStatus(iter, msg);
                        iter++;
                    }
                    tx.Commit();
                }

                if (console != null) console.Append("\n");
                else Console.Error.Write("\n");
            }
        }

        // fill in adjusted spectral count fields
        using (var reader = ExecuteReader(conn, "SELECT tag, geneid, SUM(adjSpecs) FROM genePepUsage_ GROUP BY tag, geneid ORDER BY tag, geneid"))
        {
            var rows = new List<(string tag, string geneid, int sum)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            reader.Close();

            using var tx = conn.BeginTransaction();
            foreach (var (tag, geneid, sum) in rows)
            {
                Exec(conn, $"UPDATE geneResults SET {tag}_numSpecsAdj = {sum} WHERE geneid = '{geneid}'");
            }
            tx.Commit();
        }
    }

    /// <summary>Builds g2pep_: peptide-to-gene usage counts across COMBINED and every experiment.</summary>
    public virtual void MakeTempGene2PepTable(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE g2pep_ (
              tag VARCHAR(250),
              geneid VARCHAR(100),
              modPeptide VARCHAR(250),
              wt DECIMAL(8,6),
              nspec INT DEFAULT 0
            )
            """);

        Exec(conn, $"""
            INSERT INTO g2pep_
            SELECT 'COMBINED', c.geneid, c.modPeptide,
              c.wt, COUNT(DISTINCT px.specId)
            FROM geneCombined c, pepXML px
            WHERE c.modPeptide = px.modPeptide
            AND px.iniProb >= {IniProbTh}
            GROUP BY c.geneid, c.modPeptide, c.wt
            ORDER BY c.geneid, c.modPeptide
            """);

        Exec(conn, $"""
            INSERT INTO g2pep_
            SELECT gx.tag, gx.geneid, gx.modPeptide,
              gx.wt, COUNT(DISTINCT px.specId)
            FROM geneXML gx, pepXML px
            WHERE gx.tag = px.tag
            AND gx.modPeptide = px.modPeptide
            AND px.iniProb >= {IniProbTh}
            GROUP BY gx.tag, gx.geneid, gx.modPeptide, gx.wt
            ORDER BY gx.tag, gx.geneid, gx.modPeptide
            """);

        Exec(conn, "CREATE INDEX g2pep_idx1 ON g2pep_(tag)");
        Exec(conn, "CREATE INDEX g2pep_idx2 ON g2pep_(geneid)");
        Exec(conn, "CREATE INDEX g2pep_idx4 ON g2pep_(tag,geneid)");
        Exec(conn, "CREATE INDEX g2pep_idx5 ON g2pep_(tag,modPeptide)");
        Exec(conn, "CREATE INDEX g2pep_idx6 ON g2pep_(tag,geneid,modPeptide)");
    }

    /// <summary>Appends gene descriptions (from the gene2prot file's optional 3rd column) to geneResults.</summary>
    public virtual void AppendGeneDescriptions(SqliteConnection conn)
    {
        Exec(conn, "ALTER TABLE geneResults ADD COLUMN geneDescription VARCHAR(1000)");

        using (var reader = ExecuteReader(conn, """
            SELECT g2p.geneid, g2p.geneDefline
            FROM gene2prot g2p, geneResults r
            WHERE g2p.geneid = r.geneid
            AND r.isFwd = 1
            GROUP BY g2p.geneid, g2p.geneDefline
            """))
        {
            var rows = new List<(string geneid, string defline)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
            reader.Close();

            using var cmd = conn.CreateCommand();
            using var tx = BeginTransaction(conn, cmd);
            cmd.CommandText = "UPDATE geneResults SET geneDescription = @defline WHERE geneid = @geneid";
            var pDefline = cmd.Parameters.Add("@defline", SqliteType.Text);
            var pGeneid = cmd.Parameters.Add("@geneid", SqliteType.Text);

            foreach (var (geneid, defline) in rows)
            {
                pDefline.Value = defline;
                pGeneid.Value = geneid;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }

        Exec(conn, "UPDATE geneResults SET geneDescription = 'DECOY MATCH' WHERE isFwd = 0");
    }

    /// <summary>Generates gene-centric spectral count data in NSAF format.</summary>
    public virtual void GetNsafValuesGene(SqliteConnection conn, IConsole? console)
    {
        Exec(conn, "DROP TABLE IF EXISTS nsaf_p1");
        Exec(conn, "DROP TABLE IF EXISTS nsaf");

        var msg = "\nCreating NSAF values table (gene-centric)\n";
        if (console != null) console.Append(msg);
        else Console.Error.Write(msg);

        Exec(conn, "CREATE TABLE nsaf_p1 AS SELECT geneid AS geneid FROM geneResults WHERE isFwd = 1 GROUP BY geneid ORDER BY geneid ASC");
        Exec(conn, "CREATE INDEX nsaf_p1_idx1 ON nsaf_p1(geneid)");

        Exec(conn, "CREATE TABLE nsaf AS SELECT geneid AS geneid FROM geneResults WHERE isFwd = 1 GROUP BY geneid ORDER BY geneid ASC");
        Exec(conn, "CREATE INDEX nsaf_idx1 ON nsaf(geneid)");

        var numProts = ExecScalarInt(conn, "SELECT COUNT(geneid) FROM geneResults WHERE isFwd = 1");
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
                SELECT geneid, avgProtLen,
                  {tag}_numSpecsTot,
                  {tag}_numSpecsUniq,
                  {tag}_numSpecsAdj
                FROM geneResults
                ORDER BY geneid
                """))
            {
                var lenRows = new List<(string geneid, double tot, double uniq, double adj)>();
                while (reader.Read())
                {
                    var protLen = reader.GetDouble(1);
                    lenRows.Add((reader.GetString(0), reader.GetDouble(2) / protLen, reader.GetDouble(3) / protLen, reader.GetDouble(4) / protLen));
                }
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var (geneid, tot, uniq, adj) in lenRows)
                    {
                        Exec(conn, $"""
                            UPDATE nsaf_p1
                              SET {tag}_specsTot = {tot}, {tag}_specsUniq = {uniq}, {tag}_specsAdj = {adj}
                            WHERE geneid = '{geneid}'
                            """);
                    }
                    tx.Commit();
                }
            }

            var totSum = ExecScalarDouble(conn, $"SELECT SUM({tag}_specsTot) FROM nsaf_p1");
            var uniqSum = ExecScalarDouble(conn, $"SELECT SUM({tag}_specsUniq) FROM nsaf_p1");
            var adjSum = ExecScalarDouble(conn, $"SELECT SUM({tag}_specsAdj) FROM nsaf_p1");

            using (var reader = ExecuteReader(conn, $"""
                SELECT geneid, {tag}_specsTot, {tag}_specsUniq, {tag}_specsAdj
                FROM nsaf_p1
                GROUP BY geneid, {tag}_specsTot, {tag}_specsUniq, {tag}_specsAdj
                ORDER BY geneid ASC
                """))
            {
                var nsafRows = new List<(string geneid, double t, double u, double a)>();
                while (reader.Read()) nsafRows.Add((reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3)));
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var (geneid, xT, xU, xA) in nsafRows)
                    {
                        var nsafT = totSum == 0 ? 0 : (xT / totSum) * nsafFactor;
                        var nsafU = uniqSum == 0 ? 0 : (xU / uniqSum) * nsafFactor;
                        var nsafA = adjSum == 0 ? 0 : (xA / adjSum) * nsafFactor;

                        Exec(conn, $"""
                            UPDATE nsaf
                              SET {tag}_totNSAF = {nsafT}, {tag}_uniqNSAF = {nsafU}, {tag}_adjNSAF = {nsafA}
                            WHERE geneid = '{geneid}'
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
}
