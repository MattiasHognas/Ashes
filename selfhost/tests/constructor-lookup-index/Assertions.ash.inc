let nameOf (layout: CoreConstructorLayout) = layout.name

let recursive resultName (semanticType: SemanticType) =
    match semanticType with
        | SemFunction(_argument, result, _row) -> resultName(result)
        | SemNamed(_id, name, _arguments) -> Some(name)
        | _ -> None

let resultOf (layout: CoreConstructorLayout) =
    (let scheme = layout.scheme
    in resultName(scheme.body))

let prepare (layouts: List(CoreConstructorLayout)) = constructorLookupIndex(layouts)

let recursive oldFirst (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> None
        | layout :: rest ->
            if layout.name == name
            then Some(layout)
            else oldFirst(name)(rest)

let recursive oldResult (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | layout :: rest ->
            if resultOf(layout) == Some(name)
            then layout :: oldResult(name)(rest)
            else oldResult(name)(rest)

let recursive oldField (name: Str) (layouts: List(CoreConstructorLayout)) =
    match layouts with
        | [] -> []
        | layout :: rest ->
            if constructorFieldNameOccurs(name)(layout.fieldNames)
            then layout :: oldField(name)(rest)
            else oldField(name)(rest)

let raw name marker semanticType = CoreConstructorLayout(name = name, tag = marker, fieldNames = [name, "shared", name], isZeroCost = false, tagless = false, scheme = TypeScheme(quantified = [], body = semanticType, constraints = []))

let fixtures =
    [[]
    |> SemNamed(1)("T")
    |> raw("A")(1), []
    |> SemNamed(1)("T")
    |> raw("B")(2), []
    |> SemNamed(2)("U")
    |> raw("A")(3), []
    |> SemNamed(3)("T")
    |> raw("a")(4), None
    |> SemFunction(SemVariable(7))(SemNamed(4)("G")([SemVariable(7)]))
    |> raw("C")(5), raw("D")(6)(SemVariable(9))]

let recursive checkNames (names: List(Str)) (layouts: List(CoreConstructorLayout)) index =
    match names with
        | [] -> Unit
        | name :: rest ->
            if oldFirst(name)(layouts) == lookupFirstConstructor(name)(index) && oldResult(name)(layouts) == lookupResultConstructors(name)(index) && oldField(name)(layouts) == lookupFieldConstructors(name)(index) && findRecordLayout(name)(layouts) == findRecordLayout(name)(lookupResultConstructors(name)(index)) && firstLayoutOfType(name)(layouts) == firstLayoutOfType(name)(lookupResultConstructors(name)(index)) && firstConstructorLayoutOf(name)(layouts) == firstConstructorLayoutOf(name)(lookupResultConstructors(name)(index)) && zeroCostPayloadType(name)(layouts) == zeroCostPayloadType(name)(lookupResultConstructors(name)(index)) && nullaryConstructorCount(name)(layouts) == nullaryConstructorCount(name)(lookupResultConstructors(name)(index))
            then checkNames(rest)(layouts)(index)
            else Ashes.IO.panic("lookup mismatch")

let recursive oldSoleNullary (remaining: List(CoreConstructorLayout)) (layouts: List(CoreConstructorLayout)) =
    match remaining with
        | [] -> []
        | layout :: rest ->
            match constructorResultName(layout) with
                | Some(name) ->
                    if constructorArity(layout) == 0 && nullaryConstructorCount(name)(layouts) == 1
                    then layout.name :: oldSoleNullary(rest)(layouts)
                    else oldSoleNullary(rest)(layouts)
                | None -> oldSoleNullary(rest)(layouts)

let check layouts =
    (let index = prepare(layouts)
    in
        if oldSoleNullary(layouts)(layouts) == soleNullaryConstructorNames(layouts)(index)
        then checkNames(["A", "a", "B", "C", "D", "T", "U", "G", "shared", "Shared", "missing"])(layouts)(index)
        else Ashes.IO.panic("sole nullary mismatch"))

let recursive selections depth prefix =
    if depth == 0
    then check(prefix)
    else choices(depth)(prefix)(fixtures)
and choices depth prefix remaining =
    match remaining with
        | [] -> Unit
        | layout :: rest ->
            layout :: prefix
            |> selections(depth - 1)
            |> (given (_) -> choices(depth)(prefix)(rest))

let checkSubstitution unit =
    match fixtures
    |> prepare
    |> lookupFirstConstructor("C") with
        | None -> Ashes.IO.panic("missing generic")
        | Some(layout) ->
            let scheme = layout.scheme
            in
                if scheme.body == SemFunction(SemVariable(7))(SemNamed(4)("G")([SemVariable(7)]))(None) && applySubstitution([(7, SemInt)])(scheme.body) == SemFunction(SemInt)(SemNamed(4)("G")([SemInt]))(None) && applySubstitution([(7, SemString)])(scheme.body) == SemFunction(SemString)(SemNamed(4)("G")([SemString]))(None)
                then Unit
                else Ashes.IO.panic("raw substitution changed")

let checkWrappers unit =
    (let w1 =
        raw("W1")(0)(SemFunction(SemString)(SemNamed(20)("W1")([]))(None)) with isZeroCost = true
    in
        let w2 =
            raw("W2")(0)(SemFunction(SemNamed(20)("W1")([]))(SemNamed(21)("W2")([]))(None)) with isZeroCost = true
        in
            let variable =
                raw("V")(0)(SemFunction(SemVariable(7))(SemNamed(22)("V")([]))(None)) with isZeroCost = true
            in
                let shadow =
                    raw("W1")(1)(SemFunction(SemInt)(SemNamed(20)("W1")([]))(None)) with isZeroCost = true
                in
                    let layouts = [w2, variable, w1, shadow]
                    in
                        let index = prepare(layouts)
                        in
                            if ownedTypeNameOf(SemNamed(21)("W2")([]))(index) == Some("String") && ownedTypeNameOf(SemNamed(22)("V")([]))(index) == None && ownedTypeNameOf(SemNamed(23)("Function")([]))(index) == Some("Function_") && ownedTypeNameOf(SemNamed(20)("W1")([]))(layouts
                            |> reverse
                            |> prepare) == None
                            then checkNames(["W1", "W2", "V", "missing"])(layouts)(index)
                            else Ashes.IO.panic("wrapper ownership changed"))

let recursive definitions count =
    if count == 0
    then []
    else
        raw(Ashes.Text.fromInt(count))(count)(SemNamed(count)(Ashes.Text.fromInt(count))([])) :: definitions(count - 1)

let query index =
    match lookupFirstConstructor("1")(index) with
        | Some(layout) ->
            layout.tag + length(lookupResultConstructors("1")(index)) + length(lookupFieldConstructors("1")(index))
        | None -> Ashes.IO.panic("missing")

let oldQuery layouts =
    match oldFirst("1")(layouts) with
        | Some(layout) ->
            layout.tag + length(oldResult("1")(layouts)) + length(oldField("1")(layouts))
        | None -> Ashes.IO.panic("missing")

let recursive bench count useOld layouts index total =
    if count == 0
    then
        total
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        bench(count - 1)(useOld)(layouts)(index)(total + (if useOld
        then oldQuery(layouts)
        else query(index)))

let freshIndex unit =
    12
    |> definitions
    |> prepare

let recursive plateau count useOld total =
    if count == 0
    then
        total
        |> Ashes.Text.fromInt
        |> Ashes.IO.print
    else
        plateau(count - 1)(useOld)(total + (if useOld
        then
            12
            |> definitions
            |> oldQuery
        else
            Unit
            |> freshIndex
            |> query))

match Ashes.IO.args with
    | "check" :: [] ->
        []
        |> check
        |> (given (_) -> selections(4)([]))
        |> (given (_) -> checkSubstitution(Unit))
        |> (given (_) -> checkWrappers(Unit))
        |> (given (_) -> check(fixtures))
        |> (given (_) -> check([]))
        |> (given (_) -> Ashes.IO.print("1296 lookup cases ok"))
    | "old" :: [] ->
        let layouts = definitions(2000)
        in
            bench(20000)(true)(layouts)(prepare(layouts))(0)
    | "new" :: [] ->
        let layouts = definitions(2000)
        in
            bench(20000)(false)(layouts)(prepare(layouts))(0)
    | "old1000" :: [] -> plateau(1000)(true)(0)
    | "old10000" :: [] -> plateau(10000)(true)(0)
    | "new1000" :: [] -> plateau(1000)(false)(0)
    | "new10000" :: [] -> plateau(10000)(false)(0)
    | _ -> Ashes.IO.panic("mode required")
