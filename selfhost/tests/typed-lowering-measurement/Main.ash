import Ashes.Collection.List.reverse as reverseList
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.CoreLowering.Measurement
import AshesCompiler.Semantics.CoreLowering.LoweringState.ObservedPendingGoal
import AshesCompiler.Semantics.StitchedSources.stitchedRegionOfName
import AshesCompiler.Semantics.ProjectCompilationPlanning.readSourceText
import AshesCompiler.Semantics.ProjectDiscovery
import AshesCompiler.Semantics.ProjectInference
import AshesCompiler.Semantics.ProjectStitching
import AshesCompiler.Semantics.ProjectSyntaxStitching
import AshesCompiler.Semantics.ProgramInference
import AshesCompiler.Semantics.ShippedModuleStitching
import AshesCompiler.Semantics.StandardTraits
import AshesCompiler.Semantics.TypeInference
import AshesCompiler.Semantics.Types.SemanticType
import AshesCompiler.Semantics.NodeTypeTable.NodeOperator
import AshesCompiler.Semantics.CoreLowering.LoweringState.ShadowSite
import AshesCompiler.Semantics.StitchedSources.stitchedSourceRegions
import AshesCompiler.Semantics.CoreLowering.Model.CoreLoweringResult
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Frontend.Token.TextSpan
let isAshSourceName name = Ashes.Text.length(name) > 4 && Ashes.Text.substring(name)(Ashes.Text.length(name) - 4)(4) == ".ash"

let recursive readShippedModules root names loaded =
    match names with
        | [] -> reverseList(loaded)
        | name :: rest ->
            if isAshSourceName(name) == false
            then readShippedModules(root)(rest)(loaded)
            else
                let path = Ashes.IO.Path.join(Ashes.IO.Path.Unix)(root)(name)
                in
                    match readSourceText(path) with
                        | Error(message) -> Ashes.IO.panic("cannot read " + path + ": " + message)
                        | Ok(source) ->
                            readShippedModules(root)(rest)(
                                ShippedModuleText(
                                    moduleName = "Ashes." + Ashes.Text.substring(name)(0)(Ashes.Text.length(name) - 4),
                                    sourcePath = path,
                                    source = source
                                ) :: loaded
                            )

let loadShipped unit =
    match Ashes.IO.Directory.entries("lib/Ashes") with
        | Ok(names) -> readShippedModules("lib/Ashes")(names)([])
        | Error(message) -> Ashes.IO.panic("cannot list lib/Ashes: " + message)

let clock unit = Ashes.IO.Console.monotonicMillis(Unit)

let passColumns (counts: CorePassCounts) =
    Ashes.Text.join("\t")([
        Ashes.Text.fromInt(counts.tableOperators),
        Ashes.Text.fromInt(counts.deferredOperators),
        Ashes.Text.fromInt(counts.relowerings),
        Ashes.Text.fromInt(counts.tableCallResults),
        Ashes.Text.fromInt(counts.tableFallbacks)
    ])

let printMeasurement mode stitchMillis inferMillis (measurement: CoreLoweringMeasurement) =
    (let first = measurement.first
    in
        let replay = measurement.replay
        in
            let _ = Ashes.IO.print("mode\tstitch_ms\tinfer_ms\tfirst_ms\treplay_ms\treplayed\tpending_traits\tequality_goals\tfirst_recorded_operators\tfirst_deferred_operators\tfirst_body_relowerings\tfirst_recorded_calls\tfirst_fallbacks\treplay_recorded_operators\treplay_deferred_operators\treplay_body_relowerings\treplay_recorded_calls\treplay_fallbacks\tpending_keys")
            in
                let _ =
                    [
                        mode,
                        Ashes.Text.fromInt(stitchMillis),
                        Ashes.Text.fromInt(inferMillis),
                        Ashes.Text.fromInt(measurement.firstMillis),
                        Ashes.Text.fromInt(measurement.replayMillis),
                        if measurement.replayed
                        then "yes"
                        else "no",
                        Ashes.Text.fromInt(measurement.pendingGoals),
                        Ashes.Text.fromInt(measurement.equalityGoals),
                        passColumns(first),
                        passColumns(replay),
                        Ashes.Text.join(" ")(measurement.pendingGoalKeys)
                    ]
                    |> Ashes.Text.join("\t")
                    |> Ashes.IO.print
                in
                    match (first.error, replay.error) with
                        | (Some(error), _) -> Ashes.IO.panic("first-lowering-error: " + Ashes.Trait.Show.show(error))
                        | (_, Some(error)) -> Ashes.IO.panic("replay-lowering-error: " + Ashes.Trait.Show.show(error))
                        | _ -> Unit)

