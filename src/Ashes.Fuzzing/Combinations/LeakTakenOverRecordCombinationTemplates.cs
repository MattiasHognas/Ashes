using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A generic loop (or a recursive group member) hands the list a call returned to a generic helper
/// that conses each element into a tuple of its accumulator, and the loop's result reaches the
/// caller, or a generic function holding it in a let. The list's cells are freed where the loop
/// leaves it, and the tuples' first fields keep the references the caller releases once it is done
/// with the result.
/// </summary>
internal sealed class TakenOverRecordTemplate() : LeakCombinationTemplate(
    "leak.taken-over-record", "takenOverRecord", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string pat = names.Of("LeakPat");
        string expand = names.Of("expand");
        string arms = names.Of("expandedArms");
        string cases = names.Of("expandCases");
        string count = names.Of("count");
        string make = names.Of("makeCases");
        string check = names.Of("check");
        string loop = names.Of("loop");
        bool group = names.Coin("mutual-group");
        bool holder = names.Coin("generic-let-holder");
        string use = holder
            ? $"{check}({make}(n))([\"env\"])"
            : $"{count}({cases}({make}(n))([\"env\"])([]))";
        return $$"""
            type {{pat}} =
                | {{pat}}At(Int, {{pat}})
                | {{pat}}Empty
                | {{pat}}Var(Str)

            let recursive {{expand}} pattern environment =
                match pattern with
                    | {{pat}}At(_span, inner) -> {{expand}}(inner)(environment)
                    | other -> [other]

            {{(group ? GroupDefinitions(cases, arms, expand) : SeparateDefinitions(cases, arms, expand))}}

            let recursive {{count}} xs =
                match xs with
                    | [] -> 0
                    | _ :: rest -> 1 + {{count}}(rest)

            let {{make}} (n: Int) = [({{pat}}At(n, {{pat}}Var("v" + Ashes.Text.fromInt(n))), 1, Some(n)), ({{pat}}Var("w" + Ashes.Text.fromInt(n)), 2, None)]

            let {{check}} cases environment =
                (let expanded = {{cases}}(cases)(environment)([])
                in {{count}}(expanded) + {{count}}(expanded))

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{use}})

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }

    private static string CasesBody(string cases, string arms, string expand) => $$"""
            {{cases}} cases environment acc =
                match cases with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | (pattern, body, guard) :: rest ->
                        acc
                        |> {{arms}}({{expand}}(pattern)(environment))(body)(guard)
                        |> {{cases}}(rest)(environment)
            """;

    private static string ArmsBody(string arms) => $$"""
            {{arms}} patterns body guard acc =
                match patterns with
                    | [] -> acc
                    | pattern :: rest -> {{arms}}(rest)(body)(guard)((pattern, body, guard) :: acc)
            """;

    private static string GroupDefinitions(string cases, string arms, string expand)
        => $"let recursive {CasesBody(cases, arms, expand)}\nand {ArmsBody(arms)}";

    private static string SeparateDefinitions(string cases, string arms, string expand)
        => $"let recursive {ArmsBody(arms)}\n\nlet recursive {CasesBody(cases, arms, expand)}";
}
