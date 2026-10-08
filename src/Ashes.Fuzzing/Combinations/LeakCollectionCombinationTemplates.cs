using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A persistent map threaded through <c>upsertStr</c> with a capturing combine function while the
/// walk over a list of dependencies runs, nested in a walk over functions.
/// </summary>
internal sealed class MapThreadedWhileWalkingTemplate() : LeakCombinationTemplate(
    "leak.map-threaded-while-walking", "mapThreadedWhileWalking", GeneratedFeature.ClosureCapture, GeneratedFeature.Lambda, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
    {
        string add = names.Of("addDependents");
        string of = names.Of("dependentsOf");
        string keys = names.Of("keysOf");
        string functions = names.Words(names.Between(2, 4));
        bool fromMap = names.Coin("dependencies-from-map");
        string dependencyType = fromMap ? "List((Str, Bool))" : "List(Str)";
        string dependencyPattern = fromMap ? "(dependency, _present)" : "dependency";
        string dependencies = fromMap
            ? $"Ashes.Collection.Map.empty\n            |> {keys}({LeakNames.Text(names.Between(2, 5))})\n            |> Ashes.Collection.Map.toList"
            : names.Words(names.Between(2, 5));
        return $$"""
            let recursive {{add}} (function: Str) (dependencies: {{dependencyType}}) (dependents: MapTree(Str, List(Str))) =
                match dependencies with
                    | [] -> dependents
                    | {{dependencyPattern}} :: rest ->
                        dependents
                        |> Ashes.Collection.Map.upsertStr(dependency)([function])(given (existing: List(Str)) -> function :: existing)
                        |> {{add}}(function)(rest)

            let recursive {{keys}} (n: Int) (keys: MapTree(Str, Bool)) =
                if n == 0
                then keys
                else {{keys}}(n - 1)(Ashes.Collection.Map.setStr({{names.Word()}} + Ashes.Text.fromInt(n))(true)(keys))

            let recursive {{of}} (functions: List(Str)) (dependents: MapTree(Str, List(Str))) =
                match functions with
                    | [] -> dependents
                    | key :: rest ->
                        dependents
                        |> {{add}}(key)({{dependencies}})
                        |> {{of}}(rest)

            let {{names.Entry}} (seed: Int) = Ashes.Collection.Map.size({{of}}({{functions}})(Ashes.Collection.Map.empty)) + seed
            """;
    }
}

/// <summary>
/// A tail-recursive walk down a recursive ADT whose fallback arm returns its parameter whole
/// (<c>other -&gt; other</c>), the caller matching the returned part.
/// </summary>
internal sealed class LoopParameterReturnedWholeTemplate() : LeakCombinationTemplate(
    "leak.loop-parameter-returned-whole", "loopParameterReturnedWhole", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.AsPattern)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("LeakType");
        string function = names.Of("LeakFn");
        string named = names.Of("LeakNamed");
        string leaf = names.Of("LeakVar");
        string result = names.Of("resultType");
        string seen = names.Of("seenName");
        string free = names.Of("freeTypes");
        string declarations = $$"""
            type {{type}} =
                | {{function}}({{type}}, {{type}})
                | {{named}}(Int, Str, List({{type}}))
                | {{leaf}}(Int)

            let recursive {{result}} (t: {{type}}) =
                match t with
                    | {{function}}(_parameter, result) -> {{result}}(result)
                    | other -> other
            """;
        if (names.Variant("shape", 2) == 0)
        {
            return declarations + "\n\n" + $$"""
                let {{names.Of("isFree")}} (body: {{type}}) =
                    match {{result}}(body) with
                        | {{named}}(_id, _name, []) -> 1
                        | _ -> 0

                let {{names.Entry}} (seed: Int) = {{names.Of("isFree")}}({{Tree(names, function, named, leaf, "seed")}})
                """;
        }

        return declarations + "\n\n" + $$"""
            let recursive {{seen}} (name: Str) (names: List(Str)) =
                match names with
                    | [] -> false
                    | candidate :: rest -> candidate == name || {{seen}}(name)(rest)

            let recursive {{free}} (items: List({{type}})) (seen: List(Str)) =
                match items with
                    | [] -> []
                    | body :: rest ->
                        match {{result}}(body) with
                            | {{named}}(_id, name, []) as found ->
                                if {{seen}}(name)(seen)
                                then {{free}}(rest)(seen)
                                else (name, found) :: {{free}}(rest)(name :: seen)
                            | _ -> {{free}}(rest)(seen)

            let {{names.Entry}} (seed: Int) = Ashes.Collection.List.length({{free}}([{{Tree(names, function, named, leaf, "seed")}}, {{named}}(0, {{names.Word()}}, []), {{Tree(names, function, named, leaf, "seed + 1")}}])([]))
            """;
    }

    // A chain of one to three function arrows ending in a named type with a short argument list.
    private static string Tree(LeakNames names, string function, string named, string leaf, string seed)
    {
        string arguments = string.Join(", ", Enumerable.Range(0, names.Between(0, 2)).Select(index => $"{leaf}({LeakNames.Text(index + 1)})"));
        string tree = $"{named}(0, {names.Word()}, [{arguments}])";
        int depth = names.Between(1, 3);
        for (int level = 0; level < depth; level++)
        {
            tree = $"{function}({leaf}({seed}), {tree})";
        }

        return tree;
    }
}

