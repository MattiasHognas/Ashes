using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A closure capturing a reference-counted `let` owner that outlives the owner's scope: the post
/// of a one-shot operation arm whose `let` prefix binds the owner, a closure returned from its
/// function, or one returned through a `let` naming it. The prefix's release runs before the
/// closure does, so an environment borrowing the owner reads freed memory; the entry checks every
/// result against the value computed without the closure and panics on a mismatch.
/// </summary>
internal sealed class EscapingClosureCaptureTemplate() : LeakCombinationTemplate(
    "leak.escaping-closure-capture", "escapingClosureCapture", GeneratedFeature.Capability, GeneratedFeature.Handler, GeneratedFeature.Lambda)
{
    protected override string Declarations(LeakNames names)
    {
        string capability = names.Of("LeakFetch");
        string tag = names.Of("tag");
        string probe = names.Of("probe");
        string expected = names.Of("expected");
        string loop = names.Of("loop");
        string owner = names.Pick("s", "label", "prefix");
        string arenaOwner = names.Pick("t", "count", "digits");
        string resumed = names.Pick("x", "rest", "after");
        string suffix = names.Word();
        string argument = names.Word();
        bool mixed = names.Coin("arena-capture");
        bool returnsOwner = names.Coin("returns-owner");
        string arenaLet = mixed ? $"let {arenaOwner} = Ashes.Text.fromInt(n) in " : string.Empty;
        string arenaPart = mixed ? $" + {arenaOwner}" : string.Empty;
        string closureBody = returnsOwner ? owner : $"{resumed} + {owner}{arenaPart}";
        string ownerExpected = $"{tag}(n)";
        string arenaExpected = mixed ? " + Ashes.Text.fromInt(n)" : string.Empty;
        (string declarations, string expectedBody) = names.Variant("escape", 3) switch
        {
            0 => ArmPost(names, capability, tag, probe, owner, resumed, argument, arenaLet, closureBody, returnsOwner, ownerExpected + arenaExpected),
            1 => ($$"""
                let {{names.Of("make")}} (n: Int) = let {{owner}} = {{tag}}(n) in {{arenaLet}}given ({{resumed}}) -> {{closureBody}}

                let {{probe}} (n: Int) = let f = {{names.Of("make")}}(n) in f({{argument}})
                """, returnsOwner ? ownerExpected : $"{argument} + {ownerExpected}{arenaExpected}"),
            _ => ($$"""
                let {{names.Of("make")}} (n: Int) = let {{owner}} = {{tag}}(n) in {{arenaLet}}let g = given ({{resumed}}) -> {{closureBody}} in g

                let {{probe}} (n: Int) = let f = {{names.Of("make")}}(n) in f({{argument}})
                """, returnsOwner ? ownerExpected : $"{argument} + {ownerExpected}{arenaExpected}"),
        };
        return $$"""
            let {{tag}} (n: Int) = Ashes.Text.fromInt(n) + {{suffix}}

            {{declarations}}

            let {{expected}} (n: Int) = {{expectedBody}}

            let recursive {{loop}} (n: Int) (acc: Int) =
                if n == 0
                then acc
                else (let p = {{probe}}(n) in if p == {{expected}}(n) then {{loop}}(n - 1)(acc + Ashes.Text.byteLength(p)) else Ashes.IO.panic("freed capture"))

            let {{names.Entry}} (seed: Int) = {{loop}}({{names.Between(3, 9)}})(seed)
            """;
    }

    private static (string Declarations, string ExpectedBody) ArmPost(
        LeakNames names,
        string capability,
        string tag,
        string probe,
        string owner,
        string resumed,
        string argument,
        string arenaLet,
        string closureBody,
        bool returnsOwner,
        string capturedExpected)
    {
        string returnWord = names.Word();
        bool returnArm = names.Coin("return-arm");
        string returnArmText = returnArm ? $"\n        | return(r) -> r + {returnWord}" : string.Empty;
        string resumedExpected = returnArm ? $"{argument} + {returnWord}" : argument;
        string declarations = $$"""
            capability {{capability}} =
                | get : Unit -> Str

            let {{probe}} (n: Int) =
                handle perform {{capability}}.get(Unit) with
                    | {{capability}}.get(u) -> let {{owner}} = {{tag}}(n) in {{arenaLet}}let {{resumed}} = resume({{argument}}) in {{closureBody}}{{returnArmText}}
            """;
        return (declarations, returnsOwner ? $"{tag}(n)" : $"{resumedExpected} + {capturedExpected}");
    }
}
