using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A local recursive loop over a reference-counted list of instructions that hands each jump's label
/// to an <c>Eq</c>-constrained association update. The reference the call takes for its arena result
/// goes to an owned slot; without a predecessor slot of its own, the next iteration's store
/// overwrites it and every label but the last leaks.
/// </summary>
internal sealed class DictionaryKeyInLoopTemplate() : LeakCombinationTemplate(
    "leak.dictionary-key-in-loop", "dictionaryKeyInLoop", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string op = names.Of("Op");
        string inst = names.Of("Inst");
        string set = names.Of("setAssociation");
        string remove = names.Of("removeAssociation");
        string reduce = names.Of("reduce");
        string label = names.Of("label");
        string jumps = names.Of("jumps");
        string prefix = names.Pick("end_", "shared_", "loop_", "next_");
        int jumpCount = names.Between(2, 6);
        string labelArm = names.Coin("label-arm")
            ? $"| Label(name) -> go(tail)({remove}(name)(saved))(n + 1)"
            : $"| Label(_name) -> go(tail)(saved)(n + 1)";
        return $$"""
            type {{op}} =
                | Jump(Str)
                | Label(Str)
                | Other(Int)

            type {{inst}} =
                | instruction: {{op}}
                | location: Int

            let recursive {{set}} key value entries =
                match entries with
                    | [] -> [(key, value)]
                    | (k, v) :: tail ->
                        if k == key
                        then (key, value) :: tail
                        else (k, v) :: {{set}}(key)(value)(tail)

            let recursive {{remove}} key entries =
                match entries with
                    | [] -> []
                    | (k, v) :: tail ->
                        if k == key
                        then tail
                        else (k, v) :: {{remove}}(key)(tail)

            let {{reduce}} (weight: Int) (instructions: List({{inst}})) =
                (let recursive go insts saved n =
                    match insts with
                        | [] -> n + Ashes.Collection.List.length(saved)
                        | {{inst}} { instruction = op } :: tail ->
                            match op with
                                | Jump(target) -> go(tail)({{set}}(target)([weight])(saved))(n)
                                {{labelArm}}
                                | _ -> go(tail)(saved)(n)
                in go(instructions)([])(0))

            let {{label}} (n: Int) = "{{prefix}}" + Ashes.Text.fromInt(n)

            let recursive {{jumps}} (n: Int) (k: Int) (acc: List({{inst}})) =
                if k == 0
                then {{inst}}(instruction = Label({{label}}(n)), location = 0) :: acc
                else {{jumps}}(n)(k - 1)({{inst}}(instruction = Jump({{label}}(n + k % 2)), location = k) :: acc)

            let {{names.Entry}} (seed: Int) = {{reduce}}(seed)({{jumps}}(seed)({{jumpCount}})([{{inst}}(instruction = Other(1), location = 9)]))
            """;
    }
}
