import Ashes.IO
import Ashes.Test as test
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.TraitResolution
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.DerivingExpansion
let constructor name fields = TypeConstructor(name = name, parameters = fields, fieldNames = [])

let ordinaryType name parameters constructors derivingTraits = TypeDecl(name = name, typeParameters = parameters, constructors = constructors, isRecord = false, derivingTraits = derivingTraits)

let recursive implementationTraitNames items =
    match items with
        | [] -> []
        | TopLevelImplementation(TraitImplementationDecl { traitName = traitName }) :: tail ->
            traitName :: implementationTraitNames(
                tail
            )
        | _head :: tail -> implementationTraitNames(tail)

let recursive implementationMethodNames items =
    match items with
        | [] -> []
        | TopLevelImplementation(TraitImplementationDecl { bindings = TraitImplementationMethodBinding { methodName = methodName } :: [] }) :: tail ->
            methodName :: implementationMethodNames(
                tail
            )
        | _head :: tail -> implementationMethodNames(tail)

let recursive implementationBodies items =
    match items with
        | [] -> []
        | TopLevelImplementation(TraitImplementationDecl { bindings = TraitImplementationMethodBinding { implementation = implementation } :: [] }) :: tail ->
            implementation :: implementationBodies(
                tail
            )
        | _head :: tail -> implementationBodies(tail)

let recursive appendTexts left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendTexts(tail)(right)

let recursive concatenationLiterals expression =
    match expression with
        | ExprString(value) -> [value]
        | ExprAdd(left, right) ->
            right
            |> concatenationLiterals
            |> appendTexts(concatenationLiterals(left))
        | _ -> []

let recursive equalityHasMismatchCase cases =
    match cases with
        | [] -> false
        | (PatternWildcard, ExprBool(false), None) :: _tail -> true
        | _head :: tail -> equalityHasMismatchCase(tail)

let equalityBodyIsDeterministic expression =
    match expression with
        | ExprLambda("__derived_left", inner, None) ->
            match inner with
                | ExprLambda("__derived_right", ExprMatch(ExprTuple(_values), cases, None), None) ->
                    equalityHasMismatchCase(
                        cases
                    )
                | _ -> false
        | _ -> false

let recursive orderingResults cases =
    match cases with
        | [] -> []
        | (_pattern, ExprVar(result), None) :: tail -> result :: orderingResults(tail)
        | _head :: tail -> "payload" :: orderingResults(tail)

let orderingBodyResults expression =
    match expression with
        | ExprLambda("__derived_left", inner, None) ->
            match inner with
                | ExprLambda("__derived_right", ExprMatch(_pair, cases, None), None) -> orderingResults(cases)
                | _ -> []
        | _ -> []

let recursive hashingSeeds cases =
    match cases with
        | [] -> []
        | (_pattern, ExprInt(seed), None) :: tail -> seed :: hashingSeeds(tail)
        | (_pattern, ExprAdd(ExprMultiply(ExprInt(seed), ExprInt(16777619)), _fieldHash), None) :: tail ->
            seed :: hashingSeeds(
                tail
            )
        | _head :: tail -> -1 :: hashingSeeds(tail)

let hashingBodySeeds expression =
    match expression with
        | ExprLambda("__derived_value", ExprMatch(_value, cases, None), None) -> hashingSeeds(cases)
        | _ -> []

