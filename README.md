# abacus-dotnet

A .NET (C#) port of [ABACUS](https://sourceforge.net/projects/abacustpp/files/), a spectral-counting
tool for tandem mass spectrometry proteomics (Fermin & Nesvizhskii). The original is a Java desktop
application backed by an embedded HyperSQL (HSQLDB) database; this port reproduces the same engine
as a cross-platform .NET 8 console application backed by SQLite.

**Status: functionally complete.** Every engine class from the original is ported and tested; the
only piece not ported is the original's Swing GUI (a parameter-form/progress-bar wrapper around the
same engine the CLI already drives fully — see `CLAUDE.md` for the reasoning). Verified against the
real Java `abacus.jar` binary on real production datasets — every output cell matched exactly. See
`CLAUDE.md` for the full verification history, translation notes, and known behavioral quirks
preserved from the original.

## Project layout

- `Abacus.sln` — solution
- `src/Abacus/` — the port (.NET 8 console app)
- `src/Abacus.Tests/` — xUnit tests

## Building and running

```
dotnet build Abacus.sln
dotnet test src/Abacus.Tests/Abacus.Tests.csproj
```

Run against a parameter file (same format as the original Java tool):

```
dotnet src/Abacus/bin/Release/net8.0/abacus.dll -p <param_file.txt>
```

## Documentation

- `CLAUDE.md` — detailed porting notes: translation conventions, HSQLDB→SQLite dialect gaps, bugs
  found in the original Java and fixed here, bugs/quirks investigated and deliberately preserved,
  and the verification history against real Java output.
- `TESTING.md` — remaining testing/verification gaps and suggested next steps.

## Credits

Original ABACUS by Damian Fermin and Alexey Nesvizhskii, Apache License 2.0.
