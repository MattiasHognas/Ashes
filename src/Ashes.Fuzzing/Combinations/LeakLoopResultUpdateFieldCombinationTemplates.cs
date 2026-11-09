using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over a table matched out of a call result, partially applied and mapped over records,
/// rebuilds its record parameter with a record update as its result (`fn with calls = target ::
/// fn.calls`), the shape of a devirtualization pass over a program's functions. The update builds
/// the record in the arena with retained copies of the unchanged fields; the return copies it onto
/// the reference-counted heap, which retains them again, and the arena record's retains leaked.
/// </summary>
internal sealed class LoopResultUpdateFieldTemplate() : LeakCombinationTemplate(
    "leak.loop-result-update-field", "loopResultUpdateField", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.Record, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names) => Render(names, explicitConstructor: false);

    internal static string Render(LeakNames names, bool explicitConstructor)
    {
        string tree = names.Of("Tree");
        string fn = names.Of("Fn");
        string retarget = names.Of("retarget");
        string collect = names.Of("collect");
        string run = names.Of("run");
        string sum = names.Of("sumCalls");
        string build = names.Of("build");
        string update = names.Pick("calls = target :: fn.calls", "calls = [target]", "label = target");
        string rebuilt = explicitConstructor
            ? update switch
            {
                "label = target" => $"{fn}(label = target, calls = fn.calls, tree = fn.tree)",
                _ => $"{fn}(label = fn.label, {update}, tree = fn.tree)",
            }
            : $"fn with {update}";
        int count = names.Between(1, 4);
        return $$"""
            type {{tree}} =
                | Leaf(Str)
                | Node({{tree}}, {{tree}})

            type {{fn}} =
                | label: Str
                | calls: List(Str)
                | tree: {{tree}}

            let recursive {{retarget}} (known: List((Str, Str))) (fn: {{fn}}) =
                match known with
                    | [] -> fn
                    | (label, target) :: rest ->
                        if label == fn.label
                        then {{rebuilt}}
                        else {{retarget}}(rest)(fn)

            let recursive {{collect}} (functions: List({{fn}})) (known: List((Str, Str))) =
                match functions with
                    | [] -> known
                    | fn :: rest -> {{collect}}(rest)((fn.label, fn.label + "_r") :: known)

            let {{run}} (functions: List({{fn}})) =
                match {{collect}}(functions)([]) with
                    | [] -> functions
                    | known -> Ashes.Collection.List.map({{retarget}}(known))(functions)

            let recursive {{sum}} (functions: List({{fn}})) (acc: Int) =
                match functions with
                    | [] -> acc
                    | fn :: rest -> {{sum}}(rest)(acc + Ashes.Collection.List.length(fn.calls))

            let recursive {{build}} (n: Int) (acc: List({{fn}})) =
                if n == 0
                then acc
                else {{build}}(n - 1)({{fn}}(label = "lambda_" + Ashes.Text.fromInt(n), calls = ["f"], tree = Leaf("t")) :: acc)

            let {{names.Entry}} (seed: Int) = {{sum}}({{run}}({{build}}({{count}} + seed % 2)([])))(0)
            """;
    }
}

/// <summary>
/// The same loop rebuilds its record parameter with an explicit constructor of the parameter's
/// fields (`Fn(label = fn.label, calls = target :: fn.calls, tree = fn.tree)`) inside the match
/// arm whose join is copied onto the reference-counted heap. The copy retains the fields itself, so
/// the arena cell's retains of the parameter reads are withdrawn; before, they leaked.
/// </summary>
internal sealed class LoopResultConstructorOfParameterFieldsTemplate() : LeakCombinationTemplate(
    "leak.loop-result-constructor-of-parameter-fields", "loopResultConstructorOfParameterFields", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.Record, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names) => LoopResultUpdateFieldTemplate.Render(names, explicitConstructor: true);
}
