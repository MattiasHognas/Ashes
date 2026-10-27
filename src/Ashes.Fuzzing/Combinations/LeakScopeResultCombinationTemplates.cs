using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function whose result joins a reference-counted tuple holding a split of a let-bound string
/// with a nested match or a retained binding (<c>let slashed = rebuild(path) in if flag then ("",
/// true, split(slashed)) else match split(slashed) with ...</c>). The scope releasing the string
/// copies the join into the arena first, since the join may view it; the join itself, which owned
/// its reference, was never released.
/// </summary>
internal sealed class ViewCopiedScopeJoinTemplate() : LeakCombinationTemplate(
    "leak.view-copied-scope-join", "viewCopiedScopeJoin", GeneratedFeature.Let, GeneratedFeature.Match, GeneratedFeature.Tuple)
{
    protected override string Declarations(LeakNames names)
    {
        string rebuild = names.Of("rebuild");
        string parts = names.Of("parts");
        string separator = names.Pick("\"/\"", "\",\"");
        string sep = separator.Trim('"');
        string firstArm = names.Coin("freshHead") ? "(server + \"x\", true, [])" : "(\"\", true, rest)";
        string otherArm = names.Coin("staticOther") ? "(\"\", false, [])" : "(\"\" + rebuild(\"x\"), false, [])";
        otherArm = otherArm.Replace("rebuild", rebuild, StringComparison.Ordinal);
        string elseArm = names.Variant("elseShape", 3) switch
        {
            0 => $$"""
                match Ashes.Text.split(slashed)({{separator}}) with
                                | server :: rest -> {{firstArm}}
                                | _ -> {{otherArm}}
                """,
            1 => $$"""
                match Ashes.Text.length(slashed) with
                                | {{LeakNames.Text(names.Between(1, 3))}} -> ("", true, [])
                                | _ -> {{otherArm}}
                """,
            _ => "(\"\", false, Ashes.Text.split(other)(" + separator + "))",
        };
        string path = $"\"{sep}a{sep}b{sep}{sep}c\"";
        return $$"""
            let recursive {{rebuild}} (path: Str) =
                match Ashes.Text.unconsText(path) with
                    | None -> ""
                    | Some((head, tail)) -> head + {{rebuild}}(tail)

            let {{parts}} (flag: Bool) (path: Str) (other: Str) =
                (let slashed = {{rebuild}}(path)
                in
                    if flag
                    then ("", true, Ashes.Text.split(slashed)({{separator}}))
                    else
                        {{elseArm}})

            let {{names.Entry}} (seed: Int) =
                match {{parts}}(seed > 0)({{path}} + Ashes.Text.fromInt(seed))("x{{sep}}y" + Ashes.Text.fromInt(seed)) with
                    | (prefix, _absolute, found) -> Ashes.Text.length(prefix) + Ashes.Collection.List.length(found)
            """;
    }
}