/// <summary>
/// A tail-recursive walk whose list accumulator is handed on in one arm and grown in another by a
/// cons of a name matched out of a call's owned result (<c>name :: seen</c>), beside a count.
/// </summary>
internal sealed class AccumulatorConsesCallResultPartTemplate() : LeakCombinationTemplate(
    "leak.accumulator-conses-call-result-part", "accumulatorConsesPart", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("PartType");
        string function = names.Of("PartFn");
        string named = names.Of("PartNamed");
        string leaf = names.Of("PartVar");
        string result = names.Of("resultType");
        string seen = names.Of("seenName");
        string free = names.Of("countFree");
        bool counted = names.Coin("counted");
        string countParameter = counted ? " (count: Int)" : "";
        string handOn = counted ? "(count)" : "";
        string grow = counted ? "(count + 1)" : "";
        string exit = counted ? "count" : "Ashes.Collection.List.length(seen)";
        string start = counted ? "([])(0)" : "([])";
        return $$"""
            type {{type}} =
                | {{function}}({{type}}, {{type}})
                | {{named}}(Int, Str, List({{type}}))
                | {{leaf}}(Int)

            let recursive {{result}} (t: {{type}}) =
                match t with
                    | {{function}}(_parameter, result) -> {{result}}(result)
                    | other -> other

            let recursive {{seen}} (name: Str) (names: List(Str)) =
                match names with
                    | [] -> false
                    | candidate :: rest -> candidate == name || {{seen}}(name)(rest)

            let recursive {{free}} (items: List({{type}})) (seen: List(Str)){{countParameter}} =
                match items with
                    | [] -> {{exit}}
                    | body :: rest ->
                        match {{result}}(body) with
                            | {{named}}(_id, name, []) ->
                                if {{seen}}(name)(seen)
                                then {{free}}(rest)(seen){{handOn}}
                                else {{free}}(rest)(name :: seen){{grow}}
                            | _ -> {{free}}(rest)(seen){{handOn}}

            let {{names.Entry}} (seed: Int) = {{free}}([{{named}}(seed, {{names.Word()}}, []), {{function}}({{leaf}}(seed), {{named}}(0, {{names.Word()}}, [])), {{named}}(0, {{names.Word()}}, [{{leaf}}(1)])]){{start}} + seed
            """;
    }
}

