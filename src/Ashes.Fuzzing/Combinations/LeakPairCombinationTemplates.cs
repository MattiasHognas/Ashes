using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A helper returning a tuple whose last element is <c>None</c>, whose record state part a loop
/// threads to its next step.
/// </summary>
internal sealed class TupleResultWithMaybeTemplate() : LeakCombinationTemplate(
    "leak.tuple-result-with-maybe", "tupleResultWithMaybe", GeneratedFeature.Record, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string handOff = names.Of("LeakHandOff");
        string state = names.Of("LeakState");
        string emit = names.Of("emitFlag");
        string flag = names.Of("pendingFlag");
        string prepare = names.Of("prepareArgument");
        string steps = names.Of("steps");
        bool registers = names.Coin("pending-flag");
        string prepareBody = registers
            ? $$"""
                match {{emit}}(functionTemp)(state) with
                        | (flagged, acceptsFlagTemp) ->
                            match handOff.pendingRootSlot with
                                | Some(rootSlot) ->
                                    match {{flag}}(handOff)(rootSlot)(acceptsFlagTemp)(flagged) with
                                        | (registered, flagTemp) -> (registered, argumentTemp, flagTemp, None)
                                | None -> (flagged, argumentTemp, acceptsFlagTemp, None)
                """
            : $$"""
                match {{emit}}(functionTemp)(state) with
                        | (flagged, acceptsFlagTemp) -> (flagged, argumentTemp, acceptsFlagTemp, None)
                """;
        return $$"""
            type {{handOff}} =
                | pendingRootSlot: Maybe(Int)
                | mayReach: Bool

            type {{state}} =
                | counter: Int
                | instructions: List(Int)
                | pending: List((Int, Int))

            let {{emit}} (closureTemp: Int) (state: {{state}}) =
                (state with counter = state.counter + {{LeakNames.Text(names.Between(1, 9))}}, instructions = [closureTemp, state.counter])
                |> (given (flagged) -> (flagged, state.counter + {{LeakNames.Text(names.Between(1, 9))}}))

            let {{flag}} (handOff: {{handOff}}) (rootSlot: Int) (acceptsFlagTemp: Int) (state: {{state}}) =
                match if handOff.mayReach
                then {{emit}}(rootSlot)(state)
                else (state, acceptsFlagTemp) with
                    | (flagged, flagTemp) -> ((flagged with pending = [(flagTemp, rootSlot)]), flagTemp)

            let {{prepare}} (handOff: {{handOff}}) (functionTemp: Int) (argumentTemp: Int) (state: {{state}}) =
                {{prepareBody}}

            let recursive {{steps}} (n: Int) (state: {{state}}) (total: Int) =
                if n == 0
                then total + state.counter
                else
                    match {{prepare}}({{handOff}}(pendingRootSlot = Some(n % {{LeakNames.Text(names.Between(3, 7))}}), mayReach = n % 2 == 0))(n)(n + 1)(state) with
                        | (next, passed, flagged, _handed) -> {{steps}}(n - 1)(next)(total + passed % 3 + flagged % 2)

            let {{names.Entry}} (seed: Int) = {{steps}}({{LeakNames.Text(names.Between(2, 5))}})({{state}}(counter = seed, instructions = [], pending = []))(0)
            """;
    }
}

/// <summary>
/// Shared declarations of the substitution classes: a variable-id supply record and a small ADT the
/// substitution pairs hold, with a counter of the substitution list.
/// </summary>
internal static class LeakSubstitution
{
    internal static string Types(LeakNames names, string sem, string supply, string count) => $$"""
        type {{sem}} =
            | {{names.Of("SemVariable")}}(Int)
            | {{names.Of("SemName")}}(Str)

        type {{supply}} =
            | nextId: Int

        let recursive {{count}} (items: List((Int, {{sem}}))) (total: Int) =
            match items with
                | [] -> total
                | _ :: rest -> {{count}}(rest)(total + 1)
        """;

    /// <summary>A fresh substitution element for the variable id <paramref name="id"/>.</summary>
    internal static string Fresh(LeakNames names, string id) => names.Coin("named-sem")
        ? $"{names.Of("SemName")}(Ashes.Text.fromInt({id}))"
        : $"{names.Of("SemVariable")}({id})";

