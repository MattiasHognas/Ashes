// Public package root for the dependency-free self-hosted frontend.
//
// Boundary:
// - The frontend owns source text, tokens, syntax, parsing, imports, and module planning.
// - It does not depend on semantic analysis, formatting, or native code generation.

import AshesCompiler.Frontend.Token
import AshesCompiler.Frontend.Lexer
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.ImportHeader
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.ModuleInterface
import AshesCompiler.Frontend.ModuleSource
import AshesCompiler.Frontend.ModulePlan
Unit
