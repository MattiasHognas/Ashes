using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>A loop returns an arena constructor retaining its reference-counted string parameter.</summary>
internal sealed class LoopResultConstructorFieldTemplate() : LeakCombinationTemplate(
    "leak.loop-result-constructor-field", "loopResultConstructorField", GeneratedFeature.TailCall,
    GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string lookup = names.Of("lookup");
        string follow = names.Of("follow");
        return $$"""
            let {{lookup}} (label: Str) pairs =
                Ashes.Collection.Map.getStr(label)(pairs)
            let recursive {{follow}} (count: Int) (label: Str) pairs =
                if count <= 0 then Some(label)
                else match {{lookup}}(label)(pairs) with
                    | Some(next) -> {{follow}}(count - 1)(next)(pairs)
                    | None -> None
            let {{names.Entry}} (seed: Int) =
                let text = {{names.Word()}} + Ashes.Text.fromInt(seed)
                in let pairs = Ashes.Collection.Map.setStr("start")(text)(Ashes.Collection.Map.empty)
                in if {{follow}}(1)("start")(pairs) == Some(text) then seed else 0
            """;
    }
}
