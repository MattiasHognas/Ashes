let childTypeOf (child: HeapLayoutChild) = child.childType

let agrees semanticType state =
    (let full = heapFactsOf(semanticType)(state)
    in heapChildrenOfNamed(semanticType)(state) == full.children && heapChildTypesOfNamed(semanticType)(state) == map(childTypeOf)(full.children))

let check seed =
    (let name = "Cached" + Ashes.Text.fromInt(seed)
    in
        let named = SemNamed(seed)(name)([])
        in
            let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = seed, groupTypeName = name, groupIsGeneric = false, groupFields = [("A", [SemString, SemInt]), ("B", [SemList(SemBool)])], groupGenericFields = [])]
            in
                let plain = CoreLoweringState(environment = environment, facts = Ashes.Collection.Map.empty, resolvedOverride = None)
                in
                    let cachedFacts = classifyHeapLayout(SemTuple([SemBytes, SemList(SemInt)]))(environment)
                    in
                        let cached = plain with facts = Ashes.Collection.Map.setStr(name)(cachedFacts)(Ashes.Collection.Map.empty)
                        in
                            agrees(named)(plain) && agrees(named)(cached) && heapChildrenOfNamed(named)(cached) == cachedFacts.children && agrees(SemNamed(seed)(name)([SemString]))(cached) && agrees(SemVariable(seed))((cached with resolvedOverride = Some(named))) && agrees(SemList(SemString))(plain))

let recursive repeat n =
    if n == 0
    then Ashes.IO.print("done")
    else
        if check(n)
        then repeat(n - 1)
        else Ashes.IO.panic("child projection mismatch")

match Ashes.IO.args with
    | "check" :: [] ->
        if check(1)
        then Ashes.IO.print("equivalent")
        else Ashes.IO.panic("child projection mismatch")
    | "1000" :: [] -> repeat(1000)
    | "10000" :: [] -> repeat(10000)
    | _ -> Ashes.IO.panic("mode")
