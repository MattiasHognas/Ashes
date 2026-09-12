namespace Ashes.Semantics;

public static partial class IrOptimizer
{
    // CleanupResource names a closure's resource type with this label.
    private const string ClosureResourceTypeName = "Function";

    /// <summary>
    /// Drops the closure a top-level binding materializes when the program never reads that binding.
    /// Every top-level binding of an imported module is lowered, so the entry point builds a closure
    /// for each one and cleans it up again at scope exit; for a binding nothing else names, that pair
    /// is the whole lifetime of the value.
    /// </summary>
    /// <remarks>
    /// The closure is what keeps its lifted function reachable, so eliding the pair is what lets
    /// <see cref="PruneUnreachableFunctions"/> see the function as dead. A binding is elided only
    /// when its environment captures nothing but other elided bindings, which is what makes the
    /// removal ownership-neutral: no cleanup that releases something still live is dropped.
    /// </remarks>
    private static IrFunction ElideDeadTopLevelClosureBindings(IrFunction entry)
    {
        List<IrInst> instructions = entry.Instructions;
        var analysis = BindingAnalysis.Build(instructions);

        // Assume every closure construction is dead, then disqualify until the set is stable. A
        // binding captured only by bindings that themselves fall out is dead with them, so the
        // fixed point has to shrink rather than grow.
        var dead = new HashSet<int>(analysis.ClosureConstructions);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (int index in dead.ToList())
            {
                if (!IsDeadClosureBinding(index, instructions, analysis, dead))
                {
                    dead.Remove(index);
                    changed = true;
                }
            }
        }

        if (dead.Count == 0)
        {
            return entry;
        }

        HashSet<int> toRemove = CollectBindingRemovals(dead, instructions, analysis);
        var kept = new List<IrInst>(instructions.Count - toRemove.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (!toRemove.Contains(i))
            {
                kept.Add(instructions[i]);
            }
        }