/// <summary>
/// An accumulator of a variant type (a list-bearing constructor or an empty one) threaded through a
/// helper that rebuilds it at each step of a walk.
/// </summary>
internal sealed class VariantAccumulatorThroughHelperTemplate() : LeakCombinationTemplate(
    "leak.variant-accumulator-through-helper", "variantAccumulator", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string type = names.Of("LeakNames");
        string some = names.Of("LeakSome");
        string none = names.Of("LeakNone");
        string add = names.Of("addName");
        string size = names.Of("sizeOf");
        string walk = names.Of("walk");
        bool strings = names.Coin("str-elements");
        string element = strings ? "Str" : "Int";
        string items = strings ? names.Words(names.Between(2, 5)) : names.Ints(names.Between(2, 5), 1, 9);
        string step = names.Coin("piped-step")
            ? $"acc\n                |> {add}(item)\n                |> {walk}(rest)"
            : $"{walk}(rest)({add}(item)(acc))";
        return $$"""
            type {{type}} =
                | {{some}}(List({{element}}), Int)
                | {{none}}

            let {{add}} (name: {{element}}) (acc: {{type}}) =
                match acc with
                    | {{some}}(names, count) -> {{some}}(name :: names, count + 1)
                    | {{none}} -> {{some}}([name], 1)

            let {{size}} (acc: {{type}}) =
                match acc with
                    | {{some}}(_names, count) -> count
                    | {{none}} -> 0

            let recursive {{walk}} (items: List({{element}})) (acc: {{type}}) =
                match items with
                    | [] -> {{size}}(acc)
                    | item :: rest ->
                        {{step}}

            let {{names.Entry}} (seed: Int) = {{walk}}({{items}})({{none}}) + seed
            """;
    }
}

/// <summary>
/// A worklist solver: a front and back queue and a map of queued keys, refilled by a helper returning
/// the pair of the new back queue and map, optionally merging records into a result table.
/// </summary>
internal sealed class WorklistSolveRecordsTemplate() : LeakCombinationTemplate(
    "leak.worklist-solve-records", "worklistSolve", GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.Let)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
    {
        string enqueue = names.Of("enqueue");
        string solve = names.Of("solve");
        string keys = names.Words(names.Between(2, 5));
        int fuel = names.Between(6, 14);
        string enqueueDeclaration = $$"""
            let recursive {{enqueue}} (pending: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) =
                match pending with
                    | [] -> (back, queued)
                    | key :: rest ->
                        match Ashes.Collection.Map.getStr(key)(queued) with
                            | Some(true) -> {{enqueue}}(rest)(back)(queued)
                            | _ ->
                                queued
                                |> Ashes.Collection.Map.setStr(key)(true)
                                |> {{enqueue}}(rest)(key :: back)
            """;
        if (names.Variant("shape", 2) == 0)
        {
            return enqueueDeclaration + "\n\n" + $$"""
                let recursive {{solve}} (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
                    match front with
                        | [] ->
                            match back with
                                | [] -> Ashes.Collection.Map.size(queued)
                                | _ -> {{solve}}(Ashes.Collection.List.reverse(back))([])(queued)(fuel)
                        | key :: rest ->
                            if fuel <= 0
                            then Ashes.Collection.Map.size(queued)
                            else
                                let dequeued = Ashes.Collection.Map.setStr(key)(false)(queued)
                                in
                                    if fuel % {{LeakNames.Text(names.Between(2, 4))}} == 0
                                    then {{solve}}(rest)(back)(dequeued)(fuel - 1)
                                    else
                                        match {{enqueue}}([key + {{names.Word()}}])(back)(dequeued) with
                                            | (nextBack, nextQueued) -> {{solve}}(rest)(nextBack)(nextQueued)(fuel - 1)

                let {{names.Entry}} (seed: Int) = {{solve}}({{keys}})([])(Ashes.Collection.Map.empty)({{LeakNames.Text(fuel)}}) + seed
                """;
        }

        return enqueueDeclaration + "\n\n" + SolveRecords(names, enqueue, solve, fuel);
    }

    // The registry maps three keys around a cycle, and the worklist starts from keys of the cycle,
    // so the solver reaches the arm that merges records into its table.
    private static string SolveRecords(LeakNames names, string enqueue, string solve, int fuel)
    {
        string reach = names.Of("LeakReach");
        string registry = names.Of("LeakRegistry");
        string lookup = names.Of("lookup");
        IReadOnlyList<string> cycle = names.DistinctWords(3);
        string keys = "[" + string.Join(", ", Enumerable.Range(0, names.Between(1, 3)).Select(index => cycle[index]).Append(names.Word())) + "]";
        return $$"""
            type {{reach}} =
                | fresh: Bool
                | parts: List(Str)

            type {{registry}} =
                | byKey: MapTree(Str, Str)
                | size: Int

            let {{lookup}} (key: Str) (table: MapTree(Str, {{reach}})) =
                match Ashes.Collection.Map.getStr(key)(table) with
                    | Some(found) -> found
                    | None -> {{reach}}(fresh = false, parts = [])

            let recursive {{solve}} (registry: {{registry}}) (table: MapTree(Str, {{reach}})) (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
                match front with
                    | [] ->
                        match back with
                            | [] -> table
                            | _ -> {{solve}}(registry)(table)(Ashes.Collection.List.reverse(back))([])(queued)(fuel)
                    | key :: rest ->
                        if fuel <= 0
                        then table
                        else
                            match Ashes.Collection.Map.getStr(key)(registry.byKey) with
                                | None -> {{solve}}(registry)(table)(rest)(back)(Ashes.Collection.Map.setStr(key)(false)(queued))(fuel - 1)
                                | Some(target) ->
                                    let dequeued = Ashes.Collection.Map.setStr(key)(false)(queued)
                                    in
                                        let previous = {{lookup}}(key)(table)
                                        in
                                            let merged = {{reach}}(fresh = true, parts = target :: previous.parts)
                                            in
                                                if Ashes.Collection.List.length(previous.parts) > {{LeakNames.Text(names.Between(1, 3))}}
                                                then {{solve}}(registry)(table)(rest)(back)(dequeued)(fuel - 1)
                                                else
                                                    match {{enqueue}}([target])(back)(dequeued) with
                                                        | (nextBack, nextQueued) -> {{solve}}(registry)(Ashes.Collection.Map.setStr(key)(merged)(table))(rest)(nextBack)(nextQueued)(fuel - 1)

            let {{names.Entry}} (seed: Int) =
                (let registry = {{registry}}(byKey = Ashes.Collection.Map.empty
                |> Ashes.Collection.Map.setStr({{cycle[0]}})({{cycle[1]}})
                |> Ashes.Collection.Map.setStr({{cycle[1]}})({{cycle[2]}})
                |> Ashes.Collection.Map.setStr({{cycle[2]}})({{cycle[0]}}), size = 3)
                in Ashes.Collection.Map.size({{solve}}(registry)(Ashes.Collection.Map.empty)({{keys}})([])(Ashes.Collection.Map.empty)({{LeakNames.Text(fuel)}})) + seed)
            """;
    }
}

