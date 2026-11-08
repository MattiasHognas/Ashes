using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A record of flags folded over a program's instructions, the shape of a pass recording which
/// runtime helpers a program uses. The loop's instruction list cannot be placed on the
/// reference-counted heap, and it kept the record in the arena beside it although the record holds
/// no part of the list. The step that sets a flag hands the record back on the reference-counted
/// heap, so every iteration's record leaked.
/// </summary>
internal sealed class ScalarFieldAccumulatorTemplate() : LeakCombinationTemplate(
    "leak.scalar-field-accumulator", "scalarFieldAccumulator", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.Record, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("Kind");
        string instruction = names.Of("Instruction");
        string uses = names.Of("Uses");
        string include = names.Of("includeInstruction");
        string collect = names.Of("collectUses");
        string first = names.Pick("PrintInt", "PrintStr", "ConcatStr");
        int count = names.Between(1, 4);
        string items = $"{instruction}(instruction = {first}(seed), location = None)";
        for (int i = 0; i < count; i++)
        {
            items += $", {instruction}(instruction = {(i % 2 == 0 ? "Other" : "PrintInt")}({i}), location = Some(seed))";
        }

        return $$"""
            type {{kind}} =
                | PrintInt(Int)
                | PrintStr(Int)
                | ConcatStr(Int)
                | Other(Int)

            type {{instruction}} =
                | instruction: {{kind}}
                | location: Maybe(Int)

            type {{uses}} =
                | printInt: Bool
                | printStr: Bool
                | concatStr: Bool

            let {{include}} operation (uses: {{uses}}) =
                match operation with
                    | PrintInt(_) -> uses with printInt = true
                    | PrintStr(_) -> uses with printStr = true
                    | ConcatStr(_) -> uses with concatStr = true
                    | _ -> uses

            let recursive {{collect}} instructions uses =
                match instructions with
                    | [] -> uses
                    | {{instruction}} { instruction = operation } :: rest ->
                        uses
                        |> {{include}}(operation)
                        |> {{collect}}(rest)

            let {{names.Entry}} (seed: Int) =
                match {{collect}}([{{items}}])({{uses}}(printInt = false, printStr = false, concatStr = false)) with
                    | {{uses}} { printInt = printInt, concatStr = concatStr } ->
                        if printInt || concatStr
                        then seed
                        else 0
            """;
    }
}
