using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A trait method applied to a concrete operand (<c>Ashes.Trait.Show.show(counter)</c>), its string
/// result concatenated into a label the way stage 1's optimizer names a scalar-environment variant.
/// The method is its implementation's closure applied in place, outside the call path that settles a
/// reference-counted result, and without a request in its ownership word the closure handed over the
/// string <c>fromInt</c> made, which the caller released nowhere.
/// </summary>
internal sealed class TraitMethodResultTemplate() : LeakCombinationTemplate(
    "leak.trait-method-result", "traitMethodResult", GeneratedFeature.Match, GeneratedFeature.List)
{
    protected override string Declarations(LeakNames names)
    {
        string fn = names.Of("Fn");
        string variantOf = names.Of("variantOf");
        string suffix = names.Pick("__scalarenv", "__variant", "_v");
        int modulus = names.Between(3, 12);
        return $$"""
            type {{fn}} =
                | label: Str
                | body: List(Int)

            let {{variantOf}} (callee: {{fn}}) (counter: Int) = Some(callee with label = callee.label + "{{suffix}}" + Ashes.Trait.Show.show(counter))

            let {{names.Entry}} (seed: Int) =
                match {{variantOf}}({{fn}}(label = {{names.Word()}} + Ashes.Text.fromInt(seed), body = [seed]))(seed % {{modulus.ToString(System.Globalization.CultureInfo.InvariantCulture)}}) with
                    | Some(v) -> Ashes.Text.byteLength(v.label)
                    | None -> 0
            """;
    }
}
