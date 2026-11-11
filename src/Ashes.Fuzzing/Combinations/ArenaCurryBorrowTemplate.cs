using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>A pure arena curry stage holds a borrow until its result is applied.</summary>
internal sealed class ArenaCurryBorrowTemplate() : LeakCombinationTemplate(
    "leak.arena-curry-borrow", "arenaCurryBorrow", GeneratedFeature.Match)
{
    protected override IReadOnlyList<string> RequiredImports => ["Ashes.Text", "Ashes.Collection.Map"];

    protected override string Declarations(LeakNames names)
        => $$"""
            let {{names.Entry}} (seed: Int) =
                let source = {{names.Word()}} + (if seed % 2 == 0 then {{names.Word()}} else {{names.Word()}}) in
                let index = Ashes.Collection.Map.setStr(source)(seed)(Ashes.Collection.Map.empty) in
                if seed % 2 == 0
                then Ashes.Text.length(source) + Ashes.Collection.Map.size(index) + seed
                else Ashes.Collection.Map.size(index) + Ashes.Text.length(source) + seed
            """;
}
