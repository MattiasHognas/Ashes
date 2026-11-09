using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A recursive function that passes its own non-tail call's list result to a further call, the way
/// <c>appendExports(exportsFor(item))(collectExports(rest))</c> builds a module's export list. The
/// self-call reaches the body still being lowered, so the caller reads the returns bit to learn
/// whether the result is reference-counted; taken unread, the list it handed on stayed with nobody.
/// </summary>
internal sealed class SelfCallResultArgumentTemplate() : LeakCombinationTemplate(
    "leak.self-call-result-argument", "selfCallResult", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string export = names.Of("Export");
        string valueExport = names.Of("ValueExport");
        string typeExport = names.Of("TypeExport");
        string exportsFor = names.Of("exportsFor");
        string append = names.Of("append");
        string collect = names.Of("collect");
        string count = names.Of("count");
        string threshold = names.Between(1, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string tag = names.Pick("v", "ash", "ember");
        bool piped = names.Coin("piped");
        bool typedAppend = names.Coin("typed-append");
        string appendHeader = typedAppend
            ? $"{append} (left: List({export})) (right: List({export}))"
            : $"{append} left right";
        string step = piped
            ? $"rest\n            |> {collect}\n            |> {append}({exportsFor}(item))"
            : $"{append}({exportsFor}(item))({collect}(rest))";
        int width = names.Between(2, 5);
        string items = "[" + string.Join(", ", Enumerable.Range(0, width).Select(index => index == 0 ? "seed" : index.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        return $$"""
            type {{export}} =
                | {{valueExport}}(Str)
                | {{typeExport}}(Str)

            let {{exportsFor}} (item: Int) =
                if item > {{threshold}}
                then [{{valueExport}}("{{tag}}" + Ashes.Text.fromInt(item))]
                else [{{typeExport}}("t" + Ashes.Text.fromInt(item)), {{valueExport}}("c")]

            let recursive {{appendHeader}} =
                match left with
                    | [] -> right
                    | head :: rest -> head :: {{append}}(rest)(right)

            let recursive {{collect}} (items: List(Int)) =
                match items with
                    | [] -> []
                    | item :: rest ->
                        {{step}}

            let recursive {{count}} (xs: List({{export}})) =
                match xs with
                    | [] -> 0
                    | {{valueExport}}(_) :: rest -> 1 + {{count}}(rest)
                    | {{typeExport}}(_) :: rest -> 2 + {{count}}(rest)

            let {{names.Entry}} (seed: Int) = {{count}}({{collect}}({{items}}))
            """;
    }
}
