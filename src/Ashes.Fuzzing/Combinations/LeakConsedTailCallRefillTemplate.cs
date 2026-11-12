using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>A worklist alternates consumed tails, one-cell pushes, and helper refills.</summary>
internal sealed class ConsedTailCallRefillTemplate() : LeakCombinationTemplate(
    "leak.consed-tail-call-refill", "consedTailCallRefill", GeneratedFeature.Record,
    GeneratedFeature.TailCall, GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string tree = names.Of("RefillTree");
        string job = names.Of("RefillJob");
        string result = names.Of("RefillResult");
        string resolve = names.Of("resolve");
        string prepend = names.Of("prepend");
        string walk = names.Of("walk");
        return $$"""
            type {{tree}} =
                | RefillLeaf(Str)
                | RefillNested({{tree}})
                | RefillMany(List({{tree}}))
            type {{job}} =
                | value: {{tree}}
                | path: List({{tree}})
            type {{result}} =
                | ok: Bool
                | known: List({{tree}})
            let {{resolve}} value environment =
                if environment == 0 then value else RefillNested(value)
            let recursive {{prepend}} values path pending =
                match values with
                    | [] -> pending
                    | value :: rest -> {{job}}(value = value, path = path) :: {{prepend}}(rest)(path)(pending)
            let recursive {{walk}} (pending: List({{job}})) (visited: List({{tree}})) (known: List({{tree}})) (environment: Int) (flag: Bool) =
                match pending with
                    | [] -> {{result}}(ok = true, known = known)
                    | {{job}} { value = value, path = path } :: rest ->
                        let resolved = {{resolve}}(value)(environment)
                        in match resolved with
                            | RefillLeaf(_text) -> {{walk}}(rest)(visited)(known)(environment)(flag)
                            | RefillNested(child) -> {{walk}}({{job}}(value = child, path = path) :: rest)(visited)(known)(environment)(flag)
                            | RefillMany(children) -> {{walk}}({{prepend}}(children)(resolved :: path)(rest))(visited)(known)(environment)(flag)
            let {{names.Entry}} (seed: Int) =
                let text = {{names.Word()}} + Ashes.Text.fromInt(seed)
                in let result = {{walk}}([{{job}}(value = RefillMany([RefillMany([RefillNested(RefillLeaf(text))])]), path = [])])([])([])(0)(false)
                in if result.ok then seed else 0
            """;
    }
}
