import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.HeapLayoutClassification
import Ashes.Collection.Map.MapTree
import Ashes.Collection.List.map
type CoreLoweringState =
    | environment: TypeEnvironment
    | facts: MapTree(Str, HeapLayoutFacts)
    | resolvedOverride: Maybe(SemanticType)

// The query boundary is extracted unchanged; the surrounding compiler state is injected.
let resolveType (state: CoreLoweringState) semanticType =
    match state.resolvedOverride with
        | Some(resolved) -> resolved
        | None -> semanticType

let coverageEnvironment (state: CoreLoweringState) = state.environment

let stateHeapLayoutFactsByType (state: CoreLoweringState) = state.facts
