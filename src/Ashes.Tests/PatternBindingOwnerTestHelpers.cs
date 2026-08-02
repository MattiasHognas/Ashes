using Ashes.Semantics;

namespace Ashes.Tests;

/// <summary>
/// Reads how many pattern-extracted references received a protective lexical owner, and where that
/// owner is materialized in the emitted IR: a runtime-managed <see cref="IrInst.RcDup"/> of the
/// binding's own slot stored straight back into that slot.
/// </summary>
internal static class PatternBindingOwnerTestHelpers
{
    public static int CountProtectivePatternBindingOwners(this Lowering lowering)
        => lowering.PatternBindingOwnershipDecisions
            .Count(decision => decision.PlacementOutcome == PatternBindingPlacementOutcome.ProtectiveOwnerPlaced);

    public static IReadOnlyList<int> PatternBindingOwnerSlots(this IrFunction function)
        => [.. PatternBindingOwnerDups(function).Select(pair => pair.Slot)];

    public static IReadOnlyList<(int Slot, IrInst.RcDup Dup)> PatternBindingOwnerDups(this IrFunction function)
    {
        var slots = new List<(int Slot, IrInst.RcDup Dup)>();
        IReadOnlyList<IrInst> instructions = function.Instructions;
        for (int i = 1; i + 1 < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.RcDup { RuntimeManaged: true } dup
                && instructions[i - 1] is IrInst.LoadLocal load
                && load.Target == dup.SourceTemp
                && instructions[i + 1] is IrInst.StoreLocal store
                && store.Source == dup.Target
                && store.Slot == load.Slot)
            {
                slots.Add((load.Slot, dup));
            }
        }

        return slots;
    }
}
