using Ashes.Fuzzing.Combinations;
using Ashes.Fuzzing.Configuration;
using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Tests;

internal static class TestFixture
{
    internal static (GeneratorRegistry Rules, CombinationRegistry Combinations, FuzzProfileRegistry Profiles, ProgramGenerator Generator) Create()
    {
        GeneratorRegistry rules = GeneratorRegistry.CreateDefault();
        CombinationRegistry combinations = CombinationRegistry.CreateDefault();
        FuzzProfileRegistry profiles = FuzzProfileRegistry.CreateDefault(rules, combinations);
        return (rules, combinations, profiles, new ProgramGenerator(rules, combinations));
    }
}
