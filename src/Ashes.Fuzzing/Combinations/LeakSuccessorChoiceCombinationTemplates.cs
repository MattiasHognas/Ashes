using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a list of lists that hands on a choice between cons chains onto what is left of the
/// list (<c>walk(if x &gt; 2 then (x - 1 :: xs) :: rest else xs :: rest)</c>). Each arm alone makes
/// the list a choice accumulator placed on the reference-counted heap; the choice did not, and the
/// arena cells kept the references they took for the tuple's parts. An arm handing on the bare tail
/// (<c>else rest</c>) retains it, and the join counts that retain as its own.
/// </summary>
internal sealed class ConsedTailChoiceTemplate() : LeakCombinationTemplate(
    "leak.consed-tail-choice", "consedTailChoice", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakKept");
        string walk = names.Of("walk");
        string fresh = names.Coin("chain") ? "x - 1 :: 0 :: xs" : "x - 1 :: xs";
        string thenArm = names.Coin("freshThen") ? $"({fresh}) :: rest" : "xs :: rest";
        string elseArm = names.Coin("tailElse") ? "rest" : $"({fresh}) :: rest";
        bool list = names.Coin("listSibling");
        string listParameter = list ? " (spare: List(Int))" : string.Empty;
        string listScrutinee = list ? ", spare" : string.Empty;
        string listPattern = list ? ", s" : string.Empty;
        string listExitPattern = list ? ", s" : string.Empty;
        string listArgument = list ? "(s)" : string.Empty;
        string listExit = list ? " + Ashes.Collection.List.length(s)" : string.Empty;
        string listValue = list ? $"([seed, {LeakNames.Text(names.Between(1, 9))}])" : string.Empty;
        string first = $"[{LeakNames.Text(names.Between(3, 5))}, {LeakNames.Text(names.Between(1, 9))}, seed]";
        string second = $"[{LeakNames.Text(names.Between(1, 9))}, seed + 1]";
        return $$"""
            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{walk}} (items: List(List(Int))) (kept: {{value}}){{listParameter}} (acc: Int) =
                match (items, kept{{listScrutinee}}) with
                    | ((x :: xs) :: rest, same{{listPattern}}) ->
                        if x > 0
                        then {{walk}}(if x > 2 then {{thenArm}} else {{elseArm}})(same){{listArgument}}(acc + x)
                        else {{walk}}(xs :: rest)(same){{listArgument}}(acc)
                    | (_, {{value}} { state = st, temp = k }{{listExitPattern}}) -> acc + k + Ashes.Collection.List.length(st){{listExit}}

            let {{names.Entry}} (seed: Int) =
                {{walk}}([{{first}}, {{second}}])({{value}}(state = [seed, {{LeakNames.Text(names.Between(1, 9))}}], temp = seed)){{listValue}}(seed)
            """;
    }
}

/// <summary>
/// A loop over a list of lists that hands the tail of the first inner list to a sibling list of
/// scalars (<c>((x :: xs) :: rest, s) -&gt; walk((x - 1 :: s) :: rest)(xs)</c>), and that sibling
/// on as itself elsewhere. The match retained the tail for the arm; the sibling, left in the arena,
/// kept that reference, and the value it replaced was never released.
/// </summary>
internal sealed class InnerTailToSiblingTemplate() : LeakCombinationTemplate(
    "leak.inner-tail-to-sibling", "innerTailToSibling", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string value = names.Of("LeakKept");
        string walk = names.Of("walk");
        string head = names.Coin("spareInHead") ? "x - 1 :: s" : "x - 1 :: []";
        bool record = names.Coin("recordSibling");
        string extraParameter = record ? $" (kept: {value})" : string.Empty;
        string extraScrutinee = record ? ", kept" : string.Empty;
        string extraPattern = record ? ", same" : string.Empty;
        string extraExitPattern = record ? $", {value} {{ state = st, temp = k }}" : string.Empty;
        string extraArgument = record ? "(same)" : string.Empty;
        string extraExit = record ? " + k + Ashes.Collection.List.length(st)" : string.Empty;
        string extraValue = record ? $"({value}(state = [seed, {LeakNames.Text(names.Between(1, 9))}], temp = seed))" : string.Empty;
        string first = $"[{LeakNames.Text(names.Between(2, 4))}, {LeakNames.Text(names.Between(1, 9))}, seed]";
        string second = $"[{LeakNames.Text(names.Between(1, 9))}, seed]";
        return $$"""
            type {{value}} =
                | state: List(Int)
                | temp: Int

            let recursive {{walk}} (items: List(List(Int))) (spare: List(Int)){{extraParameter}} (acc: Int) =
                match (items, spare{{extraScrutinee}}) with
                    | ((x :: xs) :: rest, s{{extraPattern}}) ->
                        if x > 0
                        then {{walk}}(({{head}}) :: rest)(xs){{extraArgument}}(acc + x)
                        else {{walk}}(xs :: rest)(s){{extraArgument}}(acc)
                    | (_, s{{extraExitPattern}}) -> acc + Ashes.Collection.List.length(s){{extraExit}}

            let {{names.Entry}} (seed: Int) =
                {{walk}}([{{first}}, {{second}}])([seed, {{LeakNames.Text(names.Between(1, 9))}}, 8]){{extraValue}}(seed)
            """;
    }
}

/// <summary>
/// A loop that grows a list held in a record parameter by a cons written as a field of a record
/// literal successor (<c>walk(rest)(State(seen = name :: state.seen, count = state.count))</c>).
/// The back edge copies the arena record, string heads copied outright and other parts retained by
/// the copy; the references the arena cell took for its head and tail were never released.
/// </summary>
internal sealed class RecordLiteralFieldConsTemplate() : LeakCombinationTemplate(
    "leak.record-literal-field-cons", "recordLiteralFieldCons", GeneratedFeature.Record, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("LeakItem");
        string state = names.Of("LeakState");
        string build = names.Of("build");
        string walk = names.Of("walk");
        bool adt = names.Coin("adtElement");
        string element = adt ? item : "Str";
        string fieldValue = names.Coin("tailField") ? "name :: rest" : "name :: state.seen";
        string count = names.Coin("countField") ? "state.count + 1" : "state.count";
        string current = names.Coin("currentRead") ? "state.current" : "None";
        string first = adt ? $"{item}(\"coal\" + Ashes.Text.fromInt(n))" : "\"coal\" + Ashes.Text.fromInt(n)";
        string second = adt ? $"{item}(\"ember{LeakNames.Text(names.Between(1, 9))}\")" : $"\"ember\" + Ashes.Text.fromInt(n + {LeakNames.Text(names.Between(1, 9))})";
        return $$"""
            type {{item}} =
                | {{item}}(Str)

            type {{state}} =
                | seen: List({{element}})
                | count: Int
                | current: Maybe(Int)

            let recursive {{walk}} (items: List({{element}})) (state: {{state}}) =
                match items with
                    | [] -> Ashes.Collection.List.length(state.seen) + state.count
                    | name :: rest -> {{walk}}(rest)({{state}}(seen = {{fieldValue}}, count = {{count}}, current = {{current}}))

            let {{build}} (n: Int) = [{{first}}, {{second}}]

            let {{names.Entry}} (seed: Int) = {{walk}}({{build}}(seed))({{state}}(seen = [], count = seed, current = None))
            """;
    }
}