/// <summary>
/// A lowering-state pipeline: a loop over arguments threads a record state holding nested group
/// records through helpers that return records wrapping the state beside an optional error.
/// </summary>
internal sealed class LoweringStatePipelineTemplate() : LeakCombinationTemplate(
    "leak.lowering-state-pipeline", "loweringStatePipeline", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.UniqueConstructorUpdate, GeneratedFeature.TailCall, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        LoweringNames n = new(names);
        bool pipeline = names.Coin("full-pipeline");
        string arguments = "[" + string.Join(", ", Enumerable.Range(0, names.Between(3, 6)).Select(_ => Argument(names, n))) + "]";
        string slots = names.Ints(names.Between(2, 5), 1, 9);
        string initial = $"{n.State}(tco = {n.Tco}(slot = None, context = None, names = {names.Words(1)}), request = {n.Request}(expected = None, site = None, transfers = false), counter = 0, items = [])";
        return Types(n) + "\n\n" + Helpers(n) + "\n\n" + Loop(n, pipeline) + "\n\n" + $$"""
            let {{names.Entry}} (seed: Int) = {{n.LoopFunction}}({{arguments}})({{slots}})(0)(seed % 7)({{n.LowerOne}})({{initial}})([])
            """;
    }

    private static string Argument(LeakNames names, LoweringNames n) => names.Between(0, 2) switch
    {
        0 => $"{n.ExprInt}({LeakNames.Text(names.Between(1, 9))})",
        1 => $"{n.ExprVar}({names.Word()})",
        _ => $"{n.ExprAdd}({n.ExprInt}(1), {n.ExprInt}(2))",
    };

    private static string Types(LoweringNames n) => $$"""
        type {{n.Expr}} =
            | {{n.ExprVar}}(Str)
            | {{n.ExprAdd}}({{n.Expr}}, {{n.Expr}})
            | {{n.ExprInt}}(Int)

        type {{n.Request}} =
            | expected: Maybe(Int)
            | site: Maybe(Str)
            | transfers: Bool

        type {{n.Tco}} =
            | slot: Maybe(Int)
            | context: Maybe((Str, Int))
            | names: List(Str)

        type {{n.State}} =
            | tco: {{n.Tco}}
            | request: {{n.Request}}
            | counter: Int
            | items: List(Int)

        type {{n.Resolution}} =
            | state: {{n.State}}
            | argumentType: Int
            | error: Maybe(Str)

        type {{n.Lowered}} =
            | state: {{n.State}}
            | temp: Int
            | error: Maybe(Str)
        """;

    private static string Helpers(LoweringNames n) => $$"""
        let {{n.WithSlot}} value (state: {{n.State}}) =
            (let group = state.tco
            in state with tco = (group with slot = value))

        let {{n.WithContext}} value (state: {{n.State}}) =
            (let group = state.tco
            in state with tco = (group with context = value))

        let {{n.WithRequest}} (expected: Int) (site: Str) (copies: Bool) (state: {{n.State}}) =
            if expected == 99 && !copies
            then state
            else state with request = {{n.Request}}(expected = Some(expected), site = Some(site), transfers = !copies)

        let {{n.Ensure}} (t: Int) (state: {{n.State}}) = {{n.Resolution}}(state = state, argumentType = t, error = None)

        let {{n.Dup}} (argument: {{n.Expr}}) (lowered: {{n.Lowered}}) =
            match lowered with
                | {{n.Lowered}} { state = state, temp = temp, error = None } -> {{n.Lowered}}(state = (state with counter = state.counter + 1), temp = temp, error = None)
                | _ -> lowered

        let {{n.LowerOne}} (argument: {{n.Expr}}) (state: {{n.State}}) =
            match argument with
                | {{n.ExprInt}}(v) -> {{n.Lowered}}(state = (state with counter = state.counter + v, items = v :: state.items), temp = state.counter, error = None)
                | _ -> {{n.Lowered}}(state = (state with items = 0 :: state.items), temp = state.counter, error = None)

        let {{n.HeadSlot}} (slots: List(Int)) =
            match slots with
                | slot :: _rest -> Some(slot)
                | [] -> None

        let {{n.RestSlots}} (slots: List(Int)) =
            match slots with
                | _slot :: rest -> rest
                | [] -> []
        """;

    private static string Loop(LoweringNames n, bool pipeline)
    {
        string[] stageCalls = pipeline
            ? [$"{n.WithContext}(Some((\"x\", ordinal)))", $"{n.WithRequest}(parameterType)(\"site\" + Ashes.Text.fromInt(ordinal))(ordinal > 3)", "lower(argument)", $"{n.Dup}(argument)"]
            : [$"{n.WithContext}(Some((\"x\", ordinal)))", $"{n.LowerOne}(argument)"];
        string stages = string.Join("\n                    ", stageCalls.Select(stage => "|> " + stage));
        return $$"""
            let recursive {{n.LoopFunction}} (arguments: List({{n.Expr}})) (slots: List(Int)) (ordinal: Int) functionType lower (state: {{n.State}}) (temps: List(Int)) =
                match arguments with
                    | [] -> state.counter + Ashes.Collection.List.length(temps) + Ashes.Collection.List.length(state.items)
                    | argument :: rest ->
                        match {{n.Ensure}}(functionType)(state) with
                            | {{n.Resolution}} { state = failedState, error = Some(_error) } -> failedState.counter
                            | {{n.Resolution}} { state = functionState, argumentType = parameterType, error = None } ->
                                match functionState
                                |> {{n.WithSlot}}({{n.HeadSlot}}(slots))
                                {{stages}} with
                                    | {{n.Lowered}} { state = failedState, error = Some(_error) } -> failedState.counter
                                    | {{n.Lowered}} { state = argumentState, temp = temp, error = None } ->
                                        {{n.LoopFunction}}(rest)({{n.RestSlots}}(slots))(ordinal + 1)(functionType)(lower)(argumentState
                                        |> {{n.WithSlot}}(None)
                                        |> {{n.WithContext}}(None))(temp :: temps)
            """;
    }

    private sealed class LoweringNames(LeakNames names)
    {
        internal string Expr { get; } = names.Of("LeakExpr");
        internal string ExprVar { get; } = names.Of("LeakExprVar");
        internal string ExprAdd { get; } = names.Of("LeakExprAdd");
        internal string ExprInt { get; } = names.Of("LeakExprInt");
        internal string Request { get; } = names.Of("LeakRequest");
        internal string Tco { get; } = names.Of("LeakTco");
        internal string State { get; } = names.Of("LeakState");
        internal string Resolution { get; } = names.Of("LeakResolution");
        internal string Lowered { get; } = names.Of("LeakLowered");
        internal string WithSlot { get; } = names.Of("withSlot");
        internal string WithContext { get; } = names.Of("withContext");
        internal string WithRequest { get; } = names.Of("withRequest");
        internal string Ensure { get; } = names.Of("ensure");
        internal string Dup { get; } = names.Of("dup");
        internal string LowerOne { get; } = names.Of("lowerOne");
        internal string HeadSlot { get; } = names.Of("headSlotOf");
        internal string RestSlots { get; } = names.Of("restSlotsOf");
        internal string LoopFunction { get; } = names.Of("loop");
    }
}