    /// <summary>
    /// The entry: instantiates a small list of variable ids from <paramref name="initialSupply"/>,
    /// passing <paramref name="trailingArguments"/> after the supply, and counts the substitution.
    /// </summary>
    internal static string Entry(LeakNames names, string instantiate, string supply, string count, string initialSupply, string trailingArguments) => $$"""
        let {{names.Entry}} (seed: Int) =
            match {{instantiate}}({{names.Ints(names.Between(2, 5), 1, 9)}})({{initialSupply}}){{trailingArguments}} with
                | (substitution, {{supply}} { nextId = next }) -> {{count}}(substitution)(0) + next % 2
        """;
}

/// <summary>
/// A function whose nested <c>let recursive</c> helper walks a list and returns the substitution it
/// built paired with the supply record threaded through a pair-returning helper.
/// </summary>
internal sealed class NestedLetRecursiveHelperTemplate() : LeakCombinationTemplate(
    "leak.nested-let-recursive-helper", "nestedLetRecursiveHelper", GeneratedFeature.Record, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string sem = names.Of("LeakSem");
        string supply = names.Of("LeakSupply");
        string count = names.Of("countItems");
        string fresh = names.Of("freshVariable");
        string instantiate = names.Of("instantiateAll");
        string helper = names.Of("go");
        string freshValue = LeakSubstitution.Fresh(names, "nextId");
        return $$"""
            {{LeakSubstitution.Types(names, sem, supply, count)}}

            let {{fresh}} (supply: {{supply}}) =
                match supply with
                    | {{supply}} { nextId = nextId } -> ({{freshValue}}, {{supply}}(nextId = nextId + 1))

            let {{instantiate}} (quantified: List(Int)) (supply: {{supply}}) =
                let recursive {{helper}} items current substitution =
                    match items with
                        | [] -> (substitution, current)
                        | variableId :: tail ->
                            match {{fresh}}(current) with
                                | (freshVariable, nextSupply) -> {{helper}}(tail, nextSupply, (variableId, freshVariable) :: substitution)
                in {{helper}}(quantified, supply, [])

            {{LeakSubstitution.Entry(names, instantiate, supply, count, $"{supply}(nextId = seed)", "")}}
            """;
    }
}

/// <summary>
/// A tail-recursive walk, often with unannotated parameters, returning the substitution list it built
/// paired with its supply state, the supply advanced directly or through helpers.
/// </summary>
internal sealed class HelperReturnsListAndStateTemplate() : LeakCombinationTemplate(
    "leak.helper-returns-list-and-state", "helperReturnsListAndState", GeneratedFeature.Record, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string sem = names.Of("LeakSem");
        string supply = names.Of("LeakSupply");
        string count = names.Of("countItems");
        string instantiate = names.Of("instantiateQuantifiers");
        string fresh = names.Of("freshVariable");
        string step = names.Of("stepSubstitution");
        string freshValue = LeakSubstitution.Fresh(names, "nextId");
        int shape = names.Variant("shape", 4);
        string initialSupply = shape == 1 ? "seed" : $"{supply}(nextId = seed)";
        string walk = Walk(names, shape, sem, supply, instantiate, fresh, step, freshValue);
        return LeakSubstitution.Types(names, sem, supply, count) + "\n\n" + walk + "\n\n" + LeakSubstitution.Entry(names, instantiate, supply, count, initialSupply, "([])");
    }

    // The walk and its helpers: the supply advanced by a pair-returning step helper, passed as an
    // integer and wrapped at the exit, advanced by separate fresh and bump helpers, or advanced by a
    // pair-returning helper matched directly in the walk.
    private static string Walk(LeakNames names, int shape, string sem, string supply, string instantiate, string fresh, string step, string freshValue)
    {
        string freshHelper = $$"""
            let {{fresh}} (supply: {{supply}}) =
                match supply with
                    | {{supply}} { nextId = nextId } -> ({{freshValue}}, {{supply}}(nextId = nextId + 1))
            """;
        return shape switch
        {
            0 => $$"""
                {{freshHelper}}

                let {{step}} variableId supply substitution =
                    match {{fresh}}(supply) with
                        | (freshVariable, nextSupply) -> ((variableId, freshVariable) :: substitution, nextSupply)

                let recursive {{instantiate}} (quantified: List(Int)) (supply: {{supply}}) (substitution: List((Int, {{sem}}))) =
                    match quantified with
                        | [] -> (substitution, supply)
                        | variableId :: tail ->
                            match {{step}}(variableId)(supply)(substitution) with
                                | (nextSubstitution, nextSupply) -> {{instantiate}}(tail, nextSupply, nextSubstitution)
                """,
            1 => $$"""
                let recursive {{instantiate}} quantified supply substitution =
                    match quantified with
                        | [] -> (substitution, {{supply}}(nextId = supply))
                        | variableId :: tail -> {{instantiate}}(tail, supply + 1, (variableId, {{names.Of("SemVariable")}}(supply)) :: substitution)
                """,
            2 => $$"""
                let {{fresh}} (supply: {{supply}}) =
                    match supply with
                        | {{supply}} { nextId = nextId } -> {{freshValue}}

                let {{step}} (supply: {{supply}}) =
                    match supply with
                        | {{supply}} { nextId = nextId } -> {{supply}}(nextId = nextId + 1)

                let recursive {{instantiate}} quantified supply substitution =
                    match quantified with
                        | [] -> (substitution, supply)
                        | variableId :: tail ->
                            let freshVariable = {{fresh}}(supply)
                            in {{instantiate}}(tail, {{step}}(supply), (variableId, freshVariable) :: substitution)
                """,
            _ => $$"""
                {{freshHelper}}

                let recursive {{instantiate}} quantified supply substitution =
                    match quantified with
                        | [] -> (substitution, supply)
                        | variableId :: tail ->
                            match {{fresh}}(supply) with
                                | (freshVariable, nextSupply) -> {{instantiate}}(tail, nextSupply, (variableId, freshVariable) :: substitution)
                """,
        };
    }
}

