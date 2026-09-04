import Ashes.Test as test
import AshesCompiler.Frontend.ImportResolution
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Frontend.Token
import AshesCompiler.Semantics.ModuleSemanticStitching
export (
    value runModuleSemanticStitchingTests,
)

let binding : Str -> Expr -> LetBindingSyntax =
    given (name) ->
        given (value) -> LetBindingSyntax(name = name, value = value, sugarParameters = [], typeAnnotation = None, requirements = [])

let moduleInterface : Str -> List(ModuleImportExport) -> ModuleImportInterface =
    given (name) ->
        given (exports) -> ModuleImportInterface(name = name, exports = exports)

let unit : Str -> Str -> Str -> List(ResolvedImport) -> ModuleImportInterface -> ProgramSyntax -> Bool -> SemanticStitchUnit =
    given (name) ->
        given (packageId) ->
            given (path) ->
                given (imports) ->
                    given (interface) ->
                        given (program) ->
                            given (isEntry) -> SemanticStitchUnit(name = name, packageId = packageId, sourcePath = path, imports = imports, interface = interface, program = program, isEntry = isEntry)

let definitionName definition =
    match definition with
        | StitchedDefinition { compilerName = compilerName, id = _id, sourceName = _sourceName, qualifiedName = _qualifiedName, moduleName = _moduleName, packageId = _packageId, sourcePath = _sourcePath, kind = _kind, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom, exported = _exported } -> compilerName

let definitionId definition =
    match definition with
        | StitchedDefinition { id = id, sourceName = _sourceName, qualifiedName = _qualifiedName, compilerName = _compilerName, moduleName = _moduleName, packageId = _packageId, sourcePath = _sourcePath, kind = _kind, definitionSpan = _span, declarationOrder = _order, visibleFrom = _visibleFrom, exported = _exported } -> id

let requireDefinition message resolved =
    match resolved with
        | Some(definition) -> definition
        | None -> test.fail(message)

let requireProject : Result(ModuleSemanticStitchError, StitchedSemanticProject) -> StitchedSemanticProject needs {ConsoleIO} =
    given (result) ->
        match result with
            | Ok(project) -> project
            | Error(_error) -> test.fail("semantic stitching should succeed")

let utilProgram =
    ProgramSyntax(items = [false
    |> TopLevelLet(binding("first")(ExprInt(1)))
    |> TopLevelAt(TextSpan(start = 10, end = 20)), TopLevelLet(
        binding("loop")(ExprVar("loop")),
        true
    ), TopLevelLet(binding("hidden")(ExprInt(2)))(false), TopLevelType(
        TypeDecl(name = "Box", typeParameters = [], constructors = [TypeConstructor(name = "Box", parameters = [TypeNamed(
            "Int"
        )], fieldNames = ["value"])], isRecord = true, derivingTraits = [])
    )], body = None)

let utilInterface =
    moduleInterface(
        "Foo.Util",
        [ImportValueExport("first"), ImportValueExport("loop"), ImportTypeExport("Box")]
    )

let utilUnit = unit("Foo.Util")("dep@1.0.0")("/dep/Foo/Util.ash")([])(utilInterface)(utilProgram)(false)

let mainProgram =
    ProgramSyntax(items = [TopLevelLet(binding("entry")(ExprInt(0)))(false)], body = Some(ExprVar("entry")))

let mainImports = [ResolvedModuleImport("Foo.Util")(None)(1)("import Foo.Util")]

let mainUnit =
    unit(
        "Main",
        "app@1.0.0",
        "/app/Main.ash",
        mainImports,
        moduleInterface("Main")([ImportValueExport("entry")]),
        mainProgram,
        true
    )

