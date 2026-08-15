using System.Text;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace Abacus;

/// <summary>
/// Top-level orchestration, ported from abacus/abacus.java: parses CLI args
/// (via Globals), sets up the working SQLite database (HyperSQL in the
/// original), streams the pepXML/protXML files into it, then drives the
/// aggregation engine (HyperSqlObject / HyperSqlObjectGene).
/// </summary>
public class Abacus
{
    public void Run(string[] args)
    {
        Console.Error.Write(PrintHeader());

        Globals.ParseCommandLineArgs(args);

        Console.Error.Write(Globals.PrintParameters());

        // Verify that the output file's directory is valid.
        // NOTE: the original Java did `new File(new File(outputFilePath).getParent())`
        // which throws a NullPointerException whenever outputFilePath is a bare
        // filename with no directory component - i.e. whenever the user relies on
        // the documented default `outputFile=ABACUS_output.tsv`. Treating an empty
        // parent as "current directory" (which always exists) fixes that crash.
        var parentPath = Path.GetDirectoryName(Globals.OutputFilePath);
        if (!string.IsNullOrEmpty(parentPath) && !Directory.Exists(parentPath))
        {
            Globals.PrintError(ParamError.OutputPathNotFound);
        }

        // verify that user input is a valid directory
        if (!Directory.Exists(Globals.SrcDir))
        {
            Globals.PrintError(ParamError.DirError);
        }

        // Clean up any leftover DB file from a previous run before creating a new one.
        // (HSQLDB spreads a database across several sidecar files - .data/.properties/
        // .script/.tmp/.log; SQLite uses a single file, so there's just one to remove.)
        var dbFile = Globals.DbName + ".db";
        if (File.Exists(dbFile))
        {
            File.Delete(dbFile);
            Console.Error.WriteLine($"Abacus disk clean up: removing {dbFile}");
        }
        Console.Error.Write("\n");

        RecordXmlFiles(Globals.SrcDir!); // record only the protXML and pepXML files

        if (!Globals.ByPeptide)
        {
            if (string.IsNullOrEmpty(Globals.FastaFile))
            {
                Console.Error.Write("No fasta file was given so protein lengths will not be reported\n\n");
            }
            else
            {
                Console.Error.WriteLine($"Retrieving protein lengths from\n'{Globals.FastaFile}'");
                Globals.ParseFasta(null);
                Console.Error.Write("\n");
            }
        }

        // By default the database lives in memory; if the user wants to keep it,
        // it's written to a single SQLite file instead (much slower).
        string connectionString;
        if (Globals.KeepDb)
        {
            connectionString = $"Data Source={dbFile}";
            Console.Error.WriteLine("\nDatabase will be written to disk within the following file:");
            Console.Error.Write($"\t{dbFile}\n\n");
            Console.Error.WriteLine("NOTE: Writing to disk slows things down so please be patient...\n\n");
        }
        else
        {
            connectionString = "Data Source=:memory:";
        }

        var startTime = DateTime.UtcNow;
        SqliteConnection? conn = null;

        try
        {
            conn = new SqliteConnection(connectionString);
            conn.Open();

            // WAL + synchronous=NORMAL is the standard low-overhead-but-still-
            // crash-safe combo: it avoids a full fsync-safe rollback-journal
            // write on every transaction commit (the default journal_mode=DELETE
            // + synchronous=FULL), which matters a lot for keepDB=true's on-disk
            // database given how many separate batched transactions the pipeline
            // commits. A no-op for the default :memory: database - SQLite always
            // uses its "memory" journal mode there regardless of this pragma.
            //
            // temp_store=MEMORY matters for *both* modes, including the default
            // :memory: one: SQLite's default temp_store (0, "compile-time
            // default") resolves to file-based temp storage for the scratch
            // B-trees ORDER BY/GROUP BY/CREATE INDEX/DISTINCT spill to when they
            // don't fit in the in-memory sort buffer - meaning a "fully
            // in-memory" run was still doing real disk I/O for every one of the
            // pipeline's many sorts and index builds unless this is set.
            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
                pragmaCmd.ExecuteNonQuery();
            }

            if (!Globals.ByPeptide)
            {
                LoadProtXml(conn, null);
                Console.Error.Write("\n");
            }

            LoadPepXml(conn, null);
            Console.Error.Write("\n");
        }
        catch (Exception e)
        {
            Console.Error.Write(e.ToString());
            Environment.Exit(-1);
        }

