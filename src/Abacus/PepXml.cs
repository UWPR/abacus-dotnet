using System.Xml;

namespace Abacus;

/// <summary>
/// A single peptide-spectrum match, ported from abacus/pepXML.java.
/// Instances are built up incrementally while streaming through a pepXML
/// (or the pep-level records embedded in a protXML) file, then written to
/// the working database.
/// </summary>
public class PepXml
{
    private string? srcFile;
    public string? SpecId { get; private set; }
    public int HitRank { get; private set; }
    public double Mass { get; private set; }
    public int Charge { get; private set; }
    public string? Peptide { get; private set; }
    public char PrevAA { get; private set; }
    public char NextAA { get; private set; }
    public string? ModPeptide { get; private set; }
    public double IniProb { get; private set; }
    private bool isProphetData;
    public double Wt { get; private set; }   // used in protXML files
    public double Nsp { get; private set; }  // used in protXML files
    public int Ntt { get; private set; }     // used in protXML files
    public int Nspecs { get; private set; }  // used in protXML files

    private Dictionary<int, int>? aaMods; // AA modification positions -> mass

    // X!Tandem search scores
    public double Hyperscore { get; private set; }
    public double Nextscore { get; private set; }
    public double XtandemExpect { get; private set; }

    // Mascot search scores
    public double MascotIonscore { get; private set; }
    public double MascotIdentityscore { get; private set; }
    public int MascotStar { get; private set; }
    public double MascotHomologyscore { get; private set; }
    public double MascotExpect { get; private set; }

    // Sequest search scores
    public double SequestXcorr { get; private set; }
    public double SequestDeltacn { get; private set; }
    public double SequestDeltacnstar { get; private set; }
    public double SequestSpscore { get; private set; }
    public double SequestSprank { get; private set; }

    public PepXml()
    {
    }

    public PepXml(string srcFile, bool isProphetData)
    {
        this.srcFile = srcFile;
        this.isProphetData = isProphetData;
        HitRank = -1; // set to 1 once the best hit has been read
    }

    // public SET functions (needed for parsing protXML files)
    public void SetPeptide(string txt) => Peptide = txt;

    public void SetCharge(string txt) => Charge = int.Parse(txt);

    public void SetIniProb(string txt) => IniProb = double.Parse(txt);

    public void SetNsp(string txt) => Nsp = double.Parse(txt);

    public void SetWt(string txt) => Wt = double.Parse(txt);

    public void SetMass(string txt) => Mass = double.Parse(txt);

    public void SetNtt(string txt) => Ntt = int.Parse(txt);

    public void SetNspecs(string txt) => Nspecs = int.Parse(txt);

    /// <summary>
    /// Parses the current pepXML <c>search_hit</c> element's attributes.
    /// `xmlReader` must be positioned on that start element.
    /// </summary>
    public void ParsePepXmlLine(XmlReader xmlReader)
    {
        if (HitRank == 1) return; // best hit for this PSM already recorded

        for (var i = 0; i < xmlReader.AttributeCount; i++)
        {
            xmlReader.MoveToAttribute(i);
            var attrName = xmlReader.LocalName;
            var attrValue = xmlReader.Value;

            if (attrName == "hit_rank") HitRank = int.Parse(attrValue);

            if (attrName == "spectrum") SpecId = attrValue;
            if (attrName == "assumed_charge") Charge = int.Parse(attrValue);
            if (attrName == "precursor_neutral_mass") Mass = double.Parse(attrValue);

            if (attrName == "peptide") Peptide = attrValue;
            if (attrName == "peptide_prev_aa") PrevAA = attrValue[0];
            if (attrName == "peptide_next_aa") NextAA = attrValue[0];
        }
        xmlReader.MoveToElement();

        if (isProphetData) Charge = 0;
    }

