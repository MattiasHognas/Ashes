let oldFact (semanticType: SemanticType) (state: CoreLoweringState) (kind: Int) =
    (let resolved = resolveType(state)(semanticType)
    in
        let cached =
            match resolved with
                | SemNamed(_id, name, []) -> Ashes.Collection.Map.getStr(name)(state.facts)
                | _ -> None
        in
            match cached with
                | Some(facts) ->
                    if kind == 0 then facts.runtimeRecordAdtSupported
                    else if kind == 1 then facts.runtimeOwnedChildAdtSupported
                    else facts.ownedChildrenDroppable
                | None ->
                    let facts = ref_classifyHeapLayout(resolved)(state.environment)
                    in
                        if kind == 0 then facts.runtimeRecordAdtSupported
                        else if kind == 1 then facts.runtimeOwnedChildAdtSupported
                        else facts.ownedChildrenDroppable)

let agrees (named: SemanticType) (state: CoreLoweringState) =
    ownedChildrenDroppable(named)(state) == oldFact(named)(state)(2) &&
    canNormalizeClosureCapture(named)(state) == oldCanNormalizeClosureCapture(named)(state)

let checkOne (named: SemanticType) (environment: TypeEnvironment) =
    (let plain = CoreLoweringState(environment = environment, facts = Ashes.Collection.Map.empty, resolvedOverride = None, resourceGuard = false)
    in
        let name = match named with | SemNamed(_id, text, _args) -> text | _ -> "unused"
        in
            let baseline = classifyHeapLayout(SemInt)(environment)
            in
                let yes = baseline with runtimeRecordAdtSupported = true, runtimeOwnedChildAdtSupported = true, ownedChildrenDroppable = true
                in
                    let no = baseline with runtimeRecordAdtSupported = false, runtimeOwnedChildAdtSupported = false, ownedChildrenDroppable = false
                    in
                        let cachedYes = plain with facts = Ashes.Collection.Map.setStr(name)(yes)(Ashes.Collection.Map.empty)
                        in
                            let cachedNo = plain with facts = Ashes.Collection.Map.setStr(name)(no)(Ashes.Collection.Map.empty)
                            in
                                agrees(named)(plain) && agrees(named)(cachedYes) && agrees(named)(cachedNo) && agrees(named)((cachedYes with resourceGuard = true)) && agrees(named)((cachedNo with resolvedOverride = Some(named))) && ownedChildrenDroppable(SemVariable(999))((cachedYes with resolvedOverride = Some(named))) == oldFact(SemVariable(999))((cachedYes with resolvedOverride = Some(named)))(2))

let recursive allAgree (types: List(SemanticType)) (environment: TypeEnvironment) =
    match types with
        | [] -> true
        | head :: tail -> checkOne(head)(environment) && allAgree(tail)(environment)

let queryAgrees semanticType environment = checkOne(semanticType)(environment)

let genericA value = SemNamed(40)("A")([value])

let genericB value = SemNamed(41)("B")([value])

let genericEnvironment leaf = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 40, groupTypeName = "A", groupIsGeneric = true, groupFields = [], groupGenericFields = [("A", [SemVariable(7), genericB(SemVariable(7))], [SemVariable(7)])]), ConstructorFieldGroup(groupSymbolId = 41, groupTypeName = "B", groupIsGeneric = true, groupFields = [], groupGenericFields = [("B", [genericA(leaf)], [SemVariable(7)])])]

let recursive compareTypes (types: List(SemanticType)) (environment: TypeEnvironment) =
    match types with
        | [] -> true
        | head :: rest -> queryAgrees(head)(environment) && compareTypes(rest)(environment)

let genericCase leaf =
    (let environment = genericEnvironment(leaf)
    in
        let roots =
            [genericA(SemInt), genericA(leaf), genericB(SemInt), genericB(leaf), SemInt
            |> genericA
            |> SemList, SemTuple([genericB(SemInt), genericA(leaf)])]
        in compareTypes(roots)(environment) && compareTypes(roots)((environment with constructorFieldGroups = Ashes.Collection.List.reverse(environment.constructorFieldGroups))))

let identityCase unit =
    (let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 1, groupTypeName = "Same", groupIsGeneric = false, groupFields = [("One", [SemString])], groupGenericFields = []), ConstructorFieldGroup(groupSymbolId = 2, groupTypeName = "Same", groupIsGeneric = false, groupFields = [("Two", [SemVariable(91)])], groupGenericFields = []), ConstructorFieldGroup(groupSymbolId = 1, groupTypeName = "Other", groupIsGeneric = false, groupFields = [("Three", [SemNamed(99)("FileHandle")([])])], groupGenericFields = [])]
    in
        let wrapped =
            environment
            |> addInferenceZeroCostTypeDefinition(2000)("TupleWrapper")([])(SemTuple([SemString, SemList(SemInt)]))
            |> addInferenceZeroCostTypeDefinition(2001)("ResourceWrapper")([])(SemNamed(99)("FileHandle")([]))
        in
            let roots =
                [SemNamed(1)("Same")([]), SemNamed(2)("Same")([]), SemNamed(1)("Other")([]), SemNamed(2000)("TupleWrapper")([]), SemNamed(2001)("ResourceWrapper")([]), []
                |> SemNamed(2000)("TupleWrapper")
                |> SemList, SemNamed(999)("Missing")([]), SemNamed(99)("FileHandle")([]), SemOpaque("Handle")]
            in
                compareTypes(roots)(wrapped) && compareTypes(roots)(emptyTypeEnvironment(Unit)) && compareTypes(roots)(wrapped))