let expectOrdinaryExpansion unit =
    (let declaration =
        ordinaryType(
            "Choice",
            [],
            [constructor("First")([]), constructor("Second")([TypeNamed("Int")])],
            ["Eq", "Ord", "Show", "Hash"]
        )
    in
        match expandDerivedImplementations(
            ProgramSyntax(items = [TopLevelType(declaration)], body = Some(ExprInt(7)))
        ) with
            | Error(UnsupportedDerivedTrait(typeName, traitName)) ->
                test.fail(
                    "unexpected unsupported derived trait " + typeName + ":" + traitName
                )
            | Error(DuplicateDerivedTrait(typeName, traitName)) ->
                test.fail(
                    "unexpected duplicate derived trait " + typeName + ":" + traitName
                )
            | Error(UnsupportedDerivedField(typeName, traitName)) ->
                test.fail(
                    "unexpected unsupported derived field " + typeName + ":" + traitName
                )
            | Ok(ProgramSyntax { items = items, body = body }) ->
                let bodyChecked =
                    match body with
                        | Some(ExprInt(7)) -> Unit
                        | _ -> test.fail("deriving should preserve the program body")
                in
                    ((given (_) ->
                        match items with
                            | TopLevelType(TypeDecl { name = "Choice", derivingTraits = [] }) :: _ ->
                                items
                                |> implementationTraitNames
                                |> test.assertEqual(["Eq", "Ord", "Show", "Hash"])
                                |> (given (_) ->
                                    items
                                    |> implementationMethodNames
                                    |> test.assertEqual(["equal", "compare", "show", "hash"]))
                            | _ ->
                                test.fail(
                                    "deriving should preserve the ordinary type without its clause"
                                )))(bodyChecked))

let expectPayloadRequirements unit =
    (let declaration =
        ordinaryType(
            "PhantomBox",
            [TypeParameter(name = "a"), TypeParameter(name = "phantom")],
            [constructor("PhantomBox")([TypeNamed("a")])],
            ["Ashes.Trait.Eq"]
        )
    in
        match expandDerivedImplementations(ProgramSyntax(items = [TopLevelType(declaration)], body = None)) with
            | Ok(ProgramSyntax { items = _typeItem :: TopLevelImplementation(implementation) :: [] }) ->
                match implementation with
                    | TraitImplementationDecl { traitName = "Eq", typeArguments = TypeApplied("PhantomBox", TypeNamed("a") :: TypeNamed("phantom") :: []) :: [], requirements = TraitConstraintSyntax { traitName = "Eq", typeArguments = TypeNamed("a") :: [] } :: [] } -> Unit
                    | _ -> test.fail("derived implementation head should retain every declared parameter")
            | _ -> test.fail("only payload-contributing parameters should require derived evidence"))

let expectZeroCostExpansion unit =
    (let declaration =
        ZeroCostTypeDecl(name = "UserId", typeParameters = [], constructor = constructor(
            "UserId",
            [TypeNamed("Int")]
        ), derivingTraits = ["Show"])
    in
        match expandDerivedImplementations(ProgramSyntax(items = [TopLevelZeroCostType(declaration)], body = None)) with
            | Ok(ProgramSyntax { items = first :: second :: [] }) ->
                match (first, second) with
                    | (TopLevelZeroCostType(ZeroCostTypeDecl { name = "UserId", derivingTraits = [] }), TopLevelImplementation(TraitImplementationDecl { traitName = "Show", typeArguments = TypeNamed("UserId") :: [] })) -> Unit
                    | _ ->
                        test.fail(
                            "zero-cost deriving should retain the declaration and emit an ordinary implementation"
                        )
            | _ -> test.fail("zero-cost deriving should retain the declaration and emit an ordinary implementation"))

let expectGeneratedMethodBodies unit =
    (let declaration =
        ordinaryType(
            "Choice",
            [],
            [constructor("First")([]), constructor("Second")([TypeNamed("Int")])],
            ["Eq", "Ord", "Hash"]
        )
    in
        match expandDerivedImplementations(ProgramSyntax(items = [TopLevelType(declaration)], body = None)) with
            | Ok(ProgramSyntax { items = items }) ->
                match implementationBodies(items) with
                    | equality :: ordering :: hashing :: [] ->
                        equality
                        |> equalityBodyIsDeterministic
                        |> test.assertEqual(true)
                        |> (given (_) ->
                            ordering
                            |> orderingBodyResults
                            |> test.assertEqual(["Equal", "Less", "Greater", "payload"]))
                        |> (given (_) ->
                            hashing
                            |> hashingBodySeeds
                            |> test.assertEqual([1, 2]))
                    | _ -> test.fail("deriving should emit one method body for each requested trait")
            | _ -> test.fail("deriving should generate deterministic Eq, Ord, and Hash method bodies"))

