using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Abacus;

/// <summary>
/// Error codes for malformed/incomplete parameter files. Ported from the
/// int constants (noDBname..outputPathNotFound) in globals.java.
/// </summary>
public enum ParamError
{
    NoDbName = 1,
    NoCombinedTag = 2,
    NoSrcDir = 3,
    NoDecoyTag = 4,
    NoMaxIniProbTh = 5,
    NoIniProbTh = 6,
    NoMinCombinedFilePw = 7,
    NoMinPw = 8,
    DirError = 9,
    NoFastaFile = 10,
    FastaNotFound = 11,
    ParamFileNotFound = 12,
    MapFileNotFound = 13,
    ParamFileNull = 14,
    OutputPathNotFound = 15,
}

/// <summary>
/// Global state and helper functions, ported from abacus/globals.java.
/// Kept as a static class with public mutable fields to mirror the original's
/// architecture, since the rest of Abacus (pepXML/protXML/hyperSQLObject)
/// reads and writes this state directly throughout a run.
/// </summary>
public static class Globals
{
    public static string? OsType;
    public static string? SrcDir;
    public static string? ParamFile;
    public static string? DbName;
    public static string? Gene2ProtFile;

    public static string? OutputFilePath; // full path to output file
    public static string? OutputPath;     // parent path of the output file

    public static string? CombinedFilePath;
    public static string? CombinedFile;

    public static string? DecoyTag;
    public static string PepXmlSuffix = "pep.xml";
    public static string ProtXmlSuffix = "prot.xml";

    // peptide modifications the user wants to consider/discard
    public static string? PepRegexText;
    public static string[]? PepModsPlus;
    public static string[]? PepModsMinus;

    public static bool KeepDb;             // true = keep the database file
    public static bool RecalcPepWts;       // true = adjust peptide weights per experiment
    public static bool ByGene;             // true = gene-centric output
    public static bool ByPeptide;          // true = peptide-level results
    public static int OutputFormat = -1;   // kind of output the user wants
    public static bool GenesHaveDescriptions; // true = parsed gene2prot file has descriptions
    public static bool DoNsaf;             // true = report NSAF-formatted spectral counts

    public static double NsafFactor = -1;      // corrects for rounding error when computing NSAF
    public static double MaxIniProbTh = -100;
    public static double IniProbTh = -100;
    public static double MinCombinedFilePw = -100;
    public static double MinPw = -100;
    public static double EpiThreshold = -100;  // experimental peptide-probability inclusion threshold

    public static string FastaFile = "";
    public static Dictionary<string, int> ProtLen = new();

    public static bool MakeVerboseOutput;

    // pepXML file -> parent protXML file
    public static Dictionary<string, string> PepTagHash = new();

    // protXML file -> short tag
    public static Dictionary<string, string> ProtTagHash = new();

    public static List<string> PepXmlFiles = new();
    public static List<string> ProtXmlFiles = new();

    public static readonly string FileSepChar = Path.DirectorySeparatorChar.ToString();

    // output type constants
    public const int DefaultOutput = 0;
    public const int ProtQspecFormat = 1;
    public const int GeneQspecFormat = 4;
    public const int CustomOutput = 2;
    public const int GeneOutput = 3;
    public const int PeptideOutput = 5;

    public static bool ProceedWithQuery; // whether a prepared statement should proceed

    // custom output column selections
    public static HashSet<string> PrintC = new();
    public static HashSet<string> PrintE = new();

    /***************************************************************************
     *
     *                        Functions
     *
     **************************************************************************/

