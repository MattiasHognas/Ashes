using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function called inside a handle builds a constructor holding a reference-counted list: a
/// perform's result, or a list a helper builds from one. The function may run while the handler's
/// one-shot post is pending, so the constructor cell stays in the arena, which never releases what
/// it holds: the list stored into the field outlived every reader.
/// </summary>
internal sealed class ArenaConstructorFreshListTemplate() : LeakCombinationTemplate(
    "leak.arena-constructor-fresh-list", "arenaCtorFreshList", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakTakeList");
        string item = names.Of("ListItem");
        string make = names.Of("make");
        string helper = names.Of("words");
        string count = names.Of("count");
        string probe = names.Of("probe");
        string loop = names.Of("loop");
        bool performsList = names.Coin("performsList");
        string field = performsList
            ? $"perform {capability}.get(Unit)"
            : $"{helper}(perform {capability}.get(Unit))";
        string resumed = performsList
            ? $"[{names.Word()}, Ashes.Text.fromInt(n)]"
            : "Ashes.Text.fromInt(n)";
        (string body, bool bare) = names.Variant("shape", 3) switch
        {
            0 => ($"{item}({field}, i)", true),
            1 => ($"[{item}({field}, i), {item}([{names.Word()}], 0)]", false),
            _ => ($"{item}({field}, i) :: [{item}([], 1)]", false),
        };
        string handled = $"handle {make}(n) with\n        | {capability}.get(u) -> resume({resumed})";
        string reader = bare ? $"{count}([{handled}])(0)" : $"{count}({handled})(0)";
        return $$"""
            capability {{capability}} =
                | get : Unit -> {{(performsList ? "List(Str)" : "Str")}}

            type {{item}} =
                | {{item}}(List(Str), Int)

            let {{helper}} (s: Str) = [s + "!", s]

            let {{make}} (i: Int) = {{body}}

            let recursive {{count}} (xs: List({{item}})) (k: Int) =
                match xs with
                    | [] -> k
                    | {{item}}(ws, w) :: rest -> {{count}}(rest)(k + w + Ashes.Collection.List.length(ws))

            let {{probe}} (n: Int) =
                {{reader}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{probe}}(n))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }
}