        try
        {
            HyperSqlObject? forProteins = null;
            HyperSqlObjectGene? forGenes = null;

            if (Globals.ByPeptide) // user wants peptide-level results
            {
                forProteins = new HyperSqlObject();
                forProteins.Initialize();
                forProteins.MakeSrcFileTable(conn!, null);

                forProteins.CorrectPepXmlTags(conn!);
                forProteins.PeptideLevelResults(conn!, null);
            }
            else if (Globals.ByGene) // user wants gene-centric output
            {
                forGenes = new HyperSqlObjectGene();
                forGenes.Initialize();

                forGenes.MakeSrcFileTable(conn!, null);
                forGenes.CorrectPepXmlTags(conn!);

                forGenes.MakeGeneTable(conn!, null);
                forGenes.MakeCombinedTable(conn!, null);
                forGenes.MakeProtXmlTable(conn!, null);

                GC.Collect(); // need more memory

                forGenes.MakeGeneCombined(conn!, null);
                forGenes.MakeGeneXml(conn!, null);
                forGenes.AdjustGenePeptideWt(conn!, null);

                forGenes.MakeTempGene2PepTable(conn!);

                forGenes.MakeGeneidSummary(conn!, null);
                forGenes.MakeGeneResults(conn!, null);

                forGenes.MakeGenePepUsageTable(conn!, null);
                forGenes.AppendIndividualExptsGc(conn!, null);

                if (Globals.DoNsaf)
                {
                    forGenes.GetNsafValuesGene(conn!, null);
                }

                if (Globals.GenesHaveDescriptions)
                {
                    forGenes.AppendGeneDescriptions(conn!);
                }

                if (Globals.OutputFormat == Globals.GeneQspecFormat)
                    forGenes.FormatQspecOutput(conn!, null);
                else
                    forGenes.DefaultResults(conn!, null);
            }
            else // default protein-centric output
            {
                forProteins = new HyperSqlObject();
                forProteins.Initialize();

                forProteins.MakeSrcFileTable(conn!, null);
                forProteins.CorrectPepXmlTags(conn!);

                forProteins.MakeCombinedTable(conn!, null);
                forProteins.MakeProtXmlTable(conn!, null);

                GC.Collect(); // need more memory

                forProteins.MakeTempProt2PepTable(conn!, null);

                forProteins.MakeProtidSummary(conn!, null);

                if (Globals.Gene2ProtFile != null)
                {
                    forProteins.MakeGeneTable(conn!, null);
                    forProteins.AppendGeneIds(conn!, null);
                    Console.Error.Write("\n");
                }

                forProteins.MakeResultsTable(conn!, null);
                forProteins.AddProteinLengths(conn!, null, 0);

                // adjust spectral counts
                forProteins.MakeWt9XgroupsTable(conn!);
                forProteins.MakePepUsageTable(conn!, null);

                // add individual experiment data to results table
                forProteins.AppendIndividualExpts(conn!, null);

                // reduce the number of columns in the results table by merging
                // the groupid and siblingGroup fields
                forProteins.MergeIdFields(conn!);

                if (Globals.DoNsaf)
                {
                    forProteins.GetNsafValuesProt(conn!, null);
                }

                if (Globals.MakeVerboseOutput)
                {
                    forProteins.AddExtraProteins(conn!, null);
                    forProteins.AddProteinLengths(conn!, null, 1);
                }

                switch (Globals.OutputFormat)
                {
                    case Globals.ProtQspecFormat:
                        forProteins.FormatQspecOutput(conn!, null);
                        break;
                    case Globals.CustomOutput:
                        forProteins.CustomOutput(conn!, null);
                        break;
                    default:
                        forProteins.DefaultResults(conn!, null);
                        break;
                }
            }

            // user elected to keep the database - remove unnecessary tables
            if (Globals.KeepDb)
            {
                if (Globals.ByGene) forGenes!.CleanUp(conn!);
                else forProteins!.CleanUp(conn!);
            }

            conn!.Close();
            conn.Dispose();
            conn = null;

            var elapsedMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
            var timeStr = Globals.FormatTime(elapsedMs);
            Console.Error.WriteLine($"\nTotal runtime (hh:mm:ss): {timeStr}\n");
            GC.Collect();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
        }
        finally
        {
            conn?.Dispose();
        }
    }

    /// <summary>
    /// Reads the entire contents of a file as text (Latin-1/ISO-8859-1, matching
    /// the Java original's "8859_1" decoder). Unused by the rest of the program
    /// in the original source, ported here only for parity.
    /// </summary>
    public static string FromFile(string filename)
    {
        return File.ReadAllText(filename, Encoding.Latin1);
    }

    /// <summary>Scans `dirPath` and records the pepXML (and, unless byPeptide, protXML) file names.</summary>
    public void RecordXmlFiles(string dirPath)
    {
        foreach (var name in Directory.EnumerateFiles(dirPath).Select(Path.GetFileName))
        {
            if (name != null && name.EndsWith(Globals.PepXmlSuffix) && !Globals.PepXmlFiles.Contains(name))
            {
                Globals.PepXmlFiles.Add(name);
            }
        }

        if (!Globals.ByPeptide)
        {
            foreach (var name in Directory.EnumerateFiles(dirPath).Select(Path.GetFileName))
            {
                if (name != null && name.EndsWith(Globals.ProtXmlSuffix) && !Globals.ProtXmlFiles.Contains(name))
                {
                    Globals.ProtXmlFiles.Add(name);
                }
            }
        }
    }

    public static bool ParseXmlDocument(string xmlFile, string dataType, IBatchInsert prep, int fileNumber, IConsole? console)
    {
        var filePath = Globals.SrcDir + Globals.FileSepChar + xmlFile;

        XmlReader? xmlReader = null;
        try
        {
            using var stream = File.OpenRead(filePath);
            xmlReader = XmlReader.Create(stream);

            var status = true;
            if (dataType == "pepXML")
            {
                status = ParsePepXml(xmlReader, xmlFile, prep, fileNumber, console);
                if (status) return status; // true means there's a problem in the pepXML file
            }
            if (dataType == "protXML")
            {
                status = ParseProtXml(xmlReader, xmlFile, prep, fileNumber, console);
                if (status) return status; // true means there's a problem in the protXML file
            }

            return false;
        }
        catch (FileNotFoundException)
        {
            if (console != null) console.Append("\nException getting input XML file.\n");
            else Console.Error.WriteLine("\nException getting input XML file.\n");
            return true;
        }
        catch (XmlException e)
        {
            if (console != null) console.Append("\nException getting XmlReader object.\n");
            else Console.Error.WriteLine(e);
            return true;
        }
        finally
        {
            xmlReader?.Dispose();
        }
    }

    /// <summary>Parses a protXML file, streaming rows into `prep` as each protein group finishes.</summary>
    public static bool ParseProtXml(XmlReader xmlReader, string xmlFile, IBatchInsert prep, int fileNumber, IConsole? console)
    {
        ProtXml? curGroup = null;    // current protein group
        string? curProtid = null;    // needed to get the protein's description
        string? curPep = null;       // needed to annotate AA modifications
        var isIprophetData = false;  // true means this is an i-prophet file

        var err = $"Parsing protXML [ {fileNumber + 1} of {Globals.ProtXmlFiles.Count} ]:  {xmlFile}\n";
        if (console != null) console.Append(err);
        else Console.Error.Write(err);

        void EndPeptide()
        {
            curGroup!.AnnotateModPeptideProtXml(curPep!);
            curPep = null;
        }

        // returns true if an unrecoverable error occurred and parsing should stop
        bool EndProtein()
        {
            curGroup!.ClassifyGroup();
            try
            {
                curGroup.WriteToDb(prep);
            }
            catch (Exception e)
            {
                if (console != null) { console.Append(e.ToString()); return true; }
                Console.Error.WriteLine(e);
                Environment.Exit(-1);
            }
            curGroup.ClearVariables();
            curProtid = null;
            return false;
        }

        bool EndProteinGroup()
        {
            curGroup!.ClassifyGroup();
            try
            {
                if (xmlFile.Contains(Globals.CombinedFile ?? string.Empty))
                {
                    if (curGroup.Pw >= Globals.MinCombinedFilePw) curGroup.WriteToDb(prep);
                }
                else
                {
                    if (curGroup.Pw >= Globals.MinPw) curGroup.WriteToDb(prep);
                }
            }
            catch (Exception e)
            {
                if (console != null) { console.Append(e.ToString()); return true; }
                Console.Error.WriteLine(e);
                Environment.Exit(-1);
            }
            curGroup.ClearVariables();
            curGroup = null;
            curProtid = null;
            return false;
        }

        try
        {
            while (xmlReader.Read())
            {
                if (xmlReader.NodeType == XmlNodeType.Element)
                {
                    var elementName = xmlReader.LocalName;
                    // .NET's XmlReader never emits a separate EndElement node for
                    // self-closing elements (Java's StAX always does); capture this
                    // now, before any attribute walk repositions the reader.
                    var isEmpty = xmlReader.IsEmptyElement;

                    if (elementName == "proteinprophet_details")
                    {
                        // identifies whether this protXML file is i-prophet output
                        for (var i = 0; i < xmlReader.AttributeCount; i++)
                        {
                            xmlReader.MoveToAttribute(i);
                            if (xmlReader.LocalName == "run_options")
                            {
                                if (xmlReader.Value.Contains("IPROPHET")) isIprophetData = true;
                                break;
                            }
                        }
                        xmlReader.MoveToElement();
                    }
                    else if (elementName == "protein_summary_header")
                    {
                        // identifies the pepXML files used to create this protXML file
                        if (Globals.ParseProtXmlHeader(xmlReader, xmlFile, console))
                        {
                            err = "\nERROR:\n"
                                + $"The pepXML files used to create '{xmlFile}' could not be found.\n"
                                + "The pepXML file names must match whatever is in the protXML file header.\n"
                                + "I have to quit now.\n\n";

                            if (console != null) { console.Append(err); return true; }
                            Console.Error.Write(err);
                            Environment.Exit(-1);
                        }
                    }
                    else if (elementName == "protein_group") // beginning of new protein group
                    {
                        curGroup = new ProtXml(xmlFile, isIprophetData);
                        curGroup.ParseProtGroupLine(xmlReader);
                        if (isEmpty && EndProteinGroup()) return true;
                    }
                    else if (elementName == "protein")
                    {
                        curProtid = curGroup!.ParseProteinLine(xmlReader);
                        if (isEmpty && EndProtein()) return true;
                    }
                    else if (elementName == "annotation")
                    {
                        for (var i = 0; i < xmlReader.AttributeCount; i++)
                        {
                            xmlReader.MoveToAttribute(i);
                            if (xmlReader.LocalName == "protein_description")
                            {
                                curGroup!.SetProtId(xmlReader.Value, curProtid!);
                                curProtid = null;
                                break;
                            }
                        }
                        xmlReader.MoveToElement();
                    }
                    else if (elementName == "indistinguishable_protein")
                    {
                        curProtid = curGroup!.ParseProteinLine(xmlReader);
                    }
                    else if (elementName == "peptide") // beginning of peptide record
                    {
                        curPep = curGroup!.ParsePeptideLine(xmlReader);
                        if (isEmpty) EndPeptide();
                    }
                    else if (elementName == "modification_info") // N-terminal modification
                    {
                        curGroup!.RecordAaModProtXml(xmlReader, curPep!);
                    }
                    else if (elementName == "mod_aminoacid_mass")
                    {
                        curGroup!.RecordAaModProtXml(xmlReader, curPep!);
                    }
                }
                else if (xmlReader.NodeType == XmlNodeType.EndElement) // end of a record
                {
                    var elementName = xmlReader.LocalName;

                    if (elementName == "peptide")
                    {
                        EndPeptide();
                    }
                    else if (elementName == "protein") // end of current protein
                    {
                        if (EndProtein()) return true;
                    }
                    else if (elementName == "protein_group") // end of protein group
                    {
                        if (EndProteinGroup()) return true;
                    }
                }
            }

            if (curGroup != null) // record last group entry
            {
                curGroup.ClassifyGroup();
                if (xmlFile.Contains(Globals.CombinedFile ?? string.Empty))
                {
                    if (curGroup.Pw >= Globals.MinCombinedFilePw) curGroup.WriteToDb(prep);
                }
                else
                {
                    if (curGroup.Pw >= Globals.MinPw) curGroup.WriteToDb(prep);
                }
                curGroup.ClearVariables();
            }
        }
        catch (XmlException e)
        {
            if (console != null)
            {
                console.Append($"Error parsing {xmlFile}: {e}");
                return true;
            }
            Console.Error.WriteLine(e);
            Environment.Exit(-1);
        }

        return false;
    }

    /// <summary>Parses a pepXML file, streaming rows into `prep` as each spectrum query finishes.</summary>
    public static bool ParsePepXml(XmlReader xmlReader, string xmlFile, IBatchInsert prep, int fileNumber, IConsole? console)
    {
        PepXml? curPsm = null; // current peptide-to-spectrum match
        var isIprophetData = false;

        var err = $"Parsing pepXML [ {fileNumber + 1} of {Globals.PepXmlFiles.Count} ]: {xmlFile}\n";
        if (console != null) console.Append(err);
        else Console.Error.Write(err);

        void EndSpectrumQuery()
        {
            curPsm!.AnnotateModPeptide();
            try
            {
                if (curPsm.IniProb >= Globals.IniProbTh) curPsm.WriteToDb(prep);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Environment.Exit(-1);
            }
            curPsm = null;
        }

        try
        {
            while (xmlReader.Read())
            {
                if (xmlReader.NodeType == XmlNodeType.Element)
                {
                    var elementName = xmlReader.LocalName;
                    var isEmpty = xmlReader.IsEmptyElement;

                    if (elementName == "analysis_summary")
                    {
                        for (var i = 0; i < xmlReader.AttributeCount; i++)
                        {
                            xmlReader.MoveToAttribute(i);
                            if (xmlReader.LocalName == "analysis")
                            {
                                if (xmlReader.Value == "interprophet") isIprophetData = true;
                                break;
                            }
                        }
                        xmlReader.MoveToElement();
                    }

                    if (elementName == "peptideprophet_summary")
                    {
                        // Matches Java's explicit extra `xmlStreamReader.next()`: StAX
                        // always synthesizes an EndElement even for a self-closing tag,
                        // so an empty <peptideprophet_summary/> would have that call just
                        // consume its own end-tag (a no-op net skip). .NET never emits
                        // that synthetic node, so only step forward when there's a real
                        // child to skip past - otherwise this would over-skip one node.
                        if (!isEmpty) xmlReader.Read();
                    }
                    else if (elementName == "spectrum_query") // new peptide record starts
                    {
                        curPsm = new PepXml(xmlFile, isIprophetData);
                        curPsm.ParsePepXmlLine(xmlReader);
                        if (isEmpty) EndSpectrumQuery();
                    }

                    if (elementName == "search_hit") curPsm!.ParsePepXmlLine(xmlReader);

                    if (elementName == "modification_info") curPsm!.RecordAaMod(xmlReader);

                    if (elementName == "mod_aminoacid_mass") curPsm!.RecordAaMod(xmlReader);

                    if (elementName == "search_score") curPsm!.ParseSearchScoreLine(xmlReader);

                    if (elementName == "peptideprophet_result") curPsm!.RecordIniProb(xmlReader);

                    // if the user provided iProphet input, take the iProphet probability
                    // instead of the PeptideProphet probability
                    if (elementName == "interprophet_result") curPsm!.RecordIniProb(xmlReader);
                }
                else if (xmlReader.NodeType == XmlNodeType.EndElement)
                {
                    if (xmlReader.LocalName == "spectrum_query") EndSpectrumQuery();
                }
            }
        }
        catch (XmlException e)
        {
            if (console != null)
            {
                console.Append($"\nDied parsing {xmlFile}\n");
                console.Append("This error means there is a problem with the formatting of your pepXML file.\n");
                console.Append("Exiting now... sorry\n");
                return true;
            }
            Console.Error.WriteLine(e);
            Environment.Exit(-1);
        }

        return false;
    }

    /// <summary>
    /// Loads every protXML file into the RAWprotXML table. Loading is always
    /// handled protein-first since the source material is always protein-centric.
    /// </summary>
    public bool LoadProtXml(SqliteConnection conn, IConsole? console)
    {
        var err = "Loading protXML files\n";
        if (console != null) console.Append(err);
        else Console.Error.Write(err);

        using (var stmt = conn.CreateCommand())
        {
            stmt.CommandText = "DROP TABLE IF EXISTS RAWprotXML";
            stmt.ExecuteNonQuery();

            stmt.CommandText = """
                CREATE TABLE RAWprotXML (
                  srcFile VARCHAR(250),
                  groupid INT,
                  siblingGroup VARCHAR(5),
                  Pw DECIMAL(8,6),
                  localPw DECIMAL(8,6),
                  protId VARCHAR(100),
                  isFwd INT,
                  peptide VARCHAR(250),
                  modPeptide VARCHAR(250),
                  charge INT,
                  iniProb DECIMAL(8,6),
                  wt DECIMAL(8,6),
                  defline VARCHAR(1000)
                )
                """;
            stmt.ExecuteNonQuery();
        }

        const string insertSql = """
            INSERT INTO RAWprotXML VALUES (
              @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13
            )
            """;
        var prep = new SqliteBatchInsert(conn, insertSql, 13);
        var status = false;

        // the database schema has been created; iterate through the protXML files
        // loading their content
        for (var i = 0; i < Globals.ProtXmlFiles.Count; i++)
        {
            Globals.ProceedWithQuery = false;
            status = ParseXmlDocument(Globals.ProtXmlFiles[i], "protXML", prep, i, console);
            if (status) return status; // true means something went wrong

            if (Globals.ProceedWithQuery) // at least 1 row was staged for insertion
            {
                prep.ExecuteBatch();
                prep.ClearBatch();
            }
        }
        prep.ClearBatch();

        return status;
    }

    /// <summary>
    /// Loads every pepXML file into the pepXML table. Loading is always handled
    /// protein-first since the source material is always protein-centric.
    /// </summary>
    public bool LoadPepXml(SqliteConnection conn, IConsole? console)
    {
        var err = "Loading pepXML files\n";
        if (console != null) console.Append(err);
        else Console.Error.Write(err);

        using (var stmt = conn.CreateCommand())
        {
            stmt.CommandText = "DROP TABLE IF EXISTS pepXML";
            stmt.ExecuteNonQuery();

            stmt.CommandText = """
                CREATE TABLE pepXML (
                  srcFile VARCHAR(250),
                  specId VARCHAR(250),
                  charge TINYINT,
                  peptide VARCHAR(250),
                  modPeptide VARCHAR(250),
                  iniProb DECIMAL(8,6)
                )
                """;
            stmt.ExecuteNonQuery();
        }

        const string insertSql = "INSERT INTO pepXML VALUES (@p1, @p2, @p3, @p4, @p5, @p6)";
        var prep = new SqliteBatchInsert(conn, insertSql, 6);
        var status = false;

        for (var i = 0; i < Globals.PepXmlFiles.Count; i++)
        {
            Globals.ProceedWithQuery = false;
            status = ParseXmlDocument(Globals.PepXmlFiles[i], "pepXML", prep, i, console);
            if (status) return status; // true means something went wrong

            if (Globals.ProceedWithQuery) // at least 1 row was staged for insertion
            {
                prep.ExecuteBatch();
                prep.ClearBatch();
            }
        }
        prep.ClearBatch();

        return status;
    }

    /// <summary>Reports the program's version and license banner.</summary>
    public string PrintHeader()
    {
        var sb = new StringBuilder();
        sb.Append("\n***********************************\n");
        sb.Append("\tAbacus\n");
        sb.Append("\tVersion: ");
        sb.Append("2.5");
        sb.Append("\n***********************************\n");
        sb.Append(
            "Developed and written by: Damian Fermin and Alexey Nesvizhskii\n" +
            "Copyright 2010 Damian Fermin\n\n" +
            "Licensed under the Apache License, Version 2.0 (the \"License\");\n" +
            "you may not use this file except in compliance with the License.\n" +
            "You may obtain a copy of the License at \n\n" +
            "http://www.apache.org/licenses/LICENSE-2.0\n\n" +
            "Unless required by applicable law or agreed to in writing, software\n" +
            "distributed under the License is distributed on an \"AS IS\" BASIS,\n" +
            "WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.\n" +
            "See the License for the specific language governing permissions and\n" +
            "limitations under the License.\n\n"
        );
        return sb.ToString();
    }
}
