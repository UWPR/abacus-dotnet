# Abacus C# Port

**Status: functionally complete as a CLI tool.** All engine logic is ported, tested (unit + full-pipeline
integration tests), and verified against the real compiled binary (see "Verification" below). The only
undone piece is the original's Swing GUI, which was deliberately not ported (see Status table) — everything
it did is available via the CLI (`abacus -p <param_file>` / `abacus -t`).

C# translation of [ABACUS](https://sourceforge.net/projects/abacustpp/files/) — a Java spectral-counting tool for
tandem MS proteomics (Fermin & Nesvizhskii, Apache 2.0 licensed). Source pulled from
`abacus_dist_2016Jul20.zip` on SourceForge; original package/class names were lowercase
(`abacus`, `pepXML`, `hyperSQLObject`) — this port uses standard C# PascalCase throughout, so
`globals.iniProbTH` becomes `Globals.IniProbTh`, `pepXML.setCharge()` becomes `PepXml.SetCharge()`, etc.
Mapping is mechanical; when in doubt, the original Java (re-downloadable from the URL above) is the source of truth.

## Project layout

- `Abacus.sln` — solution
- `src/Abacus/` — the port (.NET 8 console app)
- `src/Abacus.Tests/` — xunit tests

## Translation conventions (apply consistently to remaining files)