/// <summary>
/// An unannotated generic helper that prunes a list and returns the survivors paired with a remap
/// list, whose caller rebuilds records from the remap.
/// </summary>
internal sealed class GenericPairPruningTemplate() : LeakCombinationTemplate(
    "leak.generic-pair-pruning", "genericPairPruning", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("LeakKind");
        string load = names.Of("LoadEnv");
        string instruction = names.Of("LeakInstruction");
        string binding = names.Of("LeakBinding");
        string contains = names.Of("containsIndex");
        string collect = names.Of("collectIndices");
        string countOf = names.Of("countOf");
        string prune = names.Of("pruneCaptures");
        string remapped = names.Of("remappedIndex");
        string renumber = names.Of("renumber");
        string pruneDead = names.Of("pruneDeadCaptures");
        string header = names.Coin("annotated-outer")
            ? $"(captures: List({binding})) (instructions: List({instruction}))"
            : "captures instructions";
        int captures = names.Between(3, 4);
        string bindings = "[" + string.Join(", ", Enumerable.Range(0, captures).Select(index => $"{binding}(name = {names.Word()}, index = {LeakNames.Text(index)})")) + "]";
        int kept = names.Between(0, captures - 2);
        string instructions = $"[{instruction}(instruction = {load}(seed, {LeakNames.Text(kept)}), location = Some(seed)), {instruction}(instruction = {names.Of("AddInts")}(seed, seed, 1), location = None), {instruction}(instruction = {names.Of("Named")}({names.Word()}), location = None), {instruction}(instruction = {load}(seed + 1, {LeakNames.Text(kept + 1)}), location = None)]";
        return $$"""
            type {{kind}} =
                | {{load}}(Int, Int)
                | {{names.Of("AddInts")}}(Int, Int, Int)
                | {{names.Of("Named")}}(Str)

            type {{instruction}} =
                | instruction: {{kind}}
                | location: Maybe(Int)

            type {{binding}} =
                | name: Str
                | index: Int

            {{PruningHelpers(names, instruction, load, contains, collect, countOf, prune, remapped, renumber)}}

            let {{pruneDead}} {{header}} =
                (let usedIndices = {{collect}}(instructions)([])
                in
                    if {{countOf}}(usedIndices) >= {{countOf}}(captures)
                    then (captures, instructions)
                    else
                        match {{prune}}(captures)(usedIndices)(0)(0)([])([]) with
                            | (survivors, remap) -> (survivors, {{renumber}}(instructions)(remap)([])))

            let {{names.Entry}} (seed: Int) =
                match {{pruneDead}}({{bindings}})({{instructions}}) with
                    | (survivors, pruned) -> {{countOf}}(survivors) + {{countOf}}(pruned)
            """;
    }

    private static string PruningHelpers(LeakNames names, string instruction, string load, string contains, string collect, string countOf, string prune, string remapped, string renumber) => $$"""
        let recursive {{contains}} (index: Int) (items: List(Int)) =
            match items with
                | [] -> false
                | head :: rest ->
                    if head == index
                    then true
                    else {{contains}}(index)(rest)

        let recursive {{collect}} (instructions: List({{instruction}})) (acc: List(Int)) =
            match instructions with
                | [] -> acc
                | {{instruction}} { instruction = {{load}}(_target, index) } :: rest ->
                    if {{contains}}(index)(acc)
                    then {{collect}}(rest)(acc)
                    else {{collect}}(rest)(index :: acc)
                | _ :: rest -> {{collect}}(rest)(acc)

        let recursive {{countOf}} items =
            match items with
                | [] -> 0
                | _ :: rest -> 1 + {{countOf}}(rest)

        let recursive {{prune}} captures usedIndices (index: Int) (nextIndex: Int) survivors remap =
            match captures with
                | [] -> (Ashes.Collection.List.reverse(survivors), Ashes.Collection.List.reverse(remap))
                | capture :: rest ->
                    if {{contains}}(index)(usedIndices)
                    then {{prune}}(rest)(usedIndices)(index + 1)(nextIndex + 1)(capture :: survivors)((index, nextIndex) :: remap)
                    else {{prune}}(rest)(usedIndices)(index + 1)(nextIndex)(survivors)(remap)

        let recursive {{remapped}} (index: Int) (remap: List((Int, Int))) =
            match remap with
                | [] -> index
                | (oldIndex, newIndex) :: rest ->
                    if oldIndex == index
                    then newIndex
                    else {{remapped}}(index)(rest)

        let recursive {{renumber}} instructions remap acc =
            match instructions with
                | [] -> Ashes.Collection.List.reverse(acc)
                | {{instruction}} { instruction = {{load}}(target, index), location = location } :: rest ->
                    {{renumber}}(rest)(remap)({{instruction}}(instruction = {{load}}(target)({{remapped}}(index)(remap)), location = location) :: acc)
                | head :: rest -> {{renumber}}(rest)(remap)(head :: acc)
        """;
}

