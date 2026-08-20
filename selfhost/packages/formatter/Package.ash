// Public package root for the self-hosted canonical formatter.
//
// Boundary:
// - Formatting depends on frontend syntax only; it does not perform semantic analysis.
// - Importing the package root makes the formatter module available without adding behavior.

import AshesCompiler.Formatter.Formatter
Unit