/// <summary>
/// A loop threading a map of records that it merges each key's earlier record into, or hands on
/// unchanged once the record is full: the map is only ever the parameter itself or a call's owned
/// result, so it holds the earlier record's parts by references of its own, and the earlier record
/// is released on the edge that hands the map on unchanged.
/// </summary>
internal sealed class TableMergedOrHandedOnTemplate() : LeakCombinationTemplate(
    "leak.table-merged-or-handed-on", "tableMerged", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.Let)
{
    protected override string Declarations(LeakNames names)
    {
        string reach = names.Of("LeakReach");
        string lookup = names.Of("lookup");
        string solve = names.Of("solve");
        IReadOnlyList<string> words = names.DistinctWords(names.Between(2, 3));
        List<string> keys = [.. Enumerable.Range(0, names.Between(5, 9)).Select(index => words[index % words.Count])];
        int full = names.Between(1, 2);
        int fuel = names.Between(8, 14);
        string merged = names.Coin("key-part") ? "key :: previous.parts" : "(key + \"\") :: previous.parts";
        return $$"""
            type {{reach}} =
                | fresh: Bool
                | parts: List(Str)

            let {{lookup}} (key: Str) (table: MapTree(Str, {{reach}})) =
                match Ashes.Collection.Map.getStr(key)(table) with
                    | Some(found) -> found
                    | None -> {{reach}}(fresh = false, parts = [])

            let recursive {{solve}} (table: MapTree(Str, {{reach}})) (keys: List(Str)) (fuel: Int) =
                match keys with
                    | [] -> table
                    | key :: rest ->
                        if fuel <= 0
                        then table
                        else
                            let previous = {{lookup}}(key)(table)
                            in
                                let merged = {{reach}}(fresh = true, parts = {{merged}})
                                in
                                    if Ashes.Collection.List.length(previous.parts) > {{LeakNames.Text(full)}}
                                    then {{solve}}(table)(rest)(fuel - 1)
                                    else {{solve}}(Ashes.Collection.Map.setStr(key)(merged)(table))(rest)(fuel - 1)

            let {{names.Entry}} (seed: Int) = Ashes.Collection.Map.size({{solve}}(Ashes.Collection.Map.empty)([{{string.Join(", ", keys)}}])({{LeakNames.Text(fuel)}})) + seed
            """;
    }
}

