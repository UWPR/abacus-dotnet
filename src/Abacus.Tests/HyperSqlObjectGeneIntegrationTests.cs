using Abacus;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Abacus.Tests;

/// <summary>
/// End-to-end test running a small real fixture through the entire
/// gene-centric pipeline (mirroring Abacus.Run()'s ByGene branch), via a real
/// in-memory SQLite database. Same rationale as HyperSqlObjectIntegrationTests.
/// </summary>
public class HyperSqlObjectGeneIntegrationTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory();

    public HyperSqlObjectGeneIntegrationTests()
    {
        Globals.DecoyTag = "DECOY_";
        Globals.IniProbTh = 0.5;
        Globals.MaxIniProbTh = 0.9;
        Globals.MinCombinedFilePw = 0.5;
        Globals.MinPw = 0.0;
        Globals.EpiThreshold = 0;
        Globals.CombinedFile = "interact-COMBINED.prot.xml";
        Globals.SrcDir = dir.FullName;
        Globals.ByGene = true;
        Globals.ByPeptide = false;
        Globals.MakeVerboseOutput = false;
        Globals.DoNsaf = false;
        Globals.KeepDb = false;
        Globals.FastaFile = "";
        Globals.OutputFormat = Globals.GeneOutput;
        Globals.RecalcPepWts = false;
        Globals.GenesHaveDescriptions = false;
        Globals.PepModsPlus = null;
        Globals.PepModsMinus = null;
        Globals.PepXmlFiles.Clear();
        Globals.ProtXmlFiles.Clear();
        Globals.PepTagHash.Clear();
        Globals.ProtTagHash.Clear();

        Globals.Gene2ProtFile = Path.Combine(dir.FullName, "gene2prot.txt");
        File.WriteAllText(Globals.Gene2ProtFile, "G1\tP1\tTest gene one\n");
    }

    public void Dispose() => dir.Delete(recursive: true);

    private void WriteFixture(string name, string content) => File.WriteAllText(Path.Combine(dir.FullName, name), content);

    [Fact]
    public void FullGeneCentricPipeline_ProducesCorrectOutput()
    {
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

        var engine = new HyperSqlObjectGene();
        engine.Initialize();
        engine.MakeSrcFileTable(conn, null);
        engine.CorrectPepXmlTags(conn);

        Assert.False(engine.MakeGeneTable(conn, null));
        engine.MakeCombinedTable(conn, null);
        engine.MakeProtXmlTable(conn, null);

        engine.MakeGeneCombined(conn, null);
        engine.MakeGeneXml(conn, null);
        engine.AdjustGenePeptideWt(conn, null);

        engine.MakeTempGene2PepTable(conn);

        engine.MakeGeneidSummary(conn, null);
        engine.MakeGeneResults(conn, null);

        engine.MakeGenePepUsageTable(conn, null);
        engine.AppendIndividualExptsGc(conn, null);

        engine.AppendGeneDescriptions(conn);

        engine.DefaultResults(conn, null);

        Assert.True(File.Exists(outputFile));
        var lines = File.ReadAllLines(outputFile);
        Assert.True(lines.Length >= 2, "expected a header line plus at least one data row");

        var header = lines[0].Split('\t');
        var dataRows = lines.Skip(1).Select(l => l.Split('\t')).ToList();

        // only the forward gene should appear
        Assert.Single(dataRows);
        var row = ColumnMap(header, dataRows[0]);

        Assert.Equal("G1", row["geneid"]);
        Assert.Equal("1", row["isFwd"]);
        Assert.Equal("1", row["numProts"]);
        Assert.Equal("Test gene one", row["geneDescription"]);
        Assert.Equal("2", row["ALL_numPepsTot"]);
        Assert.Equal("3", row["ALL_numSpecsTot"]);
        Assert.Equal("2", row["RUN1_numPepsTot"]);
        Assert.Equal("3", row["RUN1_numSpecsTot"]);
        Assert.Equal("3", row["RUN1_numSpecsAdj"]); // no shared peptides, so adjusted == raw
    }

    private static Dictionary<string, string> ColumnMap(string[] header, string[] row)
    {
        var map = new Dictionary<string, string>();
        for (var i = 0; i < header.Length; i++) map[header[i]] = i < row.Length ? row[i] : "";
        return map;
    }
}
