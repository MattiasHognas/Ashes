import LegacyClassification
import Oracle
import Adversarial
import IndexedExpectedTests
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.TypeGraphIndex
import AshesCompiler.Semantics.TypeGraphQueries
import AshesCompiler.Semantics.HeapLayoutClassification
let fresh round old =
    (let a = SemNamed(1)("A")([])
    in
        let b = SemNamed(2)("B")([])
        in
            let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 1, groupTypeName = "A", groupIsGeneric = false, groupFields = [("A", [b, b])], groupGenericFields = []), ConstructorFieldGroup(groupSymbolId = 2, groupTypeName = "B", groupIsGeneric = false, groupFields = [("B", [a, SemVariable(100000 + round)])], groupGenericFields = [])]
            in
                if old
                then
                    let facts = legacy_classifyHeapAdmissibility(a)(environment)
                    in !facts.containsResource && facts.containsUnresolvedType
                else
                    match environment
                    |> emptyTypeGraphIndex
                    |> typeGraphResource(a) with
                        | (resource, next) ->
                            match typeGraphUnresolved(a)(next) with
                                | (unresolved, final) ->
                                    match typeGraphResource(b)(final) with
                                        | (childResource, _) -> !resource && !childResource && unresolved)

let recursive repeat remaining old =
    if remaining == 0
    then Ashes.IO.print("done")
    else
        if fresh(remaining)(old)
        then repeat(remaining - 1)(old)
        else Ashes.IO.panic("fresh index mismatch")

let freshAllViews round =
    (let name = "Fresh" + Ashes.Text.fromInt(round)
    in
        let semanticType = SemNamed(300)(name)([])
        in
            let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 300, groupTypeName = name, groupIsGeneric = false, groupFields = [(name, [SemString, SemList(SemInt), SemVariable(100000 + round)])], groupGenericFields = [])]
            in rootViewsAgree(semanticType)(environment))

let recursive repeatAllViews remaining =
    if remaining == 0
    then Ashes.IO.print("done")
    else
        if freshAllViews(remaining)
        then repeatAllViews(remaining - 1)
        else Ashes.IO.panic("fresh full view mismatch")

match Ashes.IO.args with
    | "full-1000" :: [] -> repeatAllViews(1000)
    | "full-10000" :: [] -> repeatAllViews(10000)
    | "expected" :: [] -> runIndexedExpectedTests(Unit)
    | "check" :: [] ->
        if adversarialCases(Unit)
        then Ashes.IO.print("equivalent")
        else Ashes.IO.panic("graph query mismatch")
    | "old-1000" :: [] -> repeat(1000)(true)
    | "old-10000" :: [] -> repeat(10000)(true)
    | "new-1000" :: [] -> repeat(1000)(false)
    | "new-10000" :: [] -> repeat(10000)(false)
    | _ -> Ashes.IO.panic("mode")