let expectRecordShowBody unit =
    (let recordConstructor =
        TypeConstructor(name = "Point", parameters = [TypeNamed("Int"), TypeNamed(
            "Int"
        )], fieldNames = ["x", "y"])
    in
        let declaration = TypeDecl(name = "Point", typeParameters = [], constructors = [recordConstructor], isRecord = true, derivingTraits = ["Show"])
        in
            match expandDerivedImplementations(ProgramSyntax(items = [TopLevelType(declaration)], body = None)) with
                | Ok(ProgramSyntax { items = _typeItem :: TopLevelImplementation(TraitImplementationDecl { bindings = TraitImplementationMethodBinding { implementation = ExprLambda("__derived_value", ExprMatch(_value, (_pattern, body, None) :: [], None), None) } :: [] }) :: [] }) ->
                    body
                    |> concatenationLiterals
                    |> test.assertEqual(["Point(", "x = ", ", y = ", ")"])
                | _ -> test.fail("derived Show should render record fields in declaration order"))

let rejectUnsupportedTrait unit =
    (let declaration = ordinaryType("Token")([])([constructor("Token")([])])(["Default"])
    in
        match expandDerivedImplementations(ProgramSyntax(items = [TopLevelType(declaration)], body = None)) with
            | Error(UnsupportedDerivedTrait("Token", "Default")) -> Unit
            | _ -> test.fail("unsupported derived traits should be rejected"))

let rejectDuplicateTrait unit =
    (let declaration = ordinaryType("Token")([])([constructor("Token")([])])(["Eq", "Ashes.Trait.Eq"])
    in
        match expandDerivedImplementations(ProgramSyntax(items = [TopLevelType(declaration)], body = None)) with
            | Error(DuplicateDerivedTrait("Token", "Eq")) -> Unit
            | _ -> test.fail("qualified and leaf spellings of the same derived trait should be duplicates"))

let derivingProgram declaration items =
    ProgramSyntax(items = appendTexts(
        items,
        [TopLevelType(declaration)]
    ), body = None)

let expectUnsupportedDerivedDeclaration expectedName expectedTrait declaration items =
    match items
    |> derivingProgram(declaration)
    |> expandDerivedImplementations with
        | Error(UnsupportedDerivedField(typeName, traitName)) ->
            typeName
            |> test.assertEqual(expectedName)
            |> (given (_) -> test.assertEqual(expectedTrait)(traitName))
        | _ -> test.fail("expected unsupported derived field " + expectedName + ":" + expectedTrait)

let derivedFieldDeclaration name traitName fieldType =
    ordinaryType(
        name,
        [],
        [constructor(name)([fieldType])],
        [traitName]
    )

let expectUnsupportedDerivedField expectedName expectedTrait fieldType items =
    fieldType
    |> derivedFieldDeclaration(expectedName)(expectedTrait)
    |> (given (declaration) -> expectUnsupportedDerivedDeclaration(expectedName)(expectedTrait)(declaration)(items))

let nonRegularTreeField unit = TypeApplied("Tree")([TypeApplied("List")([TypeNamed("a")])])

let callbackField unit = TypeArrow(TypeNamed("Int"))(TypeNamed("Int"))([])(None)

let nonRegularTreeDeclaration unit =
    [nonRegularTreeField(Unit)]
    |> constructor("Branch")
    |> (given (branch) -> ordinaryType("Tree")([TypeParameter(name = "a")])([branch])(["Eq"]))

let opaqueHandleItems unit =
    [None
    |> ExternalOpaqueType("Handle")
    |> TopLevelExternal]

let resourceDatabaseItems unit =
    [Some("databaseClose")
    |> ExternalOpaqueType("Database")
    |> TopLevelExternal]

let clockCapabilityItems unit =
    [TopLevelCapability(
        CapabilityDecl(name = "Clock", typeParameters = [], operations = [])
    )]

let nativeHandleAlias unit =
    TopLevelTypeAlias(
        TypeAliasDecl(name = "NativeHandle", typeParameters = [], target = TypeNamed("Handle"))
    )

let nativeHandleAliasItems unit =
    [None
    |> ExternalOpaqueType("Handle")
    |> TopLevelExternal, nativeHandleAlias(Unit)]

