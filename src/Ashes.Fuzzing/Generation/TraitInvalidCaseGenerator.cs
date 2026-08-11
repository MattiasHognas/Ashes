using Ashes.Frontend;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Coverage;

namespace Ashes.Fuzzing.Generation;

using AshesFormatter = Ashes.Formatter.Formatter;

internal static class TraitInvalidCaseGenerator
{
    internal static GeneratedFuzzCase Generate(
        ulong masterSeed,
        int caseIndex,
        FuzzProfile profile,
        int maximumNodes)
    {
        (string source, string diagnostic, string trace) = Source(caseIndex % 6);
        Diagnostics diagnostics = new();
        Ashes.Frontend.Program program = new Parser(source, diagnostics).ParseProgram();
        if (diagnostics.StructuredErrors.Count != 0)
        {
            throw new InvalidOperationException(
                "Invalid-semantic seed must parse successfully: " + string.Join("; ", diagnostics.Errors));
        }
        string formatted = AshesFormatter.Format(program);
        GenerationBudget budget = GenerationBudget.Create(maximumNodes);
        AstCoverageMetrics metrics = AstCoverageMetrics.Measure(program);
        GeneratedFeatureSet features = new(
        [
            GeneratedFeature.TraitDeclaration,
            GeneratedFeature.TraitImplementation,
        ]);
        GeneratedFuzzCase result = new(
            masterSeed,
            FuzzRandom.DeriveCaseSeed(masterSeed, caseIndex),
            caseIndex,
            profile.Id,
            AshesType.Int,
            program,
            formatted,
            features,
            new GenerationTrace(["invalid-semantic:" + trace]),
            metrics.Nodes,
            budget)
        {
            ExpectedDiagnosticCodes = new SortedSet<string>([diagnostic], StringComparer.Ordinal),
        };
        return result;
    }

    private static (string Source, string Diagnostic, string Trace) Source(int kind) => kind switch
    {
        0 => ("""
            trait FuzzOverlap(a) =
                | equal : a -> a -> Bool

            implement FuzzOverlap(List(a)) =
                | equal = given (left) -> given (right) -> true

            implement FuzzOverlap(List(Int)) =
                | equal = given (left) -> given (right) -> true

            0
            """, DiagnosticCodes.TraitCoherence, "overlap"),
        1 => ("""
            implement Eq(Int) =
                | equal = given (left) -> given (right) -> left == right

            0
            """, DiagnosticCodes.TraitCoherence, "orphan"),
        2 => ("""
            let ambiguous : Bool requires {Default(a)} = true
            ambiguous
            """, DiagnosticCodes.InvalidTraitDeclaration, "ambiguity"),
        3 => ("""
            type FuzzMissing =
                | FuzzMissing

            Eq.equal(FuzzMissing)(FuzzMissing)
            """, DiagnosticCodes.TraitResolution, "missing-implementation"),
        4 => ("""
            trait FuzzFirst(a) requires {FuzzSecond(a)} =
                | first : a -> a

            trait FuzzSecond(a) requires {FuzzFirst(a)} =
                | second : a -> a

            0
            """, DiagnosticCodes.TraitCycle, "supertrait-cycle"),
        _ => ("""
            trait FuzzRequired(a) =
                | required : a -> a

            implement FuzzRequired(Int) =
                | unknown = given (value) -> value

            0
            """, DiagnosticCodes.InvalidTraitImplementation, "invalid-implementation"),
    };
}
