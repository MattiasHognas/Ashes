using Ashes.Frontend;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

internal sealed class RuneRoundTripTemplate : ICombinationTemplate
{
    public string Id => "unicode.rune-roundtrip";
    public IReadOnlySet<GeneratedFeature> AdvertisedFeatures { get; } = new SortedSet<GeneratedFeature>
    {
        GeneratedFeature.Let,
        GeneratedFeature.Match,
        GeneratedFeature.RuneLiteral,
        GeneratedFeature.RuneRoundTrip,
    };

    public bool CanApply(AshesType resultType, GenerationContext context, GenerationBudget budget) =>
        resultType == AshesType.Rune && budget.RemainingNodes >= 11;

    public GenerationResult<Expr> Generate(
        AshesType resultType,
        GenerationContext context,
        GenerationBudget budget,
        ExpressionGenerator expressions,
        FuzzRandom random)
    {
        int scalar = new[] { 0x41, 0xE9, 0x20AC, 0x1F600, 0x10FFFF }[random.Next(5)];
        string original = "originalRune" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string decoded = "decodedRune" + random.Next(10000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr uncons = new Expr.Call(
            new Expr.QualifiedVar("Ashes.Text", "uncons"),
            new Expr.Call(new Expr.QualifiedVar("Ashes.Rune", "toText"), new Expr.Var(original)));
        Expr value = new Expr.Let(
            original,
            new Expr.RuneLit(scalar),
            new Expr.Match(uncons,
            [
                new MatchCase(
                    new Pattern.Constructor("Some", [new Pattern.Tuple([new Pattern.Var(decoded), new Pattern.Wildcard()])]),
                    new Expr.Var(decoded)),
                new MatchCase(new Pattern.Constructor("None", []), new Expr.Var(original)),
            ]));
        return new GenerationResult<Expr>(
            value,
            resultType,
            new GeneratedFeatureSet(AdvertisedFeatures),
            new GenerationTrace(["unicode:rune-roundtrip"]),
            11);
    }
}
