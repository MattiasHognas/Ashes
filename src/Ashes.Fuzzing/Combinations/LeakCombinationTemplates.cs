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
        registry.Register(new TupleResultWithMaybeTemplate());
        registry.Register(new NestedLetRecursiveHelperTemplate());
        registry.Register(new HelperReturnsListAndStateTemplate());
        registry.Register(new ExitArmRecordHoldsLoopParameterTemplate());
        registry.Register(new AccumulatorFromCallResultPartTemplate());
        registry.Register(new VariantAccumulatorThroughHelperTemplate());
        registry.Register(new WorklistSolveRecordsTemplate());
        registry.Register(new GenericPairPruningTemplate());
        registry.Register(new HeaderTupleDestructuredTemplate());
        registry.Register(new PassStateAndRewritePairTemplate());
        registry.Register(new ListParameterInTupleResultTemplate());
        registry.Register(new NestedGroupReplacedByHelperTemplate());
        registry.Register(new LoweringStatePipelineTemplate());
        registry.Register(new RecordUpdateSingleCellTemplate());
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
        return new GeneratedDeclarations(parsed.Items, entry, new GenerationTrace([$"leak-declarations:{Id}", .. names.Choices]));
    }

    /// <summary>
    /// The program's declarations, ending with <c>let {names.Entry} (seed: Int) = ...</c>.
    /// </summary>
    protected abstract string Declarations(LeakNames names);

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