/// <summary>
/// A tuple bound by <c>let</c> from an <c>if</c> whose arms destructure helper pairs, destructured in
/// turn and its parts stored in the result tuple.
/// </summary>
internal sealed class HeaderTupleDestructuredTemplate() : LeakCombinationTemplate(
    "leak.header-tuple-destructured", "headerTupleDestructured", GeneratedFeature.Tuple, GeneratedFeature.Let, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string alias = names.Of("LeakTokens");
        string advance = names.Of("advance");
        string currentIs = names.Of("currentIs");
        string parameters = names.Of("parameters");
        string parse = names.Of("parseBinding");
        IEnumerable<string> header = names.Coin("annotated-header")
            ? ["\":\"", names.Word()]
            : Enumerable.Range(0, names.Between(1, 3)).Select(_ => names.Word());
        IEnumerable<string> tokens = [names.Word(), .. header, "\"=\"", names.Word(), .. Enumerable.Range(0, names.Between(0, 2)).Select(_ => names.Word())];
        return $$"""
            type alias {{alias}} = (List(Str), Int)

            let {{advance}} (state: {{alias}}) =
                match state with
                    | (token :: tail, count) -> (token, (tail, count + 1))
                    | ([], _count) -> ("", state)

            let {{currentIs}} (text: Str) (state: {{alias}}) =
                match state with
                    | (token :: _, _count) -> token == text
                    | ([], _count) -> false

            let recursive {{parameters}} (reversed: List(Str)) (state: {{alias}}) =
                if {{currentIs}}("=")(state)
                then (Ashes.Collection.List.reverse(reversed), state)
                else
                    match {{advance}}(state) with
                        | (name, afterName) -> {{parameters}}(name :: reversed)(afterName)

            let {{parse}} (state: {{alias}}) =
                match {{advance}}(state) with
                    | (name, afterName) ->
                        let header =
                            if {{currentIs}}(":")(afterName)
                            then
                                match {{advance}}(afterName) with
                                    | (annotation, afterAnnotation) -> ([], Some(annotation), [], afterAnnotation)
                            else
                                match {{parameters}}([])(afterName) with
                                    | (names, afterParameters) -> (names, None, [], afterParameters)
                        in
                            match header with
                                | (names, annotation, requirements, afterHeader) ->
                                    match {{advance}}(afterHeader) with
                                        | (_equals, afterEquals) ->
                                            match {{advance}}(afterEquals) with
                                                | (value, afterValue) -> (name + value + Ashes.Text.fromInt(Ashes.Collection.List.length(names) + Ashes.Collection.List.length(requirements)), annotation, afterValue)

            let {{names.Entry}} (seed: Int) =
                match {{parse}}(([{{string.Join(", ", tokens)}}], seed)) with
                    | (text, _annotation, (_rest, count)) -> count + Ashes.Text.byteLength(text)
            """;
    }
}