    /// <summary>Parses a <c>mod_aminoacid_mass</c> (or N-term mod) element into aaMods.</summary>
    public void RecordAaMod(XmlReader xmlReader)
    {
        int k = -1;
        int v = 0;

        aaMods ??= new Dictionary<int, int>();

        for (var i = 0; i < xmlReader.AttributeCount; i++)
        {
            xmlReader.MoveToAttribute(i);
            var attrName = xmlReader.LocalName;
            var attrValue = xmlReader.Value;

            if (attrName == "mod_nterm_mass") // N-terminal modification
            {
                k = -100;
                v = 43;
                aaMods[k] = v;
            }
            else // not an N-terminal modification
            {
                if (attrName == "position") k = int.Parse(attrValue) - 1;
                if (attrName == "mass")
                {
                    // Matches Java's Math.round(double): floor(x + 0.5), not
                    // .NET's banker's-rounding default or AwayFromZero (they
                    // only coincide for positive values, which is all this
                    // ever sees in practice - AA mod masses are positive).
                    v = (int)Math.Floor(double.Parse(attrValue) + 0.5);

                    if (k > -1 && v > 0)
                    {
                        aaMods[k] = v;
                    }
                    else
                    {
                        Console.Error.Write("\nERROR: mod_aminoacid_mass line pepXML::record_AA_mod()\n");
                        Console.Error.WriteLine(SpecId + "\n");
                        Console.Error.WriteLine(xmlReader.ToString());
                        Environment.Exit(-1);
                    }
                }
            }
        }
        xmlReader.MoveToElement();
    }

    /// <summary>
    /// Parses a <c>search_score name="..." value="..."</c> element. Reads
    /// attribute values purely by index (not name) - index 0 holds the score
    /// name, index 1 holds its numeric value - matching how the original
    /// walks getAttributeValue(i)/(i+1) pairs without ever looking at
    /// attribute local names.
    /// </summary>
    public void ParseSearchScoreLine(XmlReader xmlReader)
    {
        for (int i = 0, j = 1; i < xmlReader.AttributeCount; i++, j++)
        {
            var attrValue = xmlReader.GetAttribute(i);

            // X!Tandem search scores
            if (attrValue == "hyperscore") Hyperscore = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "nextscore") Nextscore = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "expect") XtandemExpect = double.Parse(xmlReader.GetAttribute(j)!);

            // Mascot search scores
            if (attrValue == "ionscore") MascotIonscore = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "identityscore") MascotIdentityscore = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "star") MascotStar = int.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "homologyscore") MascotHomologyscore = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "expect") MascotExpect = double.Parse(xmlReader.GetAttribute(j)!);

            // Sequest search scores
            if (attrValue == "xcorr") SequestXcorr = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "deltacn") SequestDeltacn = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "deltacnstar") SequestDeltacnstar = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "spscore") SequestSpscore = double.Parse(xmlReader.GetAttribute(j)!);
            if (attrValue == "sprank") SequestSprank = double.Parse(xmlReader.GetAttribute(j)!);
        }
    }

    /// <summary>Parses out the PeptideProphet/iProphet probability.</summary>
    public void RecordIniProb(XmlReader xmlReader)
    {
        for (var i = 0; i < xmlReader.AttributeCount; i++)
        {
            xmlReader.MoveToAttribute(i);
            if (xmlReader.LocalName == "probability") IniProb = double.Parse(xmlReader.Value);
        }
        xmlReader.MoveToElement();
    }

    /// <summary>Builds ModPeptide (e.g. "n[43]PEC[160]TIDE") from Peptide + aaMods.</summary>
    public void AnnotateModPeptide()
    {
        if (aaMods == null)
        {
            ModPeptide = Peptide; // no modifications
        }
        else
        {
            var sb = new System.Text.StringBuilder();

            if (aaMods.TryGetValue(-100, out var ntermMass))
            {
                sb.Append("n[").Append(ntermMass).Append(']');
                aaMods.Remove(-100);
            }

            for (var i = 0; i < Peptide!.Length; i++)
            {
                sb.Append(Peptide[i]);
                if (aaMods.TryGetValue(i, out var mass)) sb.Append('[').Append(mass).Append(']');
            }

            ModPeptide = sb.ToString();
        }
    }

    public void WriteToDb(IBatchInsert prep)
    {
        if (!Globals.CheckModPeptide(ModPeptide!))
        {
            // don't add it to the database
        }
        else if (IniProb > 0)
        {
            try
            {
                prep.SetString(1, srcFile!.ToUpperInvariant());
                prep.SetString(2, SpecId);
                prep.SetInt(3, Charge);
                prep.SetString(4, Peptide);
                prep.SetString(5, ModPeptide);
                prep.SetDouble(6, IniProb);
                prep.AddBatch();
                Globals.ProceedWithQuery = true; // true implies the insertion worked
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Environment.Exit(-1);
            }
        }
    }
}
