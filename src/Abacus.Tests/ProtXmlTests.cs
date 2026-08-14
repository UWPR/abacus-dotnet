using System.Xml;
using Abacus;
using Xunit;

namespace Abacus.Tests;

public class ProtXmlTests
{
    private static XmlReader ReaderPositionedOnFirstElement(string xmlFragment)
    {
        var reader = XmlReader.Create(new StringReader(xmlFragment));
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element) return reader;
        }
        throw new InvalidOperationException("no element found");
    }

    public ProtXmlTests()
    {
        Globals.DecoyTag = "DECOY_";
        Globals.IniProbTh = 0.0;
    }

    [Fact]
    public void ParseProtGroupLine_ReadsGroupIdAndProbability()
    {
        var group = new ProtXml("interact-run.prot.xml", iProphetStatus: false);
        group.ParseProtGroupLine(ReaderPositionedOnFirstElement(
            """<protein_group group_number="42" probability="0.99" />"""));

        Assert.Equal(42, group.GroupId);
        Assert.Equal(0.99, group.Pw, 5);
    }

    [Fact]
    public void ParseProteinLine_ReturnsFormattedProteinIdAndRecordsLocalPw()
    {
        var group = new ProtXml("interact-run.prot.xml", iProphetStatus: false);
        var protid = group.ParseProteinLine(ReaderPositionedOnFirstElement(
            """<protein protein_name="sp|P12345|EXAMPLE_HUMAN" probability="0.87" group_sibling_id="a" />"""));

        Assert.Equal("P12345", protid);
        Assert.Equal(0.87, group.LocalPw, 5);
        Assert.Equal("a", group.SiblingGroup);
    }

    [Theory]
    [InlineData("", "No Description")]
    [InlineData("it's a kinase", "it_s a kinase")]
    public void SetProtId_HandlesEmptyAndQuoteReplacement(string defline, string expected)
    {
        var group = new ProtXml("run.prot.xml", iProphetStatus: false);
        group.SetProtId(defline, " P12345 "); // protid is trimmed before use as the map key
        group.ClassifyGroup();

        var fake = StageMinimalPeptideAndWrite(group, 0.99);

        Assert.Single(fake.Rows);
        Assert.Equal(expected, fake.Rows[0][13]);
    }

    [Fact]
    public void SetProtId_TruncatesDeflinesOver500Characters()
    {
        var group = new ProtXml("run.prot.xml", iProphetStatus: false);
        var longDefline = new string('x', 600);
        group.SetProtId(longDefline, "P1");
        group.ClassifyGroup();

        var fake = StageMinimalPeptideAndWrite(group, 0.99);
        Assert.Single(fake.Rows);
        Assert.Equal(500, ((string)fake.Rows[0][13]!).Length);
    }

    [Fact]
    public void ParsePeptideLine_BuildsUniqueKeysAndRespectsIProphetChargeOverride()
    {
        var group = new ProtXml("interact-ipro.prot.xml", iProphetStatus: true);
        var k1 = group.ParsePeptideLine(ReaderPositionedOnFirstElement(
            """<peptide peptide_sequence="PEPTIDEK" charge="2" initial_probability="0.9" calc_neutral_pep_mass="900.1" />"""));
        var k2 = group.ParsePeptideLine(ReaderPositionedOnFirstElement(
            """<peptide peptide_sequence="PEPTIDEK" charge="3" initial_probability="0.9" calc_neutral_pep_mass="900.1" />"""));

        Assert.NotEqual(k1, k2); // pepCtr suffix keeps identical-looking peptides distinct
    }

    [Fact]
    public void ClassifyGroup_MixedForwardAndDecoy_RemovesDecoysFromForwardGroup()
    {
        var group = new ProtXml("run.prot.xml", iProphetStatus: false);
        group.SetProtId("forward protein", "P1");
        group.SetProtId("decoy protein", "DECOY_P2");

        group.ClassifyGroup();

        Assert.Equal(1, group.FwdStatus);
        var fake = StageMinimalPeptideAndWrite(group, 0.99);
        // only the forward protein's row should have been written
        Assert.Single(fake.Rows);
        Assert.Equal("P1", fake.Rows[0][6]);
    }

    [Fact]
    public void ClassifyGroup_AllDecoy_KeepsGroupAsDecoy()
    {
        var group = new ProtXml("run.prot.xml", iProphetStatus: false);
        group.SetProtId("decoy only", "DECOY_P1");

        group.ClassifyGroup();

        Assert.Equal(0, group.FwdStatus);
        var fake = StageMinimalPeptideAndWrite(group, 0.99);
        Assert.Single(fake.Rows);
        Assert.Equal(0, fake.Rows[0][7]); // protClass 0 = decoy
    }

    [Fact]
    public void WriteToDb_FiltersPeptidesBelowIniProbThreshold()
    {
        Globals.IniProbTh = 0.5;
        var group = new ProtXml("run.prot.xml", iProphetStatus: false);
        group.SetProtId("desc", "P1");
        group.ClassifyGroup();

        group.ParsePeptideLine(ReaderPositionedOnFirstElement(
            """<peptide peptide_sequence="LOWPROB" charge="2" initial_probability="0.1" calc_neutral_pep_mass="500.0" />"""));
        group.ParsePeptideLine(ReaderPositionedOnFirstElement(
            """<peptide peptide_sequence="HIGHPROB" charge="2" initial_probability="0.9" calc_neutral_pep_mass="600.0" />"""));

        var fake = new FakeBatchInsert();
        group.WriteToDb(fake);

        Assert.Single(fake.Rows);
        Assert.Equal("HIGHPROB", fake.Rows[0][8]);
    }

    [Fact]
    public void ClearVariables_ResetsPerGroupState()
    {
        var group = new ProtXml("run.prot.xml", iProphetStatus: false);
        group.SetProtId("desc", "P1");
        group.ParsePeptideLine(ReaderPositionedOnFirstElement(
            """<peptide peptide_sequence="PEP" charge="2" initial_probability="0.9" calc_neutral_pep_mass="500.0" />"""));
        group.ParseProteinLine(ReaderPositionedOnFirstElement(
            """<protein protein_name="sp|P1|X" probability="0.5" group_sibling_id="s" />"""));

        group.ClearVariables();

        Assert.Equal(0.0, group.LocalPw);
        Assert.Null(group.SiblingGroup);
        var fake = new FakeBatchInsert();
        group.WriteToDb(fake); // nothing left to write - protIds was cleared
        Assert.Empty(fake.Rows);
    }

    private static FakeBatchInsert StageMinimalPeptideAndWrite(ProtXml group, double iniProb)
    {
        group.ParsePeptideLine(ReaderPositionedOnFirstElement(
            $"""<peptide peptide_sequence="PEP" charge="2" initial_probability="{iniProb}" calc_neutral_pep_mass="500.0" />"""));
        var fake = new FakeBatchInsert();
        group.WriteToDb(fake);
        return fake;
    }
}