    /// <summary>
    /// Reads a FASTA file and records each protein's sequence length.
    /// Returns true if parsing failed (file malformed); false on success
    /// or if the FASTA file doesn't exist (NSAF/QSpec output requires one,
    /// but plain spectral counting does not).
    /// </summary>
    public static bool ParseFasta(IConsole? console)
    {
        ProtLen = new Dictionary<string, int>();

        try
        {
            var fastaF = new FileInfo(FastaFile);
            if (!fastaF.Exists) return true;

            using var reader = new StreamReader(fastaF.FullName);

            string key = "";
            var seq = new StringBuilder();
            string? line;
            bool firstLine = true;

            while ((line = reader.ReadLine()) != null)
            {
                if (firstLine)
                {
                    if (!line.StartsWith(">"))
                    {
                        var err = $"ERROR! '{fastaF.Name}' is not a properly formatted FASTA File.\n";
                        if (console == null) { Console.Error.Write(err); Environment.Exit(-1); }
                        else console.Append(err);
                        return true;
                    }
                    firstLine = false;
                }

                if (line.StartsWith(">"))
                {
                    if (key.Length > 0)
                    {
                        ProtLen[key] = seq.ToString().Trim().Length;
                    }
                    key = FormatProtId(line.Substring(1));
                    seq.Clear();
                }
                else
                {
                    seq.Append(line.Trim());
                }
            }

            if (seq.Length > 0)
            {
                ProtLen[key] = seq.ToString().Trim().Length;
            }
        }
        catch
        {
            // Original code silently swallows all parse errors here; preserved.
        }

        return false;
    }

    /// <summary>Assigns command line arguments to global variables.</summary>
    public static void ParseCommandLineArgs(string[] argv)
    {
        if (argv[0] == "-p") ParamFile = argv[1];

        if (argv.Length > 1 && argv[1] == "-t")
        {
            WriteTemplate();
            Environment.Exit(0);
        }

        if (string.IsNullOrEmpty(ParamFile))
        {
            PrintError(ParamError.ParamFileNull);
            Environment.Exit(0);
        }

        FastaFile = ""; // initialize to empty
        ParseParametersFile();

        // Check that all required options were set
        if (DbName == null) PrintError(ParamError.NoDbName);
        if (CombinedFile == null) PrintError(ParamError.NoCombinedTag);
        if (SrcDir == null) PrintError(ParamError.NoSrcDir);
        if (DecoyTag == null) PrintError(ParamError.NoDecoyTag);
        if (MaxIniProbTh == -100) PrintError(ParamError.NoMaxIniProbTh);
        if (IniProbTh == -100) PrintError(ParamError.NoIniProbTh);
        if (MinCombinedFilePw == -100) PrintError(ParamError.NoMinCombinedFilePw);

        if (Gene2ProtFile == null && ByGene) PrintError(ParamError.MapFileNotFound);
    }

