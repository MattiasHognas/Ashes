using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A recursive producer conses a tuple of a fresh string and a tree onto its own result, in a
/// program with a handler, so general reference counting is off. The cell asks for a
/// reference-counted tuple, so the string is made reference-counted, but the tuple cannot own the
/// tree and stays in the arena, which never releases the string it holds.
/// </summary>
internal sealed class ProducerTupleFreshElementTemplate() : LeakCombinationTemplate(
    "leak.producer-tuple-fresh-element", "producerTupleFresh", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.Tuple, GeneratedFeature.RecursiveFunction)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakTickTuple");
        string tree = names.Of("PTree");
        string leaf = names.Of("PLeaf");
        string node = names.Of("PNode");
        string build = names.Of("build");
        string weight = names.Of("weight");
        string bindings = names.Of("bindingsOf");
        string total = names.Of("total");
        string loop = names.Of("loop");
        string name = names.Coin("labelled") ? $"Ashes.Text.fromInt(n + k) + {names.Word()}" : "Ashes.Text.fromInt(n + k)";
        return $$"""
            capability {{capability}} =
                | get : Unit -> Int

            type {{tree}} =
                | {{leaf}}(Int)
                | {{node}}(Str, {{tree}})

            let recursive {{build}} (depth: Int) (n: Int) =
                if depth == 0
                then {{leaf}}(n)
                else {{node}}(Ashes.Text.fromInt(depth), {{build}}(depth - 1)(n))

            let recursive {{weight}} (t: {{tree}}) =
                match t with
                    | {{leaf}}(n) -> n
                    | {{node}}(_s, rest) -> 1 + {{weight}}(rest)

            let recursive {{bindings}} (k: Int) (n: Int) =
                if k == 0
                then []
                else ({{name}}, {{build}}({{names.Between(1, 3)}})(n)) :: {{bindings}}(k - 1)(n)

            let recursive {{total}} (bs: List((Str, {{tree}}))) (acc: Int) =
                match bs with
                    | [] -> acc
                    | (s, t) :: rest -> {{total}}(rest)(acc + Ashes.Text.byteLength(s) + {{weight}}(t))

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else {{loop}}(n - 1)(acc + {{total}}({{bindings}}({{names.Between(1, 3)}})(n))(0))

            let {{names.Entry}} (seed: Int) =
                handle {{loop}}({{names.Between(3, 9)}})(perform {{capability}}.get(Unit)) with
                    | {{capability}}.get(u) -> resume(seed)
            """;
    }
}