let recursive regionPath (regions: List(StitchedModuleRegion)) index =
    match regions with
        | [] -> "<unknown region>"
        | region :: rest ->
            if index == 0
            then region.sourcePath
            else regionPath(rest)(index - 1)

let activeMethodText (entry: (Str, SemanticType, Str, Str)) =
    match entry with
        | (traitName, operand, method, binding) -> traitName + "." + method + "(" + Ashes.Trait.Show.show(operand) + ")=" + binding

let instanceText (instance: NodeOperatorInstance) =
    match instance with
        | PrimitiveInstance -> "primitive"
        | DeclaredInstance -> "declared"
        | DictionaryInstance -> "dictionary"

// What the table recorded for the node a site was lowered at: its operator (trait, operand type
// and instance kind) and its type. A site whose node records no operator was lowered at a node
// lowering synthesized, under the identity of the node enclosing it.
let recordedSiteText (node: Maybe(Int)) (table: Maybe(NodeTypeTable)) =
    match (node, table) with
        | (Some(identity), Some(recorded)) ->
            let operatorText =
                match nodeOperatorAt(identity)(recorded) with
                    | Some(NodeOperator { traitName = traitName, operandType = operandType, instance = instance }) -> traitName + "(" + Ashes.Trait.Show.show(operandType) + ") " + instanceText(instance)
                    | None -> "none"
            in
                " recorded_operator=" + operatorText + " recorded_type=" + Ashes.Trait.Show.show(nodeTypeAt(identity)(recorded))
        | _ -> ""

let recursive reportSites regions table sites =
    match sites with
        | [] -> Unit
        | (ObservedPendingGoal { key = key, structural = structural, deferredOperator = deferredOperator, region = region, node = node, operand = operand, activeMethods = methods }, resolved) :: rest ->
            let kind =
                if deferredOperator
                then "deferred-operator"
                else
                    if structural
                    then "equality"
                    else "trait"
            in
                let _ =
                    Ashes.IO.writeErrorLine(kind + " goal " + key + " source=" + regionPath(regions)(region) + " node=" + Ashes.Trait.Show.show(node) + " operand=" + Ashes.Trait.Show.show(operand) + " resolved=" + Ashes.Trait.Show.show(resolved) + " active_methods=" + "[" + Ashes.Text.join(", ")(Ashes.Collection.List.map(activeMethodText)(methods)) + "]" + recordedSiteText(node)(table))
                in reportSites(regions)(table)(rest)

let reportTraversal regions table (measurement: CoreLoweringMeasurement) =
    (let first = measurement.first
    in
        let replay = measurement.replay
        in
            let (traversals, retained) =
                match measurement.retainedRelowerings with
                    | Some(count) -> (3, count)
                    | None ->
                        (if measurement.replayed
                        then 2
                        else 1, 0)
            in
                let _ = Ashes.IO.writeErrorLine("concrete_copy_calls first_recorded=" + Ashes.Text.fromInt(first.recordedCopyCalls) + " first_inferred=" + Ashes.Text.fromInt(first.inferredCopyCalls) + " emit_replay_recorded=" + Ashes.Text.fromInt(replay.recordedCopyCalls) + " emit_replay_inferred=" + Ashes.Text.fromInt(replay.inferredCopyCalls))
                in
                    let _ = Ashes.IO.writeErrorLine("lowering traversals=" + Ashes.Text.fromInt(traversals) + " body_relowerings_first=" + Ashes.Text.fromInt(first.relowerings) + " body_relowerings_retained=" + Ashes.Text.fromInt(retained) + " body_relowerings_emit_replay=" + Ashes.Text.fromInt(replay.relowerings))
                    in
                        let _ =
                            match measurement.equalityGoalKeys with
                                | [] -> Unit
                                | keys -> Ashes.IO.writeErrorLine("structural equality keys: " + Ashes.Text.join(" ")(keys))
                        in reportSites(regions)(table)(measurement.goalDetails))

// What the driver reports besides the counter row: goal sites (`--goal-details`), every step-8
// shadow site (`--shadow-details`) and the hash of the program's lowered IR (`--ir-hash`).
type DriverOptions =
    | goalDetails: Bool
    | shadowDetails: Bool
    | irHash: Bool

let recursive countShadowSites (rule: Int) (sites: List(ShadowSite)) (differ: Int) (unrecorded: Int) =
    match sites with
        | [] -> (differ, unrecorded)
        | ShadowSite { entry = entry, final = final } :: rest ->
            if entry != rule
            then countShadowSites(rule)(rest)(differ)(unrecorded)
            else
                match final with
                    | Some(_answer) -> countShadowSites(rule)(rest)(differ + 1)(unrecorded)
                    | None -> countShadowSites(rule)(rest)(differ)(unrecorded + 1)

