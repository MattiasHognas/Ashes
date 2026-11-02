using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A closure capturing a reference-counted `let` owner, returned inside an aggregate: a tuple, an
/// `Option`, a record field, a list element, a generic single-constructor box, or a tuple inside an
/// `Option`, optionally with a second closure sharing the capture. The owner's release runs before
/// the aggregate is read, so an environment borrowing it reads freed memory; the entry checks every
/// result against the value computed without the closure and panics on a mismatch.
/// </summary>
internal sealed class ClosureInEscapingAggregateTemplate() : LeakCombinationTemplate(
    "leak.closure-in-escaping-aggregate", "closureInAggregate", GeneratedFeature.Lambda, GeneratedFeature.Tuple, GeneratedFeature.Adt, GeneratedFeature.EscapingClosure)
{
    protected override string Declarations(LeakNames names)
    {
        string tag = names.Of("tag");
        string make = names.Of("make");
        string probe = names.Of("probe");
        string expected = names.Of("expected");
        string loop = names.Of("loop");
        string box = names.Of("Box");
        string owner = names.Pick("s", "label", "prefix");
        string parameter = names.Pick("x", "rest", "after");
        string suffix = names.Word();
        string argument = names.Word();
        bool shared = names.Coin("second-closure");
        string closure = $"given ({parameter}) -> {parameter} + {owner}";
        string second = $"given ({parameter}) -> {owner} + {parameter}";
        int variant = names.Variant("aggregate", 6);
        string sharedExpected = !shared ? string.Empty : variant is 0 or 3 ? $" + {tag}(n) + {argument}" : $" + {argument} + {tag}(n)";
        (string types, string aggregate, string read) = variant switch
        {
            0 => (string.Empty, shared ? $"({closure}, {second})" : $"({closure}, n)",
                shared ? $"(f, g) -> f({argument}) + g({argument})" : $"(f, m) -> f({argument})"),
            1 => (string.Empty, $"Some({closure})", $"Some(f) -> f({argument}){SecondCall(shared, argument)}\n        | None -> \"\""),
            2 => ($"type {box} =\n    | run: Str -> Str\n    | k: Int\n\n", $"{box}(run = {closure}, k = n)",
                $"{box} {{ run = f, k = m }} -> f({argument}){SecondCall(shared, argument)}"),
            3 => (string.Empty, shared ? $"[{closure}, {second}]" : $"[{closure}]",
                shared ? $"f :: g :: _ -> f({argument}) + g({argument})\n        | _ -> \"\"" : $"f :: _ -> f({argument})\n        | [] -> \"\""),
            4 => ($"type {box}(a) =\n    | {box}(a)\n\n", $"{box}({closure})", $"{box}(f) -> f({argument}){SecondCall(shared, argument)}"),
            _ => (string.Empty, $"Some(({closure}, n))", $"Some((f, m)) -> f({argument}){SecondCall(shared, argument)}\n        | None -> \"\""),
        };
        return $$"""
            {{types}}let {{tag}} (n: Int) = Ashes.Text.fromInt(n) + {{suffix}}

            let {{make}} (n: Int) = let {{owner}} = {{tag}}(n) in {{aggregate}}

            let {{probe}} (n: Int) =
                match {{make}}(n) with
                    | {{read}}

            let {{expected}} (n: Int) = {{argument}} + {{tag}}(n){{sharedExpected}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else (let p = {{probe}}(n) in if p == {{expected}}(n) then {{loop}}(n - 1)(acc + Ashes.Text.byteLength(p)) else Ashes.IO.panic("freed capture"))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }

    // Only the tuple and list variants carry a second closure; elsewhere the coin calls the first
    // closure twice, reading the owner again.
    private static string SecondCall(bool shared, string argument)
        => shared ? $" + f({argument})" : string.Empty;
}
