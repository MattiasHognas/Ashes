using System.Globalization;
using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// Registers the leak templates: each generates a parametric program of one class of reference-count
/// leak, whose work runs once per evaluation of the case's value, so the leak census sees it grow.
/// </summary>
internal static class LeakCombinationTemplates
{
    internal static void RegisterAll(CombinationRegistry registry)
    {
        registry.Register(new RecordUpdateSuccessorTemplate());
        registry.Register(new MapThreadedWhileWalkingTemplate());
        registry.Register(new LoopParameterReturnedWholeTemplate());
        registry.Register(new AccumulatorConsesCallResultPartTemplate());
        registry.Register(new MappedNullaryEnumListTemplate());
        registry.Register(new ConsChainAccumulatorTemplate());
        registry.Register(new ParameterConsedOntoAccumulatorTemplate());
        registry.Register(new PairwiseHeadHandedOnTemplate());
        registry.Register(new LoopParameterTheBodyNeverReadsTemplate());
        registry.Register(new LoopExitCellOntoParameterTemplate());
        registry.Register(new AccumulatorStartedAfreshTemplate());
        registry.Register(new WorklistOfPathsWalkedAndRefilledTemplate());
        registry.Register(new CallResultOntoChoiceAccumulatorTemplate());
        registry.Register(new ExitCellPassedToCallTemplate());
        registry.Register(new AccumulatorResetByLiteralOrChoiceTemplate());
        registry.Register(new TupleResultWithMaybeTemplate());
        registry.Register(new NestedLetRecursiveHelperTemplate());
        registry.Register(new HelperReturnsListAndStateTemplate());
        registry.Register(new ExitArmRecordHoldsLoopParameterTemplate());
        registry.Register(new AccumulatorFromCallResultPartTemplate());
        registry.Register(new LoopRecordCellHoldingParameterFieldTemplate());
        registry.Register(new SelfCallBesideErrorArmTemplate());
        registry.Register(new VariantAccumulatorThroughHelperTemplate());
        registry.Register(new WorklistSolveRecordsTemplate());
        registry.Register(new TableMergedOrHandedOnTemplate());
        registry.Register(new VariantNodeHoldingWalkedHeadTemplate());
        registry.Register(new PatternStringTestedByCurriedCallTemplate());
        registry.Register(new PatternListPassedToBorrowingCallTemplate());
        registry.Register(new MmapViewPerCallTemplate());
        registry.Register(new BuiltinResultsPerCallTemplate());
        registry.Register(new GenericPairPruningTemplate());
        registry.Register(new HeaderTupleDestructuredTemplate());
        registry.Register(new TupleStateSuccessorTemplate());
        registry.Register(new UnannotatedNestedRecursionConcatTemplate());
        registry.Register(new GenericResultBesideOwnedRecordTemplate());
        registry.Register(new TupleResultHoldsOwnedPartTemplate());
        registry.Register(new PhantomTupleResultFreshRecordTemplate());
        registry.Register(new GenericTupleResultContractPartTemplate());
        registry.Register(new PassStateAndRewritePairTemplate());
        registry.Register(new ListParameterInTupleResultTemplate());
        registry.Register(new NestedGroupReplacedByHelperTemplate());
        registry.Register(new LoweringStatePipelineTemplate());
        registry.Register(new RecordUpdateSingleCellTemplate());
        registry.Register(new UnannotatedLoopStateInExitTupleTemplate());
        registry.Register(new NestedItemLoopPendingBlocksStateTemplate());
        registry.Register(new EntryTrailingPipeFreshArgumentTemplate());
        registry.Register(new LoopExitHeadUnderNestedPatternTemplate());
        registry.Register(new BorrowedChildOfLiteralSuccessorTemplate());
        registry.Register(new WorklistFrontWalkedAndRefilledTemplate());
        registry.Register(new WorklistBackOnlyRefilledTemplate());
        registry.Register(new WorklistBackGrownAndEmptiedTemplate());
        registry.Register(new ChoiceAccumulatorBorrowedHeadTemplate());
        registry.Register(new SpineHeadBoundByLetTemplate());
        registry.Register(new ClosureConsesItsParameterTemplate());
        registry.Register(new SelfCallTailUnderOwnedHeadTemplate());
        registry.Register(new AccumulatorReplacedByHelperInOneArmTemplate());
        registry.Register(new FixpointComparesRebuiltListTemplate());
        registry.Register(new PairwiseConcatAccumulatorTemplate());
        registry.Register(new PipedClosuresConsRecordTemplate());
        registry.Register(new PipedLambdaReturnsCallResultTemplate());
    }
}

