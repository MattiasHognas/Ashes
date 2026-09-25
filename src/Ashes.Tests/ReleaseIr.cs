using Ashes.Semantics;

namespace Ashes.Tests;

// A record of the ownership contract is released through a dropper synthesized once per type, so a
// release a test looks for may sit inline or behind a call to such a dropper. These count both: an
// inline runtime-managed RcDrop matching the predicate, and each call to a synthesized dropper
// whose body releases a matching value, directly or through further such calls.
internal static class ReleaseIr
{
    public static int CountReleases(IrProgram program, IEnumerable<IrInst> instructions, Func<IrInst.RcDrop, bool> matches)
    {
        Dictionary<string, IrFunction> functions = program.Functions.ToDictionary(function => function.Label, StringComparer.Ordinal);
        var releasing = new Dictionary<string, bool>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        return instructions.Count(instruction => Releases(instruction, functions, releasing, visiting, matches));
    }

    public static int CountReleases(IrProgram program, IEnumerable<IrInst> instructions, string typeName)
        => CountReleases(program, instructions, drop => string.Equals(drop.TypeName, typeName, StringComparison.Ordinal));

    public static bool Releases(IrProgram program, IEnumerable<IrInst> instructions, string typeName)
        => CountReleases(program, instructions, typeName) > 0;

    private static bool Releases(
        IrInst instruction,
        Dictionary<string, IrFunction> functions,
        Dictionary<string, bool> releasing,
        HashSet<string> visiting,
        Func<IrInst.RcDrop, bool> matches)
        => instruction switch
        {
            IrInst.RcDrop drop => drop.RuntimeManaged && matches(drop),
            IrInst.CallKnown call => DropperReleases(functions, releasing, visiting, call.FuncLabel, matches),
            _ => false,
        };

    // A dropper releasing its own type calls itself: the walk already in progress answers no there.
    private static bool DropperReleases(
        Dictionary<string, IrFunction> functions,
        Dictionary<string, bool> releasing,
        HashSet<string> visiting,
        string label,
        Func<IrInst.RcDrop, bool> matches)
    {
        if (!label.StartsWith("__rcdrop", StringComparison.Ordinal) || !functions.TryGetValue(label, out IrFunction? function))
        {
            return false;
        }

        if (releasing.TryGetValue(label, out bool known))
        {
            return known;
        }

        if (!visiting.Add(label))
        {
            return false;
        }

        bool result = function.Instructions.Any(instruction => Releases(instruction, functions, releasing, visiting, matches));
        visiting.Remove(label);
        releasing[label] = result;
        return result;
    }
}
