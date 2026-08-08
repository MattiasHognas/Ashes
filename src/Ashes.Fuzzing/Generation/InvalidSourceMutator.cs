namespace Ashes.Fuzzing.Generation;

internal sealed class InvalidSourceMutator
{
    internal string Mutate(string source, ulong seed)
    {
        FuzzRandom random = new(seed);
        if (source.Length == 0)
        {
            return "\u0000";
        }
        int position = random.Next(source.Length);
        return random.Next(7) switch
        {
            0 => source.Remove(position, 1),
            1 => source.Insert(position, source[position].ToString()),
            2 => source.Insert(position, "match "),
            3 => source[..position],
            4 => source.Insert(position, "\u2603"),
            5 => source.Insert(position, "\""),
            _ => source.Insert(position, ")]}"),
        };
    }
}