/// <summary>
/// The random choices of one leak program: a name suffix shared by its declarations, and small
/// constants, words, and variants. Every choice made through it is recorded for the case's trace.
/// </summary>
internal sealed class LeakNames
{
    private static readonly string[] WordPool = ["ash", "ember", "coal", "flint", "smoke", "spark", "cinder", "soot"];
    private readonly FuzzRandom _random;
    private readonly string _suffix;
    private readonly List<string> _choices = [];

    internal LeakNames(FuzzRandom random, string entryStem)
    {
        _random = random;
        _suffix = (random.Next(9000) + 1000).ToString(CultureInfo.InvariantCulture);
        Entry = entryStem + "Run" + _suffix;
    }

    internal string Entry { get; }

    internal IReadOnlyList<string> Choices => _choices;

    /// <summary>A declared name: the stem with the program's suffix.</summary>
    internal string Of(string stem) => stem + _suffix;

    internal string Pick(params string[] choices) => choices[_random.Next(choices.Length)];

    internal int Between(int low, int high) => low + _random.Next(high - low + 1);

    /// <summary>Chooses one of <paramref name="count"/> variants and records which.</summary>
    internal int Variant(string label, int count)
    {
        int variant = _random.Next(count);
        _choices.Add(label + "=" + variant.ToString(CultureInfo.InvariantCulture));
        return variant;
    }

    internal bool Coin(string label) => Variant(label, 2) == 1;

    internal string Word() => "\"" + Pick(WordPool) + "\"";

    internal string Words(int count) => "[" + string.Join(", ", Enumerable.Range(0, count).Select(_ => Word())) + "]";

    /// <summary><paramref name="count"/> different quoted words, at most the size of the word pool.</summary>
    internal IReadOnlyList<string> DistinctWords(int count)
    {
        List<string> pool = [.. WordPool];
        List<string> chosen = [];
        while (chosen.Count < count && pool.Count > 0)
        {
            int index = _random.Next(pool.Count);
            chosen.Add("\"" + pool[index] + "\"");
            pool.RemoveAt(index);
        }

        return chosen;
    }

    internal string Ints(int count, int low, int high) =>
        "[" + string.Join(", ", Enumerable.Range(0, count).Select(_ => Text(Between(low, high)))) + "]";

    internal static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A leak template: its declarations are one parametric program of a leak class, written as Ashes
/// source and parsed, with an entry function <c>Int -&gt; Int</c> that does the program's work; its
/// expression applies the entry function, so the work runs each time the case's value is evaluated.
/// </summary>
internal abstract class LeakCombinationTemplate : IDeclaringCombinationTemplate
{
    private const int ExpressionNodes = 3;
    private readonly string _entryStem;

    protected LeakCombinationTemplate(string id, string entryStem, params GeneratedFeature[] features)
    {
        Id = id;
        _entryStem = entryStem;
        AdvertisedFeatures = new SortedSet<GeneratedFeature>(
            [GeneratedFeature.Call, GeneratedFeature.Literal, GeneratedFeature.Variable, GeneratedFeature.TopLevelFunction, GeneratedFeature.RecursiveFunction, .. features]);
    }

    public string Id { get; }

    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; }

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        resultType == AshesType.Int && budget.RemainingNodes >= ExpressionNodes && EntryIn(context) is not null;

    public GenerationResult<Expr> Generate(AshesType resultType, GenerationContext context, GenerationBudget budget, ExpressionGenerator expressions, FuzzRandom random)
    {
        GeneratedBinding entry = EntryIn(context)
            ?? throw new InvalidOperationException($"Combination '{Id}' has no entry declaration in scope.");
        Expr call = new Expr.Call(new Expr.Var(entry.Name), new Expr.IntLit(random.Next(9) + 1));
        return new GenerationResult<Expr>(call, AshesType.Int, new GeneratedFeatureSet(AdvertisedFeatures), new GenerationTrace([$"leak:{entry.Name}"]), ExpressionNodes);
    }

    public GeneratedDeclarations Declare(FuzzRandom random)
    {
        LeakNames names = new(random, _entryStem);
        string source = Declarations(names);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program parsed = new Parser(source, diagnostics).ParseProgram();
        if (diagnostics.Errors.Count != 0)
        {
            throw new InvalidOperationException($"Combination '{Id}' declared unparsable source: {string.Join(" ", diagnostics.Errors)}{Environment.NewLine}{source}");
        }

        GeneratedBinding entry = new(names.Entry, new AshesType.Function(AshesType.Int, AshesType.Int), IsFunction: true);
        return new GeneratedDeclarations(parsed.Items, entry, new GenerationTrace([$"leak-declarations:{Id}", .. names.Choices]), TrailingWorkload(names), RequiredImports);
    }

