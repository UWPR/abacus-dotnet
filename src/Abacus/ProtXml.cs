using System.Xml;

namespace Abacus;

/// <summary>
/// A single protein group from a protXML file, ported from abacus/protXML.java.
/// Accumulates protein IDs/deflines and the peptides that support them while
/// streaming through one &lt;protein_group&gt; block, then writes the
/// flattened (protein x peptide) rows to the working database.
/// </summary>
public class ProtXml
{
    private string? srcFile;
    public int GroupId { get; private set; }
    public double Pw { get; private set; }
    public double LocalPw { get; private set; }
    public string? SiblingGroup { get; private set; }
    public int FwdStatus { get; private set; }
    private bool isProphet;

    private Dictionary<string, int> protIdClass = new();  // protId -> isFwd (0/1)
    private Dictionary<string, string> protIds = new();   // protId -> defline
    private Dictionary<string, int> protLen = new();       // protId -> protein length
    private Dictionary<string, PepXml> peptides = new();   // modPep key -> PepXml

    public ProtXml()
    {
    }

    public ProtXml(string srcFile, bool iProphetStatus)
    {
        this.srcFile = srcFile;
        protIds = new Dictionary<string, string>();
        peptides = new Dictionary<string, PepXml>();
        protIdClass = new Dictionary<string, int>();
        protLen = new Dictionary<string, int>();
        FwdStatus = 0;
        isProphet = iProphetStatus; // true means the file is an i-Prophet output file
    }

    /// <summary>Parses a &lt;protein_group&gt; element's attributes.</summary>
    public void ParseProtGroupLine(XmlReader xmlReader)
    {
        for (var i = 0; i < xmlReader.AttributeCount; i++)
        {
            xmlReader.MoveToAttribute(i);
            var attrName = xmlReader.LocalName;
            var attrValue = xmlReader.Value;

            if (attrName == "group_number") GroupId = int.Parse(attrValue);
            if (attrName == "probability") Pw = double.Parse(attrValue);
        }
        xmlReader.MoveToElement();
    }

    /// <summary>Parses a &lt;protein&gt; element's attributes; returns the formatted protein ID.</summary>
    public string? ParseProteinLine(XmlReader xmlReader)
    {
        string? protid = null;

        for (var i = 0; i < xmlReader.AttributeCount; i++)
        {
            xmlReader.MoveToAttribute(i);
            var attrName = xmlReader.LocalName;
            var attrValue = xmlReader.Value;

            if (attrName == "protein_name") protid = Globals.FormatProtId(attrValue);
            if (attrName == "probability") LocalPw = double.Parse(attrValue);
            if (attrName == "group_sibling_id") SiblingGroup = attrValue;
        }
        xmlReader.MoveToElement();

        return protid;
    }

    /// <summary>Records the current protein ID and its description (deflines are capped at 500 chars).</summary>
    public void SetProtId(string defline, string protid)
    {
        defline = defline.Replace('\'', '_');

        if (defline.Length == 0) protIds[protid.Trim()] = "No Description";
        else if (defline.Length > 500) protIds[protid.Trim()] = defline.Substring(0, 500);
        else protIds[protid.Trim()] = defline;
    }

    /// <summary>Records the current peptide into this protein group; returns its composite key.</summary>
    public string ParsePeptideLine(XmlReader xmlReader)
    {
        var curPep = new PepXml();

        for (var i = 0; i < xmlReader.AttributeCount; i++)
        {
            xmlReader.MoveToAttribute(i);
            var attrName = xmlReader.LocalName;
            var attrValue = xmlReader.Value;

            if (attrName == "peptide_sequence") curPep.SetPeptide(attrValue);
            if (attrName == "charge") curPep.SetCharge(attrValue);
            if (attrName == "initial_probability") curPep.SetIniProb(attrValue);
            if (attrName == "nsp_adjusted_probability") curPep.SetNsp(attrValue);
            if (attrName == "weight") curPep.SetWt(attrValue);
            if (attrName == "n_enzymatic_termini") curPep.SetNtt(attrValue);
            if (attrName == "n_instances") curPep.SetNspecs(attrValue);
            if (attrName == "calc_neutral_pep_mass") curPep.SetMass(attrValue);
        }
        xmlReader.MoveToElement();

        if (isProphet) curPep.SetCharge("0"); // special case for i-prophet data

        var pepCtr = peptides.Count; // needed in case the same sequence occurs twice with different charge states

        var k = $"{curPep.Peptide}-{curPep.Mass}-{curPep.Charge}-{curPep.IniProb}-#{pepCtr}";

        peptides[k] = curPep;

        return k;
    }

