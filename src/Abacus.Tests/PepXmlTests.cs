using System.Xml;
using Abacus;
using Xunit;

namespace Abacus.Tests;

public class PepXmlTests
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

    [Fact]
    public void ParsePepXmlLine_ReadsSearchHitAttributes()
    {
        var xml = """
            <search_hit hit_rank="1" peptide="PEPTIDEK" peptide_prev_aa="R" peptide_next_aa="A"
                        assumed_charge="2" precursor_neutral_mass="900.45" spectrum="run.1.1.2" />
            """;
        var reader = ReaderPositionedOnFirstElement(xml);
        var pep = new PepXml("interact-run.pep.xml", isProphetData: false);

        pep.ParsePepXmlLine(reader);

        Assert.Equal(1, pep.HitRank);
        Assert.Equal("PEPTIDEK", pep.Peptide);
        Assert.Equal('R', pep.PrevAA);
        Assert.Equal('A', pep.NextAA);
        Assert.Equal(2, pep.Charge);
        Assert.Equal(900.45, pep.Mass, 5);
        Assert.Equal("run.1.1.2", pep.SpecId);

        // reader must still be usable/positioned on the element after attribute walk
        Assert.Equal("search_hit", reader.LocalName);
    }

    [Fact]
    public void ParsePepXmlLine_ProphetData_ForcesChargeToZero()
    {
        var xml = """<search_hit hit_rank="1" peptide="PEPTIDEK" assumed_charge="3" />""";
        var reader = ReaderPositionedOnFirstElement(xml);
        var pep = new PepXml("combined.pep.xml", isProphetData: true);

        pep.ParsePepXmlLine(reader);

        Assert.Equal(0, pep.Charge);
    }

    [Fact]
    public void ParsePepXmlLine_SkipsWhenBestHitAlreadyRecorded()
    {
        var pep = new PepXml("run.pep.xml", isProphetData: false);
        pep.ParsePepXmlLine(ReaderPositionedOnFirstElement(
            """<search_hit hit_rank="1" peptide="BESTPEP" />"""));

        // a second, lower-ranked hit for the same spectrum must be ignored
        pep.ParsePepXmlLine(ReaderPositionedOnFirstElement(
            """<search_hit hit_rank="2" peptide="OTHERPEP" />"""));

        Assert.Equal("BESTPEP", pep.Peptide);
    }

    [Fact]
    public void RecordAaMod_PositionThenMass_RecordsModification()
    {
        var xml = """<mod_aminoacid_mass position="4" mass="160.03065" />""";
        var reader = ReaderPositionedOnFirstElement(xml);
        var pep = new PepXml("run.pep.xml", isProphetData: false);
        pep.SetPeptide("PEPCTIDEK");

        pep.RecordAaMod(reader);
        pep.AnnotateModPeptide();

        // position=4 (1-based) -> zero-based index 3 -> the 'C', mass rounds to 160
        Assert.Equal("PEPC[160]TIDEK", pep.ModPeptide);
    }

    [Fact]
    public void RecordAaMod_NtermMass_PrependsNTermTag()
    {
        var xml = """<mod_aminoacid_mass mod_nterm_mass="43.01" />""";
        var reader = ReaderPositionedOnFirstElement(xml);
        var pep = new PepXml("run.pep.xml", isProphetData: false);
        pep.SetPeptide("PEPTIDEK");

        pep.RecordAaMod(reader);
        pep.AnnotateModPeptide();

        Assert.Equal("n[43]PEPTIDEK", pep.ModPeptide);
    }

    [Fact]
    public void AnnotateModPeptide_NoMods_CopiesPeptideVerbatim()
    {
        var pep = new PepXml("run.pep.xml", isProphetData: false);
        pep.SetPeptide("PEPTIDEK");

        pep.AnnotateModPeptide();

        Assert.Equal("PEPTIDEK", pep.ModPeptide);
    }

    [Fact]
    public void ParseSearchScoreLine_XTandem_ReadsNameValuePairs()
    {
        var pep = new PepXml("run.pep.xml", isProphetData: false);

        foreach (var xml in new[]
                 {
                     """<search_score name="hyperscore" value="18.5" />""",
                     """<search_score name="nextscore" value="12.1" />""",
                     """<search_score name="expect" value="0.002" />""",
                 })
        {
            pep.ParseSearchScoreLine(ReaderPositionedOnFirstElement(xml));
        }

        Assert.Equal(18.5, pep.Hyperscore, 5);
        Assert.Equal(12.1, pep.Nextscore, 5);
        Assert.Equal(0.002, pep.XtandemExpect, 5);
    }

    [Fact]
    public void ParseSearchScoreLine_Mascot_ReadsNameValuePairs()
    {
        var pep = new PepXml("run.pep.xml", isProphetData: false);

        foreach (var xml in new[]
                 {
                     """<search_score name="ionscore" value="45.2" />""",
                     """<search_score name="star" value="1" />""",
                 })
        {
            pep.ParseSearchScoreLine(ReaderPositionedOnFirstElement(xml));
        }

        Assert.Equal(45.2, pep.MascotIonscore, 5);
        Assert.Equal(1, pep.MascotStar);
    }

    [Fact]
    public void RecordIniProb_ReadsProbabilityAttribute()
    {
        var xml = """<peptideprophet_result probability="0.9876" all_ntt_prob="(0.1,0.5,0.98)" />""";
        var reader = ReaderPositionedOnFirstElement(xml);
        var pep = new PepXml("run.pep.xml", isProphetData: false);

        pep.RecordIniProb(reader);

        Assert.Equal(0.9876, pep.IniProb, 5);
    }

    [Fact]
    public void WriteToDb_ValidPeptide_StagesRowAndSetsProceedFlag()
    {
        Globals.ProceedWithQuery = false;
        var pep = new PepXml("interact-run.pep.xml", isProphetData: false);
        pep.SetPeptide("PEPTIDEK");
        pep.SetCharge("2");
        pep.SetIniProb("0.95");
        pep.AnnotateModPeptide();
        var fake = new FakeBatchInsert();

        pep.WriteToDb(fake);

        Assert.Single(fake.Rows);
        Assert.Equal("INTERACT-RUN.PEP.XML", fake.Rows[0][1]);
        Assert.Equal("PEPTIDEK", fake.Rows[0][4]);
        Assert.Equal(0.95, fake.Rows[0][6]);
        Assert.True(Globals.ProceedWithQuery);
    }

    [Fact]
    public void WriteToDb_ZeroProbability_DoesNotStageRow()
    {
        var pep = new PepXml("run.pep.xml", isProphetData: false);
        pep.SetPeptide("PEPTIDEK");
        pep.SetIniProb("0"); // below threshold in write_to_db's own `> 0` gate
        pep.AnnotateModPeptide();
        var fake = new FakeBatchInsert();

        pep.WriteToDb(fake);

        Assert.Empty(fake.Rows);
    }
}
