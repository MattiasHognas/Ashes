// Public package root for self-hosted binding, typing, traits, and project planning.
//
// Boundary:
// - Semantics consumes frontend contracts and does not depend on formatter or backend code.
// - Project planning reads manifests, locks, cache entries, and source files but never solves packages.

import AshesCompiler.Semantics.Symbols
import AshesCompiler.Semantics.Scope
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.Unification
import AshesCompiler.Semantics.TypeSchemes
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeResolution
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.TraitEvidenceAbi
import AshesCompiler.Semantics.TraitEvidenceThreading
import AshesCompiler.Semantics.TraitEvidenceRewriting
import AshesCompiler.Semantics.TraitDictionaryConstruction
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.DerivingExpansion
import AshesCompiler.Semantics.ProjectManifest
import AshesCompiler.Semantics.ProjectLockFile
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectSourceEnumeration
import AshesCompiler.Semantics.ProjectDependencyGraph
import AshesCompiler.Semantics.ProjectCompilationPlanning
Unit
