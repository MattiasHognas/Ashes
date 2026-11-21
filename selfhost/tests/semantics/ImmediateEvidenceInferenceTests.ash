import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.NodeIdentity
import AshesCompiler.Semantics.NodeTypeTable
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.ShippedModuleStitching
import AshesCompiler.Semantics.ProjectInference
export (
    value runImmediateEvidenceInferenceTests,
)

let readSource path =
    match Ashes.IO.File.readText(path) with
        | Ok(source) -> source
        | Error(message) -> Ashes.IO.panic(message)

let inferFixture shipped name =
    (let path = "tests/" + name + ".ash"
    in
        match stitchWithShippedModules("evidence")(path)(readSource(path))(shipped) with
            | Error(error) -> Ashes.IO.panic("evidence fixture stitching failed: " + Ashes.Trait.Show.show(error))
            | Ok(project) ->
                project
                |> identifyStitchedProject
                |> inferIdentifiedStitchedProjectFrom("standalone"
                |> emptyTypeEnvironmentForPackage
                |> withIntrinsicBuiltinSignatures)(project))

let expectImmediate result =
    match result with
        | ProgramInferenceResult { error = Some(ProgramExpressionError(ImmediateTraitEvidenceRequired("Seed", SemVariable(_variable) :: []))) } -> Unit
        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("expected immediate Seed evidence: " + Ashes.Trait.Show.show(error))
        | _ -> Ashes.IO.panic("a strict binding selected an instance from a later use")

let expectSuccess result =
    match result with
        | ProgramInferenceResult { error = None } -> Unit
        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("available or latent evidence rejected: " + Ashes.Trait.Show.show(error))

let requireProfile name expected (table: NodeTypeTable) found node _span _expr =
    match nodeDeclarationAt(node)(table) with
        | Some((binding, _scheme)) ->
            if binding == name
            then
                if nodeEvidenceStagesAt(node)(table) == Some(expected)
                then true
                else Ashes.IO.panic("wrong source demand stage for " + name)
            else found
        | None -> found

let checkProfile program table name expected =
    if foldProgramNodes(requireProfile(name)(expected)(table))(false)(program)
    then Unit
    else Ashes.IO.panic("missing recorded binding " + name)

let stagedSource = "trait Seed(a) = | first : Unit -> a | inspect : a -> Str\nimplement Seed(Int) = | first = given u -> 1 | inspect = given v -> \"int\"\nimplement Seed(Bool) = | first = given u -> true | inspect = given v -> \"bool\"\nlet around initial = let _ = Seed.inspect(initial) in given x -> x == initial\nlet pick u = around(Seed.first(Unit))\nlet wrap v = given w -> pick(v)(w)\nlet alias = wrap\n"

let parsed source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | _ -> Ashes.IO.panic("immediate evidence program did not parse")

let infer program =
    inferProgramFromPackage("evidence")(Unit
    |> standardTraitEnvironment
    |> withIntrinsicBuiltinSignatures)(program)

let checkStages unit =
    match stagedSource + "let unused = alias\ntrue"
    |> parsed
    |> identifyProgram(0) with
        | (program, _count) ->
            match infer(program) with
                | ProgramInferenceResult { error = None, nodeTypes = table } ->
                    // Canonical requirements are Eq then Seed. The inspected initial value is
                    // used before the comparator returned by around runs.
                    [2, 1]
                    |> checkProfile(program)(table)("around")
                    |> (given (_) -> checkProfile(program)(table)("pick")([2, 1]))
                    |> (given (_) -> checkProfile(program)(table)("wrap")([2, 2]))
                    |> (given (_) -> checkProfile(program)(table)("alias")([2, 2]))
                | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("staged bindings failed: " + Ashes.Trait.Show.show(error))

let checkLocalBindings unit =
    stagedSource + "let unused = pick(Unit)\ntrue"
    |> parsed
    |> infer
    |> expectImmediate
    |> (given (_) ->
        stagedSource + "let alias = pick in let unused = alias(Unit) in true"
        |> parsed
        |> infer
        |> expectImmediate)
    |> (given (_) ->
        stagedSource + "let alias = given u -> given y -> y in let reused = alias(Unit) in (reused(1), reused(true))"
        |> parsed
        |> infer
        |> expectSuccess)

// An unknown requirement can remain latent through a returned callback. A known consumer of
// that same callback forces its first stage at the binding, including through a lexical alias.
let checkHigherOrderEvidence unit =
    (let invoke = "let invoke f = f(Unit)\nlet relay f = let local = invoke in local(f)\n"
    in
        let keep = "let keep f = f\n"
        in
            let ignore = "let ignore : (Unit -> a -> Bool) -> a -> Bool = given f -> given x -> true\n"
            in
                stagedSource + invoke + "let check = relay(pick)\ntrue"
                |> parsed
                |> infer
                |> expectImmediate
                |> (given (_) ->
                    stagedSource + keep + "let saved = keep(pick)\ntrue"
                    |> parsed
                    |> infer
                    |> expectSuccess)
                |> (given (_) ->
                    stagedSource + keep + "let check = keep(pick)(Unit)\ntrue"
                    |> parsed
                    |> infer
                    |> expectImmediate)
                |> (given (_) ->
                    stagedSource + ignore + "let check = ignore(pick)\n(check(1), check(true))"
                    |> parsed
                    |> infer
                    |> expectSuccess)
                |> (given (_) ->
                    match stagedSource + "let saved : Unit -> Str -> Bool = pick\ntrue"
                    |> parsed
                    |> infer with
                        | ProgramInferenceResult { error = Some(ProgramExpressionError(NoTraitImplementation("Seed", SemString :: []))) } -> Unit
                        | ProgramInferenceResult { error = Some(error) } -> Ashes.IO.panic("wrong missing-instance error for a constrained reference: " + Ashes.Trait.Show.show(error))
                        | _ -> Ashes.IO.panic("a function reference lost its concrete typing constraint")))

let runImmediateEvidenceInferenceTests unit =
    (let path = "lib/Ashes/Trait.ash"
    in
        let shipped = [ShippedModuleText(moduleName = "Ashes.Trait", sourcePath = path, source = readSource(path))]
        in
            "partial_requires_open_evidence_before_returning_function"
            |> inferFixture(shipped)
            |> expectImmediate
            |> (given (_) ->
                "annotated_partial_resolves_evidence_before_returning_function"
                |> inferFixture(shipped)
                |> expectSuccess)
            |> (given (_) ->
                "local_eager_seed_uses_evidence_of_its_enclosing_function"
                |> inferFixture(shipped)
                |> expectSuccess)
            |> checkStages
            |> checkLocalBindings) |> checkHigherOrderEvidence