/// <summary>
/// A loop over a list of strings growing a variant (a chain of nodes) whose every node holds a
/// head the walk matched out of the list: the variant parameter lives on the reference-counted heap
/// and is normalized at the back edge, while the arena node built for it retains the head.
/// </summary>
internal sealed class VariantNodeHoldingWalkedHeadTemplate() : LeakCombinationTemplate(
    "leak.variant-node-holding-walked-head", "variantNodeHead", GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string tree = names.Of("LeakChain");
        string leaf = names.Of("LeakEnd");
        string node = names.Of("LeakLink");
        string size = names.Of("sizeOf");
        string grow = names.Of("grow");
        string keys = names.Words(names.Between(2, 5));
        return $$"""
            type {{tree}} =
                | {{leaf}}
                | {{node}}(Str, {{tree}})

            let recursive {{size}} (chain: {{tree}}) =
                match chain with
                    | {{leaf}} -> 0
                    | {{node}}(_key, rest) -> 1 + {{size}}(rest)

            let recursive {{grow}} (keys: List(Str)) (chain: {{tree}}) =
                match keys with
                    | [] -> chain
                    | key :: rest -> {{grow}}(rest)({{node}}(key)(chain))

            let {{names.Entry}} (seed: Int) = {{size}}({{grow}}({{keys}})({{leaf}})) + seed
            """;
    }
}