    /// <summary>
    /// The program's declarations, ending with <c>let {names.Entry} (seed: Int) = ...</c>.
    /// </summary>
    protected abstract string Declarations(LeakNames names);

    /// <summary>
    /// A trailing expression the leak census runs in place of its entry-function loop, naming the
    /// rounds count <c>ROUNDS</c>, for a class that leaks only in the program's entry; null when the
    /// entry-function loop is the placement.
    /// </summary>
    protected virtual string? TrailingWorkload(LeakNames names) => null;

    /// <summary>
    /// The modules whose types the declarations name unqualified, imported ahead of the rendered
    /// program: an unimported type is not in scope (the language reference's import rules).
    /// </summary>
    protected virtual IReadOnlyList<string> RequiredImports => [];

    private GeneratedBinding? EntryIn(GenerationContext context) => context.Bindings.FirstOrDefault(binding =>
        binding.IsFunction &&
        binding.Type == new AshesType.Function(AshesType.Int, AshesType.Int) &&
        binding.Name.StartsWith(_entryStem + "Run", StringComparison.Ordinal));
}

/// <summary>
/// A record state whose list field grows by a cons in the record-update successor of a
/// tail-recursive walk.
/// </summary>
internal sealed class RecordUpdateSuccessorTemplate() : LeakCombinationTemplate(
    "leak.record-update-successor", "recordUpdateSuccessor", GeneratedFeature.Record, GeneratedFeature.UniqueConstructorUpdate, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("LeakState");
        string lower = names.Of("lowerItems");
        string build = names.Of("buildItems");
        string list = names.Pick("instructions", "emitted", "pending", "items");
        string counter = names.Pick("counter", "seen", "total");
        bool strings = names.Coin("str-elements");
        bool countsToo = names.Coin("update-counter");
        bool extra = names.Coin("extra-field");
        int length = names.Between(3, 6);
        string element = strings ? "Ashes.Text.fromInt(value)" : "value";
        string extraField = extra ? "\n    | note: Str" : "";
        string extraUpdate = extra && names.Coin("update-extra") ? $", note = {names.Word()}" : "";
        string counterUpdate = countsToo ? $", {counter} = state.{counter} + {LeakNames.Text(names.Between(1, 3))}" : "";
        string extraInit = extra ? $", note = {names.Word()}" : "";
        string items = names.Coin("literal-items") ? names.Ints(length, 1, 9) : $"{build}({LeakNames.Text(length)})([])";
        return $$"""
            type {{state}} =
                | {{list}}: List({{(strings ? "Str" : "Int")}})
                | {{counter}}: Int{{extraField}}

            let recursive {{lower}} (items: List(Int)) (state: {{state}}) =
                match items with
                    | [] -> state.{{counter}} + Ashes.Collection.List.length(state.{{list}})
                    | value :: rest -> {{lower}}(rest)((state with {{list}} = {{element}} :: state.{{list}}{{counterUpdate}}{{extraUpdate}}))

            let recursive {{build}} (n: Int) (built: List(Int)) =
                if n == 0
                then built
                else {{build}}(n - 1)(n :: built)

            let {{names.Entry}} (seed: Int) = {{lower}}({{items}})({{state}}({{list}} = [], {{counter}} = seed{{extraInit}}))
            """;
    }
}

