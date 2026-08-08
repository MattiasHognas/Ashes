using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Shrinking;

internal static class StableSizeMetric
{
    internal static int Measure(GeneratedFuzzCase testCase) => checked(testCase.NodeCount * 1024 + testCase.Source.Length);
}