/// <summary>
/// A loop threading a <c>(List(Str), Int)</c> state it matches directly, passing on the tail of the
/// state's list in a fresh tuple and returning the state beside an accumulator.
/// </summary>
internal sealed class TupleStateSuccessorTemplate() : LeakCombinationTemplate(
    "leak.tuple-state-successor", "tupleStateSuccessor", GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string alias = names.Of("LeakState");
        string parameters = names.Of("parameters");
        string stop = names.Word();
        IEnumerable<string> tokens = [.. Enumerable.Range(0, names.Between(1, 4)).Select(_ => names.Word()), stop, names.Word()];
        string exitArm = names.Coin("literal-head-exit")
            ? $$"""
                        | ({{stop}} :: _, _count) -> (reversed, state)
                        | (token :: tail, count) -> {{parameters}}(token :: reversed)((tail, count + 1))
                """
            : $$"""
                        | (token :: tail, count) ->
                            if token == {{stop}}
                            then (reversed, state)
                            else {{parameters}}(token :: reversed)((tail, count + 1))
                """;
        return $$"""
            type alias {{alias}} = (List(Str), Int)

            let recursive {{parameters}} (reversed: List(Str)) (state: {{alias}}) =
                match state with
            {{exitArm}}
                    | ([], _count) -> (reversed, state)

            let {{names.Entry}} (seed: Int) =
                match {{parameters}}([])(([{{string.Join(", ", tokens)}}], seed)) with
                    | (names, _after) -> Ashes.Collection.List.length(names)
            """;
    }
}