/// <summary>
/// One cell a round: a record walk that updates only a scalar field beside a list field built from a
/// literal, or that replaces the list field by a helper returning either a fresh literal or a cons.
/// </summary>
internal sealed class RecordUpdateSingleCellTemplate() : LeakCombinationTemplate(
    "leak.record-update-single-cell", "recordUpdateSingleCell", GeneratedFeature.Record, GeneratedFeature.UniqueConstructorUpdate, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("LeakState");
        string lower = names.Of("lowerItems");
        string replace = names.Of("replaceNames");
        string list = names.Pick("instructions", "bindings", "names");
        string counter = names.Pick("counter", "seen", "total");
        string items = names.Ints(names.Between(2, 5), 1, 9);
        if (names.Variant("shape", 2) == 0)
        {
            return $$"""
                type {{state}} =
                    | {{list}}: List(Int)
                    | {{counter}}: Int

                let recursive {{lower}} (items: List(Int)) (state: {{state}}) =
                    match items with
                        | [] -> state.{{counter}} + Ashes.Collection.List.length(state.{{list}})
                        | value :: rest -> {{lower}}(rest)((state with {{counter}} = state.{{counter}} + value))

                let {{names.Entry}} (seed: Int) = {{lower}}({{items}})({{state}}({{list}} = {{names.Ints(names.Between(1, 3), 1, 9)}}, {{counter}} = seed))
                """;
        }

        return $$"""
            type {{state}} =
                | {{list}}: List(Str)
                | tag: Str
                | {{counter}}: Int

            let {{replace}} (value: Int) (old: List(Str)) =
                if value % {{LeakNames.Text(names.Between(2, 4))}} == 0
                then {{names.Words(2)}}
                else {{names.Word()}} :: old

            let recursive {{lower}} (items: List(Int)) (state: {{state}}) =
                match items with
                    | [] -> state.{{counter}} + Ashes.Collection.List.length(state.{{list}})
                    | value :: rest -> {{lower}}(rest)((state with {{list}} = {{replace}}(value)(state.{{list}}), {{counter}} = state.{{counter}} + 1))

            let {{names.Entry}} (seed: Int) = {{lower}}({{items}})({{state}}({{list}} = {{names.Words(2)}}, tag = {{names.Word()}}, {{counter}} = seed))
            """;
    }
}

/// <summary>
/// A walk whose record state holds a nested group record that a helper replaces through a let-bound
/// field read and a nested record update.
/// </summary>
internal sealed class NestedGroupReplacedByHelperTemplate() : LeakCombinationTemplate(
    "leak.nested-group-replaced-by-helper", "nestedGroupReplaced", GeneratedFeature.Record, GeneratedFeature.UniqueConstructorUpdate, GeneratedFeature.TailCall, GeneratedFeature.Let)
{
    protected override string Declarations(LeakNames names)
    {
        string inner = names.Of("LeakGroup");
        string state = names.Of("LeakState");
        string replace = names.Of("withSpan");
        string walk = names.Of("walk");
        string group = names.Pick("inner", "group", "scope");
        string counter = names.Pick("counter", "seen", "total");
        bool spanFirst = names.Coin("span-first");
        string span = spanFirst ? "    | span: Maybe(Int)\n    | names: List(Str)" : "    | names: List(Str)\n    | span: Maybe(Int)";
        string spanInit = spanFirst ? $"span = None, names = {names.Words(names.Between(1, 3))}" : $"names = {names.Words(names.Between(1, 3))}, span = None";
        string value = names.Coin("span-value") ? "Some(n)" : $"Some(n + {LeakNames.Text(names.Between(1, 9))})";
        return $$"""
            type {{inner}} =
            {{span}}

            type {{state}} =
                | {{group}}: {{inner}}
                | {{counter}}: Int
                | items: List(Str)

            let {{replace}} (value: Maybe(Int)) (state: {{state}}) =
                (let current = state.{{group}}
                in state with {{group}} = (current with span = value))

            let recursive {{walk}} (n: Int) (state: {{state}}) =
                if n == 0
                then state.{{counter}}
                else
                    (state with {{counter}} = state.{{counter}} + 1)
                    |> {{replace}}({{value}})
                    |> {{walk}}(n - 1)

            let {{names.Entry}} (seed: Int) = {{walk}}({{LeakNames.Text(names.Between(2, 6))}})({{state}}({{group}} = {{inner}}({{spanInit}}), {{counter}} = seed, items = {{names.Words(names.Between(1, 2))}}))
            """;
    }
}