// One line per order-dependent rule of the step-8 plan: sites where the rule decided as the rule
// over the final type would, sites where it decided otherwise, and sites the table answers nothing
// for (a node lowering synthesized, or a type the recorded instantiation leaves open).
let recursive reportShadowCounts (rule: Int) (agree: List(Int)) (sites: List(ShadowSite)) =
    match agree with
        | [] -> Unit
        | agreeing :: rest ->
            match countShadowSites(rule)(sites)(0)(0) with
                | (differ, unrecorded) ->
                    let _ = Ashes.IO.writeErrorLine("shadow rule=" + Ashes.Text.fromInt(rule) + " agree=" + Ashes.Text.fromInt(agreeing) + " differ=" + Ashes.Text.fromInt(differ) + " unrecorded=" + Ashes.Text.fromInt(unrecorded))
                    in reportShadowCounts(rule + 1)(rest)(sites)

let spanText (span: Maybe(TextSpan)) =
    match span with
        | Some(TextSpan { start = start, end = end }) -> Ashes.Text.fromInt(start) + "-" + Ashes.Text.fromInt(end)
        | None -> "-"

let recursive reportShadowSites regions (sites: List(ShadowSite)) =
    match sites with
        | [] -> Unit
        | ShadowSite { entry = entry, region = region, node = node, span = span, current = current, final = final } :: rest ->
            let finalText =
                match final with
                    | Some(answer) -> answer
                    | None -> "unrecorded"
            in
                let nodeText =
                    match node with
                        | Some(identity) -> Ashes.Text.fromInt(identity)
                        | None -> "-"
                in
                    let _ = Ashes.IO.writeErrorLine("shadow-site\t" + Ashes.Text.fromInt(entry) + "\t" + regionPath(regions)(region) + ":" + spanText(span) + ":node=" + nodeText + "\t" + current + "\t" + finalText)
                    in reportShadowSites(regions)(rest)

let recursive hashLines (lines: List(Str)) (hash: Int) (count: Int) =
    match lines with
        | [] -> (hash, count)
        | line :: rest -> hashLines(rest)(hash * 1000003 + Ashes.Trait.Hash.hash(line))(count + 1)

// The lowered IR the compile command produces for the program, as one hash and a line count, so two
// sweeps compare program by program with one diff.
let reportIrHash path source (stitched: StitchedSyntaxProject) (identified: IdentifiedStitchedProject) nodeTypes =
    match nodeTypes |> lowerCoreNumberedProgramWithRegionsAndReuse(true)(stitchedSourceRegions(stitched)(path)(source))(identifiedProgramSyntax(identified.program)) with
        | CoreLoweringResult { program = Some(lowered), error = None } ->
            match hashLines(formatIr(lowered)(LoweredIr)(None))(17)(0) with
                | (hash, count) -> Ashes.IO.writeErrorLine("ir_hash=" + Ashes.Text.fromInt(hash) + " ir_lines=" + Ashes.Text.fromInt(count))
        | CoreLoweringResult { error = error } -> Ashes.IO.writeErrorLine("ir_hash=error " + Ashes.Trait.Show.show(error))

let firstPassShadowAgree (measurement: CoreLoweringMeasurement) =
    (let first = measurement.first
    in first.shadowAgree)

let firstPassShadowSites (measurement: CoreLoweringMeasurement) =
    (let first = measurement.first
    in first.shadowSites)

