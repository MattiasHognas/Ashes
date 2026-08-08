namespace Ashes.Fuzzing.Generation;

internal sealed class FuzzRandom
{
    private ulong _state;

    internal FuzzRandom(ulong seed) => _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    internal ulong NextUInt64()
    {
        ulong value = _state;
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        _state = value;
        return value * 2685821657736338717UL;
    }

    internal int Next(int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        }
        return (int)(NextUInt64() % (uint)exclusiveMaximum);
    }

    internal bool NextBool() => (NextUInt64() & 1UL) != 0;

    internal static ulong DeriveCaseSeed(ulong masterSeed, int caseIndex)
    {
        ulong value = masterSeed + 0x9E3779B97F4A7C15UL * (ulong)(caseIndex + 1);
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}
