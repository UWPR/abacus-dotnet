# Testing status and remaining gaps

The port is functionally complete (CLI-only, GUI deliberately not ported — see `CLAUDE.md`). It has
been verified exact-match against real Java `abacus.jar` output on two independent real production
datasets for protein-centric default output (`output=Default`), in both `keepDB=false` (in-memory)
and `keepDB=true` (on-disk) modes. See `CLAUDE.md` for full details of what was tested and the bugs
found/fixed along the way.

Ranked by how much this should worry a reviewer, from most to least:

1. **Gene-centric output end-to-end on real data** — the biggest real gap. All "exact match"
   verification so far is protein-centric (`output=Default`). `HyperSqlObjectGene` received the same
   fixes as the protein-centric path (numeric-precision fix, transaction-batching fix) *by
   inspection/pattern-matching*, not by an actual gene-centric run diffed against real Java gene
   output. Currently untested end-to-end on real data because it requires a `gene2prot` mapping file
   that wasn't available for the datasets used so far — worth prioritizing if one can be obtained,
   since gene-centric shares roughly half its code with protein-centric but isn't identical.

2. **`recalcPepWts=true`, `verboseResults=true`, `ProtQspec` output, peptide-level output,
   `CustomOutput`** — all structurally ported and unit-tested against small fixtures, but never run
   against a real dataset and diffed against real Java output. Both real bugs found so far (a
   NULL-handling crash, and an HSQLDB DECIMAL-truncation quirk) were things small fixtures didn't
   expose, so "unit-tested only" should be trusted less than "diffed on real data" for any of these.

3. **Malformed/edge-case input handling** — empty pepXML, a protein with zero peptides,
   negative/zero `iniProb`, duplicate protein IDs across files. Java's behavior in these cases
   (crash vs. silent 0 vs. skip) hasn't been deliberately compared; the port likely mirrors it by
   construction but this hasn't been checked point-by-point.

4. **Large-scale stress test** — datasets tested so far top out around 2,600 proteins. A much larger
   real run would validate that the transaction-batching fix (see `CLAUDE.md`) scales and wouldn't
   surface a new I/O cliff on a different storage backend.

If picking one next step: **#1 (gene-centric)**. It's the largest block of ported,
tested-in-isolation-only code, and the DECIMAL-truncation bug (see `CLAUDE.md`) already showed that
this codebase's translation risk clusters specifically around real-data edge cases that fixtures
don't reach.
