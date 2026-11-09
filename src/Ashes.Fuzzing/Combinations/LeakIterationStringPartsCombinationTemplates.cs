using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop over declarations pushes a restore record per recursive declaration onto a list of
/// pending closes, beside a pair naming the last declaration, the shape of a lowering pass over a
/// program's items. The record each iteration builds was kept back from release at the back edge
/// because the pair could hold its strings, although a string a successor keeps holds a
/// reference of its own, and every record but the last two leaked.
/// </summary>
internal sealed class IterationStringPartsTemplate() : LeakCombinationTemplate(
    "leak.iteration-string-parts", "iterationStringParts", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.Record, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string binding = names.Of("Binding");
        string state = names.Of("State");
        string close = names.Of("Close");
        string lowered = names.Of("Lowered");
        string item = names.Of("Item");
        string makeRestore = names.Of("makeRestore");
        string closePending = names.Of("closePending");
        string lowerItems = names.Of("lowerItems");
        string seenCount = names.Of("seenCount");
        int extra = names.Between(0, 3);
        string items = "ItemRec(\"a\" + Ashes.Text.fromInt(seed), Some(seed)), ItemLet(\"b\"), ItemAt(Some(9), ItemRec(\"c\", Some(2)))";
        for (int i = 0; i < extra; i++)
        {
            items += $", ItemRec(\"r{i}\", None)";
        }

        if (names.Coin("hook"))
        {
            items += ", ItemHook(given (x) -> x + 1)";
        }

        return $$"""
            type {{binding}} =
                | name: Str
                | scheme: List(Int)

            type {{state}} =
                | bindings: List({{binding}})
                | total: Int

            type {{close}} =
                | closeOwnedTypeName: Maybe(Str)
                | closeOwnerSlot: Int
                | closeSpan: Maybe(Int)
                | closeOuterBindings: List({{binding}})
                | closeBracket: Bool

            type {{lowered}} =
                | state: {{state}}
                | temp: Int
                | error: Maybe(Str)

            type {{item}} =
                | ItemLet(Str)
                | ItemRec(Str, Maybe(Int))
                | ItemAt(Maybe(Int), {{item}})
                | ItemHook(Int -> Int)

            let {{makeRestore}} (owner: Int) (span: Maybe(Int)) (outerBindings: List({{binding}})) =
                {{close}}(closeOwnedTypeName = None, closeOwnerSlot = owner, closeSpan = span, closeOuterBindings = outerBindings, closeBracket = false)

            let {{seenCount}} (seen: (Str, Int)) =
                match seen with
                    | (_last, count) -> count

            let recursive {{closePending}} (pending: List({{close}})) (result: {{lowered}}) =
                match (pending, result) with
                    | ([], _result) -> result
                    | (entry :: rest, {{lowered}} { state = bodyState, temp = resultTemp }) ->
                        if entry.closeBracket
                        then {{closePending}}(rest)({{lowered}}(state = (bodyState with total = bodyState.total + entry.closeOwnerSlot), temp = resultTemp + 1, error = None))
                        else {{closePending}}(rest)({{lowered}}(state = (bodyState with bindings = entry.closeOuterBindings), temp = resultTemp, error = None))

            let recursive {{lowerItems}} restore (items: List({{item}})) (seen: (Str, Int)) (pending: List({{close}})) (state: {{state}}) =
                match items with
                    | [] -> {{closePending}}(pending)({{lowered}}(state = state, temp = Ashes.Collection.List.length(state.bindings), error = None))
                    | ItemRec(name, span) :: rest ->
                        match state with
                            | {{state}} { bindings = outerBindings } -> {{lowerItems}}(restore)(rest)((name, {{seenCount}}(seen) + 1))(restore(span)(outerBindings) :: pending)((state with bindings = {{binding}}(name = name, scheme = [1, 2, 3]) :: outerBindings))
                    | ItemLet(_name) :: rest -> {{lowerItems}}(restore)(rest)(seen)(pending)(state)
                    | ItemAt(_span, inner) :: rest -> {{lowerItems}}(restore)(inner :: rest)(seen)(pending)(state)
                    | ItemHook(_hook) :: rest -> {{lowerItems}}(restore)(rest)(seen)(pending)(state)

            let {{names.Entry}} (seed: Int) =
                match {{lowerItems}}({{makeRestore}}(seed % 2 - 1))([{{items}}])(("", 0))([])({{state}}(bindings = [], total = seed)) with
                    | {{lowered}} { temp = temp, state = state } -> temp + state.total
            """;
    }
}
