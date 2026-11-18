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

// The heap query context of core lowering, modelled as an empty cache: every query falls through to
// the classifier over the injected environment and leaves the state unchanged.
type CoreHeapQueryFacts =
    | shallowCopy: Maybe(Bool)
    | children: Maybe(List(HeapLayoutChild))
    | childTypes: Maybe(List(SemanticType))

let coreHeapQueryFacts (_semanticType: SemanticType) (_state: CoreLoweringState) = CoreHeapQueryFacts(shallowCopy = None, children = None, childTypes = None)

let stateCoverageTypes (state: CoreLoweringState) = state.environment

let queryCoreShallowCopy (semanticType: SemanticType) (state: CoreLoweringState) = (classifyHeapShallowCopy(semanticType)(state.environment), state)

let queryCoreChildTypes (semanticType: SemanticType) (state: CoreLoweringState) = (classifyHeapChildTypes(semanticType)(state.environment), state)
