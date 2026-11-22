import Ashes.Collection.List.reverse as reverseList
import AshesCompiler.Semantics.CoreLowering
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

let recursive reportSites regions sites =
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
                    Ashes.IO.writeErrorLine(kind + " goal " + key + " source=" + regionPath(regions)(region) + " node=" + Ashes.Trait.Show.show(node) + " operand=" + Ashes.Trait.Show.show(operand) + " resolved=" + Ashes.Trait.Show.show(resolved) + " active_methods=" + "[" + Ashes.Text.join(", ")(Ashes.Collection.List.map(activeMethodText)(methods)) + "]")
                in reportSites(regions)(rest)

let reportTraversal regions (measurement: CoreLoweringMeasurement) =
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
                        in reportSites(regions)(measurement.goalDetails))

let measureStitched details path mode stitchMillis (stitched: StitchedSyntaxProject) =
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
                                                if details
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
                                                            let _ = reportTraversal(stitched.moduleRegions)(measured)
                                                            in printMeasurement(mode)(stitchMillis)(inferMillis)(measured))

let run details path mode =
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
                        | Ok(stitched) -> measureStitched(details)(path)(mode)(clock(Unit) - started)(stitched))

match Ashes.IO.args with
    | path :: mode :: [] when mode == "typed" || mode == "legacy" -> run(false)(path)(mode)
    | path :: mode :: "--goal-details" :: [] when mode == "typed" || mode == "legacy" -> run(true)(path)(mode)
    | _ -> Ashes.IO.panic("usage: typed-lowering-measurement <project/ashes.json> <legacy|typed> [--goal-details]")