/// <summary>
/// A walk over a list whose exit arm builds a record holding the walk's record parameter, or whose
/// loop state is a record holding it, destructured by the caller.
/// </summary>
internal sealed class ExitArmRecordHoldsLoopParameterTemplate() : LeakCombinationTemplate(
    "leak.exit-arm-record-holds-loop-parameter", "exitArmRecord", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("LeakState");
        string holder = names.Of("LeakHolder");
        string lower = names.Of("lowerArguments");
        string call = names.Of("lowerCall");
        string list = names.Pick("instructions", "emitted", "items");
        string counter = names.Pick("counter", "seen", "total");
        string arguments = names.Ints(names.Between(2, 5), 1, 9);
        string types = $$"""
            type {{state}} =
                | {{list}}: List(Int)
                | {{counter}}: Int

            type {{holder}} =
                | state: {{state}}
                | count: Int
            """;
        string initial = $"{state}({list} = [seed], {counter} = seed)";
        string program = names.Variant("shape", 3) switch
        {
            0 => $$"""
                let recursive {{lower}} (arguments: List(Int)) (state: {{state}}) =
                    match arguments with
                        | [] -> {{holder}}(state = state, count = 0)
                        | _argument :: rest -> {{lower}}(rest)(state)

                let {{call}} (arguments: List(Int)) (state: {{state}}) =
                    match {{lower}}(arguments)(state) with
                        | {{holder}} { state = argumentState, count = count } -> argumentState.{{counter}} + count

                let {{names.Entry}} (seed: Int) = {{call}}({{arguments}})({{initial}})
                """,
            1 => $$"""
                let recursive {{lower}} (arguments: List(Int)) (state: {{state}}) =
                    match arguments with
                        | [] ->
                            match {{holder}}(state = state, count = 0) with
                                | {{holder}} { count = count } -> count + state.{{counter}}
                        | _argument :: rest -> {{lower}}(rest)(state)

                let {{names.Entry}} (seed: Int) = {{lower}}({{arguments}})({{initial}})
                """,
            _ => $$"""
                let recursive {{lower}} (arguments: List(Int)) (current: {{holder}}) =
                    match arguments with
                        | [] -> (let s = current.state in current.count + s.{{counter}})
                        | argument :: rest ->
                            let next = {{holder}}(state = current.state, count = current.count + argument)
                            in {{lower}}(rest)(next)

                let {{names.Entry}} (seed: Int) = {{lower}}({{arguments}})({{holder}}(state = {{initial}}, count = 0))
                """,
        };
        return types + "\n\n" + program;
    }
}

/// <summary>
/// A loop consing a record that holds a field read of its reference-counted state parameter onto a
/// list accumulator, beside the state it updates, with the list either on the reference-counted
/// heap or kept in the arena by another arm that drops its newest cell.
/// </summary>
internal sealed class LoopRecordCellHoldingParameterFieldTemplate() : LeakCombinationTemplate(
    "leak.loop-record-cell-holding-parameter-field", "loopRecordCellField", GeneratedFeature.Record, GeneratedFeature.TailCall, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string close = names.Of("LeakCell");
        string state = names.Of("LeakCellState");
        string count = names.Of("countCells");
        string lower = names.Of("collectCells");
        string bindings = names.Pick("bindings", "names", "scope");
        string update = names.Coin("counter-update")
            ? $"(state with counter = state.counter + {LeakNames.Text(names.Between(1, 5))})"
            : "(state)";
        string grow = $"{lower}(rest)({close}(cellTag = value, cellBindings = state.{bindings}) :: pending){update}";
        // Dropping the newest cell in the other arm keeps the list in the arena, where its cells
        // hold arena copies of the parameter's field.
        string step = names.Coin("arena-list")
            ? $$"""
                if value % 2 == 0
                            then {{grow}}
                            else
                                match pending with
                                    | _ :: older -> {{lower}}(rest)(older)(state)
                                    | [] -> {{lower}}(rest)([])(state)
                """
            : grow;
        return $$"""
            type {{close}} =
                | cellTag: Int
                | cellBindings: List(Str)

            type {{state}} =
                | {{bindings}}: List(Str)
                | counter: Int

            let recursive {{count}} (items: List({{close}})) (total: Int) =
                match items with
                    | [] -> total
                    | _ :: rest -> {{count}}(rest)(total + 1)

            let recursive {{lower}} (items: List(Int)) (pending: List({{close}})) (state: {{state}}) =
                match items with
                    | [] -> state.counter + {{count}}(pending)(0)
                    | value :: rest ->
                        {{step}}

            let {{names.Entry}} (seed: Int) = {{lower}}({{names.Ints(names.Between(2, 5), 1, 9)}})([])({{state}}({{bindings}} = {{names.Words(names.Between(1, 3))}}, counter = seed))
            """;
    }
}

