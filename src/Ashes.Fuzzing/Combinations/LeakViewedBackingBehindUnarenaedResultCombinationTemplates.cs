using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function that builds a table of string pairs, hands it to a call whose result its scope
/// destructures, and returns a record with a recursive child. The scope's result may view the
/// table's strings and has no arena copy, so it is normalized onto the reference-counted heap and
/// the table released behind it, instead of the table being kept for the program's life.
/// </summary>
internal sealed class ViewedBackingBehindUnarenaedResultTemplate() : LeakCombinationTemplate(
    "leak.viewed-backing-behind-unarenaed-result", "viewedBacking", GeneratedFeature.Adt, GeneratedFeature.Record, GeneratedFeature.RecordPattern, GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string tree = names.Of("Tree");
        string leaf = names.Of("Leaf");
        string node = names.Of("Node");
        string module = names.Of("Module");
        string table = names.Of("table");
        string lookup = names.Of("lookup");
        string pair = names.Of("pairUp");
        string rewrite = names.Of("rewrite");
        string size = names.Of("size");
        string loop = names.Of("loop");
        string word = names.Word();
        string suffix = names.Word();
        string kept = names.Coin("keeps-renamed") ? "renamed" : "original";
        string words = names.Words(names.Between(1, 3));
        return $$"""
            type {{tree}} =
                | {{leaf}}(Str)
                | {{node}}({{tree}}, {{tree}})

            type {{module}} =
                | names: List(Str)
                | tree: {{tree}}

            let recursive {{table}} (items: List(Str)) (acc: List((Str, Str))) =
                match items with
                    | [] -> acc
                    | item :: rest -> {{table}}(rest)((item, item + {{suffix}}) :: acc)

            let recursive {{lookup}} (key: Str) (entries: List((Str, Str))) =
                match entries with
                    | [] -> key
                    | (k, v) :: rest ->
                        if k == key
                        then v
                        else {{lookup}}(key)(rest)

            let {{pair}} (left: List(Str)) (right: List(Str)) = (left, right)

            let {{rewrite}} (value: {{module}}) =
                let known = {{table}}(value.names)([])
                in
                    match value.names
                    |> Ashes.Collection.List.map(given (name: Str) -> {{lookup}}(name)(known))
                    |> {{pair}}(value.names) with
                        | (original, renamed) -> {{module}}(names = {{kept}}, tree = value.tree)

            let recursive {{size}} (value: {{tree}}) (acc: Int) =
                match value with
                    | {{leaf}}(_) -> acc + 1
                    | {{node}}(left, right) -> {{size}}(right)({{size}}(left)(acc))

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else
                    match {{rewrite}}({{module}}(names = {{word}} :: {{words}}, tree = {{node}}({{leaf}}("x"), {{leaf}}("y")))) with
                        | {{module}} { names = found, tree = shape } -> {{loop}}(n - 1)(acc + Ashes.Collection.List.length(found) + {{size}}(shape)(0))

            let {{names.Entry}} (seed: Int) = {{loop}}(3)(seed)
            """;
    }
}