    /// <summary>
    /// Determines the running OS. The original Java (getOStype) had a logic bug:
    /// its if/if/else chain meant Windows was always misreported as "nix"
    /// (the `else` bound only to the mac check). Fixed here using an
    /// unambiguous if/else-if chain against the current runtime platform.
    /// </summary>
    public static void DetermineOsType()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) OsType = "windows";
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) OsType = "mac";
        else OsType = "nix";
    }

    /// <summary>Parses the parameters file and records its values.</summary>
    public static void ParseParametersFile()
    {
        if (ParamFile == null || !File.Exists(ParamFile))
        {
            PrintError(ParamError.ParamFileNotFound);
        }

        var wsRegex = new Regex(@"\s+");

        try
        {
            foreach (var rawLine in File.ReadLines(ParamFile!))
            {
                var line = rawLine;
                if (line.StartsWith("#")) continue; // comment
                if (Regex.IsMatch(line, @"^[^\w]*$")) continue; // blank line

                // Matches Java's `line.split("=")` + ary[0]/ary[1] access: if a
                // value itself contains '=', everything past the second '=' is
                // silently dropped (faithful to the original, not fixed here).
                string[] ary;
                var split = line.Split('=');
                if (split.Length < 2)
                {
                    ary = new[] { split[0], "ERROR" };
                }
                else
                {
                    ary = new[] { split[0], split[1] };
                }

                if (ary[0].Trim() == "reqAAmods")
                {
                    var tmp = Regex.Replace(ary[1].Trim(), @"\s", "");
                    ary[1] = tmp.Length < 3 ? "ERROR" : tmp;
                }
                else if (ary[0].Trim().Contains("Prob") || ary[0].Trim().Contains("Pw"))
                {
                    ary[1] = ary[1].Trim(); // keep as-is (could be a negative number)
                }
                else
                {
                    var tmp = wsRegex.Split(ary[1].Trim());
                    ary[1] = tmp[0];
                }

                if (ary[0] == "dbName") DbName = ary[1] == "ERROR" ? "ABACUS" : ary[1];
                if (ary[0] == "combinedFile") CombinedFilePath = ary[1] == "ERROR" ? "" : ary[1];
                if (ary[0] == "srcDir") SrcDir = ary[1] == "ERROR" ? "" : ary[1];
                if (ary[0] == "fasta") FastaFile = ary[1] == "ERROR" ? "" : ary[1];
                if (ary[0] == "decoyTag") DecoyTag = ary[1] == "ERROR" ? "" : ary[1];

                if (ary[0] == "maxIniProbTH") MaxIniProbTh = ary[1] == "ERROR" ? 0.99 : double.Parse(ary[1]);
                if (ary[0] == "iniProbTH") IniProbTh = ary[1] == "ERROR" ? 0.50 : double.Parse(ary[1]);
                if (ary[0] == "epiTH") EpiThreshold = ary[1] == "ERROR" ? 0.50 : double.Parse(ary[1]);
                if (ary[0] == "minCombinedFilePw") MinCombinedFilePw = ary[1] == "ERROR" ? 0.90 : double.Parse(ary[1]);

                if (ary[0] == "verboseResults") MakeVerboseOutput = ary[1] == "true";

                if (ary[0] == "outputFile") OutputFilePath = ary[1] == "ERROR" ? "ABACUS_output.tsv" : ary[1];

                if (ary[0] == "recalcPepWts") RecalcPepWts = ary[1] == "true";

                // legacy options, kept in case they're needed again
                if (ary[0] == "protXMLsuffix") ProtXmlSuffix = ary[1] == "ERROR" ? "prot.xml" : ary[1];
                if (ary[0] == "pepXMLsuffix") PepXmlSuffix = ary[1] == "ERROR" ? "pep.xml" : ary[1];

                if (ary[0] == "keepDB") KeepDb = ary[1] == "true";

                if (ary[0] == "asNSAF") DoNsaf = ary[1] == "true";

                if (ary[0] == "reqAAmods") PepRegexText = ary[1] == "ERROR" ? "" : ary[1];

                // iniProbTH can't exceed maxIniProbTH
                if (IniProbTh > MaxIniProbTh)
                {
                    (MaxIniProbTh, IniProbTh) = (IniProbTh, MaxIniProbTh);
                }

                if (FastaFile.Length == 0 && DoNsaf)
                {
                    Console.Error.Write("\nERROR: NSAF output requires a FASTA file.\n\n");
                    Environment.Exit(-1000);
                }

                // determine output type
                if (OutputFormat == -1) // unset
                {
                    if (ary[0] == "output")
                    {
                        switch (ary[1])
                        {
                            case "Custom": OutputFormat = CustomOutput; break;
                            case "GeneQspec": OutputFormat = GeneQspecFormat; break;
                            case "ProtQspec": OutputFormat = ProtQspecFormat; break;
                            case "Default": OutputFormat = DefaultOutput; break;
                            case "Peptide":
                                ByPeptide = true;
                                OutputFormat = PeptideOutput;
                                break;
                            case "Gene":
                                ByGene = true;
                                OutputFormat = GeneOutput;
                                break;
                        }
                    }

                    if ((OutputFormat == GeneQspecFormat || OutputFormat == ProtQspecFormat) && FastaFile.Length == 0)
                    {
                        Console.Error.WriteLine("\nERROR: QSpec output requires a FATA file.\n");
                        Environment.Exit(-1000);
                    }
                }

                if (OutputFormat == CustomOutput)
                {
                    if (ary[0] == "printC") ParseCustomOutputOptions(ary);
                    if (ary[0] == "printE") ParseCustomOutputOptions(ary);
                }

                if (OutputFormat == DefaultOutput) ByGene = false; // ensure protein-centric output

                // maps protids to gene symbols; tab-delimited "gene ID<TAB>protein ID"
                if (ary[0] == "gene2prot") Gene2ProtFile = ary[1];
            }

            if (EpiThreshold == -1) EpiThreshold = 0;

            if (CombinedFilePath != null)
            {
                CombinedFile = Path.GetFileName(CombinedFilePath);
            }

            // gene2prot + GeneQspec implies gene-centric Qspec output
            if (Gene2ProtFile != null && OutputFormat == GeneQspecFormat) ByGene = true;

            if (!string.IsNullOrEmpty(PepRegexText)) FormatPepRegex();
        }
        catch (FileNotFoundException e)
        {
            Console.Error.Write("\n#\n#FileNotFoundException Error at: Globals.ParseParametersFile()\n#\n#\n\n");
            Console.Error.Write(e.ToString());
        }
        catch (IOException e)
        {
            Console.Error.Write("\n#\n#IOException Error at: Globals.ParseParametersFile()\n#\n#\n\n");
            Console.Error.Write(e.ToString());
        }

        // Make up a decoy tag that won't occur in the data if the user didn't provide one.
        // This lets the program run, labeling all proteins as forward sequences.
        if (DecoyTag == null) DecoyTag = Guid.NewGuid().ToString().Replace('-', 'x');
    }

    /// <summary>Prints the parameters that will be used for this run of Abacus.</summary>
    public static string PrintParameters()
    {
        var ret = "\n\nParameters for this execution:\n"
            + $"\tSource directory: '{SrcDir}'\n"
            + $"\tDB name:          '{DbName}'\n"
            + $"\tOutput file:      '{OutputFilePath}'\n"
            + $"\tCombined file P:   {MinCombinedFilePw}\n"
            + $"\tiniProb threshold: {IniProbTh}\n"
            + $"\tmaxIniProb:        {MaxIniProbTh}\n"
            + $"\tKeep DB files:     {KeepDb}\n"
            + $"\tRecalc Pep Wts:    {RecalcPepWts}\n";

        var outputTxt = OutputFormat switch
        {
            1 => "Protein Qspec",
            2 => "Custom",
            3 => "Gene",
            4 => "Gene Qspec",
            5 => "Peptide",
            _ => "Default",
        };

        ret += $"\tOutput format:     {outputTxt}\n";

        if (PepModsPlus is { Length: > 0 })
        {
            ret += "\tAA mods to keep:   " + string.Join(", ", PepModsPlus) + "\n";
        }

        if (PepModsMinus is { Length: > 0 })
        {
            ret += "\tAA mods to avoid:  " + string.Join(", ", PepModsMinus) + "\n";
        }

        ret += "\n";
        return ret;
    }

    /// <summary>Generates the current time as a string (e.g. 2026Aug13_1042).</summary>
    public static string FormatCurrentTime()
    {
        // Original Java indexed monthName[] with Calendar.MONDAY instead of
        // Calendar.MONTH, so it always reported "Mar" regardless of the
        // actual month. Fixed here to use the real current month.
        var now = DateTime.Now;
        var month = now.ToString("MMM", System.Globalization.CultureInfo.InvariantCulture);

        return $"{now.Year}{month}{now.Day:00}_{now.Hour:00}{now.Minute:00}";
    }

    public static void PrintError(ParamError err)
    {
        switch (err)
        {
            case ParamError.NoDbName: Console.Error.WriteLine($"\nError in {ParamFile}: dbName=?\n"); break;
            case ParamError.NoCombinedTag: Console.Error.WriteLine($"\nError in {ParamFile}: combinedTag=?\n"); break;
            case ParamError.NoSrcDir: Console.Error.WriteLine($"\nError in {ParamFile}: srcDir=?\n"); break;
            case ParamError.NoDecoyTag: Console.Error.WriteLine($"\nError in {ParamFile}: decoyTag=?\n"); break;
            case ParamError.NoMaxIniProbTh: Console.Error.WriteLine($"\nError in {ParamFile}: maxIniProbTH=?\n"); break;
            case ParamError.NoIniProbTh: Console.Error.WriteLine($"\nError in {ParamFile}: iniProbTH=?\n"); break;
            case ParamError.NoMinCombinedFilePw: Console.Error.WriteLine($"\nError in {ParamFile}: minCombinedFilePw=?\n"); break;
            case ParamError.NoMinPw: Console.Error.WriteLine($"\nError in {ParamFile}: minPw=?\n"); break;
            case ParamError.DirError: Console.Error.WriteLine($"\nError: srcdir='{SrcDir}' was not found.\n"); break;
            case ParamError.NoFastaFile: Console.Error.WriteLine($"\nError in {ParamFile}: fasta=?\n"); break;
            case ParamError.FastaNotFound: Console.Error.WriteLine($"\nError: fastaFile='{FastaFile}' was not found.\n"); break;
            case ParamError.ParamFileNotFound: Console.Error.WriteLine($"\nError: paramFile='{ParamFile}' was not found.\n"); break;
            case ParamError.MapFileNotFound: Console.Error.WriteLine($"\nError: gene2protFile='{Gene2ProtFile}'. You didn't specify a gene-to-protein ID mapping file\n"); break;
            case ParamError.ParamFileNull: Console.Error.WriteLine("\nError: No parameter file was read in. Did you forget the '-p' option?\n"); break;
            case ParamError.OutputPathNotFound: Console.Error.WriteLine("\nError: The path for the output file does not exist.\n"); break;
            default: Console.Error.WriteLine("Undefined error."); break;
        }

        Environment.Exit(-1000);
    }

    /// <summary>
    /// Parses the 'printC'/'printE' options from the param_file and records
    /// which columns the user wants in custom output.
    /// </summary>
    private static void ParseCustomOutputOptions(string[] ary)
    {
        if (ary[0] == "printC") PrintC = new HashSet<string>();
        if (ary[0] == "printE") PrintE = new HashSet<string>();

        var opts = ary[1].Split(',');
        foreach (var opt in opts)
        {
            if (ary[0] == "printE")
            {
                if (opt == "id") PrintE.Add("_id");
                if (opt == "Pw") PrintE.Add("_Pw");
                if (opt == "numPepsTot") PrintE.Add("_numPepsTot");
                if (opt == "numPepsUniq") PrintE.Add("_numPepsUniq");
                if (opt == "numSpecsTot") PrintE.Add("_numSpecsTot");
                if (opt == "numSpecsUniq") PrintE.Add("_numSpecsUniq");
                if (opt == "numSpecsAdj") PrintE.Add("_numSpecsAdj");

                if (DoNsaf)
                {
                    if (opt == "numSpecsTot") PrintE.Add("_totNSAF");
                    if (opt == "numSpecsUniq") PrintE.Add("_uniqNSAF");
                    if (opt == "numSpecsAdj") PrintE.Add("_adjNSAF");
                }
            }

            if (ary[0] == "printC")
            {
                if (opt == "id") PrintC.Add("ALL_ID");
                if (opt == "allPw") PrintC.Add("ALL_PW");
                if (opt == "localPw") PrintC.Add("ALL_LOCALPW");
                if (opt == "numPepsTot") PrintC.Add("ALL_NUMPEPSTOT");
                if (opt == "numPepsUniq") PrintC.Add("ALL_NUMPEPSUNIQ");
                if (opt == "numSpecsTot") PrintC.Add("ALL_NUMSPECSTOT");
                if (opt == "numSpecsUniq") PrintC.Add("ALL_NUMSPECSUNIQ");

                if (opt == "maxPw") PrintC.Add("MAXPW");
                if (opt == "maxIniProb") PrintC.Add("MAXINIPROB");
                if (opt == "wt_maxIniProb") PrintC.Add("WT_MAXINIPROB");
                if (opt == "maxIniProbUniq") PrintC.Add("MAXINIPROBUNIQ");

                if (opt == "protid") PrintC.Add("PROTID");
                if (opt == "isFwd") PrintC.Add("ISFWD");
                if (opt == "defline") PrintC.Add("DEFLINE");
                if (opt == "numXML") PrintC.Add("NUMXML");
                if (opt == "protLen") PrintC.Add("PROTLEN");

                if (opt == "geneid" && Gene2ProtFile != null) PrintC.Add("GENEID");
            }
        }
    }

    /// <summary>Prints a progress spinner to the terminal.</summary>
    public static void CursorStatus(int i, string msg)
    {
        const string anim = "|/-\\";
        var r = i % anim.Length;
        Console.Error.Write($"\r{msg}  [ {anim[r]} {i} Working... ]");
    }

    /// <summary>Replaces every occurrence of badChar with goodChar in src.</summary>
    public static string ReplaceAll(string src, char badChar, char goodChar)
        => src.Replace(badChar, goodChar);

    /// <summary>
    /// Extracts the pepXML files used to create the given protXML file. Data is
    /// stored in PepTagHash / ProtTagHash. `reader` must be positioned on the
    /// protXML root/header element (mirrors XMLStreamReader's contract in the
    /// Java original). Returns true if an error occurred (fewer distinct pepXML
    /// tags than protXML tags).
    /// </summary>
    public static bool ParseProtXmlHeader(XmlReader xmlReader, string protXmlFile, IConsole? console)
    {
        var status = false;

        // the combined file is never parsed for this since it always
        // contains every pepXML file
        if (!protXmlFile.Contains(CombinedFile ?? string.Empty))
        {
            string tag, origTag;

            var protMatch = Regex.Match(protXmlFile, "interact-(.+).prot.xml");
            if (protMatch.Success)
            {
                origTag = protMatch.Groups[1].Value;

                // HyperSQL can't handle column names with hyphens or that start
                // with a digit; guard against both.
                tag = Regex.IsMatch(origTag, @"^\d.*")
                    ? ReplaceAll("x" + origTag, '-', '_')
                    : ReplaceAll(origTag, '-', '_');
            }
            else
            {
                // doesn't follow the 'interact-<TAG>.prot.xml' naming convention;
                // make up a tag from the file name up to the first dot
                var dotIdx = protXmlFile.IndexOf('.');
                tag = ReplaceAll(protXmlFile.Substring(0, dotIdx), '-', '_');
                origTag = tag;
            }
            ProtTagHash[protXmlFile] = tag;

            // if a pepXML file with the same tag already exists, we don't need
            // to parse this protXML file's header line
            if (!SearchSrcDirForPepXml(origTag, tag))
            {
                var regexPattern = new Regex(@".*[\/](.+." + PepXmlSuffix + ")$");

                for (var i = 0; i < xmlReader.AttributeCount; i++)
                {
                    xmlReader.MoveToAttribute(i);
                    var n = xmlReader.LocalName;
                    var v = xmlReader.Value;

                    if (n == "source_files")
                    {
                        var ary = Regex.Split(v, @"\s+");
                        foreach (var a in ary)
                        {
                            var m = regexPattern.Match(a);
                            if (m.Success)
                            {
                                var pXml = m.Groups[1].Value;
                                PepTagHash[pXml] = tag;
                            }
                        }
                    }
                }
                // .NET's XmlReader repositions its "current node" while walking
                // attributes by index; restore it so the caller can keep reading.
                xmlReader.MoveToElement();
            }

            if (PepTagHash.Count < ProtTagHash.Count) status = true;
        }

        return status; // false means everything is okay
    }

    /// <summary>
    /// Searches SrcDir for the given pepXML file name; if not found, searches
    /// for a pepXML file named after the protXML file instead.
    /// </summary>
    private static bool SearchSrcDirForPepXml(string origProtXmlTag, string protXmlTag)
    {
        var altPepXml = origProtXmlTag == protXmlTag
            ? $"interact-{protXmlTag}.{PepXmlSuffix}"
            // original protXML tag started with a digit and was padded with 'x'
            // to keep HyperSQL happy
            : $"interact-{origProtXmlTag}.{PepXmlSuffix}";

        if (PepXmlFiles.Contains(altPepXml))
        {
            PepTagHash[altPepXml] = protXmlTag;
            return true;
        }

        return false;
    }

    /// <summary>Rounds a double to the desired number of decimal places.</summary>
    public static double RoundDbl(double d, int numDecimalPlaces)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return 0.0;
        return Math.Round(d, numDecimalPlaces, MidpointRounding.ToEven);
    }

    /// <summary>
    /// Returns a substring of the given FASTA defline up to the first
    /// non-alphanumeric character, recognizing common uniprot/refseq/IPI
    /// header formats.
    /// </summary>
    public static string FormatProtId(string line)
    {
        // NOTE: mirrors the Java original, which throws a NullPointerException
        // here if called before DecoyTag is set. In practice DecoyTag is always
        // assigned by ParseParametersFile (falling back to a random value)
        // before FormatProtId is ever called.
        var uniprotMatch = Regex.Match(line, @"^(sp|tr)\|([^\|]+).*");
        if (uniprotMatch.Success)
        {
            var ret = line.StartsWith(DecoyTag!) ? DecoyTag! : "";
            return ret + uniprotMatch.Groups[2].Value;
        }

        var refseqMatch = Regex.Match(line, @"^gi\|\d+\|ref\|([^\|]+).*");
        if (refseqMatch.Success)
        {
            var ret = line.StartsWith(DecoyTag!) ? DecoyTag! : "";
            return ret + refseqMatch.Groups[1].Value;
        }

        var ipiMatch = Regex.Match(line, @"^IPI:([^\|]+).*");
        if (ipiMatch.Success)
        {
            var ret = line.StartsWith(DecoyTag!) ? DecoyTag! : "";
            return ret + ipiMatch.Groups[1].Value;
        }

        // fallback: take everything up to the first space, capped at 100 chars
        var p = 0;
        for (var i = 0; i < line.Length; i++)
        {
            p++;
            if (line[i] == ' ') break;
        }
        if (p >= 100) p = 100;

        return line.Substring(0, p).Trim();
    }

    /// <summary>Formats elapsed time (ms) as HH:MM:SS.</summary>
    public static string FormatTime(long elapsedTimeMs)
    {
        var seconds = Math.Floor(elapsedTimeMs / 1000.0);
        var minutes = Math.Floor(elapsedTimeMs / (60 * 1000.0));
        var hours = Math.Floor(elapsedTimeMs / (60 * 60 * 1000.0));

        if (seconds > 60)
        {
            var x = Math.Floor(seconds / 60);
            minutes += x;
            seconds %= 60;
        }

        int hh = (int)hours, mm = (int)minutes, ss = (int)seconds;

        // Original Java printed `mm` twice (a typo for `ss` on the seconds
        // field); fixed here to actually print seconds.
        var ret = $"{hh}:";
        ret += (mm < 10 ? "0" : "") + mm + ":";
        ret += (ss < 10 ? "0" : "") + ss;
        return ret;
    }

    /// <summary>
    /// Parses PepRegexText and stores the modifications it lists into
    /// PepModsPlus / PepModsMinus.
    /// </summary>
    public static void FormatPepRegex()
    {
        var localRegex = Regex.Replace(PepRegexText ?? "", @"\s", "");
        var x = localRegex.Split(';');

        var nP = CountChar(localRegex, '+');
        var nM = CountChar(localRegex, '-');

        PepModsMinus = new string[nM];
        PepModsPlus = new string[nP];

        int p = 0, m = 0;
        foreach (var raw in x)
        {
            var curTxt = raw.Trim();
            if (curTxt.StartsWith("-"))
            {
                PepModsMinus[m++] = curTxt.Substring(1).ToUpperInvariant();
            }
            else if (curTxt.StartsWith("+"))
            {
                PepModsPlus[p++] = curTxt.Substring(1).ToUpperInvariant();
            }
        }
    }

    /// <summary>
    /// Returns true if modPep should be included, based on a +1/-1 score for
    /// each matched modification from PepModsPlus/PepModsMinus (score > 0
    /// to include).
    /// </summary>
    public static bool CheckModPeptide(string modPep)
    {
        if (PepModsPlus == null && PepModsMinus == null) return true;

        var score = 0;
        if (PepModsPlus is { Length: > 0 })
        {
            foreach (var mod in PepModsPlus)
                if (modPep.Contains(mod)) score++;
        }
        if (PepModsMinus is { Length: > 0 })
        {
            foreach (var mod in PepModsMinus)
                if (modPep.Contains(mod)) score--;
        }

        return score > 0;
    }

    /// <summary>Returns the frequency of `needle` in `haystack`.</summary>
    public static int CountChar(string haystack, char needle)
        => haystack.Count(c => c == needle);

    /// <summary>Records pepXML file names and their tags.</summary>
    public static void RecordPepXmlTags()
    {
        var pat = new Regex(@"(.+)\." + PepXmlSuffix + "$");
        foreach (var curFile in PepXmlFiles)
        {
            var m = pat.Match(curFile);
            if (m.Success)
            {
                var curTag = Regex.Replace(m.Groups[1].Value, "interact-", "");
                PepTagHash[curFile] = curTag;
            }
        }
    }

    /// <summary>Writes a blank input file for the user to fill in.</summary>
    public static void WriteTemplate()
    {
        using var outFile = new StreamWriter("Abacus_template.param");

        outFile.Write(
            "\n#\n# ABACUS parameter file\n" +
            $"# Generated on: {FormatCurrentTime()}\n#\n\n"
        );

        outFile.Write(
            "# Name to give the database\n" +
            "dbName=ABACUSDB\n\n" +

            "# Name of protXML file corresponding to merged/combined results\n" +
            "combinedFile=\n\n" +

            "# The directory that contains the pepXML and protXML files\n" +
            "srcDir=\n\n" +

            "# The name of the file where results will be saved to\n" +
            "outputFile=\n\n" +

            "# The minimum PeptideProphet score the best peptide match of a protein must have\n" +
            "maxIniProbTH=0.99\n\n" +

            "# The minimum PeptideProphet score a peptide must have in order to be even considered by Abacus\n" +
            "iniProbTH=0.5\n\n" +

            "# E.P.I: Experimental Peptide-probability Inclusion threshold\n" +
            "# If a protein does not contain at least one peptide exceeding this PeptideProphet score, none of the\n" +
            "# peptide evidence for this protein will be considered. This is applied on an experiment by experiment case.\n" +
            "epiTH=0\n\n" +

            "# The minimum ProteinProphet score a protein group must have in the COMBINED file\n" +
            "minCombinedFilePw=0.5\n\n" +

            "# The path the the FASTA formatted file used for the original protein search\n" +
            "# Relative paths are allowed\n" +
            "fasta=\n\n" +

            "# If true, Abacus will write ALL protein IDs belonging to a group in the COMBINED file\n" +
            "# Protein IDs starting with ':::' are additional identifiers from the same protein group in\n" +
            "# the COMBINED file. The representative protein for the group does not start with ':::'\n" +
            "verboseResults=false\n\n" +

            "# The keep the HyperSQL database files that are created after the program is done\n" +
            "keepDB=false\n\n" +

            "# Spectral count data will be reported in NSAF format.\n" +
            "# NSAF = _N_ormalized _S_pectral _A_bundance _F_actor\n" +
            "# For a detailed explanation of this method refer to this pubmed link:\n" +
            "# http://www.ncbi.nlm.nih.gov/pubmed/20166708\n" +
            "# Abacus reports NSAF values multiplied by a scaling factor. This is done to\n" +
            "# control for numeric underflow (ie: really small numbers). The scaling factor\n" +
            "# that is used is called the NSAF_FACTOR and is reported during runtime in\n" +
            "# case you would like to rescale your data.\n" +
            "asNSAF=false\n\n" +

            "# If you are using decoy proteins in your searches, specify the first few\n" +
            "# characters of the label indicating decoy proteins here\n" +
            "decoyTag=\n\n" +

            "# Output format that will be produced by this parameter file\n" +
            "output=Default\n\n"
        );

        Console.Error.Write("Template input file create: Abacus_template.param\n\n");
    }
}
