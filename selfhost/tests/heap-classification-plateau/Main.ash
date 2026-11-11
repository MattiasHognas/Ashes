import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.HeapLayoutClassification
let node depth = SemNamed(depth)("Node" + Ashes.Text.fromInt(depth))([])

let childProjection (child: HeapLayoutChild) = (child.childType, child.dropKind)

let viewsAgree (full: HeapLayoutFacts) (minimal: HeapAdmissibilityFacts) = full.containsResource == minimal.containsResource && full.containsUnresolvedType == minimal.containsUnresolvedType && Ashes.Collection.List.map(childProjection)(full.children) == minimal.children

let shallowCopy (full: HeapLayoutFacts) =
    match full with
        | HeapLayoutFacts { structuralCopy = ShallowCopy } -> true
        | _ -> false

let recursive groups depth leaf =
    (let fields =
        if depth == 0
        then [leaf]
        else [node(depth - 1), node(depth - 1)]
    in
        ConstructorFieldGroup(groupSymbolId = depth, groupTypeName = "Node" + Ashes.Text.fromInt(depth), groupIsGeneric = false, groupFields = [("Build", fields)], groupGenericFields = []) :: (if depth == 0
        then []
        else groups(depth - 1)(leaf)))

let recursive run remaining =
    if remaining == 0
    then Ashes.IO.print("done")
    else
        let resource = remaining % 3 == 0
        in
            let unresolved = remaining % 3 == 1
            in
                let leaf =
                    if resource
                    then SemNamed(99)("FileHandle")([])
                    else
                        if unresolved
                        then SemVariable(77)
                        else SemInt
                in
                    let depth = remaining % 3 + 1
                    in
                        let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = groups(depth)(leaf)
                        in
                            match classifyHeapLayout(node(depth))(environment) with
                                | HeapLayoutFacts { containsResource = actualResource, containsUnresolvedType = actualUnresolved } as full ->
                                    if classifyHeapArenaDeepCopy(node(depth))(environment) == full.arenaDeepCopySupported && classifyHeapResource(node(depth))(environment) == actualResource && actualResource == resource && actualUnresolved == unresolved && viewsAgree(full)(classifyHeapAdmissibility(node(depth))(environment)) && classifyHeapShallowCopy(node(depth))(environment) == shallowCopy(full)
                                    then run(remaining - 1)
                                    else Ashes.IO.panic("unexpected heap classification")

match Ashes.IO.args with
    | "1000" :: [] -> run(1000)
    | "10000" :: [] -> run(10000)
    | _ -> Ashes.IO.panic("usage: heap-classification-plateau 1000|10000")
