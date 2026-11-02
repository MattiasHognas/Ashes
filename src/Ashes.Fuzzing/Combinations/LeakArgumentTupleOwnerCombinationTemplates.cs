using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a list puts the binders of its head's constructor in a tuple of a list literal and
/// passes the list to a call, as stage 1 hands `[(name, value)]` to its recursive-group preparation.
/// The tuple and the list cell stay in the arena, and the duplicate each binder took for the tuple
/// was never released.
/// </summary>
internal sealed class LoopHeadBindersInArgumentTupleTemplate() : LeakCombinationTemplate(
    "leak.loop-head-binders-in-argument-tuple", "argumentTupleBinders", GeneratedFeature.Adt, GeneratedFeature.Tuple, GeneratedFeature.TailCall, GeneratedFeature.Match)
{
    protected override string Declarations(LeakNames names)
    {
        string item = names.Of("LeakItem");
        string measure = names.Of("measure");
        string walk = names.Of("walkItems");
        string make = names.Of("makeItems");
        string binder = names.Pick("name", "label", "key");
        string callee = names.Variant("callee", 2) switch
        {
            0 => $$"""
                let recursive {{measure}} (bindings: List((Str, Str))) (total: Int) =
                    match bindings with
                        | [] -> total
                        | (a, b) :: rest -> {{measure}}(rest)(total + Ashes.Text.byteLength(a) + Ashes.Text.byteLength(b))
                """,
            _ => $$"""
                let recursive {{measure}} (bindings: List((Str, Str))) (total: Int) =
                    match bindings with
                        | [] -> total
                        | (a, _b) :: rest -> {{measure}}(rest)(total + Ashes.Text.byteLength(a))
                """,
        };
        string pairs = names.Coin("two-pairs")
            ? $"[({binder}, value), (value, {binder})]"
            : $"[({binder}, value)]";
        return $$"""
            type {{item}} =
                | {{item}}(Str, Str)

            {{callee}}

            let recursive {{walk}} (items: List({{item}})) (total: Int) =
                match items with
                    | [] -> total
                    | {{item}}({{binder}}, value) :: rest -> {{walk}}(rest)({{measure}}({{pairs}})(total))

            let recursive {{make}} (k: Int) (seed: Int) =
                if k == 0
                then []
                else {{item}}(Ashes.Text.fromInt(seed + k), Ashes.Text.fromInt(seed * {{names.Between(2, 5)}} + k)) :: {{make}}(k - 1)(seed)

            let {{names.Entry}} (seed: Int) = {{walk}}({{make}}({{names.Between(2, 4)}})(seed))(0)
            """;
    }
}
