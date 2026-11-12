import AshesCompiler.Semantics.HeapLayoutClassification
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
export (
    value rootViewsAgree,
)

// The full query enters the shared gates without root inputs. Narrow views supply
// facts for their exact resolved root; every field and ordered child must agree.
let rootViewsAgree (semanticType: SemanticType) (environment: TypeEnvironment) =
    (let full = classifyHeapLayout(semanticType)(environment)
    in projectHeapCopyPlanFacts(full) == classifyHeapCopyPlan(semanticType)(environment) && projectHeapDropperFacts(full) == classifyHeapDropper(semanticType)(environment))
