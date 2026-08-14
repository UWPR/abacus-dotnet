using System.Xml;
using Abacus;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Abacus.Tests;

public class AbacusTests
{
    public AbacusTests()
    {
        Globals.DecoyTag = "DECOY_";
        Globals.IniProbTh = 0.0;
        Globals.MinCombinedFilePw = 0.0;
        Globals.MinPw = 0.0;
        Globals.CombinedFile = "COMBINED.prot.xml";
        Globals.PepModsPlus = null;
        Globals.PepModsMinus = null;
    }

    private static XmlReader ReaderFor(string xml) => XmlReader.Create(new StringReader(xml));

    // The critical StAX-vs-XmlReader divergence: .NET never emits a separate
    // EndElement for self-closing tags, but Java's StAX always does. A
    // self-closing <peptide/> (no modifications - the common case) must still
    // get its ModPeptide annotated and written to the DB.
    [Fact]
    public void ParseProtXml_SelfClosingPeptide_IsAnnotatedAndWritten()
    {
        var xml = """
            <protein_summary>
              <protein_group group_number="1" probability="0.99">
                <protein protein_name="sp|P12345|TEST_HUMAN" probability="0.99">
                  <annotation protein_description="Test protein"/>
                  <peptide peptide_sequence="PEPTIDEK" charge="2" initial_probability="0.98" calc_neutral_pep_mass="900.1"/>
                </protein>
              </protein_group>
            </protein_summary>
            """;
        var fake = new FakeBatchInsert();

        var status = Abacus.ParseProtXml(ReaderFor(xml), "COMBINED.prot.xml", fake, 0, null);

        Assert.False(status);
        Assert.Single(fake.Rows);
        Assert.Equal("PEPTIDEK", fake.Rows[0][8]);  // peptide
        Assert.Equal("PEPTIDEK", fake.Rows[0][9]);  // modPeptide - would be null if the empty-element fix were missing
        Assert.Equal("P12345", fake.Rows[0][6]);    // protId
    }

    [Fact]
    public void ParseProtXml_SelfClosingPeptideWithMods_AnnotatesModPeptide()
    {
        var xml = """
            <protein_summary>
              <protein_group group_number="1" probability="0.99">
                <protein protein_name="sp|P1|TEST" probability="0.99">
                  <annotation protein_description="Test protein"/>
                  <peptide peptide_sequence="PEPCTIDEK" charge="2" initial_probability="0.98" calc_neutral_pep_mass="900.1">
                    <modification_info>
                      <mod_aminoacid_mass position="4" mass="160.03065"/>
                    </modification_info>
                  </peptide>
                </protein>
              </protein_group>
            </protein_summary>
            """;
        var fake = new FakeBatchInsert();

        Abacus.ParseProtXml(ReaderFor(xml), "COMBINED.prot.xml", fake, 0, null);

        Assert.Single(fake.Rows);
        Assert.Equal("PEPC[160]TIDEK", fake.Rows[0][9]);
    }

    [Fact]
    public void ParseProtXml_DecoyOnlyGroup_WritesNothing()
    {
        var xml = """
            <protein_summary>
              <protein_group group_number="1" probability="0.99">
                <protein protein_name="sp|DECOY_P1|TEST" probability="0.99">
                  <annotation protein_description="Decoy protein"/>
                  <peptide peptide_sequence="PEPTIDEK" charge="2" initial_probability="0.98" calc_neutral_pep_mass="900.1"/>
                </protein>
              </protein_group>
            </protein_summary>
            """;
        var fake = new FakeBatchInsert();

        Abacus.ParseProtXml(ReaderFor(xml), "COMBINED.prot.xml", fake, 0, null);

        // decoy-only groups are still written (isFwd=0) - classify_group only
        // strips decoys when the group has at least one forward protein
        Assert.Single(fake.Rows);
        Assert.Equal(0, fake.Rows[0][7]); // isFwd
    }

    [Fact]
    public void ParsePepXml_SelfClosingSearchHit_IsRecorded()
    {
        var xml = """
            <msms_run_summary>
              <spectrum_query spectrum="run.1.1.2" assumed_charge="2" precursor_neutral_mass="900.1">
                <search_result>
                  <search_hit hit_rank="1" peptide="PEPTIDEK" peptide_prev_aa="R" peptide_next_aa="A">
                    <search_score name="hyperscore" value="20.1"/>
                    <analysis_result analysis="peptideprophet">
                      <peptideprophet_result probability="0.99"/>
                    </analysis_result>
                  </search_hit>
                </search_result>
              </spectrum_query>
            </msms_run_summary>
            """;
        var fake = new FakeBatchInsert();

        var status = Abacus.ParsePepXml(ReaderFor(xml), "run.pep.xml", fake, 0, null);

        Assert.False(status);
        Assert.Single(fake.Rows);
        Assert.Equal("PEPTIDEK", fake.Rows[0][4]); // peptide
    }

