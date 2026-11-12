import LegacyClassification
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.TypeGraphQueries
import AshesCompiler.Semantics.HeapLayoutClassification
export (
    value rootViewsAgree,
)

let failView (label: Str) (semanticType: SemanticType) = Ashes.IO.panic(label + " mismatch for " + Ashes.Trait.Show.show(semanticType))

let rootViewsAgree (semanticType: SemanticType) (environment: TypeEnvironment) =
    match environment
    |> emptyTypeGraphIndex
    |> classifyHeapLayoutIndexed(semanticType) with
        | (full, a) ->
            if full != legacy_classifyHeapLayout(semanticType)(environment)
            then
                Ashes.IO.panic("full mismatch for " + Ashes.Trait.Show.show(semanticType) + " expected=" + Ashes.Trait.Show.show(legacy_classifyHeapLayout(semanticType)(environment)) + " actual=" + Ashes.Trait.Show.show(full))
            else
                match classifyHeapLayoutIndexed(semanticType)(a) with
                    | (warm, b) ->
                        if full != warm
                        then failView("warm full")(semanticType)
                        else
                            match classifyHeapDropperIndexed(semanticType)(b) with
                                | (dropper, c) ->
                                    if dropper != legacy_classifyHeapDropper(semanticType)(environment)
                                    then failView("dropper")(semanticType)
                                    else
                                        match classifyHeapCopyPlanIndexed(semanticType)(c) with
                                            | (plan, d) ->
                                                if plan != legacy_classifyHeapCopyPlan(semanticType)(environment)
                                                then failView("copy plan")(semanticType)
                                                else
                                                    match classifyHeapAdmissibilityIndexed(semanticType)(d) with
                                                        | (admissible, e) ->
                                                            if admissible != legacy_classifyHeapAdmissibility(semanticType)(environment)
                                                            then failView("admissibility")(semanticType)
                                                            else
                                                                match classifyHeapChildrenIndexed(semanticType)(e) with
                                                                    | (children, f) ->
                                                                        if children != legacy_classifyHeapChildren(semanticType)(environment)
                                                                        then failView("children")(semanticType)
                                                                        else
                                                                            match classifyHeapChildTypesIndexed(semanticType)(f) with
                                                                                | (types, g) ->
                                                                                    if types != legacy_classifyHeapChildTypes(semanticType)(environment)
                                                                                    then failView("child types")(semanticType)
                                                                                    else
                                                                                        match classifyHeapResourceIndexed(semanticType)(g) with
                                                                                            | (resource, h) ->
                                                                                                if resource != legacy_classifyHeapResource(semanticType)(environment)
                                                                                                then failView("resource")(semanticType)
                                                                                                else
                                                                                                    match classifyHeapOwnedChildrenDroppableIndexed(semanticType)(h) with
                                                                                                        | (droppable, i) ->
                                                                                                            if droppable != legacy_classifyHeapOwnedChildrenDroppable(semanticType)(environment)
                                                                                                            then failView("droppable")(semanticType)
                                                                                                            else
                                                                                                                match classifyHeapArenaDeepCopyIndexed(semanticType)(i) with
                                                                                                                    | (arena, j) ->
                                                                                                                        if arena != legacy_classifyHeapArenaDeepCopy(semanticType)(environment)
                                                                                                                        then failView("arena")(semanticType)
                                                                                                                        else
                                                                                                                            match classifyHeapShallowCopyIndexed(semanticType)(j) with
                                                                                                                                | (shallow, k) ->
                                                                                                                                    if shallow != legacy_classifyHeapShallowCopy(semanticType)(environment)
                                                                                                                                    then failView("shallow")(semanticType)
                                                                                                                                    else
                                                                                                                                        match heapRuntimeRecursiveCopyAdtLayoutIndexed(semanticType)(k) with
                                                                                                                                            | (recursiveCopy, l) ->
                                                                                                                                                if recursiveCopy != legacy_heapRuntimeRecursiveCopyAdtLayout(semanticType)(environment)
                                                                                                                                                then failView("recursive copy")(semanticType)
                                                                                                                                                else
                                                                                                                                                    match heapRuntimeOwnedFieldLayoutIndexed(semanticType)([(40, "A")])(l) with
                                                                                                                                                        | (ownedField, m) ->
                                                                                                                                                            if ownedField != heapRuntimeOwnedFieldLayout(semanticType)(environment)([(40, "A")])
                                                                                                                                                            then failView("owned path")(semanticType)
                                                                                                                                                            else
                                                                                                                                                                match heapRuntimeOwnedTupleElementLayoutIndexed(semanticType)([(40, "A")])(m) with
                                                                                                                                                                    | (ownedTuple, _) -> ownedTuple == heapRuntimeOwnedTupleElementLayout(semanticType)(environment)([(40, "A")])