let measureStitched (options: DriverOptions) path source mode stitchMillis (stitched: StitchedSyntaxProject) =
    (let _ = Ashes.IO.writeErrorLine("project load and stitch complete")
    in
        let _ = Ashes.IO.writeErrorLine("inference start")
        in
            let started = clock(Unit)
            in
                let identified = identifyStitchedProject(stitched)
                in
                    let environment =
                        "standalone"
                        |> emptyTypeEnvironmentForPackage
                        |> withIntrinsicBuiltinSignatures
                    in
                        match inferIdentifiedStitchedProjectFrom(environment)(stitched)(identified) with
                            | ProgramInferenceResult { error = Some(error), errorLocation = location } -> Ashes.IO.panic("infer-error: " + Ashes.Trait.Show.show(error) + " at " + Ashes.Trait.Show.show(location))
                            | ProgramInferenceResult { nodeTypes = nodeTypes, error = None } ->
                                let inferMillis = clock(Unit) - started
                                in
                                    let _ = Ashes.IO.writeErrorLine("inference complete")
                                    in
                                        let _ = Ashes.IO.writeErrorLine("first lowering and optional replay start")
                                        in
                                            let regions =
                                                if options.goalDetails
                                                then
                                                    stitched
                                                    |> stitchedRegionOfName
                                                    |> Some
                                                else None
                                            in
                                                let program =
                                                    if mode == "typed"
                                                    then identifiedProgramSyntax(identified.program)
                                                    else stitched.program
                                                in
                                                    let measured =
                                                        measureStitchedLowering(mode == "typed")(regions)(given (_) -> Ashes.IO.writeErrorLine("first lowering complete, replay starting"))(clock)(nodeTypes)(path)(program)
                                                    in
                                                        let _ =
                                                            Ashes.IO.writeErrorLine(if measured.replayed
                                                            then "first lowering and replay complete"
                                                            else "first lowering complete, replay skipped")
                                                        in
                                                            let recordedTable =
                                                                if mode == "typed"
                                                                then Some(nodeTypes)
                                                                else None
                                                            in
                                                                let _ = reportTraversal(stitched.moduleRegions)(recordedTable)(measured)
                                                                in
                                                                    let _ =
                                                                        measured
                                                                        |> firstPassShadowSites
                                                                        |> reportShadowCounts(1)(firstPassShadowAgree(measured))
                                                                    in
                                                                        let _ =
                                                                            if options.shadowDetails
                                                                            then
                                                                                measured
                                                                                |> firstPassShadowSites
                                                                                |> reportShadowSites(stitched.moduleRegions)
                                                                            else Unit
                                                                        in
                                                                            let _ =
                                                                                if options.irHash
                                                                                then reportIrHash(path)(source)(stitched)(identified)(nodeTypes)
                                                                                else Unit
                                                                            in printMeasurement(mode)(stitchMillis)(inferMillis)(measured))

let run options path mode =
    (let _ = Ashes.IO.writeErrorLine("project load and stitch start")
    in
        let started = clock(Unit)
        in
            match loadProject(Ashes.IO.Path.Unix)(path) with
                | Error(error) -> Ashes.IO.panic("load-error: " + Ashes.Trait.Show.show(error))
                | Ok(layout) ->
                    match Unit
                    |> loadShipped
                    |> stitchProject(Ashes.IO.Path.Unix)(layout) with
                        | Error(error) ->
                            match projectStitchParseDiagnostics(error) with
                                | Some(diagnostics) -> Ashes.IO.panic("parse-error: " + Ashes.Trait.Show.show(diagnostics))
                                | None -> Ashes.IO.panic("stitch-error: " + Ashes.Trait.Show.show(error))
                        | Ok(stitched) -> measureStitched(options)(path)("")(mode)(clock(Unit) - started)(stitched))

// A single `.ash` file is stitched with the shipped modules it imports, as `compile <file>` does.
let runFile options path mode =
    (let _ = Ashes.IO.writeErrorLine("project load and stitch start")
    in
        let started = clock(Unit)
        in
            match readSourceText(path) with
                | Error(message) -> Ashes.IO.panic("cannot read " + path + ": " + message)
                | Ok(source) ->
                    let base = Ashes.IO.Path.basename(Ashes.IO.Path.Unix)(path)
                    in
                        match Unit
                        |> loadShipped
                        |> stitchWithShippedModules(Ashes.Text.substring(base)(0)(Ashes.Text.length(base) - 4))(path)(source) with
                            | Error(error) -> Ashes.IO.panic("stitch-error: " + Ashes.Trait.Show.show(error))
                            | Ok(stitched) -> measureStitched(options)(path)(source)(mode)(clock(Unit) - started)(stitched))

let runPath options path mode =
    if isAshSourceName(path)
    then runFile(options)(path)(mode)
    else run(options)(path)(mode)

let usage = "usage: typed-lowering-measurement <project/ashes.json | file.ash> [typed] [--goal-details] [--shadow-details] [--ir-hash] (the stitched-program lowering the legacy mode measured is gone)"

let recursive parseOptions (arguments: List(Str)) (options: DriverOptions) =
    match arguments with
        | [] -> Some(options)
        | "typed" :: rest -> parseOptions(rest)(options)
        | "--goal-details" :: rest -> parseOptions(rest)((options with goalDetails = true))
        | "--shadow-details" :: rest -> parseOptions(rest)((options with shadowDetails = true))
        | "--ir-hash" :: rest -> parseOptions(rest)((options with irHash = true))
        | _other -> None

match Ashes.IO.args with
    | path :: flags ->
        match parseOptions(flags)(DriverOptions(goalDetails = false, shadowDetails = false, irHash = false)) with
            | Some(options) -> runPath(options)(path)("typed")
            | None -> Ashes.IO.panic(usage)
    | [] -> Ashes.IO.panic(usage)
