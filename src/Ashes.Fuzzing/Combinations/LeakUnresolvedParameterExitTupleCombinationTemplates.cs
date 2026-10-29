using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A loop splitting its input between two accumulators, one unannotated, returned together at the
/// exit (<c>| [] -> (known, conflicting)</c>) while a second unannotated parameter is passed through
/// unchanged. The unannotated accumulator's layout is unresolved where the exit tuple is built, so
/// the tuple stays in the arena and stores a retain of it that nothing released: the whole
/// accumulator was left alive at every call.
/// </summary>
internal sealed class UnresolvedParameterInArenaExitTupleTemplate() : LeakCombinationTemplate(
    "leak.unresolved-parameter-in-arena-exit-tuple", "unresolvedExitTuple", GeneratedFeature.Tuple, GeneratedFeature.Match, GeneratedFeature.TailCall)
{
    protected override string Declarations(LeakNames names)
    {
        string entry = names.Of("LeakResolvedEntry");
        string other = names.Of("LeakConflict");
        string loop = names.Of("splitLoop");
        int knownShape = names.Variant("knownElement", 3);
        bool recordSibling = names.Coin("recordSibling");
        bool returnsExtra = names.Coin("returnsExtra");
        bool stringExtra = names.Coin("stringExtra");
        string word = names.Word();
        string known = knownShape switch
        {
            0 => $"Ashes.Text.fromInt(x) + {word}",
            1 => $"(Ashes.Text.fromInt(x), x)",
            _ => $"{entry}(label = Ashes.Text.fromInt(x) + {word}, index = x)",
        };
        string siblingType = recordSibling ? other : "Int";
        string sibling = recordSibling ? $"{other}(at = x)" : "x";
        string exit = returnsExtra ? "(known, conflicting, extra)" : "(known, conflicting)";
        string pattern = returnsExtra ? "(built, _, _)" : "(built, _)";
        string extra = stringExtra ? $"Ashes.Text.fromInt(seed) + {names.Word()}" : "seed";
        int modulus = names.Between(2, 4);
        string items = names.Ints(names.Between(2, 6), 0, 9);
        return $$"""
            type {{entry}} =
                | label: Str
                | index: Int

            type {{other}} =
                | at: Int

            let recursive {{loop}} (items: List(Int)) known (conflicting: List({{siblingType}})) extra =
                match items with
                    | [] -> {{exit}}
                    | x :: tail ->
                        if x % {{modulus}} == 0
                        then {{loop}}(tail)(known)({{sibling}} :: conflicting)(extra)
                        else {{loop}}(tail)(({{known}}) :: known)(conflicting)(extra)

            let {{names.Entry}} (seed: Int) =
                match {{loop}}(seed :: {{items}})([])([])({{extra}}) with
                    | {{pattern}} -> Ashes.Collection.List.length(built) + seed
            """;
    }
}