    /// <summary>Clears out variables in preparation for the next protein group.</summary>
    public void ClearVariables()
    {
        protIds.Clear();
        peptides.Clear();
        protLen.Clear();
        LocalPw = 0.0;
        SiblingGroup = null;
    }

    /// <summary>Records the current peptide's modifications (if any).</summary>
    public void RecordAaModProtXml(XmlReader xmlReader, string k)
    {
        peptides[k].RecordAaMod(xmlReader);
    }

    /// <summary>Annotates the modPeptide string for peptide `k`, if not already done.</summary>
    public void AnnotateModPeptideProtXml(string k)
    {
        var curPep = peptides[k];
        if (curPep.ModPeptide == null)
        {
            curPep.AnnotateModPeptide();
        }
    }

    /// <summary>The protein length is included by default in protXML files now.</summary>
    public void RecordProtLen(string curProtid, string pl)
    {
        protLen[curProtid] = int.Parse(pl);
    }

    /// <summary>
    /// Determines whether the group is Forward or Decoy. If it's a forward
    /// group, all decoy matches in it are removed - this program takes the
    /// optimistic view that a mix of forward and decoy proteins in one group
    /// means the decoys are a fluke and should be dropped.
    /// </summary>
    public void ClassifyGroup()
    {
        var isFwdCtr = 0;

        foreach (var k in protIds.Keys)
        {
            if (k.StartsWith(Globals.DecoyTag!)) protIdClass[k] = 0;
            else
            {
                protIdClass[k] = 1;
                isFwdCtr++;
            }
        }

        if (isFwdCtr > 0) // the group (overall) is a forward group
        {
            FwdStatus = 1;

            var newProtids = new Dictionary<string, string>();
            var newProtidsClass = new Dictionary<string, int>();

            foreach (var k in protIds.Keys) // remove decoys from group
            {
                if (!k.StartsWith(Globals.DecoyTag!))
                {
                    newProtids[k] = protIds[k];
                    newProtidsClass[k] = 1;
                }
            }

            protIdClass = newProtidsClass;
            protIds = newProtids;
        }
    }

    /// <summary>Writes the collected data for the current protein group to the RAWprotXML table.</summary>
    public void WriteToDb(IBatchInsert prep)
    {
        foreach (var k in protIds.Keys) // iterate over protein IDs
        {
            var defline = protIds[k];
            var protClass = protIdClass.TryGetValue(k, out var cls) ? cls : 0;

            // iterate over every peptide for this protein
            foreach (var pepId in peptides.Keys)
            {
                var curPep = peptides[pepId];

                if (!Globals.CheckModPeptide(curPep.ModPeptide!)) continue;
                if (curPep.IniProb < Globals.IniProbTh) continue;

                try
                {
                    prep.SetString(1, srcFile!.ToUpperInvariant());
                    prep.SetInt(2, GroupId);
                    prep.SetString(3, SiblingGroup);
                    prep.SetDouble(4, Pw);
                    prep.SetDouble(5, LocalPw);
                    prep.SetString(6, k);
                    prep.SetInt(7, protClass);

                    // peptide-level data
                    prep.SetString(8, curPep.Peptide);
                    prep.SetString(9, curPep.ModPeptide);
                    prep.SetInt(10, curPep.Charge);
                    prep.SetDouble(11, curPep.IniProb);
                    prep.SetDouble(12, curPep.Wt);

                    prep.SetString(13, defline);
                    prep.AddBatch();
                    Globals.ProceedWithQuery = true; // implies the insertion worked
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    Environment.Exit(-1);
                }
            }
        }
    }
}