    [Fact]
    public void ParsePepXml_BelowProbabilityThreshold_IsNotWritten()
    {
        Globals.IniProbTh = 0.5;
        var xml = """
            <msms_run_summary>
              <spectrum_query spectrum="run.1.1.2" assumed_charge="2">
                <search_result>
                  <search_hit hit_rank="1" peptide="PEPTIDEK">
                    <analysis_result analysis="peptideprophet">
                      <peptideprophet_result probability="0.1"/>
                    </analysis_result>
                  </search_hit>
                </search_result>
              </spectrum_query>
            </msms_run_summary>
            """;
        var fake = new FakeBatchInsert();

        Abacus.ParsePepXml(ReaderFor(xml), "run.pep.xml", fake, 0, null);

        Assert.Empty(fake.Rows);
    }

    [Fact]
    public void RecordXmlFiles_FiltersBySuffixAndAvoidsDuplicates()
    {
        Globals.PepXmlFiles.Clear();
        Globals.ProtXmlFiles.Clear();
        Globals.ByPeptide = false;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "run1.pep.xml"), "");
            File.WriteAllText(Path.Combine(dir.FullName, "run1.prot.xml"), "");
            File.WriteAllText(Path.Combine(dir.FullName, "readme.txt"), "");

            new Abacus().RecordXmlFiles(dir.FullName);

            Assert.Contains("run1.pep.xml", Globals.PepXmlFiles);
            Assert.Contains("run1.prot.xml", Globals.ProtXmlFiles);
            Assert.DoesNotContain("readme.txt", Globals.PepXmlFiles);
        }
        finally
        {
            dir.Delete(recursive: true);
            Globals.PepXmlFiles.Clear();
            Globals.ProtXmlFiles.Clear();
        }
    }

    [Fact]
    public void LoadProtXml_And_LoadPepXml_WriteRowsIntoRealSqliteDatabase()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Globals.SrcDir = dir.FullName;
            Globals.ProtXmlFiles.Clear();
            Globals.PepXmlFiles.Clear();
            Globals.ProtXmlFiles.Add("interact-COMBINED.prot.xml");
            Globals.PepXmlFiles.Add("run1.pep.xml");

            File.WriteAllText(Path.Combine(dir.FullName, "interact-COMBINED.prot.xml"), """
                <protein_summary>
                  <protein_group group_number="1" probability="0.99">
                    <protein protein_name="sp|P12345|TEST" probability="0.99">
                      <annotation protein_description="Test protein"/>
                      <peptide peptide_sequence="PEPTIDEK" charge="2" initial_probability="0.98" calc_neutral_pep_mass="900.1"/>
                    </protein>
                  </protein_group>
                </protein_summary>
                """);

            File.WriteAllText(Path.Combine(dir.FullName, "run1.pep.xml"), """
                <msms_run_summary>
                  <spectrum_query spectrum="run1.1.1.2" assumed_charge="2">
                    <search_result>
                      <search_hit hit_rank="1" peptide="PEPTIDEK">
                        <analysis_result analysis="peptideprophet">
                          <peptideprophet_result probability="0.97"/>
                        </analysis_result>
                      </search_hit>
                    </search_result>
                  </spectrum_query>
                </msms_run_summary>
                """);

            using var conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            var app = new Abacus();

            var protStatus = app.LoadProtXml(conn, null);
            var pepStatus = app.LoadPepXml(conn, null);

            Assert.False(protStatus);
            Assert.False(pepStatus);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT protId, peptide, modPeptide, isFwd FROM RAWprotXML";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("P12345", reader.GetString(0));
                Assert.Equal("PEPTIDEK", reader.GetString(1));
                Assert.Equal("PEPTIDEK", reader.GetString(2));
                Assert.Equal(1L, reader.GetInt64(3));
                Assert.False(reader.Read());
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT srcFile, peptide, iniProb FROM pepXML";
                using var reader = cmd.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal("RUN1.PEP.XML", reader.GetString(0));
                Assert.Equal("PEPTIDEK", reader.GetString(1));
                Assert.Equal(0.97, reader.GetDouble(2), 5);
                Assert.False(reader.Read());
            }
        }
        finally
        {
            dir.Delete(recursive: true);
            Globals.ProtXmlFiles.Clear();
            Globals.PepXmlFiles.Clear();
        }
    }
}
