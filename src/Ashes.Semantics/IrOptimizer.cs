namespace Ashes.Semantics;

/// <summary>
/// IR-level optimization pass pipeline.
/// Runs after semantic lowering, before the backend.
/// All optimizations are invisible to the user — observable behaviour is identical.
/// </summary>
public static class IrOptimizer
{
    /// <summary>
    /// Runs the full optimization pipeline on the given IR program.
    /// Returns a new IrProgram with optimized instructions.
    /// </summary>
    public static IrProgram Optimize(IrProgram program)
    {
        // Aggressive compile-time evaluation runs first: it reduces pure, constant-argument
        // calls to constants, after which the per-function passes below eliminate the now-dead
        // argument/closure construction and the redundant arena brackets around the removed call.
        program = IrCompileTimeEval.Evaluate(program);

        var optimizedEntry = OptimizeFunction(program.EntryFunction);
        var optimizedFuncs = program.Functions.Select(OptimizeFunction).ToList();

        // Interprocedural: strip arena save/restore/reclaim brackets that provably guard no
        // allocation. Runs after the per-function passes so devirtualized calls (CallKnown) and
        // dead MakeClosures are already resolved, and needs whole-program non-allocation
        // summaries for known callees.
        var nonAllocating = ComputeNonAllocatingFunctions(optimizedEntry, optimizedFuncs);
        optimizedEntry = StripRedundantArenaBrackets(optimizedEntry, nonAllocating);
        optimizedFuncs = optimizedFuncs.Select(f => StripRedundantArenaBrackets(f, nonAllocating)).ToList();

        return program with
        {
            EntryFunction = optimizedEntry,
            Functions = optimizedFuncs,
        };
    }

    private static IrFunction OptimizeFunction(IrFunction function)
    {
        var instructions = function.Instructions;

        // Pass ordering matters — each pass may enable further optimizations in subsequent passes.
        instructions = ElideTrivialOwnershipCopies(instructions);
        instructions = SinkRuntimeRcDupsIntoDiamonds(instructions);
        instructions = FuseAdjacentRuntimeRcPairs(instructions);
        instructions = DevirtualizeKnownClosureCalls(instructions);
        instructions = FoldConstants(instructions);
        instructions = ReduceIdentitiesAndStrength(instructions);
        instructions = ElideUnreachableCode(instructions);
        instructions = ElideDeadCode(instructions);
        instructions = ElideErasedRcDrops(instructions);

        return function with
        {
            Instructions = instructions,
        };
    }

    private readonly record struct RuntimeRcDupSinkPlan(
        int DupIndex,
        int InsertIndex,
        int DeadDropIndex,
        IrInst.RcDup Dup);

    // Sink a runtime duplicate from immediately before a simple if/else diamond into the only
    // branch that meaningfully consumes it. The other branch must merely drop the duplicate and
    // must not use the source, since doing so could observe the reference-count change.
    private static List<IrInst> SinkRuntimeRcDupsIntoDiamonds(List<IrInst> instructions)
    {
        List<IrInst> current = instructions;
        while (TryFindRuntimeRcDupSink(current, out RuntimeRcDupSinkPlan plan))
        {
            List<IrInst> rewritten = new(current.Count - 1);
            for (int i = 0; i < current.Count; i++)
            {
                if (i == plan.InsertIndex)
                {
                    rewritten.Add(plan.Dup);
                }

                if (i != plan.DupIndex && i != plan.DeadDropIndex)
                {
                    rewritten.Add(current[i]);
                }
            }

            current = rewritten;
        }

        return current;
    }

    private static bool TryFindRuntimeRcDupSink(
        List<IrInst> instructions,
        out RuntimeRcDupSinkPlan plan)
    {
        for (int i = 0; i + 2 < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.RcDup { RuntimeManaged: true } dup
                || instructions[i + 1] is not IrInst.JumpIfFalse branch
                || !TryDescribeDiamond(instructions, i + 2, branch.Target, out int elseIndex, out int endIndex))
            {
                continue;
            }

            int thenStart = i + 2;
            int thenEnd = elseIndex - 1;
            int elseStart = elseIndex + 1;
            int elseEnd = endIndex;
            List<int> thenDrops = FindRuntimeRcDrops(instructions, thenStart, thenEnd, dup.Target);
            List<int> elseDrops = FindRuntimeRcDrops(instructions, elseStart, elseEnd, dup.Target);
            bool thenUses = HasMeaningfulTempUse(instructions, thenStart, thenEnd, dup.Target);
            bool elseUses = HasMeaningfulTempUse(instructions, elseStart, elseEnd, dup.Target);
            if (thenDrops.Count != 1 || elseDrops.Count != 1 || thenUses == elseUses
                || IsTempUsedInRange(instructions, endIndex + 1, instructions.Count, dup.Target))
            {
                continue;
            }

            int unusedStart = thenUses ? elseStart : thenStart;
            int unusedEnd = thenUses ? elseEnd : thenEnd;
            if (!IsRuntimeRcDropOnlyBranch(instructions, unusedStart, unusedEnd, dup.Target))
            {
                continue;
            }

            plan = thenUses
                ? new RuntimeRcDupSinkPlan(i, thenStart, elseDrops[0], dup)
                : new RuntimeRcDupSinkPlan(i, elseStart, thenDrops[0], dup);
            return true;
        }

