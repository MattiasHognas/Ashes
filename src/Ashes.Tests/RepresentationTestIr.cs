using Ashes.Semantics;

namespace Ashes.Tests;

// The representation test gives every copy site two branches: a reference-counted value is retained
// (an RcDup right after the jump on its IsReferenceCounted result) and anything else is copied (the
// block under an rc_representation_copy or rc_tail_copy label). Tests that count the retains and
// copies the ownership design asks for leave out that per-site retain and fallback copy.
internal static class RepresentationTestIr
{
    public static int CountOutsideGuards(IEnumerable<IrInst> instructions, Func<IrInst, bool> predicate)
    {
        var testTemps = new HashSet<int>();
        bool inFallback = false;
        IrInst? previous = null;
        int count = 0;
        foreach (IrInst instruction in instructions)
        {
            switch (instruction)
            {
                case IrInst.IsReferenceCounted test:
                    testTemps.Add(test.Target);
                    break;
                case IrInst.Label label:
                    inFallback = label.Name.StartsWith("rc_representation_copy", StringComparison.Ordinal)
                        || label.Name.StartsWith("rc_tail_copy", StringComparison.Ordinal);
                    break;
            }

            bool guardRetain = instruction is IrInst.RcDup
                && previous is IrInst.JumpIfFalse jump
                && testTemps.Contains(jump.CondTemp);
            if (!inFallback && !guardRetain && predicate(instruction))
            {
                count++;
            }

            previous = instruction;
        }

        return count;
    }
}