/// <summary>
/// A loop over records testing a string field bound under a cons pattern with a curried library
/// call and branching on the result: the call's result counts as a use of the field, last read by
/// the branch, so the field's release must go before the branch for every arm to take it.
/// </summary>
internal sealed class PatternStringTestedByCurriedCallTemplate() : LeakCombinationTemplate(
    "leak.pattern-string-tested-by-curried-call", "stringTest", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string box = names.Of("LeakBox");
        string count = names.Of("countMatching");
        string build = names.Of("boxesFrom");
        IReadOnlyList<string> words = names.DistinctWords(names.Between(3, 5));
        string labels = "[" + string.Join(", ", words.Select(word => word + " + \"x\"")) + "]";
        string test = names.Pick(
            "Ashes.Text.startsWith(label)(\"s\")",
            "Ashes.Text.startsWith(\"s\")(label)",
            "Ashes.Text.contains(label)(\"x\")");
        string hit = names.Coin("hit-passes-count") ? $"{count}(rest)(total)" : $"{count}(rest)(total + 1)";
        string miss = names.Coin("miss-passes-count") ? $"{count}(rest)(total)" : $"{count}(rest)(total + 2)";
        return $$"""
            type {{box}} =
                | label: Str
                | tag: Int

            let recursive {{count}} (boxes: List({{box}})) (total: Int) =
                match boxes with
                    | [] -> total
                    | {{box}} { label = label } :: rest ->
                        if {{test}}
                        then {{hit}}
                        else {{miss}}

            let recursive {{build}} (labels: List(Str)) (built: List({{box}})) =
                match labels with
                    | [] -> built
                    | label :: rest -> {{build}}(rest)({{box}}(label = label + "", tag = 1) :: built)

            let {{names.Entry}} (seed: Int) = {{count}}({{build}}({{labels}})([]))(0) + seed
            """;
    }
}

/// <summary>
/// A loop over records searches each record's list field, bound under a cons pattern, with a
/// helper that only reads it, and returns early with what the search found: the field stays live
/// past the call, and the borrowing call must not take a duplicate of it that nothing releases.
/// </summary>
internal sealed class PatternListPassedToBorrowingCallTemplate() : LeakCombinationTemplate(
    "leak.pattern-list-passed-to-borrowing-call", "borrowSearch", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("LeakItem");
        string box = names.Of("LeakBox");
        string find = names.Of("findItem");
        string scan = names.Of("scanBoxes");
        string build = names.Of("boxesFrom");
        IReadOnlyList<string> words = names.DistinctWords(names.Between(2, 4));
        string labels = "[" + string.Join(", ", words) + "]";
        string target = names.Coin("search-hits") ? words[^1] + " + \"x\"" : "\"absent\"";
        string found = names.Pick("Some(found)", "Some(found + \"!\")");
        return $$"""
            type {{item}} =
                | itemName: Str
                | weight: Int

            type {{box}} =
                | label: Str
                | items: List({{item}})

            let recursive {{find}} (name: Str) (items: List({{item}})) =
                match items with
                    | [] -> None
                    | entry :: rest ->
                        if entry.itemName == name
                        then Some(entry.itemName + "!")
                        else {{find}}(name)(rest)

            let recursive {{scan}} name (boxes: List({{box}})) =
                match boxes with
                    | [] -> None
                    | {{box}} { items = items } :: rest ->
                        match {{find}}(name)(items) with
                            | Some(found) -> {{found}}
                            | None -> {{scan}}(name)(rest)

            let recursive {{build}} (labels: List(Str)) (built: List({{box}})) =
                match labels with
                    | [] -> built
                    | label :: rest -> {{build}}(rest)({{box}}(label = label + "", items = [{{item}}(itemName = label + "x", weight = 1)]) :: built)

            let {{names.Entry}} (seed: Int) =
                match {{scan}}({{target}})({{build}}({{labels}})([])) with
                    | Some(found) -> Ashes.Text.byteLength(found) + seed
                    | None -> seed
            """;
    }
}

