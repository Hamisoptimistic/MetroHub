using System.Runtime.CompilerServices;

// The test assembly disables the removable developer log (MarkdownLog) and may assert on
// internal diagnostics; nothing else in this project is surfaced to tests.
[assembly: InternalsVisibleTo("MetroHub.Tests")]
