using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop whose exit passes a cell consed onto one of its list parameters to a call that may keep
/// it (<c>| [] -&gt; rev([])(first :: acc)</c>), the pairwise pass of <c>Text.join</c> as first
/// written. An arena cell there retained its head through a marker the loop's placement upgraded
/// only after the call's frame had closed, so nothing released it.
/// </summary>
internal sealed class ExitCellPassedToCallTemplate() : LeakCombinationTemplate(
    "leak.exit-cell-passed-to-call", "exitCellCall", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectParts");
        string count = names.Of("countParts");
        string rev = names.Of("reverseParts");
        string loop = names.Of("exitThroughCall");
        string word = names.Word();
        string loopDeclaration = names.Variant("exit", 3) switch
        {
            0 => $$"""
                let recursive {{loop}} (acc: List(Str)) (parts: List(Str)) =
                    match parts with
                        | [] -> {{rev}}([])(acc)
                        | first :: rest ->
                            match rest with
                                | [] -> {{rev}}([])(first :: acc)
                                | second :: more -> {{loop}}((first + second) :: acc)(more)
                """,
            1 => $$"""
                let recursive {{loop}} (acc: List(Str)) (parts: List(Str)) =
                    match parts with
                        | [] -> Ashes.Collection.List.reverse(acc)
                        | first :: rest ->
                            match rest with
                                | [] -> Ashes.Collection.List.reverse(first :: acc)
                                | _ :: more -> {{loop}}(acc)(more)
                """,
            _ => $$"""
                let recursive {{loop}} (acc: List(Str)) (parts: List(Str)) =
                    match parts with
                        | [] -> {{rev}}([])({{word}} :: acc)
                        | part :: rest -> {{loop}}((part + {{word}}) :: acc)(rest)
                """,
        };
        return $$"""
            let recursive {{collect}} (n: Int) (acc: List(Str)) =
                if n <= 0
                then acc
                else {{collect}}(n - 1)(Ashes.Text.fromInt(n) :: acc)

            let recursive {{count}} (items: List(Str)) (total: Int) =
                match items with
                    | [] -> total
                    | head :: rest -> {{count}}(rest)(total + Ashes.Text.byteLength(head))

            let recursive {{rev}} (acc: List(Str)) (xs: List(Str)) =
                match xs with
                    | [] -> acc
                    | head :: tail -> {{rev}}(head :: acc)(tail)

            {{loopDeclaration}}

            let {{names.Entry}} (seed: Int) = {{count}}({{loop}}([])({{collect}}(3)([])))(0) + seed
            """;
    }
}
