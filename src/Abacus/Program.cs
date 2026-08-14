namespace Abacus;

/// <summary>
/// Entry point, ported from mainFunction.java. Decides CLI vs. GUI; since no
/// GUI has been ported yet (see CLAUDE.md), the "else" branch always takes
/// the path Java took when running headless (no display available) - prints
/// the same recommendation to use `-p`. The original's `-dbgui` option
/// launched HSQLDB's bundled DatabaseManagerSwing tool, which has no SQLite
/// equivalent and is dropped rather than ported.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "-t")
        {
            Globals.WriteTemplate();
            return;
        }

        if (args.Length == 2 && args[0] == "-p")
        {
            var inputFile = args[1];
            if (File.Exists(inputFile))
            {
                new Abacus().Run(args);
            }
            else
            {
                Console.Error.Write($"\nError: paramFile='{inputFile}' was not found.\n");
                Environment.Exit(-1);
            }
            return;
        }

        var header = new Abacus().PrintHeader();
        Console.Error.Write("\n\n" + header + "\n");
        Console.Error.Write(
            "\nERROR!\n" +
            "I was unable to start the GUI. Perhaps you are using a " +
            "remote terminal connection?\n\n" +
            "Recommended command line usage: abacus -p <parameter_file.txt>\n\n"
        );
        Environment.Exit(-1);
    }
}