        return entry with { Instructions = kept };
    }

    /// <summary>
    /// Checks that a closure construction flows into exactly one binding slot whose every reader
    /// either cleans the closure up or captures it into another dead binding's environment, and that
    /// its own environment holds nothing but such bindings.
    /// </summary>
    private static bool IsDeadClosureBinding(
        int index,
        List<IrInst> instructions,
        BindingAnalysis analysis,
        HashSet<int> dead)
    {
        (int target, int environmentPointer, int environmentSize) = DescribeClosure(instructions[index]);

        // The construction must reach exactly one store, and that store must own its slot outright.
        if (analysis.TempReaders(target) is not [int storeIndex]
            || instructions[storeIndex] is not IrInst.StoreLocal store
            || store.Source != target
            || analysis.SlotStores(store.Slot) is not [int soleStore]
            || soleStore != storeIndex)
        {
            return false;
        }

        foreach (int readerIndex in analysis.SlotReaders(store.Slot))
        {
            // An arena watermark or coroutine restore reads a slot without naming it in a LoadLocal;
            // such a reader is not a binding use this pass can account for.
            if (instructions[readerIndex] is not IrInst.LoadLocal load || load.Slot != store.Slot)
            {
                return false;
            }

            foreach (int useIndex in analysis.TempReaders(load.Target))
            {
                if (!IsDeadBindingUse(instructions[useIndex], load.Target, instructions, analysis, dead))
                {
                    return false;
                }
            }
        }

        return environmentSize == 0 || IsDeadClosureEnvironment(environmentPointer, index, instructions, analysis);
    }

    // A dead binding may only be released, or captured into an environment that is itself dead:
    // either another dead binding's, or one orphaned when its closure was already elided.
    private static bool IsDeadBindingUse(
        IrInst use,
        int temp,
        List<IrInst> instructions,
        BindingAnalysis analysis,
        HashSet<int> dead)
        => use switch
        {
            IrInst.CleanupResource cleanup => cleanup.SourceTemp == temp
                && string.Equals(cleanup.TypeName, ClosureResourceTypeName, StringComparison.Ordinal),
            IrInst.StoreMemOffset capture => capture.Source == temp
                && IsDeadEnvironment(capture.BasePtr, instructions, analysis, dead),
            _ => false,
        };

    /// <summary>
    /// Checks that an environment allocation can never be read: either the closure that would read
    /// it is itself being elided, or no closure owns it at all because one already was.
    /// </summary>
    private static bool IsDeadEnvironment(
        int environmentPointer,
        List<IrInst> instructions,
        BindingAnalysis analysis,
        HashSet<int> dead)
    {
        if (analysis.EnvironmentOwner(environmentPointer) is int owner)
        {
            return dead.Contains(owner);
        }

        if (analysis.TempDefinition(environmentPointer) is not int allocationIndex
            || instructions[allocationIndex] is not (IrInst.Alloc or IrInst.AllocStack))
        {
            return false;
        }

        // Nothing but capture writes reach the allocation, so its bytes are unobservable.
        foreach (int useIndex in analysis.TempReaders(environmentPointer))
        {
            if (instructions[useIndex] is not IrInst.StoreMemOffset capture
                || capture.BasePtr != environmentPointer)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks that a closure's environment is a fresh allocation written only with bindings this
    /// pass is already removing, so dropping the environment strands no live value.
    /// </summary>
    private static bool IsDeadClosureEnvironment(
        int environmentPointer,
        int closureIndex,
        List<IrInst> instructions,
        BindingAnalysis analysis)
    {
        if (analysis.TempDefinition(environmentPointer) is not int allocIndex
            || instructions[allocIndex] is not IrInst.Alloc)
        {
            return false;
        }

        foreach (int useIndex in analysis.TempReaders(environmentPointer))
        {
            if (useIndex == closureIndex)
            {
                continue;
            }

            // Anything but a capture write into this environment could observe the allocation.
            if (instructions[useIndex] is not IrInst.StoreMemOffset capture
                || capture.BasePtr != environmentPointer
                || analysis.TempDefinition(capture.Source) is not int sourceIndex
                || instructions[sourceIndex] is not IrInst.LoadLocal)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Collects every instruction that exists only to build, capture, or release a dead binding.</summary>
    private static HashSet<int> CollectBindingRemovals(
        HashSet<int> dead,
        List<IrInst> instructions,
        BindingAnalysis analysis)
    {
        var toRemove = new HashSet<int>();
        foreach (int index in dead)
        {
            (int target, int environmentPointer, int environmentSize) = DescribeClosure(instructions[index]);
            toRemove.Add(index);

            foreach (int storeIndex in analysis.TempReaders(target))
            {
                toRemove.Add(storeIndex);
                var store = (IrInst.StoreLocal)instructions[storeIndex];
                foreach (int readerIndex in analysis.SlotReaders(store.Slot))
                {
                    toRemove.Add(readerIndex);
                    var load = (IrInst.LoadLocal)instructions[readerIndex];
                    toRemove.UnionWith(analysis.TempReaders(load.Target));
                }
            }

            if (environmentSize == 0)
            {
                // The environment pointer is a shared constant, removable only once nothing reads it.
                if (analysis.TempDefinition(environmentPointer) is int constantIndex
                    && instructions[constantIndex] is IrInst.LoadConstInt
                    && analysis.TempReaders(environmentPointer) is [int soleReader]
                    && soleReader == index)
                {
                    toRemove.Add(constantIndex);
                }

                continue;
            }

            toRemove.Add(analysis.TempDefinition(environmentPointer)!.Value);
            toRemove.UnionWith(analysis.TempReaders(environmentPointer));
        }

        return toRemove;
    }

    private static (int Target, int EnvironmentPointer, int EnvironmentSize) DescribeClosure(IrInst instruction)
        => instruction switch
        {
            IrInst.MakeClosure closure => (closure.Target, closure.EnvPtrTemp, closure.EnvSizeBytes),
            IrInst.MakeClosureStack closure => (closure.Target, closure.EnvPtrTemp, closure.EnvSizeBytes),
            _ => throw new InvalidOperationException($"Not a closure construction: {instruction.GetType().Name}."),
        };

    /// <summary>
    /// Use-def tables over one function's instructions: which instructions read a temp or a slot,
    /// which instruction defines a temp, and which closure owns an environment allocation.
    /// </summary>
    private sealed class BindingAnalysis
    {
        private static readonly int[] NoIndices = [];

        private readonly Dictionary<int, List<int>> _tempReaders = [];
        private readonly Dictionary<int, List<int>> _slotReaders = [];
        private readonly Dictionary<int, List<int>> _slotStores = [];
        private readonly Dictionary<int, int> _tempDefinition = [];
        private readonly Dictionary<int, int> _environmentOwner = [];

        public List<int> ClosureConstructions { get; } = [];

        public static BindingAnalysis Build(List<IrInst> instructions)
        {
            var analysis = new BindingAnalysis();
            var temps = new HashSet<int>();
            for (int i = 0; i < instructions.Count; i++)
            {
                IrInst instruction = instructions[i];

                temps.Clear();
                CollectUsedTemps(instruction, temps);
                foreach (int temp in temps)
                {
                    Append(analysis._tempReaders, temp, i);
                }

                foreach (int slot in StateMachineTransform.GetReadLocalSlots(instruction))
                {
                    Append(analysis._slotReaders, slot, i);
                }

                analysis.RecordDefinitions(instruction, i);
            }

            return analysis;
        }

        public IReadOnlyList<int> TempReaders(int temp)
            => _tempReaders.TryGetValue(temp, out List<int>? readers) ? readers : NoIndices;

        public IReadOnlyList<int> SlotReaders(int slot)
            => _slotReaders.TryGetValue(slot, out List<int>? readers) ? readers : NoIndices;

        public IReadOnlyList<int> SlotStores(int slot)
            => _slotStores.TryGetValue(slot, out List<int>? stores) ? stores : NoIndices;

        public int? TempDefinition(int temp)
            => _tempDefinition.TryGetValue(temp, out int index) ? index : null;

        public int? EnvironmentOwner(int environmentPointer)
            => _environmentOwner.TryGetValue(environmentPointer, out int index) ? index : null;

        private void RecordDefinitions(IrInst instruction, int index)
        {
            switch (instruction)
            {
                case IrInst.StoreLocal store:
                    Append(_slotStores, store.Slot, index);
                    break;
                case IrInst.LoadLocal load:
                    _tempDefinition[load.Target] = index;
                    break;
                case IrInst.LoadConstInt constant:
                    _tempDefinition[constant.Target] = index;
                    break;
                case IrInst.AllocStack stackAllocation:
                    _tempDefinition[stackAllocation.Target] = index;
                    break;
                case IrInst.Alloc { RuntimeManaged: false } allocation:
                    _tempDefinition[allocation.Target] = index;
                    break;

                // A reference-counted closure is released through RC rather than a cleanup pair, so
                // only the arena and stack forms are candidates here.
                case IrInst.MakeClosure { RuntimeManaged: false } closure:
                    _tempDefinition[closure.Target] = index;
                    _environmentOwner[closure.EnvPtrTemp] = index;
                    ClosureConstructions.Add(index);
                    break;
                case IrInst.MakeClosureStack closure:
                    _tempDefinition[closure.Target] = index;
                    _environmentOwner[closure.EnvPtrTemp] = index;
                    ClosureConstructions.Add(index);
                    break;
                default:
                    break;
            }
        }

        private static void Append(Dictionary<int, List<int>> table, int key, int index)
        {
            if (!table.TryGetValue(key, out List<int>? indices))
            {
                indices = [];
                table[key] = indices;
            }

            indices.Add(index);
        }
    }
}