let expectSequentialVisibility (project: StitchedSemanticProject) =
    match resolveStitchedUnqualified("Foo.Util")(0)(StitchedValue)("first")(project) with
        | Some(_) -> test.fail("ordinary declaration should not be visible in its own boundary")
        | None ->
            project
            |> resolveStitchedUnqualified("Foo.Util")(1)(StitchedValue)("first")
            |> requireDefinition("ordinary declaration should become visible after its boundary")
            |> definitionId
            |> test.assertEqual(0)
            |> (given (_) ->
                project
                |> resolveStitchedUnqualified("Foo.Util")(1)(StitchedValue)("loop")
                |> requireDefinition("recursive declaration should be visible in its own boundary")
                |> definitionId
                |> test.assertEqual(1))

let expectImports (project: StitchedSemanticProject) =
    project
    |> resolveStitchedUnqualified("Main")(-1)(StitchedValue)("first")
    |> requireDefinition("whole import should introduce exported values")
    |> definitionName
    |> test.assertEqual("Foo_Util_first")
    |> (given (_) ->
        project
        |> resolveStitchedQualified("Main")("Foo.Util")(StitchedValue)("first")
        |> requireDefinition("full module qualifier should resolve")
        |> definitionId
        |> test.assertEqual(0))
    |> (given (_) ->
        project
        |> resolveStitchedQualified("Main")("Util")(StitchedValue)("first")
        |> requireDefinition("unique leaf module qualifier should resolve")
        |> definitionId
        |> test.assertEqual(0))
    |> (given (_) ->
        match resolveStitchedUnqualified("Main")(-1)(StitchedValue)("hidden")(project) with
            | None -> Unit
            | Some(_) -> test.fail("private declaration should not cross the module interface"))

let expectStablePrivateNames (project: StitchedSemanticProject) =
    project
    |> resolveStitchedUnqualified("Foo.Util")(3)(StitchedValue)("hidden")
    |> requireDefinition("private local value should remain visible in its module")
    |> definitionName
    |> test.assertEqual("__ashes_private_value_Foo_Util_hidden")
    |> (given (_) ->
        project
        |> resolveStitchedUnqualified("Foo.Util")(4)(StitchedConstructor)("Box")
        |> requireDefinition("hidden constructor should remain visible in its module")
        |> definitionName
        |> test.assertEqual("AshesPrivateConstructor_Foo_Util_Box"))

let expectDefinitionMetadata (project: StitchedSemanticProject) =
    match resolveStitchedUnqualified("Foo.Util")(1)(StitchedValue)("first")(project) with
        | Some(StitchedDefinition { id = 0, sourceName = "first", qualifiedName = "Foo.Util.first", compilerName = "Foo_Util_first", moduleName = "Foo.Util", packageId = "dep@1.0.0", sourcePath = "/dep/Foo/Util.ash", kind = StitchedValue, definitionSpan = Some(TextSpan { start = 10, end = 20 }), declarationOrder = 0, visibleFrom = 1, exported = true }) -> Unit
        | Some(_) -> test.fail("stitched definition metadata should preserve identity, provenance, and span")
        | None -> test.fail("stitched definition metadata should exist")

let checkPrimaryPlan unitValue =
    [utilUnit, mainUnit]
    |> buildStitchedSemanticProject
    |> requireProject
    |> (given (project) ->
        project
        |> expectSequentialVisibility
        |> (given (_) -> expectImports(project))
        |> (given (_) -> expectStablePrivateNames(project))
        |> (given (_) -> expectDefinitionMetadata(project)))

let collisionUnit name path =
    unit(
        name,
        "pkg",
        "/" + path,
        [],
        moduleInterface(name)([ImportValueExport("c")]),
        ProgramSyntax(items = [TopLevelLet(binding("c")(ExprInt(0)))(false)], body = None),
        false
    )

let checkCompilerNameCollision unitValue =
    match buildStitchedSemanticProject(
        [collisionUnit("A.B")("A/B.ash"), unit(
            "A",
            "pkg",
            "/A.ash",
            [],
            moduleInterface("A")([ImportValueExport("B_c")]),
            ProgramSyntax(items = [TopLevelLet(binding("B_c")(ExprInt(0)))(false)], body = None),
            false
        )]
    ) with
        | Error(CompilerPrivateNameCollision("A_B_c", "A.B.c", "A.B_c")) -> Unit
        | Error(_error) -> test.fail("unexpected compiler-name collision")
        | Ok(_) -> test.fail("ambiguous compiler names should be rejected")

