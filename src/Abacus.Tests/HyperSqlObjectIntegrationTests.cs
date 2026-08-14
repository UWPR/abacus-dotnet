using Abacus;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Abacus.Tests;

/// <summary>
/// End-to-end tests running small real fixtures through the entire
/// protein-centric pipeline via a real in-memory SQLite database, mirroring
/// the exact call sequence Abacus.Run() uses. This is what actually catches
/// SQL-translation mistakes (wrong JOIN, wrong aggregate, off-by-one
/// threshold) - see CLAUDE.md "Testing approach".
/// </summary>
public class HyperSqlObjectIntegrationTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory();

    public HyperSqlObjectIntegrationTests()
    {
        Globals.DecoyTag = "DECOY_";
        Globals.IniProbTh = 0.5;
        Globals.MaxIniProbTh = 0.9;
        Globals.MinCombinedFilePw = 0.5;
        Globals.MinPw = 0.0;
        Globals.EpiThreshold = 0;
        Globals.CombinedFile = "interact-COMBINED.prot.xml";
        Globals.SrcDir = dir.FullName;
        Globals.ByGene = false;
        Globals.ByPeptide = false;
        Globals.MakeVerboseOutput = false;
        Globals.DoNsaf = false;
        Globals.KeepDb = false;
        Globals.Gene2ProtFile = null;
        Globals.FastaFile = "";
        Globals.OutputFormat = Globals.DefaultOutput;
        Globals.RecalcPepWts = false;
        Globals.PepModsPlus = null;
        Globals.PepModsMinus = null;
        Globals.PepXmlFiles.Clear();
        Globals.ProtXmlFiles.Clear();
        Globals.PepTagHash.Clear();
        Globals.ProtTagHash.Clear();
    }

    public void Dispose() => dir.Delete(recursive: true);

    private void WriteFixture(string name, string content) => File.WriteAllText(Path.Combine(dir.FullName, name), content);

    [Fact]
    public void FullProteinCentricPipeline_ProducesCorrectOutput()
    {
        // one forward protein (P1, 2 peptides, 3 spectra across 1 experiment) + one decoy-only group
        WriteFixture("interact-COMBINED.prot.xml", """
            <protein_summary>
              <protein_summary_header source_files="interact-run1.pep.xml"/>
              <protein_group group_number="1" probability="0.99">
                <protein protein_name="sp|P1|TEST_PROTEIN" probability="0.99" group_sibling_id="a">
                  <annotation protein_description="Test protein one"/>
                  <peptide peptide_sequence="PEPTIDEA" charge="2" initial_probability="0.95" weight="1.0" calc_neutral_pep_mass="900.1"/>
                  <peptide peptide_sequence="PEPTIDEB" charge="2" initial_probability="0.90" weight="1.0" calc_neutral_pep_mass="850.1"/>
                </protein>
              </protein_group>
              <protein_group group_number="2" probability="0.99">
                <protein protein_name="sp|DECOY_P2|DECOY_PROTEIN" probability="0.99" group_sibling_id="a">
                  <annotation protein_description="Decoy protein"/>
                  <peptide peptide_sequence="DECOYPEP" charge="2" initial_probability="0.95" weight="1.0" calc_neutral_pep_mass="700.1"/>
                </protein>
              </protein_group>
            </protein_summary>
            """);

        WriteFixture("interact-run1.prot.xml", """
            <protein_summary>
              <protein_summary_header source_files="interact-run1.pep.xml"/>
              <protein_group group_number="1" probability="0.99">
                <protein protein_name="sp|P1|TEST_PROTEIN" probability="0.99" group_sibling_id="a">
                  <annotation protein_description="Test protein one"/>
                  <peptide peptide_sequence="PEPTIDEA" charge="2" initial_probability="0.95" weight="1.0" calc_neutral_pep_mass="900.1"/>
                  <peptide peptide_sequence="PEPTIDEB" charge="2" initial_probability="0.90" weight="1.0" calc_neutral_pep_mass="850.1"/>
                </protein>
              </protein_group>
            </protein_summary>
            """);

        WriteFixture("interact-run1.pep.xml", """
            <msms_run_summary>
              <spectrum_query spectrum="run1.1.1.2" assumed_charge="2">
                <search_result>
                  <search_hit hit_rank="1" peptide="PEPTIDEA">
                    <analysis_result analysis="peptideprophet"><peptideprophet_result probability="0.95"/></analysis_result>
                  </search_hit>
                </search_result>
              </spectrum_query>
              <spectrum_query spectrum="run1.2.2.2" assumed_charge="2">
                <search_result>
                  <search_hit hit_rank="1" peptide="PEPTIDEA">
                    <analysis_result analysis="peptideprophet"><peptideprophet_result probability="0.93"/></analysis_result>
                  </search_hit>
                </search_result>
              </spectrum_query>
              <spectrum_query spectrum="run1.3.3.2" assumed_charge="2">
                <search_result>
                  <search_hit hit_rank="1" peptide="PEPTIDEB">
                    <analysis_result analysis="peptideprophet"><peptideprophet_result probability="0.90"/></analysis_result>
                  </search_hit>
                </search_result>
              </spectrum_query>
            </msms_run_summary>
            """);

        var outputFile = Path.Combine(dir.FullName, "out.tsv");
        Globals.OutputFilePath = outputFile;

        var app = new Abacus();
        app.RecordXmlFiles(dir.FullName);

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        Assert.False(app.LoadProtXml(conn, null));
        Assert.False(app.LoadPepXml(conn, null));

        var engine = new HyperSqlObject();
        engine.Initialize();
        engine.MakeSrcFileTable(conn, null);
        engine.CorrectPepXmlTags(conn);
        engine.MakeCombinedTable(conn, null);
        engine.MakeProtXmlTable(conn, null);
        engine.MakeTempProt2PepTable(conn, null);
        engine.MakeProtidSummary(conn, null);
        engine.MakeResultsTable(conn, null);
        engine.AddProteinLengths(conn, null, 0);
        engine.MakeWt9XgroupsTable(conn);
        engine.MakePepUsageTable(conn, null);
        engine.AppendIndividualExpts(conn, null);
        engine.MergeIdFields(conn);
        engine.DefaultResults(conn, null);

        Assert.True(File.Exists(outputFile));
        var lines = File.ReadAllLines(outputFile);
        Assert.True(lines.Length >= 2, "expected a header line plus at least one data row");

        var header = lines[0].Split('\t');
        var dataRows = lines.Skip(1).Select(l => l.Split('\t')).ToList();

        // only the forward protein should appear - decoy-only group is dropped
        Assert.Single(dataRows);
        var row = ColumnMap(header, dataRows[0]);

        Assert.Equal("P1", row["protid"]);
        Assert.Equal("1", row["isFwd"]);
        Assert.Equal("Test protein one", row["defline"]);
        Assert.Equal("2", row["ALL_numPepsTot"]);   // PEPTIDEA + PEPTIDEB
        Assert.Equal("2", row["ALL_numPepsUniq"]);  // both peptides are unique (wt=1.0 >= 0.9)
        Assert.Equal("3", row["ALL_numSpecsTot"]);  // 2 spectra for PEPTIDEA + 1 for PEPTIDEB
        Assert.Equal("3", row["ALL_numSpecsUniq"]);

        // per-experiment (single tag "RUN1") columns should mirror the combined totals
        Assert.Equal("2", row["RUN1_numPepsTot"]);
        Assert.Equal("3", row["RUN1_numSpecsTot"]);
        Assert.Equal("3", row["RUN1_numSpecsAdj"]); // no shared peptides, so adjusted == raw

        Assert.False(header.Contains("ALL_groupid"), "mergeIDfields should have collapsed groupid/siblingGroup into ALL_id");
        Assert.Contains("ALL_id", header);
    }

    private static Dictionary<string, string> ColumnMap(string[] header, string[] row)
    {
        var map = new Dictionary<string, string>();
        for (var i = 0; i < header.Length; i++) map[header[i]] = i < row.Length ? row[i] : "";
        return map;
    }
}
