import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.Types
import AshesCompiler.Semantics.EvidenceDemand.indexEvidenceUse
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

let expectFixtureLocation result =
    (let source = readSource("tests/partial_requires_open_evidence_before_returning_function.ash")
    in
        match result with
            | ProgramInferenceResult { errorLocation = Some(InferenceErrorLocation { span = Some(TextSpan { start = start }) }) } ->
                if start == Ashes.Text.indexOf(source)("pick(Unit)")
                then expectImmediate(result)
                else Ashes.IO.panic("immediate evidence should point to the binding RHS")
            | _ -> Ashes.IO.panic("missing RHS location for immediate evidence"))

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

// Passing the second tuple field to an unknown function cannot force a second application
// of the function in the first field. Its Eq requirement stays on the returned comparator.
let checkTupleCallbackKeepsReturnedRequirement unit =
    (let source = "let invoke pair = match pair with | (action, arg) -> action(arg)\nlet agree u = given x -> x == x\nlet inline = invoke((agree, Unit))\nlet stored = (agree, Unit)\nlet delayed = invoke(stored)\n(inline(1), inline(true), delayed(1), delayed(true))"
    in
        match source
        |> parsed
        |> identifyProgram(0) with
            | (program, _count) ->
                match infer(program) with
                    | ProgramInferenceResult { error = None, nodeTypes = table } ->
                        [1]
                        |> checkProfile(program)(table)("inline")
                        |> (given (_) -> checkProfile(program)(table)("delayed")([1]))
                    | result -> expectSuccess(result))

let checkAggregateEvidence unit =
    (let invoke = "let invoke pair = match pair with | (action, arg) -> action(arg)\n"
    in
        let ignore = "let ignore : ((Unit -> a -> Bool), Unit) -> a -> Bool = given pair -> given value -> true\n"
        in
            let boxes = "type Callbacks(a) = | first: Unit -> a -> Bool | second: Unit -> a -> Bool\nlet callFirst (box: Callbacks(a)) = box.first(Unit)\n"
            in
                let holder = "let holder = Callbacks(first = pick, second = given u -> given v -> true)\n"
                in
                    stagedSource + invoke + "let check = invoke((pick, Unit))\ntrue"
                    |> parsed
                    |> infer
                    |> expectImmediate
                    |> (given (_) ->
                        stagedSource + invoke + "let stored = (pick, Unit)\nlet check = invoke(stored)\ntrue"
                        |> parsed
                        |> infer
                        |> expectImmediate)
                    |> (given (_) ->
                        stagedSource + ignore + "let check = ignore((pick, Unit))\n(check(1), check(true))"
                        |> parsed
                        |> infer
                        |> expectSuccess)
                    |> (given (_) ->
                        stagedSource + boxes + holder + "let check = callFirst(holder)\ntrue"
                        |> parsed
                        |> infer
                        |> expectImmediate)
                    |> (given (_) ->
                        stagedSource + boxes + holder + "let check = holder.second(Unit)\n(check(1), check(true))"
                        |> parsed
                        |> infer
                        |> expectSuccess))

let checkRecursiveEvidence unit =
    (let eager = "let recursive relay u = worker(u) and worker u = around(Seed.first(Unit))\n"
    in
        let delayed = "let recursive relay u = given x -> worker(u)(x) and worker u = around(Seed.first(Unit))\n"
        in
            let callbacks = "let recursive relay f = called(f) and called f = f(Unit)\n"
            in
                let cycle = "let recursive relay n = if n == 0 then around(Seed.first(Unit)) else later(n - 1) and later n = relay(n)\n"
                in
                    stagedSource + eager + "let check = relay(Unit)\ntrue"
                    |> parsed
                    |> infer
                    |> expectImmediate
                    |> (given (_) ->
                        stagedSource + delayed + "let check = relay(Unit)\n(check(1),check(true))"
                        |> parsed
                        |> infer
                        |> expectSuccess)
                    |> (given (_) ->
                        stagedSource + callbacks + "let check = relay(pick)\ntrue"
                        |> parsed
                        |> infer
                        |> expectImmediate)
                    |> (given (_) ->
                        stagedSource + cycle + "let check = later(0)\ntrue"
                        |> parsed
                        |> infer
                        |> expectImmediate))

// A member with no constraints of its own still forwards the other member's callback use.
// Returning the callback leaves both of its original dictionary demands on the saved value.
let checkReturnedPeerStages unit =
    match stagedSource + "let recursive relay f = helper(f) and helper f = f\nlet delayed = relay(pick)\ntrue"
    |> parsed
    |> identifyProgram(0) with
        | (program, _count) ->
            match infer(program) with
                | ProgramInferenceResult { error = None, nodeTypes = table } -> checkProfile(program)(table)("delayed")([2, 1])
                | result -> expectSuccess(result)

let recursive checkRepeatedUses remaining indexed =
    match remaining with
        | [] -> Unit
        | use :: rest ->
            match indexEvidenceUse(use)(indexed) with
                | None -> checkRepeatedUses(rest)(indexed)
                | Some(_next) -> Ashes.IO.panic("duplicate evidence use survived indexing")

let recursive checkDistinctUses originals remaining indexed =
    match remaining with
        | [] -> checkRepeatedUses(originals)(indexed)
        | use :: rest ->
            match indexEvidenceUse(use)(indexed) with
                | None -> Ashes.IO.panic("distinct evidence uses compared equal")
                | Some(next) -> checkDistinctUses(originals)(rest)(next)

let checkEvidenceUseIdentity unit =
    (let baseUse = EvidenceParameterUse(parameter = 0, path = [], applied = 1, stage = 1, returned = false)
    in
        let uses = [baseUse, (baseUse with parameter = 1), (baseUse with applied = -1), (baseUse with stage = 2), (baseUse with returned = true), (baseUse with path = ["left", "right"]), (baseUse with path = ["left"]), (baseUse with path = ["left.right"]), (baseUse with path = ["left", "result"])]
        in checkDistinctUses(uses)(uses)(Ashes.Collection.Map.empty))

let runImmediateEvidenceInferenceTests unit =
    (let path = "lib/Ashes/Trait.ash"
    in
        let shipped = [ShippedModuleText(moduleName = "Ashes.Trait", sourcePath = path, source = readSource(path))]
        in
            "partial_requires_open_evidence_before_returning_function"
            |> inferFixture(shipped)
            |> expectFixtureLocation
            |> (given (_) ->
                "annotated_partial_resolves_evidence_before_returning_function"
                |> inferFixture(shipped)
                |> expectSuccess)
            |> (given (_) ->
                "local_eager_seed_uses_evidence_of_its_enclosing_function"
                |> inferFixture(shipped)
                |> expectSuccess)
            |> (given (_) ->
                "recursive_peer_returns_callback_without_invoking_it"
                |> inferFixture(shipped)
                |> expectSuccess)
            |> (given (_) ->
                "recursive_peer_invokes_callback_during_binding"
                |> inferFixture(shipped)
                |> expectImmediate)
            |> checkStages
            |> checkLocalBindings)
    |> checkHigherOrderEvidence
    |> checkAggregateEvidence
    |> checkTupleCallbackKeepsReturnedRequirement
    |> checkRecursiveEvidence
    |> checkReturnedPeerStages
    |> checkEvidenceUseIdentity
