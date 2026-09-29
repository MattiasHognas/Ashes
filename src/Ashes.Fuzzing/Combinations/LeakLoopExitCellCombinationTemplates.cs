using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop returning a cell consed onto one of its list parameters: a literal onto the accumulator
/// its back edges grow (<c>| [] -&gt; "end" :: acc</c>), a head matched out of the walked list onto
/// a parameter handed on unchanged (<c>| [] -&gt; first :: acc</c>), or an element onto a sorted
/// list the caller keeps using (<c>then item :: set</c>). An arena cell there held references to
/// its head and to the parameter that nothing released, and a cell sharing a lent list has to own a
/// reference to it.
/// </summary>
internal sealed class LoopExitCellOntoParameterTemplate() : LeakCombinationTemplate(
    "leak.loop-exit-cell-onto-parameter", "loopExitCell", GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string collect = names.Of("collectParts");
        string count = names.Of("countParts");
        string loop = names.Of("exitCell");
        string word = names.Word();
        (string loopDeclaration, string use) = names.Variant("exit", 3) switch
        {
            0 => ($$"""
                let recursive {{loop}} (acc: List(Str)) (parts: List(Str)) =
                    match parts with
                        | [] -> {{word}} :: acc
                        | part :: rest -> {{loop}}((part + {{word}}) :: acc)(rest)
                """, $"{count}({loop}([])({collect}(seed)([])))(0)"),
            1 => ($$"""
                let recursive {{loop}} (acc: List(Str)) (parts: List(Str)) =
                    match parts with
                        | [] -> acc
                        | first :: rest ->
                            match rest with
                                | [] -> first :: acc
                                | _ :: more -> {{loop}}(acc)(more)
                """, $"{count}({loop}([{word}])({collect}(seed + 1)([])))(0)"),
            _ => ($$"""
                let recursive {{loop}} (item: Str) (set: List(Str)) =
                    match set with
                        | [] -> [item]
                        | head :: rest ->
                            if Ashes.Text.byteLength(head) >= Ashes.Text.byteLength(item)
                            then item :: set
                            else head :: {{loop}}(item)(rest)
                """, $"(let parts = {collect}(seed)([]) in {count}({loop}({word})(parts))(0) + {count}(parts)(0))"),
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

            {{loopDeclaration}}

            let {{names.Entry}} (seed: Int) = {{use}} + seed
            """;
    }
}