        plan = default;
        return false;
    }

    private static bool IsRuntimeRcDropOnlyBranch(
        List<IrInst> instructions,
        int start,
        int end,
        int temp)
    {
        for (int i = start; i < end; i++)
        {
            if (instructions[i] is not IrInst.RcDrop { SourceTemp: var source, RuntimeManaged: true }
                || source != temp)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDescribeDiamond(
        List<IrInst> instructions,
        int thenStart,
        string elseLabel,
        out int elseIndex,
        out int endIndex)
    {
        elseIndex = instructions.FindIndex(thenStart, inst => inst is IrInst.Label { Name: var name }
            && string.Equals(name, elseLabel, StringComparison.Ordinal));
        if (elseIndex <= thenStart
            || instructions[elseIndex - 1] is not IrInst.Jump endJump)
        {
            endIndex = -1;
            return false;
        }

        endIndex = instructions.FindIndex(elseIndex + 1, inst => inst is IrInst.Label { Name: var name }
            && string.Equals(name, endJump.Target, StringComparison.Ordinal));
        return endIndex > elseIndex;
    }

    private static List<int> FindRuntimeRcDrops(
        List<IrInst> instructions,
        int start,
        int end,
        int temp)
    {
        List<int> drops = [];
        for (int i = start; i < end; i++)
        {
            if (instructions[i] is IrInst.RcDrop { SourceTemp: var source, RuntimeManaged: true }
                && source == temp)
            {
                drops.Add(i);
            }
        }

        return drops;
    }

    private static bool HasMeaningfulTempUse(
        List<IrInst> instructions,
        int start,
        int end,
        int temp)
    {
        HashSet<int> usedTemps = [];
        for (int i = start; i < end; i++)
        {
            if (instructions[i] is IrInst.RcDrop { SourceTemp: var source, RuntimeManaged: true }
                && source == temp)
            {
                continue;
            }

            usedTemps.Clear();
            CollectUsedTemps(instructions[i], usedTemps);
            if (usedTemps.Contains(temp))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTempUsedInRange(
        List<IrInst> instructions,
        int start,
        int end,
        int temp)
    {
        HashSet<int> usedTemps = [];
        for (int i = start; i < end; i++)
        {
            usedTemps.Clear();
            CollectUsedTemps(instructions[i], usedTemps);
            if (usedTemps.Contains(temp))
            {
                return true;
            }
        }

        return false;
    }

    // Adjacent runtime dup/drop fusion. No instruction may occur between the pair because an
    // RcIsUnique or arbitrary call could observe the temporary increment. Dropping the duplicate
    // cancels the split outright; dropping the source transfers its ownership to the identity-
    // preserving duplicate, whose later uses can be remapped back to the source temp.
    private static List<IrInst> FuseAdjacentRuntimeRcPairs(List<IrInst> instructions)
    {
        List<IrInst> result = new(instructions.Count);
        Dictionary<int, int> remap = [];

        for (int i = 0; i < instructions.Count; i++)
        {
            IrInst instruction = RemapSourceTemps(instructions[i], remap);
            if (instruction is not IrInst.RcDup { RuntimeManaged: true } dup
                || i + 1 >= instructions.Count
                || RemapSourceTemps(instructions[i + 1], remap) is not IrInst.RcDrop { RuntimeManaged: true } drop)
            {
                result.Add(instruction);
                continue;
            }

            if (drop.SourceTemp == dup.Target && !IsTempUsedAfter(instructions, i + 2, dup.Target))
            {
                i++;
                continue;
            }

            if (drop.SourceTemp == dup.SourceTemp)
            {
                remap[dup.Target] = dup.SourceTemp;
                i++;
                continue;
            }

            result.Add(instruction);
        }

        return result;
    }

    private static bool IsTempUsedAfter(List<IrInst> instructions, int startIndex, int temp)
    {
        HashSet<int> usedTemps = [];
        for (int i = startIndex; i < instructions.Count; i++)
        {
            usedTemps.Clear();
            CollectUsedTemps(instructions[i], usedTemps);
            if (usedTemps.Contains(temp))
            {
                return true;
            }
        }

        return false;
    }

    // Redundant arena-bracket elision
    // Lowering brackets every function body and every copy-type-returning helper call in
    // SaveArenaState / RestoreArenaState / ReclaimArenaChunks. For a region that provably
    // performs no arena allocation the bracket is pure overhead — worse, the reclaim's chunk
    // loop (a syscall loop with a dynamic stack slot) makes tiny accessors like Map.height
    // ineligible for LLVM inlining. Two eliminations, both conservative:
    //  (a) whole function: if every instruction of a function is non-allocating (direct calls
    //      only to non-allocating functions, via a whole-program fixpoint), every arena
    //      bracket instruction in it is a no-op and is removed;
    //  (b) straight-line caller regions: a Save…Restore(+Reclaim) triple with no label, jump,
    //      or potentially-allocating instruction between save and restore is removed.
    // Anything not on the explicit non-allocating whitelist (indirect calls, externals,
    // intrinsics that build values, copy-outs, allocs) keeps its brackets.

    private static bool IsNonAllocatingInst(IrInst inst, HashSet<string> nonAllocatingFns) => inst switch
    {
        IrInst.LoadConstInt or IrInst.LoadConstFloat or IrInst.LoadConstBool or IrInst.LoadConstStr
            or IrInst.LoadLocal or IrInst.StoreLocal or IrInst.LoadEnv
            or IrInst.LoadMemOffset or IrInst.StoreMemOffset
            or IrInst.AddInt or IrInst.SubInt or IrInst.MulInt or IrInst.DivInt or IrInst.DivUInt
            or IrInst.AndInt or IrInst.OrInt or IrInst.XorInt or IrInst.ShlInt or IrInst.ShrInt
            or IrInst.AddFloat or IrInst.SubFloat or IrInst.MulFloat or IrInst.DivFloat
            or IrInst.IntToFloat or IrInst.FloatToInt or IrInst.FloatUnaryIntrinsic or IrInst.CallLibm
            or IrInst.CmpIntGt or IrInst.CmpIntGe or IrInst.CmpIntLt or IrInst.CmpIntLe
            or IrInst.CmpUIntGt or IrInst.CmpUIntGe or IrInst.CmpUIntLt or IrInst.CmpUIntLe
            or IrInst.CmpIntEq or IrInst.CmpIntNe
            or IrInst.CmpFloatGt or IrInst.CmpFloatGe or IrInst.CmpFloatLt or IrInst.CmpFloatLe
            or IrInst.CmpFloatEq or IrInst.CmpFloatNe
            or IrInst.CmpStrEq or IrInst.CmpStrNe
            or IrInst.LoadFuncAddr or IrInst.GetAdtTag or IrInst.GetAdtField or IrInst.SetAdtField
            or IrInst.Borrow or IrInst.RcDup or IrInst.RcDrop or IrInst.RcIsUnique
            or IrInst.BytesLength or IrInst.BytesGet or IrInst.BytesCompare or IrInst.BytesIndexOf
            or IrInst.BytesHash or IrInst.BytesGetU16Le or IrInst.BytesGetU32Le or IrInst.BytesGetU64Le
            or IrInst.TextByteLength
            or IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks
            or IrInst.SaveStackPointer or IrInst.RestoreStackPointer
            or IrInst.Label or IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag or IrInst.Return
            => true,
        IrInst.CallKnown ck => nonAllocatingFns.Contains(ck.FuncLabel),
        _ => false,
    };

    private static HashSet<string> ComputeNonAllocatingFunctions(IrFunction entry, IReadOnlyList<IrFunction> functions)
    {
        // Least fixpoint: start from "every function might be non-allocating", knock out any
        // whose body contains a non-whitelisted instruction or a known call to a knocked-out
        // callee, and iterate until stable. The entry function is never a callee, so it is not
        // in the candidate set.
        var candidates = new HashSet<string>(functions.Select(f => f.Label), StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var f in functions)
            {
                if (!candidates.Contains(f.Label))
                {
                    continue;
                }

                foreach (var inst in f.Instructions)
                {
                    if (!IsNonAllocatingInst(inst, candidates))
                    {
                        candidates.Remove(f.Label);
                        changed = true;
                        break;
                    }
                }
            }
        }

        return candidates;
    }

    private static IrFunction StripRedundantArenaBrackets(IrFunction function, HashSet<string> nonAllocatingFns)
    {
        var instructions = function.Instructions;

        // (a) Whole-function elision: nothing in this function can move the arena cursor, so
        // every save/restore/reclaim in it observes and restores an unchanged arena.
        bool wholeFunction = nonAllocatingFns.Contains(function.Label);
        if (!wholeFunction)
        {
            // The entry function has no label in the candidate set; check it directly.
            wholeFunction = instructions.All(i => IsNonAllocatingInst(i, nonAllocatingFns));
        }

        if (wholeFunction)
        {
            if (!instructions.Any(i => i is IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks))
            {
                return function;
            }

            var stripped = instructions
                .Where(i => i is not (IrInst.SaveArenaState or IrInst.RestoreArenaState or IrInst.ReclaimArenaChunks))
                .ToList();
            return function with { Instructions = stripped };
        }

        // (b) Straight-line region elision within an allocating function.
        var toRemove = FindStraightLineBracketRemovals(instructions, nonAllocatingFns);

        if (toRemove.Count == 0)
        {
            return function;
        }

        var result = new List<IrInst>(instructions.Count - toRemove.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (!toRemove.Contains(i))
            {
                result.Add(instructions[i]);
            }
        }

        return function with { Instructions = result };
    }

    /// <summary>
    /// Finds the instruction indices of Save…Restore(+Reclaim) arena-bracket triples with no
    /// label, jump, or potentially-allocating instruction between save and restore.
    /// </summary>
    private static HashSet<int> FindStraightLineBracketRemovals(List<IrInst> instructions, HashSet<string> nonAllocatingFns)
    {
        var toRemove = new HashSet<int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.SaveArenaState save)
            {
                continue;
            }

            for (int j = i + 1; j < instructions.Count; j++)
            {
                var inst = instructions[j];
                if (inst is IrInst.RestoreArenaState restore
                    && restore.CursorLocalSlot == save.CursorLocalSlot
                    && restore.EndLocalSlot == save.EndLocalSlot)
                {
                    toRemove.Add(i);
                    toRemove.Add(j);
                    if (j + 1 < instructions.Count
                        && instructions[j + 1] is IrInst.ReclaimArenaChunks reclaim
                        && reclaim.SavedEndSlot == save.EndLocalSlot
                        && reclaim.PreRestoreEndSlot == restore.PreRestoreEndSlot)
                    {
                        toRemove.Add(j + 1);
                    }

                    break;
                }

                // Any control flow, allocation, or unknown instruction ends the attempt.
                if (inst is IrInst.Label or IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag
                    || !IsNonAllocatingInst(inst, nonAllocatingFns))
                {
                    break;
                }
            }
        }

        return toRemove;
    }

    // Known-closure devirtualization
    // A CallClosure whose closure temp is produced by MakeClosure/MakeClosureStack with a
    // statically-known function label becomes a direct CallKnown of that label with the
    // closure's captured env pointer. The indirect call through the closure struct's code
    // pointer is opaque to LLVM, so callees like tiny accessors could never be inlined; a
    // direct call inlines normally. Safety: only single-definition temps are rewritten (a
    // unique definition dominates every use in well-formed IR), and the env temp must itself
    // be single-definition so its value at the call site equals the value stored into the
    // closure at construction. Closures whose target is also stored/escapes elsewhere keep
    // their MakeClosure (dead-code elimination removes it only when no use remains).

    private static List<IrInst> DevirtualizeKnownClosureCalls(List<IrInst> instructions)
    {
        // Count definitions per temp; remember the defining instruction of single-def temps.
        var defCount = new Dictionary<int, int>();
        var singleDef = new Dictionary<int, IrInst>();
        foreach (var inst in instructions)
        {
            foreach (var d in StateMachineTransform.GetDefinedTemps(inst))
            {
                defCount[d] = defCount.GetValueOrDefault(d) + 1;
                singleDef[d] = inst;
            }
        }

        bool changed = false;
        var result = new List<IrInst>(instructions.Count);
        foreach (var inst in instructions)
        {
            if (inst is IrInst.CallClosure cc
                && defCount.GetValueOrDefault(cc.ClosureTemp) == 1
                && singleDef.TryGetValue(cc.ClosureTemp, out var def))
            {
                (string Label, int EnvTemp)? known = def switch
                {
                    IrInst.MakeClosure mk => (mk.FuncLabel, mk.EnvPtrTemp),
                    IrInst.MakeClosureStack mks => (mks.FuncLabel, mks.EnvPtrTemp),
                    _ => null,
                };
                if (known is { } k && defCount.GetValueOrDefault(k.EnvTemp) == 1)
                {
                    result.Add(new IrInst.CallKnown(cc.Target, k.Label, k.EnvTemp, cc.ArgTemp) { Location = cc.Location });
                    changed = true;
                    continue;
                }
            }

            result.Add(inst);
        }

        return changed ? result : instructions;
    }

    // Trivial ownership-copy elision
    // Remove erased RcDup markers and eligible Borrow instructions, remapping all uses of their
    // targets back to the original source temp.
    //
    // Elidable borrows:
    // (a) Copy-type sources: when the source temp is produced by
    //     LoadConstInt / LoadConstFloat / LoadConstBool. Copy types have no
    //     ownership semantics, so the borrow is semantically a no-op.
    // (b) Single-use borrows: when the borrow target is used exactly once.
    //     The borrowed reference is consumed at a single point, so it is safe
    //     to substitute the original source directly.
    //
    // Chains of borrows (Borrow(t2, t1) where t1 itself was remapped) are
    // resolved transitively so that all uses point back to the original source.

    private static List<IrInst> ElideTrivialOwnershipCopies(List<IrInst> instructions)
    {
        // Build use-def information.
        var (copyTypeProducers, useCount) = CollectBorrowElisionInfo(instructions);

        // Identify elidable Borrows and build a remap table.
        var remap = new Dictionary<int, int>();

        foreach (var inst in instructions)
        {
            if (inst is IrInst.RcDup { RuntimeManaged: false } dup)
            {
                remap[dup.Target] = ResolveTemp(remap, dup.SourceTemp);
            }
            else if (inst is IrInst.Borrow b)
            {
                // Follow chains: if the source was already remapped, resolve transitively.
                int source = ResolveTemp(remap, b.SourceTemp);

                bool isCopyTypeSource = copyTypeProducers.Contains(source);
                bool isSingleUse = useCount.GetValueOrDefault(b.Target) <= 1;

                if (isCopyTypeSource || isSingleUse)
                {
                    remap[b.Target] = source;
                }
            }
        }

        if (remap.Count == 0)
        {
            return instructions;
        }

        // Rewrite the instruction list — remove elided Borrows and
        // remap all source-temp references to the original source.
        var result = new List<IrInst>(instructions.Count);

        foreach (var inst in instructions)
        {
            if (inst is IrInst.RcDup { RuntimeManaged: false } dup && remap.ContainsKey(dup.Target))
            {
                continue; // erased marker
            }

            if (inst is IrInst.Borrow b && remap.ContainsKey(b.Target))
            {
                continue; // elide this Borrow
            }

            result.Add(RemapSourceTemps(inst, remap));
        }

        return result;
    }

    /// <summary>
    /// Builds the use-def information for borrow elision: which temps are produced by
    /// copy-type constant instructions, and how many times each temp is read as a
    /// source operand.
    /// </summary>
    private static (HashSet<int> CopyTypeProducers, Dictionary<int, int> UseCount) CollectBorrowElisionInfo(List<IrInst> instructions)
    {
        // Track which temps are produced by copy-type constant instructions.
        var copyTypeProducers = new HashSet<int>();

        // Count how many times each temp is read as a source operand.
        var useCount = new Dictionary<int, int>();
        var tempBuf = new HashSet<int>();

        foreach (var inst in instructions)
        {
            switch (inst)
            {
                case IrInst.LoadConstInt lci: copyTypeProducers.Add(lci.Target); break;
                case IrInst.LoadConstFloat lcf: copyTypeProducers.Add(lcf.Target); break;
                case IrInst.LoadConstBool lcb: copyTypeProducers.Add(lcb.Target); break;
            }

            tempBuf.Clear();
            CollectUsedTemps(inst, tempBuf);
            foreach (var t in tempBuf)
            {
                useCount[t] = useCount.GetValueOrDefault(t) + 1;
            }
        }

        return (copyTypeProducers, useCount);
    }

    /// <summary>
    /// Follows the remap chain for a temp index until a fixed point is reached.
    /// If <paramref name="temp"/> → a → b exists, returns b.
    /// Returns the original temp if it is not in the map.
    /// </summary>
    private static int ResolveTemp(Dictionary<int, int> remap, int temp)
    {
        while (remap.TryGetValue(temp, out int resolved))
        {
            temp = resolved;
        }

        return temp;
    }

    /// <summary>
    /// Returns a copy of <paramref name="inst"/> with all source (read) temps
    /// rewritten according to <paramref name="remap"/>. Target (write) temps are
    /// left unchanged. Instructions with no source temps are returned as-is.
    /// </summary>
    private static IrInst RemapSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return RemapArithmeticSourceTemps(inst, remap)
            ?? RemapMemorySourceTemps(inst, remap)
            ?? RemapIoSourceTemps(inst, remap)
            ?? RemapBytesAndOwnershipSourceTemps(inst, remap)
            ?? RemapAsyncSourceTemps(inst, remap)
            ?? inst switch
            {
                // Capabilities.
                IrInst.StoreCapabilityHandler se => se with { Source = R(se.Source) },

                // Control flow.
                IrInst.PanicStr p => p with { Source = R(p.Source) },
                IrInst.JumpIfFalse j => j with { CondTemp = R(j.CondTemp) },
                IrInst.SwitchTag s => s with { TagTemp = R(s.TagTemp) },
                IrInst.Return r => r with { Source = R(r.Source) },

                // Instructions with no source temps — pass through unchanged.
                _ => inst,
            };
    }

    private static IrInst? RemapArithmeticSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            // Binary arithmetic / comparison — remap Left and Right.
            IrInst.AddInt a => a with { Left = R(a.Left), Right = R(a.Right) },
            IrInst.SubInt s => s with { Left = R(s.Left), Right = R(s.Right) },
            IrInst.MulInt m => m with { Left = R(m.Left), Right = R(m.Right) },
            IrInst.DivInt d => d with { Left = R(d.Left), Right = R(d.Right) },
            IrInst.DivUInt d => d with { Left = R(d.Left), Right = R(d.Right) },
            IrInst.AndInt a => a with { Left = R(a.Left), Right = R(a.Right) },
            IrInst.OrInt o => o with { Left = R(o.Left), Right = R(o.Right) },
            IrInst.XorInt x => x with { Left = R(x.Left), Right = R(x.Right) },
            IrInst.ShlInt s => s with { Left = R(s.Left), Right = R(s.Right) },
            IrInst.ShrInt s => s with { Left = R(s.Left), Right = R(s.Right) },
            IrInst.AddFloat a => a with { Left = R(a.Left), Right = R(a.Right) },
            IrInst.SubFloat s => s with { Left = R(s.Left), Right = R(s.Right) },
            IrInst.MulFloat m => m with { Left = R(m.Left), Right = R(m.Right) },
            IrInst.DivFloat d => d with { Left = R(d.Left), Right = R(d.Right) },
            IrInst.IntToFloat i => i with { ValueTemp = R(i.ValueTemp) },
            IrInst.FloatToInt f => f with { ValueTemp = R(f.ValueTemp) },
            IrInst.FloatUnaryIntrinsic u => u with { ValueTemp = R(u.ValueTemp) },
            IrInst.CallLibm c => c with { Args = c.Args.Select(R).ToList() },
            IrInst.RegexCompile c => c with { Pattern = R(c.Pattern) },
            IrInst.RegexCompileError c => c with { Pattern = R(c.Pattern) },
            IrInst.RegexFind c => c with { Code = R(c.Code), Subject = R(c.Subject), Start = R(c.Start) },
            IrInst.RegexCaptures c => c with { Code = R(c.Code), Subject = R(c.Subject), Start = R(c.Start) },
            IrInst.RegexSubstitute c => c with { Code = R(c.Code), Subject = R(c.Subject), Replacement = R(c.Replacement) },
            IrInst.CmpIntGt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntGe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntLt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntLe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntEq c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpIntNe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatGt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatGe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatLt c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatLe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatEq c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpFloatNe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpStrEq c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.CmpStrNe c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.ConcatStr c => c with { Left = R(c.Left), Right = R(c.Right) },
            IrInst.ConcatStrTip c => c with { Left = R(c.Left), Right = R(c.Right) },

            _ => null,
        };
    }

    private static IrInst? RemapMemorySourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            // Memory operations.
            IrInst.StoreLocal s => s with { Source = R(s.Source) },
            IrInst.StoreMemOffset s => s with { BasePtr = R(s.BasePtr), Source = R(s.Source) },
            IrInst.LoadMemOffset l => l with { BasePtr = R(l.BasePtr) },

            // Closures.
            IrInst.MakeClosure mc => mc with { EnvPtrTemp = R(mc.EnvPtrTemp) },
            IrInst.MakeClosureStack mc => mc with { EnvPtrTemp = R(mc.EnvPtrTemp) },
            IrInst.CallClosure cc => cc with { ClosureTemp = R(cc.ClosureTemp), ArgTemp = R(cc.ArgTemp) },
            IrInst.CallKnown ck => ck with { EnvTemp = R(ck.EnvTemp), ArgTemp = R(ck.ArgTemp) },
            IrInst.ToCString c => c with { StrTemp = R(c.StrTemp) },
            IrInst.CallExternal c => c with { ArgTemps = c.ArgTemps.Select(R).ToList() },

            // ADTs.
            IrInst.SetAdtField sf => sf with { Ptr = R(sf.Ptr), Source = R(sf.Source) },
            IrInst.GetAdtTag gt => gt with { Ptr = R(gt.Ptr) },
            IrInst.GetAdtField gf => gf with { Ptr = R(gf.Ptr) },

            _ => null,
        };
    }

    private static IrInst? RemapIoSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            // I/O — remap source temps.
            IrInst.PrintInt p => p with { Source = R(p.Source) },
            IrInst.PrintStr p => p with { Source = R(p.Source) },
            IrInst.PrintBool p => p with { Source = R(p.Source) },
            IrInst.WriteStr w => w with { Source = R(w.Source) },
            IrInst.FileReadText f => f with { PathTemp = R(f.PathTemp) },
            IrInst.FileWriteText f => f with { PathTemp = R(f.PathTemp), TextTemp = R(f.TextTemp) },
            IrInst.FileExists f => f with { PathTemp = R(f.PathTemp) },
            IrInst.FileOpen f => f with { PathTemp = R(f.PathTemp) },
            IrInst.FileReadChunk f => f with { HandleTemp = R(f.HandleTemp), CountTemp = R(f.CountTemp) },
            IrInst.FileReadLine f => f with { HandleTemp = R(f.HandleTemp) },
            IrInst.FileClose f => f with { HandleTemp = R(f.HandleTemp) },
            IrInst.TextUncons t => t with { TextTemp = R(t.TextTemp) },
            IrInst.TextParseInt t => t with { TextTemp = R(t.TextTemp) },
            IrInst.TextParseFloat t => t with { TextTemp = R(t.TextTemp) },
            IrInst.TextFromInt t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.TextFromFloat t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.TextFormatFloat t => t with { ValueTemp = R(t.ValueTemp), DecimalsTemp = R(t.DecimalsTemp) },
            IrInst.BigIntFromInt t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.BigIntToString t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.BigIntToInt t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.BigIntFromString t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.BigIntBinary t => t with { Left = R(t.Left), Right = R(t.Right) },
            IrInst.BigIntCompare t => t with { Left = R(t.Left), Right = R(t.Right) },
            IrInst.TextToHex t => t with { ValueTemp = R(t.ValueTemp) },
            IrInst.TextAsciiCase t => t with { SourceTemp = R(t.SourceTemp) },
            IrInst.TextByteLength t => t with { TextTemp = R(t.TextTemp) },
            IrInst.ReadExact r => r with { CountTemp = R(r.CountTemp) },
            IrInst.ConsolePoll cp => cp with { TimeoutTemp = R(cp.TimeoutTemp) },
            IrInst.FileReadAllBytes f => f with { PathTemp = R(f.PathTemp) },
            IrInst.FileMmap f => f with { PathTemp = R(f.PathTemp) },
            IrInst.SpawnProcess s => s with { ExeTemp = R(s.ExeTemp), ArgsTemp = R(s.ArgsTemp) },
            IrInst.ProcessWriteStdin p => p with { ProcessTemp = R(p.ProcessTemp), TextTemp = R(p.TextTemp) },
            IrInst.ProcessReadStdoutLine p => p with { ProcessTemp = R(p.ProcessTemp) },
            IrInst.ProcessReadStderrLine p => p with { ProcessTemp = R(p.ProcessTemp) },
            IrInst.ProcessWaitForExit p => p with { ProcessTemp = R(p.ProcessTemp) },
            IrInst.ProcessKill p => p with { ProcessTemp = R(p.ProcessTemp) },
            IrInst.HttpGet h => h with { UrlTemp = R(h.UrlTemp) },
            IrInst.HttpPost h => h with { UrlTemp = R(h.UrlTemp), BodyTemp = R(h.BodyTemp) },
            IrInst.NetTcpConnect n => n with { HostTemp = R(n.HostTemp), PortTemp = R(n.PortTemp) },
            IrInst.NetTcpSend n => n with { SocketTemp = R(n.SocketTemp), TextTemp = R(n.TextTemp) },
            IrInst.NetTcpReceive n => n with { SocketTemp = R(n.SocketTemp), MaxBytesTemp = R(n.MaxBytesTemp) },
            IrInst.NetTcpClose n => n with { SocketTemp = R(n.SocketTemp) },
            IrInst.NetTcpListen n => n with { PortTemp = R(n.PortTemp) },
            IrInst.NetTcpAccept n => n with { SocketTemp = R(n.SocketTemp) },

            _ => null,
        };
    }

    private static IrInst? RemapBytesAndOwnershipSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            IrInst.BytesEmpty b => b,
            IrInst.BytesSingleton b => b with { ByteTemp = R(b.ByteTemp) },
            IrInst.BytesLength b => b with { BytesTemp = R(b.BytesTemp) },
            IrInst.BytesGet b => b with { BytesTemp = R(b.BytesTemp), IndexTemp = R(b.IndexTemp) },
            IrInst.BytesIndexOf b => b with { BytesTemp = R(b.BytesTemp), NeedleTemp = R(b.NeedleTemp), FromTemp = R(b.FromTemp) },
            IrInst.BytesCompare b => b with { LeftTemp = R(b.LeftTemp), RightTemp = R(b.RightTemp) },
            IrInst.BytesScanHash b => b with { BytesTemp = R(b.BytesTemp), NeedleTemp = R(b.NeedleTemp), FromTemp = R(b.FromTemp) },
            IrInst.BytesSubText b => b with { BytesTemp = R(b.BytesTemp), StartTemp = R(b.StartTemp), LenTemp = R(b.LenTemp) },
            IrInst.BytesSubView b => b with { BytesTemp = R(b.BytesTemp), StartTemp = R(b.StartTemp), LenTemp = R(b.LenTemp) },
            IrInst.BytesAppend b => b with { LeftTemp = R(b.LeftTemp), RightTemp = R(b.RightTemp) },
            IrInst.BytesAppendByte b => b with { BytesTemp = R(b.BytesTemp), ByteTemp = R(b.ByteTemp) },
            IrInst.BytesFromList b => b with { ListTemp = R(b.ListTemp) },
            IrInst.BytesHash b => b with { BytesTemp = R(b.BytesTemp) },
            IrInst.BytesU16Le b => b with { ValueTemp = R(b.ValueTemp) },
            IrInst.BytesU32Le b => b with { ValueTemp = R(b.ValueTemp) },
            IrInst.BytesU64Le b => b with { ValueTemp = R(b.ValueTemp) },
            IrInst.BytesGetU16Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp) },
            IrInst.BytesGetU32Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp) },
            IrInst.BytesGetU64Le b => b with { BytesTemp = R(b.BytesTemp), OffsetTemp = R(b.OffsetTemp) },
            IrInst.FileWriteBytes f => f with { PathTemp = R(f.PathTemp), BytesTemp = R(f.BytesTemp) },

            // Ownership.
            // NOTE: Keep these source-temp users in sync with CollectUsedTemps().
            IrInst.CleanupResource d => d with { SourceTemp = R(d.SourceTemp) },
            IrInst.RcDrop d => d with { SourceTemp = R(d.SourceTemp) },
            IrInst.RcDup d => d with { SourceTemp = R(d.SourceTemp) },
            IrInst.RcIsUnique u => u with { SourceTemp = R(u.SourceTemp) },
            IrInst.Borrow b => b with { SourceTemp = R(b.SourceTemp) },
            IrInst.CopyOutArena co => co with { SrcTemp = R(co.SrcTemp) },
            IrInst.CopyOutArenaToSpace co => co with { SrcTemp = R(co.SrcTemp) },
            IrInst.CopyFixedInto ci => ci with { DestTemp = R(ci.DestTemp), SrcTemp = R(ci.SrcTemp) },
            IrInst.CopyStringIntoOrFresh cs => cs with { OldBlobTemp = R(cs.OldBlobTemp), SrcTemp = R(cs.SrcTemp) },
            IrInst.CopyFixedIntoOrFresh cf => cf with { OldBlobTemp = R(cf.OldBlobTemp), SrcTemp = R(cf.SrcTemp) },
            IrInst.CopyOutList co => co with { SrcTemp = R(co.SrcTemp) },
            IrInst.CopyOutClosure co => co with { SrcTemp = R(co.SrcTemp) },
            IrInst.CopyOutTcoListCell co => co with { SrcTemp = R(co.SrcTemp) },

            _ => null,
        };
    }

    private static IrInst? RemapAsyncSourceTemps(IrInst inst, Dictionary<int, int> remap)
    {
        int R(int temp) => remap.TryGetValue(temp, out int resolved) ? resolved : temp;

        return inst switch
        {
            // Async.
            IrInst.CreateTask ct => ct with { ClosureTemp = R(ct.ClosureTemp) },
            IrInst.CreateCompletedTask ct => ct with { ResultTemp = R(ct.ResultTemp) },
            IrInst.AwaitTask at => at with { TaskTemp = R(at.TaskTemp) },
            IrInst.RunTask rt => rt with { TaskTemp = R(rt.TaskTemp) },
            IrInst.SpawnTask st => st with { TaskTemp = R(st.TaskTemp) },
            IrInst.AllocReusing ar => ar with { TokenTemp = R(ar.TokenTemp) },
            IrInst.ParallelFork pf => pf with { RightClosureTemp = R(pf.RightClosureTemp) },
            IrInst.ParallelJoin pj => pj with { DescTemp = R(pj.DescTemp) },
            IrInst.ParallelCleanup pc => pc with { DescTemp = R(pc.DescTemp) },
            IrInst.StoreParallelWorkerOverride so => so with { Source = R(so.Source) },
            IrInst.ParallelQueueStart qs => qs with { FClosureTemp = R(qs.FClosureTemp), CombineClosureTemp = R(qs.CombineClosureTemp), ListTemp = R(qs.ListTemp) },
            IrInst.ParallelQueueAwait qa => qa with { DescTemp = R(qa.DescTemp) },
            IrInst.ParallelQueueCleanup qc => qc with { DescTemp = R(qc.DescTemp) },
            IrInst.AsyncSleep sl => sl with { MillisecondsTemp = R(sl.MillisecondsTemp) },
            IrInst.CreateTcpConnectTask t => t with { HostTemp = R(t.HostTemp), PortTemp = R(t.PortTemp) },
            IrInst.CreateTcpSendTask t => t with { SocketTemp = R(t.SocketTemp), TextTemp = R(t.TextTemp) },
            IrInst.CreateTcpReceiveTask t => t with { SocketTemp = R(t.SocketTemp), MaxBytesTemp = R(t.MaxBytesTemp) },
            IrInst.CreateTcpCloseTask t => t with { SocketTemp = R(t.SocketTemp) },
            IrInst.CreateTcpListenTask t => t with { PortTemp = R(t.PortTemp) },
            IrInst.CreateForkWorkersTask t => t with { PortTemp = R(t.PortTemp), CountTemp = R(t.CountTemp) },
            IrInst.SetDrainTimeout t => t with { MsTemp = R(t.MsTemp) },
            IrInst.CreateTcpAcceptTask t => t with { SocketTemp = R(t.SocketTemp) },
            IrInst.CreateHttpGetTask t => t with { UrlTemp = R(t.UrlTemp) },
            IrInst.CreateHttpPostTask t => t with { UrlTemp = R(t.UrlTemp), BodyTemp = R(t.BodyTemp) },
            IrInst.CreateTlsConnectTask t => t with { HostTemp = R(t.HostTemp), PortTemp = R(t.PortTemp) },
            IrInst.CreateTlsHandshakeTask t => t with { SocketTemp = R(t.SocketTemp), HostTemp = R(t.HostTemp) },
            IrInst.CreateTlsServerHandshakeTask t => t with { SocketTemp = R(t.SocketTemp), CertTemp = R(t.CertTemp), KeyTemp = R(t.KeyTemp) },
            IrInst.CreateTlsSendTask t => t with { SslTemp = R(t.SslTemp), TextTemp = R(t.TextTemp) },
            IrInst.CreateTlsReceiveTask t => t with { SslTemp = R(t.SslTemp), MaxBytesTemp = R(t.MaxBytesTemp) },
            IrInst.CreateTlsCloseTask t => t with { SslTemp = R(t.SslTemp) },
            IrInst.AsyncAll aa => aa with { TaskListTemp = R(aa.TaskListTemp) },
            IrInst.AsyncRace ar => ar with { TaskListTemp = R(ar.TaskListTemp) },
            IrInst.Suspend s => s with
            {
                StateStructTemp = R(s.StateStructTemp),
                AwaitedTaskTemp = R(s.AwaitedTaskTemp),
                SaveVars = s.SaveVars.Select(v => (v.SlotOffset, R(v.SourceTemp))).ToList(),
            },
            IrInst.Resume r => r with { StateStructTemp = R(r.StateStructTemp) },

            _ => null,
        };
    }

    // Constant folding
    // Evaluate arithmetic on known constant operands at compile time.
    // Labels with a single predecessor preserve constant knowledge from
    // that predecessor, enabling folding across branch boundaries.

    private static List<IrInst> FoldConstants(List<IrInst> instructions)
    {
        // Map from temp → known constant value
        var knownInts = new Dictionary<int, long>();
        var knownFloats = new Dictionary<int, double>();
        var knownBools = new Dictionary<int, bool>();

        // Pre-scan: count how many explicit branches (Jump/JumpIfFalse) target
        // each label. Combined with fall-through analysis, this tells us the
        // total predecessor count at each label.
        var branchRefs = CountBranchRefsToLabels(instructions);

        // Saved constant state for single-predecessor labels reached by a
        // JumpIfFalse or Jump from elsewhere (not by fall-through).
        var savedIntStates = new Dictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
        var savedFloatStates = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
        var savedBoolStates = new Dictionary<string, Dictionary<int, bool>>(StringComparer.Ordinal);

        var result = new List<IrInst>(instructions.Count);
        bool changed = false;
        bool prevIsTerminator = false; // tracks whether the previous instruction was Jump/Return

        foreach (var inst in instructions)
        {
            if (TryRecordConstantLoad(inst, knownInts, knownFloats, knownBools, result))
            {
                prevIsTerminator = false;
                continue;
            }

            if (TryFoldIntArithmetic(inst, knownInts, result, ref changed)
                || TryFoldIntBitwise(inst, knownInts, result, ref changed)
                || TryFoldFloatArithmetic(inst, knownFloats, result, ref changed)
                || TryFoldIntEquality(inst, knownInts, knownBools, result, ref changed)
                || TryFoldIntOrdering(inst, knownInts, knownBools, result, ref changed))
            {
                // A folded instruction leaves the terminator flag unchanged, matching the
                // original single-switch form.
                continue;
            }

            prevIsTerminator = HandleConstantControlFlow(
                inst, prevIsTerminator, branchRefs, knownInts, knownFloats, knownBools,
                savedIntStates, savedFloatStates, savedBoolStates, result);
        }

        return changed ? result : instructions;
    }

    private static bool TryRecordConstantLoad(
        IrInst inst,
        Dictionary<int, long> knownInts,
        Dictionary<int, double> knownFloats,
        Dictionary<int, bool> knownBools,
        List<IrInst> result)
    {
        switch (inst)
        {
            case IrInst.LoadConstInt lci:
                knownInts[lci.Target] = lci.Value;
                result.Add(inst);
                return true;

            case IrInst.LoadConstFloat lcf:
                knownFloats[lcf.Target] = lcf.Value;
                result.Add(inst);
                return true;

            case IrInst.LoadConstBool lcb:
                knownBools[lcb.Target] = lcb.Value;
                result.Add(inst);
                return true;

            default:
                return false;
        }
    }

    private static bool TryFoldIntArithmetic(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.AddInt add when knownInts.ContainsKey(add.Left) && knownInts.ContainsKey(add.Right):
                {
                    long folded = knownInts[add.Left] + knownInts[add.Right];
                    knownInts[add.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(add.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.SubInt sub when knownInts.ContainsKey(sub.Left) && knownInts.ContainsKey(sub.Right):
                {
                    long folded = knownInts[sub.Left] - knownInts[sub.Right];
                    knownInts[sub.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(sub.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.MulInt mul when knownInts.ContainsKey(mul.Left) && knownInts.ContainsKey(mul.Right):
                {
                    long folded = knownInts[mul.Left] * knownInts[mul.Right];
                    knownInts[mul.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(mul.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.DivInt div when knownInts.ContainsKey(div.Left) && knownInts.ContainsKey(div.Right)
                                       && knownInts[div.Right] != 0:
                {
                    long folded = knownInts[div.Left] / knownInts[div.Right];
                    knownInts[div.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(div.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.DivUInt divU when knownInts.ContainsKey(divU.Left) && knownInts.ContainsKey(divU.Right)
                                         && knownInts[divU.Right] != 0:
                {
                    long folded = (long)((ulong)knownInts[divU.Left] / (ulong)knownInts[divU.Right]);
                    knownInts[divU.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(divU.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryFoldIntBitwise(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.AndInt bitAnd when knownInts.ContainsKey(bitAnd.Left) && knownInts.ContainsKey(bitAnd.Right):
                {
                    long folded = knownInts[bitAnd.Left] & knownInts[bitAnd.Right];
                    knownInts[bitAnd.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(bitAnd.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.OrInt bitOr when knownInts.ContainsKey(bitOr.Left) && knownInts.ContainsKey(bitOr.Right):
                {
                    long folded = knownInts[bitOr.Left] | knownInts[bitOr.Right];
                    knownInts[bitOr.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(bitOr.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.XorInt bitXor when knownInts.ContainsKey(bitXor.Left) && knownInts.ContainsKey(bitXor.Right):
                {
                    long folded = knownInts[bitXor.Left] ^ knownInts[bitXor.Right];
                    knownInts[bitXor.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(bitXor.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.ShlInt shl when knownInts.ContainsKey(shl.Left) && knownInts.ContainsKey(shl.Right):
                {
                    long folded = knownInts[shl.Left] << (int)(knownInts[shl.Right] & 63);
                    knownInts[shl.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(shl.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.ShrInt shr when knownInts.ContainsKey(shr.Left) && knownInts.ContainsKey(shr.Right):
                {
                    // Match the language spec's logical right shift: reinterpret
                    // the signed Int bits as unsigned so the high bits are zero-filled.
                    long folded = (long)((ulong)knownInts[shr.Left] >> (int)(knownInts[shr.Right] & 63));
                    knownInts[shr.Target] = folded;
                    result.Add(new IrInst.LoadConstInt(shr.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryFoldFloatArithmetic(IrInst inst, Dictionary<int, double> knownFloats, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.AddFloat addF when knownFloats.ContainsKey(addF.Left) && knownFloats.ContainsKey(addF.Right):
                {
                    double folded = knownFloats[addF.Left] + knownFloats[addF.Right];
                    knownFloats[addF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(addF.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.SubFloat subF when knownFloats.ContainsKey(subF.Left) && knownFloats.ContainsKey(subF.Right):
                {
                    double folded = knownFloats[subF.Left] - knownFloats[subF.Right];
                    knownFloats[subF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(subF.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.MulFloat mulF when knownFloats.ContainsKey(mulF.Left) && knownFloats.ContainsKey(mulF.Right):
                {
                    double folded = knownFloats[mulF.Left] * knownFloats[mulF.Right];
                    knownFloats[mulF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(mulF.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.DivFloat divF when knownFloats.ContainsKey(divF.Left) && knownFloats.ContainsKey(divF.Right):
                {
                    double folded = knownFloats[divF.Left] / knownFloats[divF.Right];
                    knownFloats[divF.Target] = folded;
                    result.Add(new IrInst.LoadConstFloat(divF.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryFoldIntEquality(
        IrInst inst,
        Dictionary<int, long> knownInts,
        Dictionary<int, bool> knownBools,
        List<IrInst> result,
        ref bool changed)
    {
        switch (inst)
        {
            case IrInst.CmpIntEq cmpEq when knownInts.ContainsKey(cmpEq.Left) && knownInts.ContainsKey(cmpEq.Right):
                {
                    bool folded = knownInts[cmpEq.Left] == knownInts[cmpEq.Right];
                    knownBools[cmpEq.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpEq.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.CmpIntNe cmpNe when knownInts.ContainsKey(cmpNe.Left) && knownInts.ContainsKey(cmpNe.Right):
                {
                    bool folded = knownInts[cmpNe.Left] != knownInts[cmpNe.Right];
                    knownBools[cmpNe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpNe.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryFoldIntOrdering(
        IrInst inst,
        Dictionary<int, long> knownInts,
        Dictionary<int, bool> knownBools,
        List<IrInst> result,
        ref bool changed)
    {
        switch (inst)
        {
            case IrInst.CmpIntGt cmpGt when knownInts.ContainsKey(cmpGt.Left) && knownInts.ContainsKey(cmpGt.Right):
                {
                    bool folded = knownInts[cmpGt.Left] > knownInts[cmpGt.Right];
                    knownBools[cmpGt.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpGt.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.CmpIntGe cmpGe when knownInts.ContainsKey(cmpGe.Left) && knownInts.ContainsKey(cmpGe.Right):
                {
                    bool folded = knownInts[cmpGe.Left] >= knownInts[cmpGe.Right];
                    knownBools[cmpGe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpGe.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.CmpIntLt cmpLt when knownInts.ContainsKey(cmpLt.Left) && knownInts.ContainsKey(cmpLt.Right):
                {
                    bool folded = knownInts[cmpLt.Left] < knownInts[cmpLt.Right];
                    knownBools[cmpLt.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpLt.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            case IrInst.CmpIntLe cmpLe when knownInts.ContainsKey(cmpLe.Left) && knownInts.ContainsKey(cmpLe.Right):
                {
                    bool folded = knownInts[cmpLe.Left] <= knownInts[cmpLe.Right];
                    knownBools[cmpLe.Target] = folded;
                    result.Add(new IrInst.LoadConstBool(cmpLe.Target, folded) { Location = inst.Location });
                    changed = true;
                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles the control-flow instructions of the constant-folding pass (labels, jumps,
    /// switch, and every unhandled instruction), appending the instruction to
    /// <paramref name="result"/>. Returns the new "previous instruction was an
    /// unconditional terminator" flag.
    /// </summary>
    private static bool HandleConstantControlFlow(
        IrInst inst,
        bool prevIsTerminator,
        Dictionary<string, int> branchRefs,
        Dictionary<int, long> knownInts,
        Dictionary<int, double> knownFloats,
        Dictionary<int, bool> knownBools,
        Dictionary<string, Dictionary<int, long>> savedIntStates,
        Dictionary<string, Dictionary<int, double>> savedFloatStates,
        Dictionary<string, Dictionary<int, bool>> savedBoolStates,
        List<IrInst> result)
    {
        switch (inst)
        {
            // Labels, jumps, and control flow invalidate constant knowledge —
            // unless the label has a single predecessor, in which case we can
            // propagate constants from that predecessor.
            case IrInst.Label lbl:
                ApplyLabelConstantState(lbl, prevIsTerminator, branchRefs, knownInts, knownFloats, knownBools, savedIntStates, savedFloatStates, savedBoolStates);
                result.Add(inst);
                return false;

            case IrInst.JumpIfFalse jif:
                // Save state for the target label — will be used if the label turns
                // out to be a single-predecessor label (only this branch targets it).
                savedIntStates[jif.Target] = new Dictionary<int, long>(knownInts);
                savedFloatStates[jif.Target] = new Dictionary<int, double>(knownFloats);
                savedBoolStates[jif.Target] = new Dictionary<int, bool>(knownBools);
                result.Add(inst);
                return false; // JumpIfFalse is conditional, not a terminator

            case IrInst.Jump jmp:
                // Save state for the target label.
                savedIntStates[jmp.Target] = new Dictionary<int, long>(knownInts);
                savedFloatStates[jmp.Target] = new Dictionary<int, double>(knownFloats);
                savedBoolStates[jmp.Target] = new Dictionary<int, bool>(knownBools);
                result.Add(inst);
                return true; // Jump is an unconditional terminator

            case IrInst.SwitchTag:
                // Multi-way terminator. Do not save per-target constant state — each case
                // label is multi-predecessor by construction, so the Label handler clears
                // constant knowledge there. Marking it a terminator prevents the following
                // label from inheriting stale fall-through constants.
                result.Add(inst);
                return true;

            default:
                result.Add(inst);
                return inst is IrInst.Return;
        }
    }

    /// <summary>
    /// Applies the constant-state transition for a label in the constant-folding pass:
    /// restores the branch point's saved state for a single-predecessor label, keeps the
    /// current state for a fall-through-only label, and clears everything otherwise.
    /// </summary>
    private static void ApplyLabelConstantState(
        IrInst.Label lbl,
        bool prevIsTerminator,
        Dictionary<string, int> branchRefs,
        Dictionary<int, long> knownInts,
        Dictionary<int, double> knownFloats,
        Dictionary<int, bool> knownBools,
        Dictionary<string, Dictionary<int, long>> savedIntStates,
        Dictionary<string, Dictionary<int, double>> savedFloatStates,
        Dictionary<string, Dictionary<int, bool>> savedBoolStates)
    {
        bool hasFallthrough = !prevIsTerminator;
        int branchCount = branchRefs.GetValueOrDefault(lbl.Name);
        int totalPredecessors = branchCount + (hasFallthrough ? 1 : 0);

        if (totalPredecessors <= 1 && savedIntStates.TryGetValue(lbl.Name, out var savedInts) && !hasFallthrough)
        {
            // Single-predecessor label reached only by a branch (no fall-through):
            // restore the saved state from the branch point.
            knownInts.Clear();
            foreach (var kv in savedInts) knownInts[kv.Key] = kv.Value;
            knownFloats.Clear();
            foreach (var kv in savedFloatStates[lbl.Name]) knownFloats[kv.Key] = kv.Value;
            knownBools.Clear();
            foreach (var kv in savedBoolStates[lbl.Name]) knownBools[kv.Key] = kv.Value;
        }
        else if (totalPredecessors <= 1 && hasFallthrough && branchCount == 0)
        {
            // Fall-through-only label (no branches target it) — keep current
            // constant state because sequential execution is the only path.
        }
        else
        {
            // Multiple predecessors — clear all constant knowledge.
            knownInts.Clear();
            knownFloats.Clear();
            knownBools.Clear();
        }

        // Clean up any saved state for this label.
        savedIntStates.Remove(lbl.Name);
        savedFloatStates.Remove(lbl.Name);
        savedBoolStates.Remove(lbl.Name);
    }

    // Identity elimination and strength reduction
    // Simplify arithmetic with known identity values:
    //   x + 0 → x, 0 + x → x, x - 0 → x
    //   x * 1 → x, 1 * x → x, x * 0 → 0, 0 * x → 0
    //   x / 1 → x
    //   x * 2 → x + x (strength reduction)

    private static List<IrInst> ReduceIdentitiesAndStrength(List<IrInst> instructions)
    {
        var knownInts = new Dictionary<int, long>();
        var branchRefs = CountBranchRefsToLabels(instructions);
        var savedIntStates = new Dictionary<string, Dictionary<int, long>>(StringComparer.Ordinal);
        var result = new List<IrInst>(instructions.Count);
        bool changed = false;
        bool prevIsTerminator = false;

        foreach (var inst in instructions)
        {
            if (inst is IrInst.LoadConstInt lci)
            {
                knownInts[lci.Target] = lci.Value;
                result.Add(inst);
                prevIsTerminator = false;
                continue;
            }

            if (TryReduceIntAddSub(inst, knownInts, result, ref changed)
                || TryReduceIntMul(inst, knownInts, result, ref changed)
                || TryReduceIntDiv(inst, knownInts, result, ref changed))
            {
                // A rewritten (or passed-through) arithmetic instruction leaves the
                // terminator flag unchanged, matching the original single-switch form.
                continue;
            }

            prevIsTerminator = HandleIdentityControlFlow(inst, prevIsTerminator, branchRefs, knownInts, savedIntStates, result);
        }

        return changed ? result : instructions;
    }

    private static bool TryReduceIntAddSub(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.AddInt add:
                {
                    bool leftZero = knownInts.TryGetValue(add.Left, out long lv) && lv == 0;
                    bool rightZero = knownInts.TryGetValue(add.Right, out long rv) && rv == 0;
                    if (leftZero)
                    {
                        // 0 + x → x: copy Right → Target
                        result.Add(new IrInst.Borrow(add.Target, add.Right) { Location = inst.Location });
                        changed = true;
                    }
                    else if (rightZero)
                    {
                        // x + 0 → x: copy Left → Target
                        result.Add(new IrInst.Borrow(add.Target, add.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            case IrInst.SubInt sub:
                {
                    bool rightZero = knownInts.TryGetValue(sub.Right, out long rv) && rv == 0;
                    if (rightZero)
                    {
                        // x - 0 → x
                        result.Add(new IrInst.Borrow(sub.Target, sub.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryReduceIntMul(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.MulInt mul:
                {
                    bool leftKnown = knownInts.TryGetValue(mul.Left, out long lv);
                    bool rightKnown = knownInts.TryGetValue(mul.Right, out long rv);

                    if ((leftKnown && lv == 0) || (rightKnown && rv == 0))
                    {
                        // x * 0 or 0 * x → 0
                        result.Add(new IrInst.LoadConstInt(mul.Target, 0) { Location = inst.Location });
                        changed = true;
                    }
                    else if (leftKnown && lv == 1)
                    {
                        // 1 * x → x
                        result.Add(new IrInst.Borrow(mul.Target, mul.Right) { Location = inst.Location });
                        changed = true;
                    }
                    else if (rightKnown && rv == 1)
                    {
                        // x * 1 → x
                        result.Add(new IrInst.Borrow(mul.Target, mul.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else if (rightKnown && rv == 2)
                    {
                        // x * 2 → x + x (strength reduction)
                        result.Add(new IrInst.AddInt(mul.Target, mul.Left, mul.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else if (leftKnown && lv == 2)
                    {
                        // 2 * x → x + x (strength reduction)
                        result.Add(new IrInst.AddInt(mul.Target, mul.Right, mul.Right) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    private static bool TryReduceIntDiv(IrInst inst, Dictionary<int, long> knownInts, List<IrInst> result, ref bool changed)
    {
        switch (inst)
        {
            case IrInst.DivInt div:
                {
                    bool rightOne = knownInts.TryGetValue(div.Right, out long rv) && rv == 1;
                    if (rightOne)
                    {
                        // x / 1 → x
                        result.Add(new IrInst.Borrow(div.Target, div.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            case IrInst.DivUInt divU:
                {
                    bool rightOne = knownInts.TryGetValue(divU.Right, out long rvu) && rvu == 1;
                    if (rightOne)
                    {
                        // x / 1 → x
                        result.Add(new IrInst.Borrow(divU.Target, divU.Left) { Location = inst.Location });
                        changed = true;
                    }
                    else
                    {
                        result.Add(inst);
                    }

                    return true;
                }

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles the control-flow instructions of the identity-reduction pass (labels, jumps,
    /// switch, and every unhandled instruction), appending the instruction to
    /// <paramref name="result"/>. Returns the new "previous instruction was an
    /// unconditional terminator" flag.
    /// </summary>
    private static bool HandleIdentityControlFlow(
        IrInst inst,
        bool prevIsTerminator,
        Dictionary<string, int> branchRefs,
        Dictionary<int, long> knownInts,
        Dictionary<string, Dictionary<int, long>> savedIntStates,
        List<IrInst> result)
    {
        switch (inst)
        {
            // Labels: preserve state across single-predecessor labels.
            case IrInst.Label lbl:
                {
                    bool hasFallthrough = !prevIsTerminator;
                    int branchCount = branchRefs.GetValueOrDefault(lbl.Name);
                    int totalPredecessors = branchCount + (hasFallthrough ? 1 : 0);

                    if (totalPredecessors <= 1 && savedIntStates.TryGetValue(lbl.Name, out var savedInts) && !hasFallthrough)
                    {
                        knownInts.Clear();
                        foreach (var kv in savedInts) knownInts[kv.Key] = kv.Value;
                    }
                    else if (totalPredecessors <= 1 && hasFallthrough && branchCount == 0)
                    {
                        // Fall-through-only — keep current state.
                    }
                    else
                    {
                        knownInts.Clear();
                    }

                    savedIntStates.Remove(lbl.Name);
                    result.Add(inst);
                    return false;
                }

            case IrInst.JumpIfFalse jif:
                savedIntStates[jif.Target] = new Dictionary<int, long>(knownInts);
                result.Add(inst);
                return false;

            case IrInst.Jump jmp:
                savedIntStates[jmp.Target] = new Dictionary<int, long>(knownInts);
                result.Add(inst);
                return true;

            case IrInst.SwitchTag:
                // Multi-way terminator — see the matching note in the constant-folding pass.
                result.Add(inst);
                return true;

            default:
                result.Add(inst);
                return inst is IrInst.Return;
        }
    }

    // Unreachable code elimination
    // Remove instructions after unconditional jumps or returns until the
    // next label (which re-establishes reachability).

    private static List<IrInst> ElideUnreachableCode(List<IrInst> instructions)
    {
        var result = new List<IrInst>(instructions.Count);
        bool unreachable = false;
        bool changed = false;

        foreach (var inst in instructions)
        {
            if (inst is IrInst.Label)
            {
                // Labels re-establish reachability.
                unreachable = false;
                result.Add(inst);
                continue;
            }

            if (unreachable)
            {
                // Skip instructions after an unconditional terminator.
                changed = true;
                continue;
            }

            result.Add(inst);

            // Unconditional terminators: Jump, Return, and SwitchTag make subsequent code
            // unreachable until the next label.
            if (inst is IrInst.Jump or IrInst.Return or IrInst.SwitchTag)
            {
                unreachable = true;
            }
        }

        return changed ? result : instructions;
    }

    // Dead code elimination
    // Remove LoadConst instructions whose target temp is never used,
    // and StoreLocal instructions whose slot is never loaded.

    private static List<IrInst> ElideDeadCode(List<IrInst> instructions)
    {
        // Run to a fixed point: removing a dead StoreLocal may leave its
        // source temp with no remaining uses, making the producing LoadConst*
        // dead as well. Iterate until no further instructions are removed.
        var current = instructions;
        while (true)
        {
            var result = ElideDeadCodeOnce(current);
            if (ReferenceEquals(result, current))
            {
                return result;
            }

            current = result;
        }
    }

    private static List<IrInst> ElideDeadCodeOnce(List<IrInst> instructions)
    {
        // Collect all temps that are used as operands (sources)
        var usedTemps = new HashSet<int>();
        foreach (var inst in instructions)
        {
            CollectUsedTemps(inst, usedTemps);
        }

        // Collect all local slots that are read by any LoadLocal
        var loadedSlots = new HashSet<int>();
        foreach (var inst in instructions)
        {
            if (inst is IrInst.LoadLocal ll)
            {
                loadedSlots.Add(ll.Slot);
            }
        }

        var result = new List<IrInst>(instructions.Count);
        bool changed = false;

        foreach (var inst in instructions)
        {
            if (IsDeadInstruction(inst, usedTemps, loadedSlots))
            {
                changed = true;
                continue;
            }

            result.Add(inst);
        }

        return changed ? result : instructions;
    }

    /// <summary>
    /// Returns true if the instruction is removable dead code: its result is observed
    /// by no remaining use.
    /// </summary>
    private static bool IsDeadInstruction(IrInst inst, HashSet<int> usedTemps, HashSet<int> loadedSlots)
    {
        // Remove LoadConst* instructions whose target is never read
        if (inst is IrInst.LoadConstInt lci && !usedTemps.Contains(lci.Target))
        {
            return true;
        }

        if (inst is IrInst.LoadConstFloat lcf && !usedTemps.Contains(lcf.Target))
        {
            return true;
        }

        if (inst is IrInst.LoadConstBool lcb && !usedTemps.Contains(lcb.Target))
        {
            return true;
        }

        // Remove StoreLocal instructions whose slot is never loaded
        if (inst is IrInst.StoreLocal sl && !loadedSlots.Contains(sl.Slot))
        {
            return true;
        }

        // Remove closure constructions whose target is never read — a pure arena/stack
        // allocation plus stores into that fresh allocation, observable by nothing once the
        // pointer is unused. Devirtualization routinely strands these.
        if (inst is IrInst.MakeClosure mc && !usedTemps.Contains(mc.Target))
        {
            return true;
        }

        if (inst is IrInst.MakeClosureStack mcs && !usedTemps.Contains(mcs.Target))
        {
            return true;
        }

        return false;
    }

    // Erased RC marker elision
    // RcDrop has no runtime behavior while arenas own ordinary heap reclamation. Remove each marker
    // and its otherwise-dead LoadLocal, then remove stores to slots with no remaining loads.
    // CleanupResource is a separate instruction and can never enter this pass.
    private static List<IrInst> ElideErasedRcDrops(List<IrInst> instructions)
    {
        // Build analysis data.

        // Map: temp → instruction index of the instruction that defines it.
        var tempDefinedAt = new Dictionary<int, int>();

        // Count how many times each temp is read as a source operand.
        var useCount = new Dictionary<int, int>();
        var tempBuf = new HashSet<int>();

        for (int i = 0; i < instructions.Count; i++)
        {
            var inst = instructions[i];

            // Track definitions from LoadLocal (these feed RcDrop markers).
            if (inst is IrInst.LoadLocal ll)
            {
                tempDefinedAt[ll.Target] = i;
            }

            tempBuf.Clear();
            CollectUsedTemps(inst, tempBuf);
            foreach (var t in tempBuf)
            {
                useCount[t] = useCount.GetValueOrDefault(t) + 1;
            }
        }

        // Identify erased RcDrop markers and their feeding LoadLocals.
        var toRemove = CollectElidableDropRemovals(instructions, tempDefinedAt, useCount);

        if (toRemove.Count == 0)
        {
            return instructions;
        }

        AddDeadStoresAfterDropElision(instructions, toRemove);

        // Rebuild the instruction list excluding removed instructions.
        var result = new List<IrInst>(instructions.Count - toRemove.Count);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (!toRemove.Contains(i))
            {
                result.Add(instructions[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Finds the instruction indices of erased RcDrop markers and of the LoadLocals that feed
    /// them and have no other use.
    /// </summary>
    private static HashSet<int> CollectElidableDropRemovals(List<IrInst> instructions, Dictionary<int, int> tempDefinedAt, Dictionary<int, int> useCount)
    {
        var toRemove = new HashSet<int>();

        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is not IrInst.RcDrop { RuntimeManaged: false } drop) continue;

            // Erased ordinary-value marker: safe to elide while arenas own reclamation.
            toRemove.Add(i);

            // If the LoadLocal feeding this Drop has its target used only here,
            // remove the LoadLocal too.
            if (tempDefinedAt.TryGetValue(drop.SourceTemp, out int defIdx)
                && instructions[defIdx] is IrInst.LoadLocal
                && useCount.GetValueOrDefault(drop.SourceTemp) <= 1)
            {
                toRemove.Add(defIdx);
            }
        }

        return toRemove;
    }

    /// <summary>
    /// Checks for StoreLocals to slots that have no remaining LoadLocals.
    /// After removing drop-related LoadLocals, some slots may have zero loads,
    /// making their StoreLocals dead code; their indices are added to
    /// <paramref name="toRemove"/>.
    /// </summary>
    private static void AddDeadStoresAfterDropElision(List<IrInst> instructions, HashSet<int> toRemove)
    {
        var slotLoadCount = new Dictionary<int, int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (toRemove.Contains(i)) continue;
            if (instructions[i] is IrInst.LoadLocal ll)
            {
                slotLoadCount[ll.Slot] = slotLoadCount.GetValueOrDefault(ll.Slot) + 1;
            }
        }

        for (int i = 0; i < instructions.Count; i++)
        {
            if (toRemove.Contains(i)) continue;
            if (instructions[i] is IrInst.StoreLocal sl
                && slotLoadCount.GetValueOrDefault(sl.Slot) == 0)
            {
                toRemove.Add(i);
            }
        }
    }

    /// <summary>
    /// Collects all temp indices that are read (used as operands) by an instruction.
    /// This does NOT include target/destination temps — only sources.
    /// Dispatches through per-group collectors; every instruction kind with source
    /// temps is handled by exactly one collector, and the others fall through as no-ops.
    /// </summary>
    internal static void CollectUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        CollectIntArithmeticUsedTemps(inst, usedTemps);
        CollectFloatAndRegexUsedTemps(inst, usedTemps);
        CollectComparisonUsedTemps(inst, usedTemps);
        CollectStringMemoryClosureUsedTemps(inst, usedTemps);
        CollectAdtAndFileUsedTemps(inst, usedTemps);
        CollectTextAndBigIntUsedTemps(inst, usedTemps);
        CollectProcessUsedTemps(inst, usedTemps);
        CollectNetworkUsedTemps(inst, usedTemps);
        CollectBytesUsedTemps(inst, usedTemps);
        CollectBytesEncodingUsedTemps(inst, usedTemps);
        CollectOwnershipUsedTemps(inst, usedTemps);
        CollectTaskParallelUsedTemps(inst, usedTemps);
        CollectNetTaskUsedTemps(inst, usedTemps);
        CollectTlsTaskUsedTemps(inst, usedTemps);
        CollectSuspendControlUsedTemps(inst, usedTemps);

        // LoadConstInt, LoadConstFloat, LoadConstBool, LoadConstStr, LoadLocal,
        // LoadEnv, LoadProgramArgs, ReadLine, Alloc, AllocAdt, Label, Jump:
        // These either have no source temps or only define targets.
    }

    private static void CollectIntArithmeticUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.AddInt a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.SubInt s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.MulInt m: usedTemps.Add(m.Left); usedTemps.Add(m.Right); break;
            case IrInst.DivInt d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.DivUInt d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.AndInt a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.OrInt o: usedTemps.Add(o.Left); usedTemps.Add(o.Right); break;
            case IrInst.XorInt x: usedTemps.Add(x.Left); usedTemps.Add(x.Right); break;
            case IrInst.ShlInt s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.ShrInt s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
        }
    }

    private static void CollectFloatAndRegexUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.AddFloat a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.SubFloat s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.MulFloat m: usedTemps.Add(m.Left); usedTemps.Add(m.Right); break;
            case IrInst.DivFloat d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.IntToFloat i: usedTemps.Add(i.ValueTemp); break;
            case IrInst.FloatToInt f: usedTemps.Add(f.ValueTemp); break;
            case IrInst.FloatUnaryIntrinsic u: usedTemps.Add(u.ValueTemp); break;
            case IrInst.CallLibm c: foreach (int a in c.Args) { usedTemps.Add(a); } break;
            case IrInst.RegexCompile c: usedTemps.Add(c.Pattern); break;
            case IrInst.RegexCompileError c: usedTemps.Add(c.Pattern); break;
            case IrInst.RegexFind c: usedTemps.Add(c.Code); usedTemps.Add(c.Subject); usedTemps.Add(c.Start); break;
            case IrInst.RegexCaptures c: usedTemps.Add(c.Code); usedTemps.Add(c.Subject); usedTemps.Add(c.Start); break;
            case IrInst.RegexSubstitute c: usedTemps.Add(c.Code); usedTemps.Add(c.Subject); usedTemps.Add(c.Replacement); break;
        }
    }

    private static void CollectComparisonUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CmpIntGt c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntGe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntLt c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntLe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatGt c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatGe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatLt c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatLe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
        }
    }

    private static void CollectStringMemoryClosureUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CmpStrEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpStrNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.ConcatStr c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.ConcatStrTip c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.StoreLocal s: usedTemps.Add(s.Source); break;
            case IrInst.StoreMemOffset s: usedTemps.Add(s.BasePtr); usedTemps.Add(s.Source); break;
            case IrInst.LoadMemOffset l: usedTemps.Add(l.BasePtr); break;
            case IrInst.MakeClosure mc: usedTemps.Add(mc.EnvPtrTemp); break;
            case IrInst.MakeClosureStack mc: usedTemps.Add(mc.EnvPtrTemp); break;
            case IrInst.CallClosure cc: usedTemps.Add(cc.ClosureTemp); usedTemps.Add(cc.ArgTemp); break;
            case IrInst.CallKnown ck: usedTemps.Add(ck.EnvTemp); usedTemps.Add(ck.ArgTemp); break;
            case IrInst.ToCString c: usedTemps.Add(c.StrTemp); break;
            case IrInst.CallExternal c:
                foreach (var argTemp in c.ArgTemps) usedTemps.Add(argTemp);
                break;
        }
    }

    private static void CollectAdtAndFileUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.SetAdtField sf: usedTemps.Add(sf.Ptr); usedTemps.Add(sf.Source); break;
            case IrInst.GetAdtTag gt: usedTemps.Add(gt.Ptr); break;
            case IrInst.GetAdtField gf: usedTemps.Add(gf.Ptr); break;
            case IrInst.PrintInt p: usedTemps.Add(p.Source); break;
            case IrInst.PrintStr p: usedTemps.Add(p.Source); break;
            case IrInst.PrintBool p: usedTemps.Add(p.Source); break;
            case IrInst.WriteStr w: usedTemps.Add(w.Source); break;
            case IrInst.FileReadText f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileWriteText f: usedTemps.Add(f.PathTemp); usedTemps.Add(f.TextTemp); break;
            case IrInst.FileExists f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileOpen f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileReadChunk f: usedTemps.Add(f.HandleTemp); usedTemps.Add(f.CountTemp); break;
            case IrInst.FileReadLine f: usedTemps.Add(f.HandleTemp); break;
            case IrInst.FileClose f: usedTemps.Add(f.HandleTemp); break;
        }
    }

    private static void CollectTextAndBigIntUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.TextUncons t: usedTemps.Add(t.TextTemp); break;
            case IrInst.TextParseInt t: usedTemps.Add(t.TextTemp); break;
            case IrInst.TextParseFloat t: usedTemps.Add(t.TextTemp); break;
            case IrInst.TextFromInt t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.TextFromFloat t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.TextFormatFloat t: usedTemps.Add(t.ValueTemp); usedTemps.Add(t.DecimalsTemp); break;
            case IrInst.BigIntFromInt t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.BigIntToString t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.BigIntToInt t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.BigIntFromString t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.BigIntBinary t: usedTemps.Add(t.Left); usedTemps.Add(t.Right); break;
            case IrInst.BigIntCompare t: usedTemps.Add(t.Left); usedTemps.Add(t.Right); break;
            case IrInst.TextToHex t: usedTemps.Add(t.ValueTemp); break;
            case IrInst.TextAsciiCase t: usedTemps.Add(t.SourceTemp); break;
            case IrInst.TextByteLength t: usedTemps.Add(t.TextTemp); break;
        }
    }

    private static void CollectProcessUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.ReadExact r: usedTemps.Add(r.CountTemp); break;
            case IrInst.ConsolePoll cp: usedTemps.Add(cp.TimeoutTemp); break;
            case IrInst.FileReadAllBytes f: usedTemps.Add(f.PathTemp); break;
            case IrInst.FileMmap f: usedTemps.Add(f.PathTemp); break;
            case IrInst.SpawnProcess s: usedTemps.Add(s.ExeTemp); usedTemps.Add(s.ArgsTemp); break;
            case IrInst.ProcessWriteStdin p: usedTemps.Add(p.ProcessTemp); usedTemps.Add(p.TextTemp); break;
            case IrInst.ProcessReadStdoutLine p: usedTemps.Add(p.ProcessTemp); break;
            case IrInst.ProcessReadStderrLine p: usedTemps.Add(p.ProcessTemp); break;
            case IrInst.ProcessWaitForExit p: usedTemps.Add(p.ProcessTemp); break;
            case IrInst.ProcessKill p: usedTemps.Add(p.ProcessTemp); break;
        }
    }

    private static void CollectNetworkUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.HttpGet h: usedTemps.Add(h.UrlTemp); break;
            case IrInst.HttpPost h: usedTemps.Add(h.UrlTemp); usedTemps.Add(h.BodyTemp); break;
            case IrInst.NetTcpConnect n: usedTemps.Add(n.HostTemp); usedTemps.Add(n.PortTemp); break;
            case IrInst.NetTcpSend n: usedTemps.Add(n.SocketTemp); usedTemps.Add(n.TextTemp); break;
            case IrInst.NetTcpReceive n: usedTemps.Add(n.SocketTemp); usedTemps.Add(n.MaxBytesTemp); break;
            case IrInst.NetTcpClose n: usedTemps.Add(n.SocketTemp); break;
            case IrInst.NetTcpListen n: usedTemps.Add(n.PortTemp); break;
            case IrInst.NetTcpAccept n: usedTemps.Add(n.SocketTemp); break;
        }
    }

    private static void CollectBytesUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.BytesEmpty: break;
            case IrInst.BytesSingleton b: usedTemps.Add(b.ByteTemp); break;
            case IrInst.BytesLength b: usedTemps.Add(b.BytesTemp); break;
            case IrInst.BytesGet b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.IndexTemp); break;
            case IrInst.BytesIndexOf b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.NeedleTemp); usedTemps.Add(b.FromTemp); break;
            case IrInst.BytesCompare b: usedTemps.Add(b.LeftTemp); usedTemps.Add(b.RightTemp); break;
            case IrInst.BytesScanHash b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.NeedleTemp); usedTemps.Add(b.FromTemp); break;
            case IrInst.BytesSubText b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.StartTemp); usedTemps.Add(b.LenTemp); break;
            case IrInst.BytesSubView b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.StartTemp); usedTemps.Add(b.LenTemp); break;
            case IrInst.BytesAppend b: usedTemps.Add(b.LeftTemp); usedTemps.Add(b.RightTemp); break;
            case IrInst.BytesAppendByte b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.ByteTemp); break;
            case IrInst.BytesFromList b: usedTemps.Add(b.ListTemp); break;
            case IrInst.BytesHash b: usedTemps.Add(b.BytesTemp); break;
        }
    }

    private static void CollectBytesEncodingUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.BytesU16Le b: usedTemps.Add(b.ValueTemp); break;
            case IrInst.BytesU32Le b: usedTemps.Add(b.ValueTemp); break;
            case IrInst.BytesU64Le b: usedTemps.Add(b.ValueTemp); break;
            case IrInst.BytesGetU16Le b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.OffsetTemp); break;
            case IrInst.BytesGetU32Le b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.OffsetTemp); break;
            case IrInst.BytesGetU64Le b: usedTemps.Add(b.BytesTemp); usedTemps.Add(b.OffsetTemp); break;
            case IrInst.FileWriteBytes f: usedTemps.Add(f.PathTemp); usedTemps.Add(f.BytesTemp); break;
        }
    }

    // NOTE: Keep these source-temp users in sync with RemapSourceTemps().
    private static void CollectOwnershipUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CleanupResource d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.RcDrop d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.RcDup d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.RcIsUnique u: usedTemps.Add(u.SourceTemp); break;
            case IrInst.Borrow b: usedTemps.Add(b.SourceTemp); break;
            case IrInst.CopyOutArena c: usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyOutArenaToSpace c: usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyFixedInto c: usedTemps.Add(c.DestTemp); usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyStringIntoOrFresh c: usedTemps.Add(c.OldBlobTemp); usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyFixedIntoOrFresh c: usedTemps.Add(c.OldBlobTemp); usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyOutList c: usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyOutClosure c: usedTemps.Add(c.SrcTemp); break;
            case IrInst.CopyOutTcoListCell c: usedTemps.Add(c.SrcTemp); break;
        }
    }

    private static void CollectTaskParallelUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CreateTask ct: usedTemps.Add(ct.ClosureTemp); break;
            case IrInst.CreateCompletedTask ct: usedTemps.Add(ct.ResultTemp); break;
            case IrInst.AwaitTask at: usedTemps.Add(at.TaskTemp); break;
            case IrInst.RunTask rt: usedTemps.Add(rt.TaskTemp); break;
            case IrInst.SpawnTask st: usedTemps.Add(st.TaskTemp); break;
            case IrInst.AllocReusing ar: usedTemps.Add(ar.TokenTemp); break;
            case IrInst.ParallelFork pf: usedTemps.Add(pf.RightClosureTemp); break;
            case IrInst.ParallelJoin pj: usedTemps.Add(pj.DescTemp); break;
            case IrInst.ParallelCleanup pc: usedTemps.Add(pc.DescTemp); break;
            case IrInst.StoreParallelWorkerOverride so: usedTemps.Add(so.Source); break;
            case IrInst.ParallelQueueStart qs: usedTemps.Add(qs.FClosureTemp); usedTemps.Add(qs.CombineClosureTemp); usedTemps.Add(qs.ListTemp); break;
            case IrInst.ParallelQueueAwait qa: usedTemps.Add(qa.DescTemp); break;
            case IrInst.ParallelQueueCleanup qc: usedTemps.Add(qc.DescTemp); break;
            case IrInst.AsyncSleep sl: usedTemps.Add(sl.MillisecondsTemp); break;
        }
    }

    private static void CollectNetTaskUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CreateTcpConnectTask t: usedTemps.Add(t.HostTemp); usedTemps.Add(t.PortTemp); break;
            case IrInst.CreateTcpSendTask t: usedTemps.Add(t.SocketTemp); usedTemps.Add(t.TextTemp); break;
            case IrInst.CreateTcpReceiveTask t: usedTemps.Add(t.SocketTemp); usedTemps.Add(t.MaxBytesTemp); break;
            case IrInst.CreateTcpCloseTask t: usedTemps.Add(t.SocketTemp); break;
            case IrInst.CreateTcpListenTask t: usedTemps.Add(t.PortTemp); break;
            case IrInst.CreateForkWorkersTask t: usedTemps.Add(t.PortTemp); usedTemps.Add(t.CountTemp); break;
            case IrInst.SetDrainTimeout t: usedTemps.Add(t.MsTemp); break;
            case IrInst.CreateTcpAcceptTask t: usedTemps.Add(t.SocketTemp); break;
            case IrInst.CreateHttpGetTask t: usedTemps.Add(t.UrlTemp); break;
            case IrInst.CreateHttpPostTask t: usedTemps.Add(t.UrlTemp); usedTemps.Add(t.BodyTemp); break;
        }
    }

    private static void CollectTlsTaskUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.CreateTlsConnectTask t: usedTemps.Add(t.HostTemp); usedTemps.Add(t.PortTemp); break;
            case IrInst.CreateTlsHandshakeTask t: usedTemps.Add(t.SocketTemp); usedTemps.Add(t.HostTemp); break;
            case IrInst.CreateTlsServerHandshakeTask t2: usedTemps.Add(t2.SocketTemp); usedTemps.Add(t2.CertTemp); usedTemps.Add(t2.KeyTemp); break;
            case IrInst.CreateTlsSendTask t: usedTemps.Add(t.SslTemp); usedTemps.Add(t.TextTemp); break;
            case IrInst.CreateTlsReceiveTask t: usedTemps.Add(t.SslTemp); usedTemps.Add(t.MaxBytesTemp); break;
            case IrInst.CreateTlsCloseTask t: usedTemps.Add(t.SslTemp); break;
            case IrInst.AsyncAll aa: usedTemps.Add(aa.TaskListTemp); break;
            case IrInst.AsyncRace ar: usedTemps.Add(ar.TaskListTemp); break;
        }
    }

    private static void CollectSuspendControlUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.Suspend s:
                usedTemps.Add(s.StateStructTemp);
                usedTemps.Add(s.AwaitedTaskTemp);
                foreach (var (_, sourceTemp) in s.SaveVars) usedTemps.Add(sourceTemp);
                break;
            case IrInst.Resume r:
                usedTemps.Add(r.StateStructTemp);
                break;
            case IrInst.PanicStr p: usedTemps.Add(p.Source); break;
            case IrInst.StoreCapabilityHandler se: usedTemps.Add(se.Source); break;
            case IrInst.JumpIfFalse j: usedTemps.Add(j.CondTemp); break;
            case IrInst.SwitchTag s: usedTemps.Add(s.TagTemp); break;
            case IrInst.Return r: usedTemps.Add(r.Source); break;
        }
    }

    /// <summary>
    /// Counts the number of explicit branch instructions (Jump and JumpIfFalse)
    /// that target each label. Used to determine whether a label has a single
    /// predecessor and can safely propagate constant knowledge.
    /// </summary>
    private static Dictionary<string, int> CountBranchRefsToLabels(List<IrInst> instructions)
    {
        var refs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var inst in instructions)
        {
            if (inst is IrInst.SwitchTag sw)
            {
                // Every case label plus the default label is a predecessor edge.
                foreach (var (_, caseLabel) in sw.Cases)
                {
                    refs[caseLabel] = refs.GetValueOrDefault(caseLabel) + 1;
                }

                refs[sw.DefaultLabel] = refs.GetValueOrDefault(sw.DefaultLabel) + 1;
                continue;
            }

            string? target = inst switch
            {
                IrInst.Jump j => j.Target,
                IrInst.JumpIfFalse jf => jf.Target,
                _ => null
            };

            if (target is not null)
            {
                refs[target] = refs.GetValueOrDefault(target) + 1;
            }
        }

        return refs;
    }
}