/// <summary>
/// An optimizer-pass shape: a walk over records that matches a helper's pair of state and rewritten
/// record, consing the rewritten record onto its accumulator.
/// </summary>
internal sealed class PassStateAndRewritePairTemplate() : LeakCombinationTemplate(
    "leak.pass-state-and-rewrite-pair", "passStateAndRewrite", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.AsPattern)
{
    protected override string Declarations(LeakNames names)
    {
        string where = names.Of("LeakWhere");
        string instr = names.Of("LeakInstr");
        string pass = names.Of("PassState");
        string step = names.Of("step");
        string walk = names.Of("pass");
        string build = names.Of("build");
        string weigh = names.Of("weigh");
        string empty = names.Of("emptyState");
        int label = names.Between(5, 7);
        bool threaded = names.Coin("threaded-state");
        string header = $$"""
            type {{where}} =
                | line: Int
                | column: Int

            type {{instr}} =
                | kind: Int
                | target: Int
                | location: {{where}}

            type {{pass}} =
                | cache: List((Int, Int))
                | fresh: List(Int)
                | seen: Int

            let {{empty}} = {{pass}}(cache = [], fresh = [], seen = 0)
            """;
        string stepAndWalk = StepAndWalk(names, threaded, label, where, instr, pass, step, walk, empty);
        string initialState = threaded ? $"({empty})" : "";
        return $$"""
            {{header}}

            {{stepAndWalk}}

            let recursive {{build}} (n: Int) (acc: List({{instr}})) =
                if n == 0
                then acc
                else {{build}}(n - 1)({{instr}}(kind = n % 8, target = n, location = {{where}}(line = n, column = {{LeakNames.Text(names.Between(1, 4))}})) :: acc)

            let recursive {{weigh}} (xs: List({{instr}})) (total: Int) =
                match xs with
                    | [] -> total
                    | {{instr}} { kind = kind, target = target, location = location } :: rest -> {{weigh}}(rest)(total + kind + target + location.line)

            let {{names.Entry}} (seed: Int) = {{weigh}}({{walk}}({{build}}({{LeakNames.Text(names.Between(3, 8))}})([])){{initialState}}([]))(seed)
            """;
    }

    // The step and the walk: a step returning the pass state it updated beside the rewritten record,
    // threaded through the walk and reset at a label, or a step returning a scalar beside it.
    private static string StepAndWalk(LeakNames names, bool threaded, int label, string where, string instr, string pass, string step, string walk, string empty) =>
        threaded
            ? $$"""
                let {{step}} (kind: Int) (target: Int) (location: {{where}}) (state: {{pass}}) =
                    match kind with
                        | 0 -> ((state with fresh = target :: state.fresh), {{instr}}(kind = kind, target = target, location = location))
                        | 1 -> ((state with cache = (target, kind) :: state.cache), {{instr}}(kind = {{LeakNames.Text(names.Between(8, 9))}}, target = target, location = location))
                        | _ -> (state, {{instr}}(kind = kind, target = target, location = location))

                let recursive {{walk}} (instructions: List({{instr}})) (state: {{pass}}) (acc: List({{instr}})) =
                    match instructions with
                        | [] -> Ashes.Collection.List.reverse(acc)
                        | ({{instr}} { kind = {{LeakNames.Text(label)}} } as label) :: tail -> {{walk}}(tail)({{empty}})(label :: acc)
                        | {{instr}} { kind = kind, target = target, location = location } :: tail ->
                            match {{step}}(kind)(target)(location)(state) with
                                | (nextState, rewritten) -> {{walk}}(tail)(nextState)(rewritten :: acc)
                """
            : $$"""
                let {{step}} (kind: Int) (target: Int) (location: {{where}}) = (target, {{instr}}(kind = kind + {{LeakNames.Text(names.Between(1, 3))}}, target = target, location = location))

                let recursive {{walk}} (instructions: List({{instr}})) (acc: List({{instr}})) =
                    match instructions with
                        | [] -> Ashes.Collection.List.reverse(acc)
                        | {{instr}} { kind = kind, target = target, location = location } :: tail ->
                            match {{step}}(kind)(target)(location) with
                                | (_n, rewritten) -> {{walk}}(tail)(rewritten :: acc)
                """;
}

/// <summary>
/// A list built by a call and passed to a helper that returns it inside a tuple beside the helper's
/// other parameter, the caller reading the list back out of the tuple.
/// </summary>
internal sealed class ListParameterInTupleResultTemplate() : LeakCombinationTemplate(
    "leak.list-parameter-in-tuple-result", "listParameterInTuple", GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string with = names.Of("withTokens");
        string build = names.Of("build");
        string tokens = names.Of("tokensOf");
        string alias = names.Of("LeakParserState");
        int length = names.Between(3, 8);
        string builder = $$"""
            let recursive {{build}} (n: Int) (acc: List(Int)) =
                if n == 0
                then acc
                else {{build}}(n - 1)(n :: acc)
            """;
        if (names.Variant("shape", 2) == 0)
        {
            return $$"""
                let {{with}} (state: Int) (tokens: List(Int)) =
                    match state with
                        | flag -> (tokens, flag)

                {{builder}}

                let {{names.Entry}} (seed: Int) =
                    match []
                    |> {{build}}({{LeakNames.Text(length)}})
                    |> {{with}}(seed) with
                        | (tokens, flag) -> Ashes.Collection.List.length(tokens) + flag
                """;
        }

        return $$"""
            type alias {{alias}} = (List(Int), List(Str), Bytes, Bool)

            let {{with}} (state: {{alias}}) (tokens: List(Int)) =
                match state with
                    | (_old, notes, source, flag) -> (tokens, notes, source, flag)

            let {{tokens}} (state: {{alias}}) =
                match state with
                    | (tokens, _notes, _source, _flag) -> tokens

            {{builder}}

            let {{names.Of("parseOne")}} (state: {{alias}}) =
                (let temporary = Ashes.Collection.List.append({{build}}({{LeakNames.Text(length)}})([]))([0])
                in
                    let temporaryState = {{with}}(state)(temporary)
                    in
                        temporaryState
                        |> {{tokens}}
                        |> Ashes.Collection.List.length)

            let {{names.Entry}} (seed: Int) = {{names.Of("parseOne")}}(([seed], {{names.Words(names.Between(0, 2))}}, Ashes.Byte.fromText({{names.Word()}}), false))
            """;
    }
}