let adversarialCases unit =
    genericCase(SemInt) && genericCase(SemVariable(88)) && genericCase(SemString) && genericCase(SemNamed(99)("FileHandle")([])) && identityCase(Unit)

let check unit =
    (let environment = heapLayoutTestEnvironment(Unit)
    in
        [namedTypeAt("Some")([SemInt])(environment), namedTypeAt("Some")([SemString])(environment), namedTypeAt("Some")([SemVariable(7)])(environment), namedTypeAt("Wrap")([])(environment), namedTypeAt("Cons")([])(environment), namedTypeAt("Point")([])(environment), namedTypeAt("Named")([])(environment), namedTypeAt("Call")([])(environment), SemNamed(2000)("Unknown")([]), SemNamed(2001)("Handle")([])]
        |> allAgree
        |> (given (run) -> run(environment)))

let freshQuery seed =
    (let name = "Root" + Ashes.Text.fromInt(seed)
    in
        let named = SemNamed(seed)(name)([])
        in
            let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = seed, groupTypeName = name, groupIsGeneric = false, groupFields = [("A", [SemString, SemInt]), ("B", [SemList(SemBool)])], groupGenericFields = [])]
            in checkOne(named)(environment))

let recursive repeat remaining =
    if remaining == 0 then Ashes.IO.print("done")
    else if freshQuery(remaining) then repeat(remaining - 1)
    else Ashes.IO.panic("query mismatch")

let recursive repeatedFields (count: Int) =
    if count == 0 then [] else SemString :: repeatedFields(count - 1)

let recursive timedQueries (remaining: Int) (old: Bool) (named: SemanticType) (state: CoreLoweringState) (expected: Bool) =
    if remaining == 0 then Ashes.IO.print("benchmarked")
    else
        let actual = if old then oldCanNormalizeClosureCapture(named)(state) else canNormalizeClosureCapture(named)(state)
        in
            if actual == expected then timedQueries(remaining - 1)(old)(named)(state)(expected)
            else Ashes.IO.panic("benchmark mismatch")

let benchmark (old: Bool) (cached: Bool) =
    (let fields = repeatedFields(64)
    in
        let named = SemNamed(9000)("Bench")([])
        in
            let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 9000, groupTypeName = "Bench", groupIsGeneric = false, groupFields = [("Bench", fields)], groupGenericFields = [])]
            in
                let full = classifyHeapLayout(SemTuple(fields))(environment) with runtimeRecordAdtSupported = true
                in
                    let facts = if cached then Ashes.Collection.Map.setStr("Bench")(full)(Ashes.Collection.Map.empty) else Ashes.Collection.Map.empty
                    in
                        let state = CoreLoweringState(environment = environment, facts = facts, resolvedOverride = None, resourceGuard = false)
                        in timedQueries(if cached then 50000 else 1000)(old)(named)(state)(oldCanNormalizeClosureCapture(named)(state)))

let genericQuery (seed: Int) (old: Bool) (nonregular: Bool) =
    (let leaf = if seed % 2 == 0 then SemString else SemList(SemInt)
    in
        let environment =
            if nonregular then genericEnvironment(leaf)
            else emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 40, groupTypeName = "A", groupIsGeneric = true, groupFields = [], groupGenericFields = [("A", [SemVariable(7)], [SemVariable(7)])])]
        in
            let named = if nonregular then genericA(SemInt) else genericA(leaf)
            in
                let state = CoreLoweringState(environment = environment, facts = Ashes.Collection.Map.empty, resolvedOverride = None, resourceGuard = false)
                in
                    let droppable = if old then oldFact(named)(state)(2) else ownedChildrenDroppable(named)(state)
                    in
                        let capture = if old then oldCanNormalizeClosureCapture(named)(state) else canNormalizeClosureCapture(named)(state)
                        in droppable && !capture)

let recursive genericRepeat (remaining: Int) (old: Bool) (nonregular: Bool) =
    if remaining == 0 then Ashes.IO.print("done")
    else if genericQuery(remaining)(old)(nonregular) then genericRepeat(remaining - 1)(old)(nonregular)
    else Ashes.IO.panic("generic query mismatch")

match Ashes.IO.args with
    | "old-generic100" :: [] -> genericRepeat(100)(true)(false)
    | "old-generic1000" :: [] -> genericRepeat(1000)(true)(false)
    | "new-generic100" :: [] -> genericRepeat(100)(false)(false)
    | "new-generic1000" :: [] -> genericRepeat(1000)(false)(false)
    | "old-nonregular100" :: [] -> genericRepeat(100)(true)(true)
    | "old-nonregular1000" :: [] -> genericRepeat(1000)(true)(true)
    | "new-nonregular100" :: [] -> genericRepeat(100)(false)(true)
    | "new-nonregular1000" :: [] -> genericRepeat(1000)(false)(true)
    | "old-cache" :: [] -> benchmark(true)(true)
    | "new-cache" :: [] -> benchmark(false)(true)
    | "old-miss" :: [] -> benchmark(true)(false)
    | "new-miss" :: [] -> benchmark(false)(false)
    | "check" :: [] -> if check(Unit) && adversarialCases(Unit) then Ashes.IO.print("equivalent") else Ashes.IO.panic("query mismatch")
    | "1000" :: [] -> repeat(1000)
    | "10000" :: [] -> repeat(10000)
    | _ -> Ashes.IO.panic("mode")
