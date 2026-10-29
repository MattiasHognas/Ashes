using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop that rewrites a list of records into an accumulator through a helper call, after an
/// earlier arm long enough that the walked tail's definition lies far behind the back edge: the
/// back edge must still trace the tail to the loop parameter and release the helper's result the
/// arena record holds.
/// </summary>
internal sealed class BackEdgeDerivationPastLongArmTemplate() : LeakCombinationTemplate(
    "leak.back-edge-derivation-past-long-arm", "derivationPastLongArm", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
    {
        string kind = names.Of("LeakKind");
        string inst = names.Of("LeakInst");
        string lookup = names.Of("lookup");
        string resolve = names.Of("resolve");
        string remap = names.Of("remapSources");
        string fuse = names.Of("fusePass");
        string weight = names.Of("weight");
        int shift = names.Between(1, 9);
        return $$"""
            type {{kind}} =
                | Load(Int, Int)
                | Store(Int, Int)
                | Dup(Int, Int)
                | Drop(Int, Int)

            type {{inst}} =
                | instruction: {{kind}}
                | location: Int

            let recursive {{lookup}} (temp: Int) (remap: List((Int, Int))) =
                match remap with
                    | [] -> None
                    | (key, value) :: rest ->
                        if key == temp
                        then Some(value)
                        else {{lookup}}(temp)(rest)

            let recursive {{resolve}} (remap: List((Int, Int))) (temp: Int) =
                match {{lookup}}(temp)(remap) with
                    | None -> temp
                    | Some(next) -> {{resolve}}(remap)(next)

            let {{remap}} (kind: {{kind}}) (remap: List((Int, Int))) =
                match kind with
                    | Load(target, slot) -> Load(target, slot)
                    | Store(slot, source) -> Store(slot, {{resolve}}(remap)(source))
                    | Dup(target, source) -> Dup(target, {{resolve}}(remap)(source))
                    | Drop(source, slot) -> Drop(source + {{shift}}, slot)

            let recursive {{fuse}} (insts: List({{inst}})) (remap: List((Int, Int))) (acc: List({{inst}})) =
                match insts with
                    | [] -> Ashes.Collection.List.reverse(acc)
                    | {{inst}} { instruction = Dup(dest, src) } :: {{inst}} { instruction = Drop(dropSrc, _) } :: tail ->
                        if dropSrc == src
                        then {{fuse}}(tail)((dest, src) :: remap)(acc)
                        else {{fuse}}(tail)(remap)(acc)
                    | {{inst}} { instruction = kind, location = loc } :: tail -> {{fuse}}(tail)(remap)({{inst}}(instruction = {{remap}}(kind)(remap), location = loc) :: acc)

            let recursive {{weight}} (instructions: List({{inst}})) (total: Int) =
                match instructions with
                    | [] -> total
                    | {{inst}} { instruction = Store(_, source) } :: rest -> {{weight}}(rest)(total + source)
                    | _ :: rest -> {{weight}}(rest)(total + 1)

            let {{names.Entry}} (seed: Int) =
                {{weight}}({{fuse}}([{{inst}}(instruction = Load(1, 2), location = seed), {{inst}}(instruction = Dup(3, 1), location = 0), {{inst}}(instruction = Drop(1, 2), location = 0), {{inst}}(instruction = Store(4, 3), location = {{names.Between(1, 9)}}), {{inst}}(instruction = Load(5, 4), location = 0)])([])([]))(seed)
            """;
    }
}

/// <summary>
/// A loop whose successor wraps a constructor over the loop parameter in a helper call
/// (`build(wrap(0)(Call(current, argument)))`): the constructor is the call's argument, not a cell
/// of the successor, so the retain it takes of the parameter is released after the call.
/// </summary>
internal sealed class CallArgumentCellInSuccessorTemplate() : LeakCombinationTemplate(
    "leak.call-argument-cell-in-successor", "callArgumentCellInSuccessor", GeneratedFeature.Adt, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
    {
        string expression = names.Of("LeakExpr");
        string wrap = names.Of("wrap");
        string build = names.Of("build");
        string depth = names.Of("depth");
        string word = names.Word();
        return $$"""
            type {{expression}} =
                | Name(Str)
                | Applied({{expression}}, {{expression}})
                | Spanned(Int, {{expression}})

            let {{wrap}} (start: Int) (inner: {{expression}}) = Spanned(start, inner)

            let recursive {{build}} (current: {{expression}}) (remaining: List({{expression}})) =
                match remaining with
                    | [] -> current
                    | argument :: tail -> {{build}}({{wrap}}({{names.Between(0, 9)}})(Applied(current, argument)))(tail)

            let recursive {{depth}} (value: {{expression}}) =
                match value with
                    | Applied(function, _) -> 1 + {{depth}}(function)
                    | Spanned(_, inner) -> {{depth}}(inner)
                    | _ -> 0

            let {{names.Entry}} (seed: Int) =
                {{depth}}({{build}}(Name({{word}} + Ashes.Text.fromInt(seed)))([Name({{word}}), Name("x" + Ashes.Text.fromInt(seed))])) + seed
            """;
    }
}