- **DB engine**: HyperSQL (HSQLDB) → SQLite via `Microsoft.Data.Sqlite`. `Connection`/`Statement`/`PreparedStatement`/`ResultSet` → `SqliteConnection`/`SqliteCommand`/`SqliteDataReader`.
- **JDBC batch inserts**: `PreparedStatement.addBatch()`/`executeBatch()` has no SQLite equivalent, so `IBatchInsert` (narrow interface: `SetString`/`SetInt`/`SetDouble`/`AddBatch`, 1-based indices matching the Java call sites exactly) is implemented by `SqliteBatchInsert`, which truly stages rows and only hits the DB on `ExecuteBatch()` (opens one transaction, replays every row, commits) — matching JDBC's stage-then-flush contract rather than executing immediately.
- **GUI console** (`abacus_textArea`): abstracted as `IConsole` (`Append`, `MonitorBoxInit/Update`, `CloseMonitorBox`). Always passed as `null` in the current CLI-only build — the GUI (`abacusUI.java`/`abacus_textArea.java`) has not been ported (see Remaining work). `console == null` branches (stderr/`Console.Error`) are the ones actually exercised; `console != null` branches are structurally ported but currently dead code.
- **StAX vs `System.Xml.XmlReader`**: Java's StAX always synthesizes an `EndElement` event even for self-closing tags (`<peptide/>`); .NET's `XmlReader` does not — self-closing elements never get a separate `EndElement` node. Every place that has END_ELEMENT-triggered logic must check `xmlReader.IsEmptyElement` on the START and run the "end" logic inline if true. See `Abacus.ParseProtXml`/`ParsePepXml` for the pattern (shared local functions so the real end-tag branch and the empty-element fast path can't drift apart). This was a critical, easy-to-miss bug source — `<peptide>` self-closes constantly in real protXML (any unmodified peptide).
- **HSQLDB → SQLite SQL dialect gaps** (relevant to `hyperSQLObject*.java`):
  - `CREATE CACHED TABLE` / `CREATE MEMORY TABLE` → plain `CREATE TABLE`.
  - `CREATE TABLE x (col1, col2, ...) AS (SELECT ...) WITH DATA` → SQLite's `CREATE TABLE ... AS SELECT` does **not** support a pre-`AS` column list; it takes column names from the SELECT's output expressions. Translate by dropping the column list, dropping `WITH DATA`, and adding explicit `AS colname` aliases in the SELECT so downstream column-name references still resolve.
  - `ALTER TABLE x ADD COLUMN y TYPE BEFORE z` → SQLite has no `BEFORE`; columns always append at the end. This changes the final TSV output's **column order** (not values) versus the original tool in places that relied on `BEFORE` to insert mid-table. Low practical impact since output always has a header row (name-based lookup), but worth knowing if a downstream script assumes fixed column positions.
  - `ALTER TABLE x DROP COLUMN y` → supported natively by modern SQLite (3.35+, bundled by `Microsoft.Data.Sqlite`), no change needed.
  - `CREATE FUNCTION ... BEGIN ATOMIC ... END` (custom scalar SQL function, e.g. `sumNumer`) → SQLite has no such DDL. Replace call sites with an equivalent correlated subquery instead of registering a function.
  - `UPDATE tbl alias SET ...` (JDBC/HSQLDB allows aliasing the update target) → drop the alias, reference the real table name in any correlated subquery instead (safer portability, avoids relying on SQLite UPDATE-alias support).
  - Dynamic column-count `INSERT`/`UPDATE` built via raw string concatenation of values (e.g. `addExtraProteins`) — ported using parameterized `SqliteCommand` with dynamically added `@pN` parameters instead of inlining values into SQL text, even though the Java original inlines them. This isn't a "faithfulness" call — it's closing a real (if low-stakes, locally-sourced-data) SQL-injection-shaped pattern in the original while translating it, per the instruction to not introduce/reproduce insecure code.
  - Java's `ResultSetMetaData.getColumnType(i)` (`Types.INTEGER`/`VARCHAR`/default→double) dynamic dispatch for writing output files → `SqliteDataReader.GetFieldType(i)` (`typeof(long)`/`typeof(string)`/else→double). Note **`peptideLevelResults`'s output writer does not round doubles** (`Double.toString` directly) while `defaultResults`/`formatQspecOutput`/`customOutput` all round via `globals.roundDbl(d, 4)` — this is a genuine inconsistency in the original, preserved as-is rather than homogenized.
  - HSQLDB auto-drops (cascades) any index that references a column removed via `ALTER TABLE ... DROP COLUMN`; SQLite does **not** - it leaves a dangling index and later statements against it fail with `no such column`. Every `DROP COLUMN` must be preceded by explicit `DROP INDEX IF EXISTS` for every index covering that column (found via the integration test: `protXML_idx5` on `srcFile`, `res_gid_idx` on `ALL_groupid`/`ALL_siblingGroup`).
  - SQLite errors with `database table is locked` if a DDL statement (`CREATE`/`ALTER`/`DROP TABLE`, `CREATE`/`DROP INDEX`) runs on a connection while *any other statement* on that same connection hasn't been fully stepped through/closed - even a `SELECT` on a completely unrelated table, and even though HSQLDB tolerates this fine. Concretely: don't call a helper that does DDL (e.g. the original's `retNumPeps`, which built the count via a scratch `CREATE TABLE nptmp_ AS ...`) from inside a `while (reader.Read())` loop that's still iterating. Fix used here: rewrite `RetNumPeps` as a pure read-only `SELECT COUNT(*) FROM (SELECT ... GROUP BY ...)` subquery instead of a scratch table - same result, no DDL, safe to call from anywhere. Plain DML (`INSERT`/`UPDATE`/`DELETE`) executed via a second command while a reader is open does *not* hit this, only DDL does.
  - HSQLDB's `CONCAT(a, b)` function has no SQLite equivalent by that name; use the portable `a || b` operator instead (SQLite auto-coerces non-text operands to text for `||`).
  - HSQLDB's row-value `UPDATE tbl SET (a, b) = (SELECT x, y FROM ...)` multi-column assignment **is** supported by the SQLite version `Microsoft.Data.Sqlite` 10.0.11 bundles (SQLite 3.15+) - confirmed via the gene-centric integration test, no rewrite needed there.
  - A few places in the Java original build a value via a per-row `SELECT` + `UPDATE` loop where a single `UPDATE ... SET x = (correlated subquery)` produces the identical result (e.g. `hyperSQLObject_gene.adjustGenePeptideWT`'s per-tag/per-modPeptide loop, and its own abandoned/commented-out attempt at exactly this). Ported as the single correlated-subquery UPDATE instead of the loop - simpler, faster, and avoids the DDL/open-reader lock risk entirely (see below) since it does one statement instead of thousands.
  - Java's `Math.round(double)` = `floor(x + 0.5)`, **not** the same as C#'s `Math.Round` default (banker's/`ToEven`) or `MidpointRounding.AwayFromZero` for negative values. Use `Math.Floor(x + 0.5)` when porting a literal `Math.round` call.
  - `String.format("%.3g%n", x)` then `Double.parseDouble(...)` (round to 3 significant figures) — ported as a direct numeric "round to N significant figures" helper rather than a locale-sensitive string round-trip (the Java original is actually a latent locale bug: `String.format` without `Locale.US` would emit a comma decimal separator on non-US-locale JVMs and then fail to parse).

## Bugs found in the original Java and fixed in the port

(Not preserved — these are outright defects, not behavioral quirks worth keeping. Each is called out in code comments at the fix site.)

- `globals.formatCurrentTime()` indexed the month name array with `Calendar.MONDAY` instead of `Calendar.MONTH` — always printed "Mar" regardless of actual month.
- `globals.formatTime()` printed the minutes value twice instead of minutes-then-seconds.
- `globals.getOStype()` — dead code (never called anywhere in the source tree), but its if/if/else logic meant Windows always misreported as "nix" anyway. Ported faithfully (fixed) in case the GUI port ever needs it.
- `abacus.main()`'s output-directory check (`new File(outputFilePath).getParent()`) throws `NullPointerException` whenever `outputFilePath` is a bare filename with no directory component — i.e. whenever the user relies on the **documented default** `outputFile=ABACUS_output.tsv`. Fixed by treating an empty parent as "current directory".

## Bugs/quirks investigated and *not* changed (faithfully preserved)

- `protXML`'s end-of-`<protein>` handler unconditionally calls `write_to_db` and clears state *before* the end-of-`<protein_group>` handler's `Pw >= minCombinedFilePw`/`minPw` gate ever runs — meaning that gate is structurally dead in the typical single-protein-per-group case. Very likely intentional/harmless: `RAWprotXML` is a staging table and the real threshold filtering happens via SQL (`WHERE Pw >= ...`) downstream in `hyperSQLObject.makeCombinedTable`/`makeProtXMLTable`. Confirmed this is the case once `hyperSQLObject.java` was read.
- Parameter-file parsing truncates any value containing a literal `=` at the second `=` (Java's `split("=")` + `ary[1]`-only access silently drops the rest). Preserved as-is; low risk since abacus parameter values don't normally contain `=`.
- `epiThreshold`'s "unset" sentinel check compares against `-1`, but the field's actual default is `-100` — so the "reset unset epiThreshold to 0" logic never fires unless the user explicitly writes `epiTH=-1`. Preserved; flagged for awareness in case it turns out to matter once more of the epiThreshold consumer logic is ported.

## Status

| Java file | Lines | C# file | Status |
|---|---|---|---|
| `globals.java` | 1092 | `Globals.cs` | done, tested |
| `pepXML.java` | 407 | `PepXml.cs` | done, tested |
| `protXML.java` | 319 | `ProtXml.cs` | done, tested |
| `abacus.java` | 850 | `Abacus.cs` | done, tested |
| `hyperSQLObject.java` | 3535 | `HyperSqlObject.cs` | done, tested (unit + full-pipeline integration test) |
| `hyperSQLObject_gene.java` | 1371 | `HyperSqlObjectGene.cs` | done, tested (unit + full-pipeline integration test) |
| `abacus_textArea.java` + `abacusUI.java`/`.form` | 218 + 2937 | — | **decided: not porting.** CLI-only by explicit user decision (2026-08-13) - the GUI was only ever a parameter-form/progress-bar convenience wrapper around the same engine the CLI already drives fully. `IConsole` remains the integration seam if this is revisited. |
| `mainFunction.java` | 112 | `Program.cs` | done. `-dbgui` (launched HSQLDB's bundled DatabaseManagerSwing) dropped - no SQLite equivalent, not ported. Verified with a real compiled-binary end-to-end run (not just in-process tests) - see "Verification" below |

`HyperSqlObject.cs`/`HyperSqlObjectGene.cs` currently expose the full method surface `Abacus.cs` calls (all `virtual`, stub bodies `throw new NotImplementedException()`), so `Abacus.Run()` compiles and is structurally complete already — filling in `HyperSqlObject.cs` for real does not require touching `Abacus.cs`'s call sites.

`hyperSQLObject_gene extends hyperSQLObject` in Java → `HyperSqlObjectGene : HyperSqlObject` in C#; several methods are shared/inherited (`initialize`, `makeSrcFileTable`, `correctPepXMLTags`, `makeCombinedTable`, `makeProtXMLTable`, `formatQspecOutput`, `defaultResults`, `cleanUp`, `makeGeneTable`) — check the real Java source for which are overridden vs. inherited-as-is when porting `hyperSQLObject_gene.java`.

## Testing approach

Given the file-size cliff after the first four files (`hyperSQLObject.java` alone is larger than everything ported
before it combined, and is a long, deeply interdependent SQL pipeline), exhaustive per-method unit tests for every
helper aren't the right cost/benefit trade-off. Established pattern:

1. Unit-test genuinely risky translation points in isolation (StAX/XmlReader divergence, significant-figure rounding, the `CREATE FUNCTION`→subquery replacement) with hand-built fixtures.
2. One end-to-end integration test per major pipeline (protein-centric default, gene-centric, peptide-level, QSpec, custom output) that runs a small real fixture (2-3 proteins/peptides across 2 experiment files) through the *entire* chain via a real in-memory SQLite `SqliteConnection`, asserting on the final output file's actual field values. This is what actually catches SQL-translation mistakes (wrong JOIN, wrong aggregate, off-by-one in a threshold) — unit-testing individual SQL-heavy methods in isolation mostly just re-states the SQL.
3. `dotnet build` / `dotnet test` after every file — never leave the tree in a non-building state between files.

**Test parallelization must stay disabled** (`src/Abacus.Tests/AssemblyInfo.cs`, `[assembly: CollectionBehavior(DisableTestParallelization = true)]`).
`Globals` is deliberately global mutable state (matching the Java original's static fields), and several ported
code paths faithfully call `Environment.Exit()` on error (matching Java's `System.exit()`). xunit runs different
test *classes* in parallel by default; without this, one test's `Globals` mutation bleeds into a concurrently-running
test, and an error path triggered by that stale/wrong state can call `Environment.Exit()` and kill the entire test
host process (observed directly while adding the `hyperSQLObject.java` integration test — do not remove this without
re-verifying the whole suite survives parallel execution first).

## Verification

Beyond the automated test suite, the compiled binary itself was run end-to-end against a real fixture directory
(2-experiment protein-centric case) via `dotnet .../abacus.dll -p <param file>`, confirmed to produce byte-identical
numeric output to the equivalent in-process integration test. Worth re-doing this (or something like it) as a final
sanity check after any change that touches `Program.cs`, connection-string/DB-lifecycle code, or file I/O paths,
since the in-process tests don't exercise the real `Main` entry point or a real on-disk SQLite file.

### Verified against a real 6-experiment production run (2026-08-13)

Ran the C# port against the same 6-experiment/1-combined-file protein-centric dataset used to generate
`ABACUS_output.tsv` with the real `abacus.jar` (`Abacus_parameters.txt`), then diffed every cell. Found and fixed
two real bugs this exposed (neither caught by the small fixture-based integration tests):

- **Crash**: `HyperSqlObject.UpdateSpectralCounts`'s `UPDATE results SET {tag}_numSpecsAdj = (SELECT X FROM
  adjSpecs_ WHERE ...)` is unconditional (touches every row, not just matched ones), so any protein with zero
  adjusted-spectra rows for that tag gets set to SQL NULL instead of keeping its `ADD COLUMN ... DEFAULT 0`. Java's
  `rs.getDouble()`/`getInt()` silently return 0.0/0 for a NULL column (JDBC spec), masking this; `SqliteDataReader
  .GetDouble()` throws `InvalidOperationException` instead, which crashed `GetNsafValuesProt` on real data (small
  fixtures never happened to hit a zero-adjSpecs protein). Fixed with the same `UPDATE ... SET x = 0 WHERE x IS
  NULL` cleanup already used for `pepUsage_.adjSpecs` and `genePepUsage_.numer`/`denom` elsewhere in this file -
  this was simply the one place that pattern got missed.
- **Wrong output for real (non-fixture) NULLs**: `FormatCell`'s "" fallback for NULL cells (see its doc comment)
  turned out not to be rare in practice - real data has plenty of proteins absent from a given experiment, so
  their per-tag `_Pw`/`_id` columns are genuinely NULL. Java prints `"0.0"` for a NULL DOUBLE and the literal text
  `"null"` for a NULL VARCHAR (JDBC `getDouble`/direct `(String)null` concatenation); emitting `""` for both
  produced ~700 wrong cells across a 2651-protein run. Fixed by adding `GetColumnTypes` (reads `PRAGMA
  table_info(table)`, which SQLite preserves for the `ALTER TABLE ... ADD COLUMN <type>` columns that make up
  nearly all of `results`/`geneResults`) and threading it through `FormatCell` so a NULL cell's declared type
  picks the right literal. `DefaultResults` and `FormatQspecOutput` now pass this map; `CustomOutput`'s NULL
  handling was already separately verified correct and left alone.

**Root-caused and fixed via a real side-by-side engine trace**: after the two fixes above, row count, protein set,
and 64 of 76 output columns matched exactly, but `_numSpecsAdj` (and the `_adjNSAF` columns computed from it, since
adjNSAF normalizes by the sum of every protein's adjSpecs) still differed for a handful of proteins per experiment
tag (e.g. C# 2 vs Java 1, sometimes off by as much as 3-4x for a given protein - not a rounding wobble). Since the
small bundled fixtures never reproduced it, and reasoning about SQL semantics in the abstract had already produced
one wrong theory, this was tracked down empirically: the real `abacus.jar` bundles its own full Java source
(`unzip -o abacus.jar 'abacus/*.java' hsqldb.jar`), so `hyperSQLObject.java`'s `makePepUsageTable` was patched with
`System.err.println` instrumentation identical in shape to matching temporary C# instrumentation, both recompiled
(`javac -cp hsqldb.jar`) and run against the real production data, and the resulting `pepUsage_` traces for the
same proteins/tag were diffed line-by-line. Every `numer`/`denom`/`nspecs` value matched exactly between engines -
only `alpha` differed, in its last digit (Java `0.166666` vs C# `0.166667` for a 1/6 split).

That pointed at exactly the DECIMAL-arithmetic gap flagged (but initially misdiagnosed) above. Direct testing
against a real HSQLDB instance (`java -cp hsqldb.jar`, same jar) showed the actual mechanism: Java's
`CAST(numer AS DECIMAL(16,6)) / CAST(denom AS DECIMAL(16,6))` does **not** compute the division at extended
precision and then round - it computes the quotient directly at the operand scale (6) and **truncates**, so the
subsequent `ROUND(...,6)` in the SQL is a no-op on an already-6-decimal value (`1/6` -> `0.166666`, not the
correctly-rounded `0.166667`; confirmed this is specifically a division quirk, not a `ROUND()` quirk, since
`CAST(... AS DOUBLE)` division on the same operands rounds correctly). A prior fix attempt used C#'s `decimal`
with proper round-half-up for this step, which is more mathematically correct than Java but for exactly that
reason doesn't reproduce Java's output - `nspecs * alpha` rounded to 0 decimals flips by 1 whenever the
correctly-rounded vs. truncated `alpha` straddles a `.5` boundary (e.g. `nspecs=3` against `alpha=0.166666` gives
`0.499998` -> `0` while `0.166667` gives `0.500001` -> `1`), and this happens often enough across ~190K
`pepUsage_` rows to visibly skew results. Fixed by truncating to 6 decimals in C# (`Math.Truncate(x * 1_000_000m)
/ 1_000_000m`) instead of rounding, in both `HyperSqlObject.MakePepUsageTable` (protein-centric) and
`HyperSqlObjectGene.MakeGenePepUsageTable`'s analogous `genePepUsage_.alpha` (gene-centric, same
`CAST(...AS DECIMAL(16,6))/CAST(...AS DECIMAL(16,6))` shape in the Java source, confirmed by the same HSQLDB test,
though not independently re-verified end-to-end since the gene-centric path isn't exercised by the `output=Default`
protein-centric case this was diffed against). The second `ROUND` (`nspecs*alpha` to 0 decimals) is unaffected -
it's a full-precision product of two already-fixed-scale values, not a division, and was independently confirmed
to round-half-up correctly on the same real HSQLDB instance.

Note this is the opposite instinct from most of this file's translation fixes: HSQLDB's DECIMAL division-truncation
here is not a defect to route around with more-correct arithmetic - it's part of Java's actual, intentional-or-not
output, and matching it means reproducing the truncation, not "fixing" it.

**Verified end-to-end**: re-ran the C# port against the same real 6-experiment production dataset after this fix.
Every one of the 76 x 2651 = 201,476 output cells now matches the real `abacus.jar`'s output exactly (numerically -
a handful of whole-number DOUBLE cells differ only in trailing-zero display, e.g. `"1"` vs `"1.0"`, which is the
pre-existing, intentional `FormatCell`/`RoundDbl` formatting convention, not a data difference).

## Performance: transaction batching (2026-08-13)

In the port's default mode (`keepDB=false`, `:memory:` SQLite), performance is already comparable to Java on the
real 6-experiment dataset (Java ~4:04, C# ~4:13-4:41 across several runs - within normal run-to-run variance).

`keepDB=true` (persists the database to a real file - the tool's own log warns "writing to disk slows things
down") was a different story: it hung for 40+ minutes without finishing a single step (`Curating`/`Recalculating
peptide weights` on the combined table) that completes in seconds in the default mode, confirmed still doing real
(if glacial) disk I/O via `/proc/<pid>/io`, not deadlocked.

**Root cause**: the Java original wraps essentially every "populate/update N rows" loop in `hyperSQLObject.java`
with `conn.setAutoCommit(false); ...; executeBatch(); conn.setAutoCommit(true)` - one batched transaction per
loop. The C# port only reproduced that pattern for one loop (the initial XML-to-raw-table load, via
`SqliteBatchInsert`/`IBatchInsert` - see the Translation conventions section above). The other ~30 loops across
`HyperSqlObject.cs`/`HyperSqlObjectGene.cs` called `ExecuteNonQuery()` per-row with no wrapping transaction, so
SQLite auto-commits (and on a real file, fsyncs) every single statement individually. Cheap on `:memory:`
(explaining why default-mode performance looked fine); catastrophic on disk.

**Fix**: added `HyperSqlObject.BeginTransaction(conn, params SqliteCommand[] cmds)` - a thin wrapper around
`conn.BeginTransaction()` - and wrapped every one of those loops with it, matching Java's per-loop batching
granularity. One critical, easy-to-miss subtlety verified by direct experiment (a standalone Microsoft.Data.Sqlite
test, and later confirmed the hard way when it broke `MakeGeneTable`'s gene2prot loader mid-fix):
**`SqliteCommand.Transaction` is captured at `conn.CreateCommand()` time, not resolved dynamically at execution
time.** A command created *after* `BeginTransaction()` auto-adopts the ambient transaction with no extra code; one
created *before* does not, and executing it while the connection has a pending transaction throws
`InvalidOperationException: ... command to have a transaction ...`. Since this codebase's existing style is
"create the reusable command, then start iterating," most sites had the command created first - so `BeginTransaction`
takes the command(s) that need to be reused across the batched loop and explicitly assigns `cmd.Transaction = tx`
to each, sidestepping the ordering trap entirely. Loops that use the shared `Exec()`/`ExecScalar*`/`ExecuteReader()`
helpers (which call `conn.CreateCommand()` fresh every invocation, always *after* the loop's `BeginTransaction()`
call) don't need this - they auto-adopt correctly regardless of ordering, and are left as plain
`using var tx = conn.BeginTransaction();`.

A second gap was found and fixed the same way after the first fix pass: `CurateOnMaxLocalPw`'s per-group `DELETE`
loop (called for the combined file) used the `Exec()` helper - safe from the ordering trap, but still unwrapped,
still auto-committing per statement. Found empirically (it was the *next* thing a `keepDB=true` run got stuck on
after the first fix pass resolved the original bottleneck), not by static search - a from-scratch static audit for
"loop bodies containing `Exec(conn,` or `.ExecuteNonQuery()` with no enclosing transaction" was attempted but
proved unreliable (false positives/negatives from raw-string SQL interpolation braces confusing brace-depth
matching), so the empirical signal (rerun `keepDB=true`, watch where it stalls next) was trusted over the static
one. If another such gap surfaces, the same approach applies: watch a `keepDB=true` run's progress log and
`/proc/<pid>/io`, and if a step that should be sub-second sits with active-but-slow I/O and a repeating spinner
message, grep that step's console message text back to its C# method and check whether its loop is transaction-wrapped.

**Verified**: after both fixes, `keepDB=true` sailed through every step that previously hung indefinitely
(combined-table curate/recalculate, protXML table population, protXML table indexing, all peptide-to-protein
mapping) in minutes rather than never finishing. Output correctness after the transaction-batching changes was
reconfirmed via a fresh `keepDB=false` run diffed byte-for-byte against `ABACUS_output.tsv` (0 mismatches) -
transactions only change *when* writes commit, never *what* they contain, so this was never expected to affect
results, only speed.

A second static-audit pass (rewritten to be indentation-based - matching each loop's closing `}` at the
same-or-shallower indentation, rather than raw brace-counting, which was corrupted by `$"...{x}..."`
interpolation braces) found several more unwrapped loops that the first pass missed, all fixed the same way:
`HyperSqlObject.GetNsafValuesProt` (both per-protein update loops - this was the actual live bottleneck a
subsequent `keepDB=true` run stalled on next), `MakeWt9XgroupsTable`, `CleanUp`, `MergeIdFields`,
`ReformatResults`, and (mirroring the same shapes in the gene-centric file) `HyperSqlObjectGene
.AdjustGenePeptideWt`, `MakeGeneidSummary`, `AppendIndividualExptsGc`, `GetNsafValuesGene`. This pass reported
zero remaining gaps.

**Verified end-to-end (2026-08-14)**: re-ran `keepDB=true` against the real 6-experiment production dataset to
full completion (not aborted early this time) - total runtime 0:20:07 (vs. `keepDB=false`'s ~4:04-4:41; the
tool's own "writing to disk slows things down" warning is accurate, but the run now actually finishes instead of
hanging indefinitely). Diffed the resulting output against `ABACUS_output.tsv` by column name (case/order
differ cosmetically - HSQLDB uppercases unquoted identifiers and Java's `ALTER TABLE ... ADD COLUMN ... BEFORE`
column-ordering has no SQLite equivalent, both already noted above) and by `protid` rather than raw row
position: all 76 x 2651 = 201,476 cells matched exactly. No Java `keepDB=true` timing exists to compare against
(only Java's default-mode runtime was benchmarked) - not expected to matter, since `keepDB` only changes where
SQLite writes, not query logic, in either language.

### Algorithmic and I/O-overhead fixes (2026-08-14)

With transaction batching in place, `keepDB=true` completed rather than hanging, but was still ~5x slower than
`keepDB=false` (~20 min vs. ~2 min) on the production dataset. A follow-up code review specifically looking for
further speed opportunities (not correctness issues - the port was already exact-match verified) found three
tiers of fixes, in descending order of how much each mattered in practice:

- **Algorithmic (N+1 query pattern)**: `MakeProtidSummary`'s "Building Heuristics (2/2)" loop called
  `RetNumPeps`/`RetNumSpectra`/`RetMaxIniProb`/`RetWtMaxIniProb` - 7 separate SQL round trips - once per protein
  (~18K query executions for ~2.6K proteins). Rewritten as a single `GROUP BY protid` aggregate query (a CTE plus
  one correlated subquery for `wtMaxIniProb`, which depends on the per-protid `maxIniProb` computed in the same
  pass) executed once, cached in a `Dictionary<protid, stats>`, looked up per row instead of recomputed. Faithfully
  reproduces each original helper's exact filter semantics, including `RetNumSpectra`'s `iniProb` parameter going
  unused (preserved as-is, not "fixed") and the 0/0.0-if-no-matching-rows behavior `ExecScalarInt`/`ExecScalarDouble`
  give a NULL scalar result. **Honest result**: measured impact on `keepDB=false` was within noise (~114.5s ->
  ~112.7s CPU time, ~1.4%) - SQLite serves repeated small queries against an in-memory B-tree cheaply regardless of
  round-trip count, so eliminating the round trips mostly saves ADO.NET/SQL-parse overhead, not I/O wait. Kept
  anyway since it's strictly better and cost nothing in complexity.
- **Parameterized-command reuse**: ~10 more loop sites across `HyperSqlObject.cs`/`HyperSqlObjectGene.cs`
  (`GetNsafValuesProt`/`GetNsafValuesGene`'s per-protein/gene update loops, `MakeProtidSummary`'s `protidSummary`
  insert loop, `MakeGeneidSummary`, `AppendIndividualExptsGc`'s two loops, and a per-`(tag,geneid)` `DELETE` loop
  in the gene-exclusion-table builder) converted from `Exec(conn, $"...")` - a brand-new SQL string with inlined
  literal values plus a brand-new `SqliteCommand` every row, forcing SQLite to reparse/replan on every single
  execution - to a single prepared `SqliteCommand` created once with bound parameters rebound per row, matching
  the pattern `MakeProtXmlTable`'s ~2.9M-row population loop and `MakePepUsageTable` already used correctly.
  Several methods initially suspected of the same problem (`ReformatResults`, `MakeWt9XgroupsTable`, `CleanUp`,
  `MergeIdFields`) turned out on inspection to already be efficient per-*tag* (a handful of iterations, not
  per-row) loops of already-set-based statements - false positives from a `grep`-only first pass, correctly ruled
  out before touching any code. One genuine bonus find along the way: `MakeProtidSummary`'s `protidSummary` INSERT
  loop had no transaction wrap at all, missed by both earlier transaction-batching audit passes.
- **SQLite PRAGMA tuning**: added `PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;` immediately after
  `conn.Open()` in `Abacus.cs`. A no-op for the default `:memory:` database (SQLite always uses its "memory"
  journal mode there regardless of this pragma), but for `keepDB=true` this was **the dominant win** of the three:
  it avoids a full fsync-safe rollback-journal write on every one of the pipeline's many transaction commits, in
  favor of a WAL append plus a periodic checkpoint. WAL+NORMAL is the standard safe-but-fast combo (crash-safe,
  just not durable against OS-level power loss mid-write, which doesn't matter for a scratch analysis database).

**Verified end-to-end (2026-08-14)**: re-ran both modes against the same real 6-experiment production dataset
after all three fixes, each diffed against `ABACUS_output.tsv` the same way as before - exact match, 0
mismatches across all 201,476 cells, in both modes. 30/30 unit tests passing throughout.

| | `keepDB=false` | `keepDB=true` |
|---|---|---|
| Before this pass | 114.5s CPU, 3.6 GB peak RSS, 2:11 wall | ~20:07 total runtime |
| After this pass | 111.8s CPU, 2.98 GB peak RSS, 2:07.50 wall | 3:57.85 wall (**~5x faster**), 1.15 GB peak RSS |

`keepDB=true` remains slower than `keepDB=false` (disk writes are still disk writes), but the gap shrank from
~10x to under 2x, making disk mode - previously painful enough to avoid unless necessary - now practical for
routine use whenever inspecting intermediate tables after a run is useful (see the README/user-facing docs for
when that's worth doing).

### `keepDB=false` speed investigation (2026-08-14)

The fixes above barely moved `keepDB=false`'s runtime, which made sense once measured directly: a live run
piped through per-line timestamps (`ts`) to find real stage boundaries showed the code touched by the fixes
above was only ~5% of `keepDB=false`'s total time. The actual breakdown on the production dataset (~126s
total): protXML parsing 31.0%, building the protXML table (2.9M rows) 24.9%, mapping peptides to proteins
10.7%, pepXML parsing 10.1%, indexing the protXML table 9.6%, building the combined table 6.0% - XML parsing
(41.1%) and protXML table build+index (34.5%) dominate, not the smaller per-protein loops fixed earlier.

Two fixes came out of chasing those two categories:

- **`PRAGMA temp_store=MEMORY`** (added to the same pragma block as the `keepDB=true` fixes above): SQLite's
  default `temp_store` (`0`, "compile-time default") resolves to file-based temp storage for the scratch
  B-trees `ORDER BY`/`GROUP BY`/`CREATE INDEX`/`DISTINCT` spill to - meaning the *default* `:memory:` run was
  still doing real disk I/O for its many sorts and index builds. Measured impact was modest (system/kernel time
  dropped ~28%, from 14.18s to 10.23s, confirming real I/O was eliminated, but total CPU barely moved) - kept
  anyway since it's a free, zero-downside win.
- **`SqliteBatchInsert` rewrite**: this class is what every parsed protXML/pepXML row funnels through (the
  `IBatchInsert prep` used by `ProtXml.WriteToDb` and the equivalent pepXML path), so it's on the hottest path
  in the whole port - called millions of times across a real run. It had two real inefficiencies: `AddBatch()`
  cloned a full `Dictionary<int, object?>` per row (hash + bucket allocation every single call), and
  `ExecuteBatch()` looked up each `SqliteParameter` by name (`cmd.Parameters[$"@p{i}"]`, allocating a fresh
  string) inside its per-row replay loop instead of once. Rewritten to array-backed storage (1-based, matching
  the existing `Set*(index, ...)` call convention exactly) and pre-captured parameter references, with
  `current` explicitly cleared after each `AddBatch()` snapshot so an unset index still falls through to
  `DBNull` (matching the original dictionary's "key not present" behavior) rather than silently reusing a
  stale value from the previous row. **Not exercised by the unit test suite** - it uses `FakeBatchInsert`
  instead - so this was only validated by a real end-to-end production run, the first time this class's new
  code path actually ran.

**Verified (2026-08-14)**: both changes together, re-run against the same real 6-experiment production
dataset, diffed against `ABACUS_output.tsv` the same way as every prior pass - exact match, 0 mismatches
across all 201,476 cells. 30/30 unit tests passing (though, per the note above, that doesn't cover
`SqliteBatchInsert` itself - only the real-data diff does).

| | CPU | Wall clock | Peak RSS |
|---|---|---|---|
| Before | 111.8s | 2:07.50 | 2.98 GB |
| After (temp_store + SqliteBatchInsert rewrite) | 104.95s | 2:02.09 | 3.00 GB |

About 6% faster - real, but modest compared to the `keepDB=true` win above. Most of the 41%-of-runtime XML
parsing cost looks inherent to streaming XML traversal and per-field string allocation, not further-fixable
overhead; the protXML-vs-pepXML per-byte parsing speed gap (protXML: ~50 MB/s vs pepXML: ~100 MB/s) most
likely reflects protXML's more deeply nested/verbose element structure rather than a port inefficiency, but
this wasn't dug into further. `SqliteBatchInsert` is used identically regardless of `keepDB`, so this fix
should help `keepDB=true` too, though that wasn't separately re-measured.

## Commands

```
dotnet build Abacus.sln
dotnet test src/Abacus.Tests/Abacus.Tests.csproj
```
