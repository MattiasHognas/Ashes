let testNode id = SemNamed(id)("Node" + Ashes.Text.fromInt(id))([])

let recursive testPower n = if n == 0 then 1 else 2 * testPower(n - 1)
let testEdge mask bit = mask / testPower(bit) % 2 == 1

let recursive testFields mask from target =
    if target == 3 then []
    else if testEdge(mask)(from * 3 + target)
    then testNode(target) :: testFields(mask)(from)(target + 1)
    else testFields(mask)(from)(target + 1)

let testLeaf mode =
    match mode with
        | 0 -> SemInt
        | 1 -> SemVariable(77)
        | 2 -> SemNamed(99)("FileHandle")([])
        | _ -> SemFunction(SemNamed(99)("FileHandle")([]))(SemVariable(77))(None)

let recursive testGroups mask leaf id =
    if id == 3 then []
    else ConstructorFieldGroup(groupSymbolId = id, groupTypeName = "Node" + Ashes.Text.fromInt(id), groupIsGeneric = false, groupFields = [("Build", (if id == 0 then [SemTuple([SemList(leaf)])] else []) |> append(testFields(mask)(id)(0)))], groupGenericFields = []) :: testGroups(mask)(leaf)(id + 1)

let compareDrop semanticType environment =
    let old = referenceCanDropValueGraph(semanticType)(environment)
    in let current = heapCanDropValueGraph(semanticType)(environment)
    in if old == current then Unit else Ashes.IO.panic("drop graph mismatch")

let recursive checkRoots environment id =
    if id == 3 then Unit
    else compareDrop(testNode(id))(environment) |> (given (_) -> checkRoots(environment)(id + 1))

let recursive exhaustive mask mode =
    if mask == 512 then Unit
    else if mode == 4 then exhaustive(mask + 1)(0)
    else
        let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = testGroups(mask)(testLeaf(mode))(0)
        in checkRoots(environment)(0) |> (given (_) -> exhaustive(mask)(mode + 1))

let genericA value = SemNamed(40)("A")([value])
let genericB value = SemNamed(41)("B")([value])
let genericEnvironment leaf = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 40, groupTypeName = "A", groupIsGeneric = true, groupFields = [], groupGenericFields = [("A", [SemVariable(7), genericB(SemVariable(7))], [SemVariable(7)])]), ConstructorFieldGroup(groupSymbolId = 41, groupTypeName = "B", groupIsGeneric = true, groupFields = [], groupGenericFields = [("B", [genericA(leaf)], [SemVariable(7)])])]

let specialCases unit =
    let resource = SemNamed(99)("FileHandle")([])
    in let environment = genericEnvironment(resource)
    in if heapResourceBearing(genericA(SemInt))(environment) == false && heapCanDropValueGraph(genericA(SemInt))(environment) == false
       then compareDrop(genericA(SemInt))(environment)
         |> (given (_) -> compareDrop(genericB(SemInt))(environment))
         |> (given (_) -> compareDrop(genericA(SemInt))(genericEnvironment(SemVariable(88))))
         |> (given (_) -> compareDrop(genericA(SemInt))(genericEnvironment(SemInt)))
         |> (given (_) -> compareDrop(SemNamed(999)("Missing")([]))(emptyTypeEnvironment(Unit)))
         |> (given (_) -> compareDrop(SemOpaque("Handle"))(emptyTypeEnvironment(Unit)))
         |> (given (_) -> compareDrop(SemTuple([SemFunction(resource)(resource)(None), SemBytes, SemBigInt]))(environment))
       else Ashes.IO.panic("resource-before-cut contract changed")

let recursive compareTypes types environment =
    match types with
        | [] -> Unit
        | head :: rest -> compareDrop(head)(environment) |> (given (_) -> compareTypes(rest)(environment))

