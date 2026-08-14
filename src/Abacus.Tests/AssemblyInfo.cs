using Xunit;

// Globals is deliberately global mutable state (matching the Java original's
// static fields), and several ported code paths faithfully call
// Environment.Exit() on error paths (matching Java's System.exit()). Running
// test classes in parallel lets one test's Globals mutation bleed into
// another's, and an error path triggered by that can kill the whole test
// host process. Force fully sequential execution.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
