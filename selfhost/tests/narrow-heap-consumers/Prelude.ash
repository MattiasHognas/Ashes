import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.HeapLayoutClassification
import ReferenceHeapLayoutClassification
import Ashes.Collection.Map.MapTree
type CoreLoweringState =
    | environment: TypeEnvironment
    | facts: MapTree(Str, HeapLayoutFacts)
    | resolvedOverride: Maybe(SemanticType)
    | resourceGuard: Bool

let resolveType (state: CoreLoweringState) (semanticType: SemanticType) =
    match state.resolvedOverride with
        | Some(resolved) -> resolved
        | None -> semanticType

let coverageEnvironment (state: CoreLoweringState) = state.environment

let stateHeapLayoutFactsByType (state: CoreLoweringState) = state.facts

// These unchanged surrounding services are injected. Named-query tests below do not
// use resultSurvivesReset; the explicit guard lets us prove cached true cannot bypass it.
let isResourceHandle (semanticType: SemanticType) (state: CoreLoweringState) = state.resourceGuard

let resultSurvivesReset (semanticType: SemanticType) (state: CoreLoweringState) = canArenaResetLayout(semanticType)
