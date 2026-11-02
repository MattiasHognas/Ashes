using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function called inside a handle builds a constructor holding a perform's result: returned as
/// it is, consed onto a list, or consed onto a tail-recursive loop's accumulator. The function may
/// run while the handler's one-shot post is pending, so the constructor cell stays in the arena,
/// which never releases what it holds: the perform's reference-counted string stored into the
/// field outlived every reader.
/// </summary>
internal sealed class ArenaConstructorFreshStringTemplate() : LeakCombinationTemplate(
    "leak.arena-constructor-fresh-string", "arenaCtorFreshString", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakTake");
        string item = names.Of("Item");
        string build = names.Of("build");
        string count = names.Of("count");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        string cell = $"{item}(perform {capability}.get(Unit), i)";
        string builder = names.Variant("shape", 3) switch
        {
            0 => $"let {build} (i: Int) = [{cell}]",
            1 => $"let {build} (i: Int) = {cell} :: [{item}({names.Word()}, 0)]",
            _ => $"""
                let {build} (i: Int) =
                    let recursive go (j: Int) (acc: List({item})) =
                        if j == 0
                        then acc
                        else go(j - 1)({item}(perform {capability}.get(Unit), j) :: acc)
                    in go(i)([])
                """,
        };
        string arm = names.Coin("post")
            ? $"let x = resume({names.Word()}) in {item}({names.Word()}, 1) :: x"
            : $"resume({names.Word()})";
        return $$"""
            capability {{capability}} =
                | get : Unit -> Str

            type {{item}} =
                | {{item}}(Str, Int)

            {{builder}}

            let recursive {{count}} (xs: List({{item}})) (k: Int) =
                match xs with
                    | [] -> k
                    | {{item}}(s, w) :: rest -> {{count}}(rest)(k + w)

            let {{probe}} (n: Int) =
                {{count}}(handle {{build}}({{names.Between(1, 4)}}) with
                    | {{capability}}.get(u) -> {{arm}}
                    | return(r) -> r)(0)

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