/// <summary>
/// A loop returning a `Result` whose error arm forwards a helper's error, beside a tail self call
/// in the branch lowered first: the loop's error type must come from the forwarded error, not be
/// left a variable, or the error's parts are never released.
/// </summary>
internal sealed class SelfCallBesideErrorArmTemplate() : LeakCombinationTemplate(
    "leak.self-call-beside-error-arm", "selfCallBesideErrorArm", GeneratedFeature.Match, GeneratedFeature.TailCall, GeneratedFeature.If, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string error = names.Of("LeakFailure");
        string check = names.Of("checkItem");
        string walk = names.Of("walkItems");
        string describe = names.Of("describeWalk");
        int limit = names.Between(3, 7);
        string forward = names.Coin("rebuild-error")
            ? $"| Error({error}Bad(at, text)) -> Error({error}Bad(at)(text))"
            : "| Error(failure) -> Error(failure)";
        return $$"""
            type {{error}} =
                | {{error}}Bad(Int, Str)

            let {{check}} (value: Int) =
                if value > {{LeakNames.Text(limit)}}
                then Error({{error}}Bad(value)({{names.Word()}} + Ashes.Text.fromInt(value)))
                else Ok(value)

            let recursive {{walk}} (n: Int) (acc: List(Int)) =
                if n == 0
                then Ok(acc)
                else
                    if n == 100
                    then {{walk}}(n - 1)(acc)
                    else
                        match {{check}}(n) with
                            {{forward}}
                            | Ok(value) -> {{walk}}(n - 1)(value :: acc)

            let {{describe}} result =
                match result with
                    | Ok(items) ->
                        match items with
                            | [] -> 0
                            | head :: _ -> head
                    | Error({{error}}Bad(at, text)) -> at + Ashes.Text.length(text)

            let {{names.Entry}} (seed: Int) = {{describe}}({{walk}}({{LeakNames.Text(limit + 1)}} + seed % 3)([]))
            """;
    }
}

/// <summary>
/// A list accumulator whose successor is matched out of a helper's record result, beside a record
/// state updated in the other arm.
/// </summary>
internal sealed class AccumulatorFromCallResultPartTemplate() : LeakCombinationTemplate(
    "leak.accumulator-from-call-result-part", "accumulatorFromCallResult", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.TailCall, GeneratedFeature.If)
{
    protected override string Declarations(LeakNames names)
    {
        string close = names.Of("LeakClose");
        string state = names.Of("LeakState");
        string opened = names.Of("LeakOpened");
        string open = names.Of("openScope");
        string lower = names.Of("lowerItems");
        string bindings = names.Pick("bindings", "names", "scope");
        int parity = names.Between(2, 3);
        string items = Items(names, parity);
        return $$"""
            type {{close}} =
                | closeName: Maybe(Str)
                | closeBindings: List(Str)

            type {{state}} =
                | {{bindings}}: List(Str)
                | tag: Str
                | counter: Int

            type {{opened}} =
                | openedState: {{state}}
                | openedPending: List({{close}})

            let {{open}} (value: Int) (pending: List({{close}})) (state: {{state}}) =
                {{opened}}(openedState = (state with counter = state.counter + value), openedPending = {{close}}(closeName = Some({{names.Word()}}), closeBindings = state.{{bindings}}) :: pending)

            let recursive {{lower}} (items: List(Int)) (pending: List({{close}})) (state: {{state}}) =
                match items with
                    | [] -> state.counter + Ashes.Collection.List.length(pending)
                    | value :: rest ->
                        if value % {{LeakNames.Text(parity)}} == 0
                        then {{lower}}(rest)({{close}}(closeName = None, closeBindings = state.{{bindings}}) :: pending)((state with tag = {{names.Word()}}))
                        else
                            match {{open}}(value)(pending)(state) with
                                | {{opened}} { openedState = next, openedPending = nextPending } -> {{lower}}(rest)(nextPending)(next)

            let {{names.Entry}} (seed: Int) = {{lower}}({{items}})([])({{state}}({{bindings}} = {{names.Words(2)}}, tag = {{names.Word()}}, counter = seed))
            """;
    }

    // The leak shows once at least three items take the helper arm (a value not divisible by the
    // parity), so three to four of them do, with up to two others placed at random among them.
    private static string Items(LeakNames names, int parity)
    {
        List<int> items = [];
        int opened = names.Between(3, 4);
        int others = names.Between(0, 2);
        while (opened + others > 0)
        {
            bool open = others == 0 || (opened > 0 && names.Between(0, 1) == 0);
            int value = open ? (parity * names.Between(0, 3)) + names.Between(1, parity - 1) : names.Between(1, 9);
            items.Add(value);
            opened -= open ? 1 : 0;
            others -= open ? 0 : 1;
        }

        return "[" + string.Join(", ", items.Select(LeakNames.Text)) + "]";
    }
}