let recursive genericCases mode =
    if mode == 4 then Unit
    else let environment = genericEnvironment(testLeaf(mode))
         in let roots = [genericA(SemInt), genericA(testLeaf(mode)), genericB(SemInt), genericB(testLeaf(mode)), SemList(genericA(SemInt)), SemTuple([genericB(SemInt), genericA(testLeaf(mode))])]
         in compareTypes(roots)(environment)
            |> (given (_) -> compareTypes(roots)(environment with constructorFieldGroups = reverse(environment.constructorFieldGroups)))
            |> (given (_) -> genericCases(mode + 1))

let identityAndErasure unit =
    let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = [ConstructorFieldGroup(groupSymbolId = 1, groupTypeName = "Same", groupIsGeneric = false, groupFields = [("One", [SemString])], groupGenericFields = []), ConstructorFieldGroup(groupSymbolId = 2, groupTypeName = "Same", groupIsGeneric = false, groupFields = [("Two", [SemVariable(91)])], groupGenericFields = []), ConstructorFieldGroup(groupSymbolId = 1, groupTypeName = "Other", groupIsGeneric = false, groupFields = [("Three", [SemNamed(99)("FileHandle")([])])], groupGenericFields = [])]
    in let wrapped = environment |> addInferenceZeroCostTypeDefinition(2000)("TupleWrapper")([])(SemTuple([SemString, SemList(SemInt)])) |> addInferenceZeroCostTypeDefinition(2001)("ResourceWrapper")([])(SemNamed(99)("FileHandle")([]))
    in compareTypes([SemNamed(1)("Same")([]), SemNamed(2)("Same")([]), SemNamed(1)("Other")([]), SemNamed(2000)("TupleWrapper")([]), SemNamed(2001)("ResourceWrapper")([]), SemList(SemNamed(2000)("TupleWrapper")([]))])(wrapped)

let recursive diamond depth =
    ConstructorFieldGroup(groupSymbolId = depth, groupTypeName = "Node" + Ashes.Text.fromInt(depth), groupIsGeneric = false, groupFields = [("Build", (if depth == 0 then [SemString] else [testNode(depth - 1), testNode(depth - 1)]))], groupGenericFields = []) :: (if depth == 0 then [] else diamond(depth - 1))

let benchmark useOld =
    let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = diamond(18)
    in let result = if useOld then referenceCanDropValueGraph(testNode(18))(environment) else heapCanDropValueGraph(testNode(18))(environment)
    in if result then Ashes.IO.print("droppable") else Ashes.IO.panic("diamond mismatch")

let recursive fresh count useOld =
    if count == 0 then Ashes.IO.print("done")
    else let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = testGroups(count % 512)(testLeaf(count % 4))(0)
         in let actual = if useOld then referenceCanDropValueGraph(testNode(count % 3))(environment) else heapCanDropValueGraph(testNode(count % 3))(environment)
         in if actual == referenceCanDropValueGraph(testNode(count % 3))(environment)
            then fresh(count - 1)(useOld)
            else Ashes.IO.panic("fresh mismatch")

let singleCase mode =
    let environment = emptyTypeEnvironment(Unit) with constructorFieldGroups = testGroups(511)(testLeaf(mode))(0)
    in compareDrop(testNode(0))(environment) |> (given (_) -> Ashes.IO.print("case ok"))

match Ashes.IO.args with
    | "case0" :: [] -> singleCase(0)
    | "case1" :: [] -> singleCase(1)
    | "case2" :: [] -> singleCase(2)
    | "case3" :: [] -> singleCase(3)
    | "check" :: [] -> exhaustive(0)(0) |> (given (_) -> specialCases(Unit)) |> (given (_) -> genericCases(0)) |> (given (_) -> identityAndErasure(Unit)) |> (given (_) -> Ashes.IO.print("6144 graph roots and special cases agree"))
    | "old" :: [] -> benchmark(true)
    | "new" :: [] -> benchmark(false)
    | "old1000" :: [] -> fresh(1000)(true)
    | "old10000" :: [] -> fresh(10000)(true)
    | "new1000" :: [] -> fresh(1000)(false)
    | "new10000" :: [] -> fresh(10000)(false)
    | _ -> Ashes.IO.panic("mode required")