let duplicateExportModule name path =
    unit(
        name,
        "pkg",
        path,
        [],
        moduleInterface(name)([ImportValueExport("value")]),
        ProgramSyntax(items = [TopLevelLet(binding("value")(ExprInt(0)))(false)], body = None),
        false
    )

let collidingImportEntry program =
    unit(
        "Main",
        "pkg",
        "/Main.ash",
        [ResolvedModuleImport("Left")(None)(1)("import Left"), ResolvedModuleImport(
            "Right",
            None,
            2,
            "import Right"
        )],
        moduleInterface("Main")([]),
        program,
        true
    )

let collidingImportProject program = buildStitchedSemanticProject([duplicateExportModule("Left")("/Left.ash"), duplicateExportModule("Right")("/Right.ash"), collidingImportEntry(program)])

let requireModuleOwner expected message (resolved: Maybe(StitchedDefinition)) =
    match resolved with
        | Some(definition) ->
            if definition.moduleName == expected
            then Unit
            else test.fail(message)
        | None -> test.fail(message)

let checkUnusedImportCollision unitValue =
    ProgramSyntax(items = [], body = None)
    |> collidingImportProject
    |> requireProject
    |> resolveStitchedUnqualified("Main")(0)(StitchedValue)("value")
    |> requireModuleOwner("Left")("an unused import collision should keep the first import's binding")

let checkReferencedImportCollision unitValue =
    match collidingImportProject(ProgramSyntax(items = [], body = Some(ExprVar("value")))) with
        | Error(ConflictingStitchedImport("Main", "value")) -> Unit
        | Error(_error) -> test.fail("unexpected import collision")
        | Ok(_) -> test.fail("an unqualified use of a name two imports export should be rejected")

let checkShadowedImportCollision unitValue =
    ProgramSyntax(items = [TopLevelLet(binding("value")(ExprInt(0)))(false)], body = Some(ExprVar("value")))
    |> collidingImportProject
    |> requireProject
    |> resolveStitchedUnqualified("Main")(10)(StitchedValue)("value")
    |> requireModuleOwner("Main")("a local definition should shadow a colliding imported name")

let checkDependencyOrder unitValue =
    match buildStitchedSemanticProject([mainUnit, utilUnit]) with
        | Error(MissingStitchedImportModule("Main", "Foo.Util")) -> Unit
        | Error(_error) -> test.fail("unexpected dependency-order error")
        | Ok(_) -> test.fail("a dependent module should not precede its dependency")

let checkShortQualifierCollision unitValue =
    (let left = duplicateExportModule("Foo.Util")("/Foo/Util.ash")
    in
        let right = duplicateExportModule("Bar.Util")("/Bar/Util.ash")
        in
            let entry =
                unit(
                    "Main",
                    "pkg",
                    "/Main.ash",
                    [ResolvedModuleImport("Foo.Util")(None)(1)("import Foo.Util"), ResolvedModuleImport(
                        "Bar.Util",
                        None,
                        2,
                        "import Bar.Util"
                    )],
                    moduleInterface("Main")([]),
                    ProgramSyntax(items = [], body = None),
                    true
                )
            in
                match buildStitchedSemanticProject([left, right, entry]) with
                    | Error(ConflictingModuleQualifier("Main", "Util")) -> Unit
                    | Error(_error) -> test.fail("unexpected short-qualifier collision")
                    | Ok(_) -> test.fail("ambiguous short qualifiers should be rejected"))

let runModuleSemanticStitchingTests unitValue =
    unitValue
    |> checkPrimaryPlan
    |> checkCompilerNameCollision
    |> checkUnusedImportCollision
    |> checkReferencedImportCollision
    |> checkShadowedImportCollision
    |> checkDependencyOrder
    |> checkShortQualifierCollision