/// <summary>
/// A loop whose parameters are unannotated returns its record state inside a tuple from its base
/// case, lowered before any arm resolves the state's type: the tuple stays in the arena and the
/// retain it takes of the parameter needs an owner that releases it.
/// </summary>
internal sealed class UnannotatedLoopStateInExitTupleTemplate() : LeakCombinationTemplate(
    "leak.unannotated-loop-state-in-exit-tuple", "unannotatedExitTuple", GeneratedFeature.Record, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string state = names.Of("LeakState");
        string step = names.Of("stepState");
        string walk = names.Of("walkItems");
        string count = names.Of("countItems");
        string list = names.Pick("substitution", "instructions", "labels");
        string items = names.Ints(names.Between(2, 5), 1, 9);
        int extra = names.Between(0, 9);
        bool stateFirst = names.Coin("state-first");
        string exitTuple = stateFirst ? $"(state, {LeakNames.Text(extra)})" : $"({LeakNames.Text(extra)}, state)";
        string exitPattern = stateFirst ? "(state, extra)" : "(extra, state)";
        string successor = names.Variant("successor", 3) switch
        {
            0 => $"{step}(value)(state)",
            1 => $"(state |> {step}(value))",
            _ => $"{step}(value)({step}(1)(state))",
        };
        return $$"""
            type {{state}} =
                | {{list}}: List(Int)
                | counter: Int

            let {{step}} (value: Int) (state: {{state}}) = state with {{list}} = value :: state.{{list}}

            let recursive {{walk}} items state =
                match items with
                    | [] -> {{exitTuple}}
                    | value :: rest -> {{walk}}(rest)({{successor}})

            let recursive {{count}} (values: List(Int)) (total: Int) =
                match values with
                    | [] -> total
                    | _ :: rest -> {{count}}(rest)(total + 1)

            let {{names.Entry}} (seed: Int) =
                match {{walk}}({{items}})({{state}}({{list}} = [seed], counter = seed)) with
                    | {{exitPattern}} -> state.counter + extra + {{count}}(state.{{list}})(0)
            """;
    }
}

/// <summary>
/// A loop over nested items whose record state is threaded through a helper, beside a pending
/// list only the helper arms grow: an item wrapped in a span re-enters the loop with a state a
/// call produced, and the pending list, which no placement admits, blocks the release of the
/// state an earlier iteration left.
/// </summary>
internal sealed class NestedItemLoopPendingBlocksStateTemplate() : LeakCombinationTemplate(
    "leak.nested-item-loop-pending-blocks-state", "nestedItemLoop", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.Adt, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("LeakItem");
        string binding = names.Of("LeakBinding");
        string state = names.Of("LeakState");
        string close = names.Of("LeakClose");
        string opened = names.Of("LeakOpened");
        string withSpan = names.Of("withSpan");
        string open = names.Of("openItem");
        string openGroup = names.Of("openGroup");
        string restore = names.Of("restoreBindings");
        string countCloses = names.Of("countCloses");
        string lower = names.Of("lowerItems");
        string program = names.Of("program");
        return $$"""
            type {{item}} =
                | At(Int, {{item}})
                | Plain(Str)
                | Grouped(Str)

            type {{binding}} =
                | name: Str
                | slot: Int

            type {{state}} =
                | bindings: List({{binding}})
                | labels: List(Str)
                | counter: Int

            type {{close}} =
                | closeName: Maybe(Str)
                | closeBindings: List({{binding}})

            type {{opened}} =
                | openedState: {{state}}
                | openedPending: List({{close}})

            let {{withSpan}} (span: Int) (state: {{state}}) = state with counter = state.counter + span

            let {{open}} (name: Str) (pending: List({{close}})) (state: {{state}}) = {{opened}}(openedState = (state with bindings = {{binding}}(name = name, slot = state.counter) :: state.bindings), openedPending = {{close}}(closeName = Some(name), closeBindings = state.bindings) :: pending)

            let {{openGroup}} (name: Str) (state: {{state}}) = state with labels = name :: state.labels

            let {{restore}} (bindings: List({{binding}})) = {{close}}(closeName = None, closeBindings = bindings)

            let recursive {{countCloses}} (closes: List({{close}})) (total: Int) =
                match closes with
                    | [] -> total
                    | _ :: rest -> {{countCloses}}(rest)(total + 1)

            let recursive {{lower}} (items: List({{item}})) (pending: List({{close}})) (state: {{state}}) =
                match items with
                    | [] -> state.counter + {{countCloses}}(pending)(0)
                    | At(span, inner) :: rest ->
                        state
                        |> {{withSpan}}(span)
                        |> {{lower}}(inner :: rest)(pending)
                    | Plain(name) :: rest ->
                        match {{open}}(name)(pending)(state) with
                            | {{opened}} { openedState = opened, openedPending = nextPending } -> {{lower}}(rest)(nextPending)(opened)
                    | Grouped(name) :: rest ->
                        state
                        |> {{openGroup}}(name)
                        |> {{lower}}(rest)({{restore}}(state.bindings) :: pending)

            let {{program}} = [{{Items(names)}}]

            let {{names.Entry}} (seed: Int) = {{lower}}({{program}})([])({{state}}(bindings = [], labels = [], counter = seed))
            """;
    }

    // Four to six items, each a plain or grouped one wrapped in one or two spans.
    private static string Items(LeakNames names)
    {
        List<string> items = [];
        int count = names.Between(4, 6);
        for (int index = 0; index < count; index++)
        {
            string leaf = names.Coin("grouped") ? $"Grouped({names.Word()})" : $"Plain({names.Word()})";
            string wrapped = $"At({LeakNames.Text(index + 1)})({leaf})";
            items.Add(names.Coin("double-span") ? $"At({LeakNames.Text(index + 7)})({wrapped})" : wrapped);
        }

        return string.Join(", ", items);
    }
}