let rejectUnsupportedFields unit =
    Unit
    |> callbackField
    |> (given (fieldType) ->
        expectUnsupportedDerivedField(
            "Callback",
            "Eq",
            fieldType,
            []
        ))
    |> (given (_) ->
        Unit
        |> nonRegularTreeDeclaration
        |> (given (declaration) -> expectUnsupportedDerivedDeclaration("Tree")("Eq")(declaration)([])))

let rejectSemanticallyUnsupportedFields unit =
    Unit
    |> opaqueHandleItems
    |> expectUnsupportedDerivedField(
        "OpaqueBox",
        "Show",
        TypeNamed("Handle")
    )
    |> (given (_) ->
        Unit
        |> resourceDatabaseItems
        |> expectUnsupportedDerivedField(
            "ResourceBox",
            "Eq",
            TypeApplied("List")([TypeNamed("Database")])
        ))
    |> (given (_) ->
        Unit
        |> clockCapabilityItems
        |> expectUnsupportedDerivedField(
            "CapabilityBox",
            "Hash",
            TypeNamed("Clock")
        ))
    |> (given (_) ->
        Unit
        |> nativeHandleAliasItems
        |> expectUnsupportedDerivedField(
            "AliasBox",
            "Ord",
            TypeNamed("NativeHandle")
        ))

let derivingInferenceUnit packageId items = ProgramInferenceUnit(packageId = packageId, program = ProgramSyntax(items = items, body = None))

let opaqueHandleInferenceUnit unit =
    Unit
    |> opaqueHandleItems
    |> derivingInferenceUnit("dependency")

let opaqueBoxItems unit =
    [TypeNamed("Handle")
    |> derivedFieldDeclaration("OpaqueBox")("Show")
    |> TopLevelType]

let opaqueBoxInferenceUnit unit =
    Unit
    |> opaqueBoxItems
    |> derivingInferenceUnit("entry")

let rejectUnsupportedFieldFromSeparateUnit unit =
    [opaqueHandleInferenceUnit(Unit), opaqueBoxInferenceUnit(Unit)]
    |> (given (units) ->
        inferProgramUnitsFrom(standardTraitEnvironment(Unit))(units)(None)("entry"))
    |> (given (result) ->
        match result with
            | ProgramInferenceResult { error = Some(ProgramDerivingExpansionError(UnsupportedDerivedField("OpaqueBox", "Show"))) } -> Unit
            | _ -> test.fail("stitched units should share deriving eligibility context"))

let expectExpansionBeforeCoherence unit =
    (let declaration =
        ordinaryType(
            "Color",
            [],
            [constructor("Red")([]), constructor("Blue")([TypeNamed("Int")])],
            ["Eq", "Ord", "Show", "Hash"]
        )
    in
        match inferProgramFromPackage(
            "deriving-tests",
            standardTraitEnvironment(Unit),
            ProgramSyntax(items = [TopLevelType(declaration)], body = None)
        ) with
            | ProgramInferenceResult { environment = environment, error = None } ->
                match resolveTraitEvidence(
                    TraitConstraint(traitName = "Eq", typeArguments = [SemNamed(3)("Color")([])]),
                    environment
                ) with
                    | TraitEvidenceResolution { plan = Some(TraitEvidenceInstance(_constraint, _implementation, [], [])), error = None } -> Unit
                    | _ -> test.fail("derived implementations should participate in ordinary evidence resolution")
            | ProgramInferenceResult { error = Some(error) } ->
                test.fail(
                    "derived implementation should infer before coherence: " + Ashes.Trait.Show.show(error)
                ))

let runDerivingExpansionTests unit =
    unit
    |> expectOrdinaryExpansion
    |> expectPayloadRequirements
    |> expectZeroCostExpansion
    |> expectGeneratedMethodBodies
    |> expectRecordShowBody
    |> rejectUnsupportedTrait
    |> rejectDuplicateTrait
    |> rejectUnsupportedFields
    |> rejectSemanticallyUnsupportedFields
    |> rejectUnsupportedFieldFromSeparateUnit
    |> expectExpansionBeforeCoherence
    |> (given (_) -> Ashes.IO.print("all self-hosted deriving expansion tests passed"))