/// <summary>
/// A file mapped once per round and read through the view: the view is a borrowed window over
/// the program-lifetime mapping that no release reaches, so the backend must build it in the
/// arena rather than in a reference-counted cell of its own.
/// </summary>
internal sealed class MmapViewPerCallTemplate() : LeakCombinationTemplate(
    "leak.mmap-view-per-call", "mmapView", GeneratedFeature.Match, GeneratedFeature.Call)
{
    protected override string Declarations(LeakNames names)
    {
        string path = names.Pick("leak_mmap_view.txt", "leak_mmap_input.txt");
        string read = names.Pick(
            "Ashes.Byte.length(bytes)",
            "Ashes.Number.UInt.toInt(Ashes.Byte.get(bytes)(0))",
            "Ashes.Text.byteLength(Ashes.Byte.subText(bytes)(0)(3))");
        return $$"""
            let {{names.Entry}} (seed: Int) =
                match Ashes.IO.File.writeText("{{path}}")("hello, bytes!") with
                    | Ok(_) ->
                        match Ashes.IO.File.mmap("{{path}}") with
                            | Ok(bytes) -> {{read}} + seed
                            | Error(_message) -> seed
                    | Error(_message) -> seed
            """;
    }
}

/// <summary>
/// A builtin answering a <c>Result</c>, a <c>Maybe</c> or a tuple once per round: the result cell,
/// its error message and the text or bytes inside are arena values (a message a constant global)
/// that lowering never releases, so the backend must not build them as reference-counted cells.
/// </summary>
internal sealed class BuiltinResultsPerCallTemplate() : LeakCombinationTemplate(
    "leak.builtin-results-per-call", "builtinResult", GeneratedFeature.Match, GeneratedFeature.Call)
{
    protected override string Declarations(LeakNames names)
    {
        string body = names.Variant("builtin", 4) switch
        {
            0 => """
                match Ashes.IO.File.writeText("leak_builtin_results.txt")("hello") with
                        | Ok(_) ->
                            match Ashes.IO.File.readText("leak_builtin_results.txt") with
                                | Ok(text) -> Ashes.Text.byteLength(text) + seed
                                | Error(message) -> Ashes.Text.byteLength(message) + seed
                        | Error(_message) -> seed
                """,
            1 => """
                match Ashes.IO.Environment.get("HOME") with
                        | Ok(Some(value)) -> Ashes.Text.byteLength(value) + seed
                        | Ok(None) -> seed
                        | Error(message) -> Ashes.Text.byteLength(message) + seed
                """,
            2 => """
                match Ashes.IO.Environment.temporaryDirectory(Unit) with
                        | Ok(directory) -> Ashes.Text.byteLength(directory) + seed
                        | Error(message) -> Ashes.Text.byteLength(message) + seed
                """,
            _ => """
                match Ashes.Byte.scanHash(Ashes.Byte.fromText("Hamburg;12.0"))(59)(0) with
                        | (index, _hash) -> index + seed
                """,
        };
        return $$"""
            let {{names.Entry}} (seed: Int) =
                {{body}}
            """;
    }
}