/// <summary>
/// The shared program of the spine-head templates: a record a helper returns consed onto the list a
/// recursive producer builds, by the cons arm the template gives, and the list folded.
/// </summary>
internal static class SpineHeadProgram
{
    internal static string Declarations(LeakNames names, Func<string, string, string> consArm)
    {
        string entry = names.Of("LeakEntry");
        string make = names.Of("makeEntry");
        string step = names.Of("stepEntries");
        string count = names.Of("countEntries");
        string slot = names.Pick("slot", "block", "index");
        string flag = names.Pick("live", "seen", "marked");
        string items = names.Ints(names.Between(2, 5), 1, 9);
        string modulus = LeakNames.Text(names.Between(2, 3));
        return $$"""
            type {{entry}} =
                | {{slot}}: Int
                | {{flag}}: Bool
                | done: Bool

            let {{make}} (value: Int) = {{entry}}({{slot}} = value, {{flag}} = value % {{modulus}} == 0, done = false)

            let recursive {{step}} (remaining: List(Int)) =
                match remaining with
                    | [] -> []
                    | value :: rest ->
                        {{consArm(make, step)}}

            let recursive {{count}} (entries: List({{entry}})) (total: Int) =
                match entries with
                    | [] -> total
                    | {{entry}} { {{slot}} = found } :: rest -> {{count}}(rest)(total + found)

            let {{names.Entry}} (seed: Int) = {{count}}({{step}}({{items}}))(seed)
            """;
    }
}

/// <summary>
/// A record a helper returns, let-bound and consed onto the spine the producer builds in place: the
/// cell retains the binding's value, and the binding's own reference is released with the iteration.
/// </summary>
internal sealed class SpineHeadBoundByLetTemplate() : LeakCombinationTemplate(
    "leak.spine-head-bound-by-let", "spineHeadByLet", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string bound = names.Pick("entry", "made", "cell");
        return SpineHeadProgram.Declarations(names, (make, step) => $"let {bound} = {make}(value)\n                        in {bound} :: {step}(rest)");
    }
}

/// <summary>
/// A closure applied in a pipe conses its parameter onto the producer's recursive tail: the
/// parameter reaches the result only as a list cell's head, so its entry takes no owned copy.
/// </summary>
internal sealed class ClosureConsesItsParameterTemplate() : LeakCombinationTemplate(
    "leak.closure-conses-its-parameter", "closureConsesParameter", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.Lambda, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string parameter = names.Pick("entry", "made", "cell");
        bool pipedValue = names.Coin("piped-value");
        return SpineHeadProgram.Declarations(names, (make, step) => pipedValue
            ? $"value\n                        |> {make}\n                        |> (given ({parameter}) -> {parameter} :: {step}(rest))"
            : $"{make}(value) |> (given ({parameter}) -> {parameter} :: {step}(rest))");
    }
}

/// <summary>
/// A non-tail call of the recursive binding from its own body, let-bound and consed under a record
/// a helper returns owned: the call asks for no arena result, so the reference-counted cell holds a
/// reference-counted tail. Without the rule the program reads freed memory.
/// </summary>
internal sealed class SelfCallTailUnderOwnedHeadTemplate() : LeakCombinationTemplate(
    "leak.self-call-tail-under-owned-head", "selfCallTailUnderHead", GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string tail = names.Pick("tail", "built", "following");
        return SpineHeadProgram.Declarations(names, (make, step) => $"let {tail} = {step}(rest)\n                        in {make}(value) :: {tail}");
    }
}
