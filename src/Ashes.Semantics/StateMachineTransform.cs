namespace Ashes.Semantics;

/// <summary>
/// Result of the state machine transform applied to a coroutine's instruction list.
/// </summary>
/// <param name="Instructions">The transformed instruction list with state dispatch and save/restore sequences.</param>
/// <param name="StateCount">Number of states (N await points produce N+1 states).</param>
/// <param name="StateStructSize">Total size of the task/state struct in bytes (header + captures + live vars).</param>
/// <param name="MaxTemp">Highest temp index used (including temps added by the transform).</param>
public sealed record StateMachineResult(
    List<IrInst> Instructions,
    int StateCount,
    int StateStructSize,
    int MaxTemp
);

/// <summary>
/// Transforms a linear list of IR instructions (from an async body) into a
/// state-machine form. Each AwaitTask instruction becomes a suspend point
/// that splits the coroutine into numbered states.
///
/// The transform:
/// 1. Identifies AwaitTask instructions as split points.
/// 2. Computes which temps are live across each split point.
/// 3. Assigns state-struct slots for live temps.
/// 4. Rewrites instructions with a state-dispatch header, save/restore
///    sequences at suspend/resume points, and proper return values.
///
/// State struct layout:
///   [0]:  state_index (i64)
///   [8]:  coroutine_fn (i64)   — set by CreateTask, not touched here
///   [16]: result_slot (i64)    — awaited task result / final result
///   [24]: awaited_task (i64)   — pointer to sub-task being awaited
///   [32]: capture_0 (i64)      — captured env variables
///   [32 + captureCount*8]: live_var_0 (i64) — live variable slots
///   ...
/// </summary>
public static class StateMachineTransform
{
    /// <summary>
    /// Transforms the instruction list of a coroutine function.
    /// If there are no AwaitTask instructions, the transform adds a minimal
    /// state-0-only wrapper that stores the result and returns COMPLETED.
    /// </summary>
    /// <param name="instructions">The linear instruction list from lowering the async body.</param>
    /// <param name="captureCount">Number of captured env variables (determines offset for live vars).</param>
    /// <returns>The transformed instruction list with state machine structure.</returns>
    public static StateMachineResult Transform(List<IrInst> instructions, int captureCount)
    {
        // A `both`'s worker descriptor and worker arena are NOT part of the serialized coroutine
        // state struct and cannot survive a suspend/resume, so a fork/join/cleanup group must stay
        // within a single coroutine segment (no `await` between a fork and its cleanup). Assert it
        // rather than assume it, now that (once wired) coroutines can carry user `both`s.
        AssertParallelForkJoinWithinSegment(instructions);

        // Find all AwaitTask instructions and their positions
        var awaitPositions = FindAwaitPositions(instructions);

        int stateCount = awaitPositions.Count + 1;

        // Compute live temps across each await point
        var liveAcross = ComputeLiveTempsAcrossAwaits(instructions, awaitPositions);

        // Compute local slots that are written before and read after each await point. With a
        // backward jump in the body (an async tail-recursive loop's restart edge), positional
        // before/after liveness is unsound for locals: a local written only in the entry code
        // (e.g. a captured value copied to its local) can be read after a resume via the back-edge,
        // where the entry code does not re-execute. Temps stay positional — every temp's defining
        // instruction lies inside the loop and re-executes ahead of any back-edge reachable use.
        var liveLocalsAcross = ComputeLiveLocalsAcrossAwaits(instructions, awaitPositions, HasBackwardJump(instructions));

        int stateStructSize = AssignStateStructSlots(
            liveAcross, liveLocalsAcross, captureCount,
            out var tempToSlotOffset, out var localToSlotOffset);

        // Build the transformed instruction list
        var result = new List<IrInst>();

        // Track the highest temp used in original instructions
        int maxTemp = ComputeMaxBodyTemp(instructions);

        // Reserve temps that don't conflict with body temps
        int stateStructTemp = maxTemp + 1;
        int stateIdxTemp = maxTemp + 2;
        int statusTemp = maxTemp + 3;
        int awaitResultTemp = maxTemp + 4;
        maxTemp = maxTemp + 4;

        // Emit: load state struct pointer from local[0] into a dedicated temp
        result.Add(new IrInst.LoadLocal(stateStructTemp, 0));

        if (awaitPositions.Count == 0)
        {
            EmitSingleStateBody(instructions, result, stateStructTemp, statusTemp, captureCount, ref maxTemp);
            return new StateMachineResult(result, stateCount, stateStructSize, maxTemp);
        }

        // --- Multi-state coroutine ---
        EmitMultiStateBody(
            instructions, result, awaitPositions, liveAcross, liveLocalsAcross,
            tempToSlotOffset, localToSlotOffset, stateCount,
            stateStructTemp, stateIdxTemp, statusTemp, captureCount, ref maxTemp);

        return new StateMachineResult(result, stateCount, stateStructSize, maxTemp);
    }

    /// <summary>
    /// Finds the positions of all AwaitTask instructions in the body.
    /// </summary>
    private static List<int> FindAwaitPositions(List<IrInst> instructions)
    {
        var awaitPositions = new List<int>();
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.AwaitTask)
            {
                awaitPositions.Add(i);
            }
        }

        return awaitPositions;
    }

    /// <summary>
    /// Assigns state-struct slot offsets to every temp and local slot that is live across any
    /// await point, and returns the resulting total state struct size in bytes.
    /// </summary>
    private static int AssignStateStructSlots(
        List<HashSet<int>> liveAcross,
        List<HashSet<int>> liveLocalsAcross,
        int captureCount,
        out Dictionary<int, int> tempToSlotOffset,
        out Dictionary<int, int> localToSlotOffset)
    {
        // Build the union of all live temps (each gets a unique slot in the state struct)
        var allLiveTemps = new SortedSet<int>();
        foreach (var set in liveAcross)
        {
            allLiveTemps.UnionWith(set);
        }

        // Build the union of all live locals
        var allLiveLocals = new SortedSet<int>();
        foreach (var set in liveLocalsAcross)
        {
            allLiveLocals.UnionWith(set);
        }

        // Assign state struct offsets for live temps
        int liveVarBaseOffset = TaskStructLayout.HeaderSize + captureCount * 8;
        tempToSlotOffset = new Dictionary<int, int>();
        int slotIndex = 0;
        foreach (int temp in allLiveTemps)
        {
            tempToSlotOffset[temp] = liveVarBaseOffset + slotIndex * 8;
            slotIndex++;
        }

        // Assign state struct offsets for live locals (after temps)
        int localSaveBaseOffset = liveVarBaseOffset + allLiveTemps.Count * 8;
        localToSlotOffset = new Dictionary<int, int>();
        int localSlotIndex = 0;
        foreach (int local in allLiveLocals)
        {
            localToSlotOffset[local] = localSaveBaseOffset + localSlotIndex * 8;
            localSlotIndex++;
        }

        return localSaveBaseOffset + allLiveLocals.Count * 8;
    }

    /// <summary>
    /// Returns the highest temp index referenced by the original body instructions.
    /// </summary>
    private static int ComputeMaxBodyTemp(List<IrInst> instructions)
    {
        int maxTemp = 0;
        foreach (var inst in instructions)
        {
            foreach (int t in GetAllTemps(inst))
            {
                if (t > maxTemp) maxTemp = t;
            }
        }

        return maxTemp;
    }

    /// <summary>
    /// Emits the body of a coroutine that has no await points: the original instructions run
    /// unchanged except the final Return, which becomes the store-result-and-COMPLETED epilogue.
    /// </summary>
    private static void EmitSingleStateBody(
        List<IrInst> instructions, List<IrInst> result,
        int stateStructTemp, int statusTemp, int captureCount, ref int maxTemp)
    {
        // No await points — just run the body and store the result.
        // Load env captures into the appropriate env-loading pattern.
        // The body instructions use LoadEnv to access captures via local[0],
        // which is the state struct pointer. Captures are at HeaderSize + i*8.
        // We need to adjust LoadEnv offsets to account for the header.

        // Emit all original instructions except the final Return.
        // Replace the Return with storing the result in the state struct.
        for (int i = 0; i < instructions.Count; i++)
        {
            var inst = instructions[i];
            if (inst is IrInst.Return ret)
            {
                // Store result in state struct result slot
                result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.ResultSlot, ret.Source));
                // Set state_index = -1 (COMPLETED)
                int completedConst = ++maxTemp;
                result.Add(new IrInst.LoadConstInt(completedConst, -1));
                result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.StateIndex, completedConst));
                // Return 1 (COMPLETED status)
                result.Add(new IrInst.LoadConstInt(statusTemp, 1));
                result.Add(new IrInst.Return(statusTemp));
            }
            else
            {
                result.Add(AdjustLoadEnvForStateStruct(inst, stateStructTemp, captureCount));
            }
        }
    }

    /// <summary>
    /// Emits the multi-state coroutine body: the state dispatch header followed by each state's
    /// resume prologue and instruction segment.
    /// </summary>
    private static void EmitMultiStateBody(
        List<IrInst> instructions, List<IrInst> result, List<int> awaitPositions,
        List<HashSet<int>> liveAcross, List<HashSet<int>> liveLocalsAcross,
        Dictionary<int, int> tempToSlotOffset, Dictionary<int, int> localToSlotOffset,
        int stateCount, int stateStructTemp, int stateIdxTemp, int statusTemp,
        int captureCount, ref int maxTemp)
    {
        // Generate state dispatch header
        // Load state index from state struct
        result.Add(new IrInst.LoadMemOffset(stateIdxTemp, stateStructTemp, TaskStructLayout.StateIndex));

        // Generate labels for each state
        var stateLabels = new string[stateCount];
        for (int i = 0; i < stateCount; i++)
        {
            stateLabels[i] = $"__state_{i}";
        }

        EmitStateDispatch(result, stateLabels, stateIdxTemp, ref maxTemp);

        // Split original instructions into segments at await points
        var segments = SplitAtAwaits(instructions, awaitPositions);

        // Emit each state
        for (int stateIdx = 0; stateIdx < stateCount; stateIdx++)
        {
            result.Add(new IrInst.Label(stateLabels[stateIdx]));

            if (stateIdx > 0)
            {
                EmitResumePrologue(
                    instructions, result, awaitPositions, liveAcross, liveLocalsAcross,
                    tempToSlotOffset, localToSlotOffset, stateStructTemp, stateIdx, ref maxTemp);
            }

            EmitStateSegment(
                segments[stateIdx], result, stateIdx, liveAcross, liveLocalsAcross,
                tempToSlotOffset, localToSlotOffset, stateStructTemp, statusTemp,
                captureCount, ref maxTemp);
        }
    }

    /// <summary>
    /// Emits the dispatch chain that routes the loaded state index to its state label.
    /// </summary>
    private static void EmitStateDispatch(List<IrInst> result, string[] stateLabels, int stateIdxTemp, ref int maxTemp)
    {
        // Dispatch: check state index against each state and jump
        // We use a chain of comparisons since IR doesn't have a switch instruction.
        for (int i = 1; i < stateLabels.Length; i++)
        {
            int cmpTemp = ++maxTemp;
            int constTemp = ++maxTemp;
            result.Add(new IrInst.LoadConstInt(constTemp, i));
            result.Add(new IrInst.CmpIntEq(cmpTemp, stateIdxTemp, constTemp));
            result.Add(new IrInst.JumpIfFalse(cmpTemp, i + 1 < stateLabels.Length ? $"__dispatch_{i + 1}" : stateLabels[0]));
            result.Add(new IrInst.Jump(stateLabels[i]));
            if (i + 1 < stateLabels.Length)
            {
                result.Add(new IrInst.Label($"__dispatch_{i + 1}"));
            }
        }
        // Default: state 0
        result.Add(new IrInst.Jump(stateLabels[0]));
    }

    /// <summary>
    /// Emits the resume prologue for a state: restores live temps and locals from the state
    /// struct and loads the awaited sub-task's result into the AwaitTask's target temp.
    /// </summary>
    private static void EmitResumePrologue(
        List<IrInst> instructions, List<IrInst> result, List<int> awaitPositions,
        List<HashSet<int>> liveAcross, List<HashSet<int>> liveLocalsAcross,
        Dictionary<int, int> tempToSlotOffset, Dictionary<int, int> localToSlotOffset,
        int stateStructTemp, int stateIdx, ref int maxTemp)
    {
        // Resume: restore live temps from state struct
        var liveTempsAtThisPoint = liveAcross[stateIdx - 1];
        var restoreVars = new List<(int SlotOffset, int TargetTemp)>();
        foreach (int temp in liveTempsAtThisPoint)
        {
            if (tempToSlotOffset.TryGetValue(temp, out int offset))
            {
                result.Add(new IrInst.LoadMemOffset(temp, stateStructTemp, offset));
                restoreVars.Add((offset, temp));
            }
        }

        // Resume: restore live locals from state struct
        var liveLocalsAtThisPoint = liveLocalsAcross[stateIdx - 1];
        foreach (int local in liveLocalsAtThisPoint)
        {
            if (localToSlotOffset.TryGetValue(local, out int offset))
            {
                int loadTemp = ++maxTemp;
                result.Add(new IrInst.LoadMemOffset(loadTemp, stateStructTemp, offset));
                result.Add(new IrInst.StoreLocal(local, loadTemp));
            }
        }

        // Load the result from the awaited sub-task into the AwaitTask's target temp
        var awaitInst = (IrInst.AwaitTask)instructions[awaitPositions[stateIdx - 1]];
        result.Add(new IrInst.LoadMemOffset(awaitInst.Target, stateStructTemp, TaskStructLayout.ResultSlot));

        result.Add(new IrInst.Resume(stateStructTemp, awaitInst.Target, restoreVars));
    }

    /// <summary>
    /// Emits one state's instruction segment: a trailing AwaitTask becomes the suspend sequence
    /// and a final Return becomes the store-result-and-COMPLETED epilogue.
    /// </summary>
    private static void EmitStateSegment(
        List<IrInst> segment, List<IrInst> result, int stateIdx,
        List<HashSet<int>> liveAcross, List<HashSet<int>> liveLocalsAcross,
        Dictionary<int, int> tempToSlotOffset, Dictionary<int, int> localToSlotOffset,
        int stateStructTemp, int statusTemp, int captureCount, ref int maxTemp)
    {
        // Emit the segment's instructions
        for (int i = 0; i < segment.Count; i++)
        {
            var inst = segment[i];

            if (inst is IrInst.AwaitTask awaitTask)
            {
                EmitSuspendAtAwait(
                    awaitTask, result, stateIdx, liveAcross, liveLocalsAcross,
                    tempToSlotOffset, localToSlotOffset, stateStructTemp, statusTemp, ref maxTemp);
            }
            else if (inst is IrInst.Return ret)
            {
                // Final state: store result and return COMPLETED
                result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.ResultSlot, ret.Source));
                int completedConst = ++maxTemp;
                result.Add(new IrInst.LoadConstInt(completedConst, -1));
                result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.StateIndex, completedConst));
                result.Add(new IrInst.LoadConstInt(statusTemp, 1));
                result.Add(new IrInst.Return(statusTemp));
            }
            else
            {
                result.Add(AdjustLoadEnvForStateStruct(inst, stateStructTemp, captureCount));
            }
        }
    }

    /// <summary>
    /// Emits the suspend sequence at an await point: saves live temps and locals to the state
    /// struct, stores the awaited sub-task pointer, advances the state index, and returns the
    /// SUSPENDED status.
    /// </summary>
    private static void EmitSuspendAtAwait(
        IrInst.AwaitTask awaitTask, List<IrInst> result, int stateIdx,
        List<HashSet<int>> liveAcross, List<HashSet<int>> liveLocalsAcross,
        Dictionary<int, int> tempToSlotOffset, Dictionary<int, int> localToSlotOffset,
        int stateStructTemp, int statusTemp, ref int maxTemp)
    {
        // Suspend: save live temps to state struct
        var liveTempsAtThisPoint = liveAcross[stateIdx];
        var saveVars = new List<(int SlotOffset, int SourceTemp)>();
        foreach (int temp in liveTempsAtThisPoint)
        {
            if (tempToSlotOffset.TryGetValue(temp, out int offset))
            {
                result.Add(new IrInst.StoreMemOffset(stateStructTemp, offset, temp));
                saveVars.Add((offset, temp));
            }
        }

        // Suspend: save live locals to state struct
        var liveLocalsAtThisPoint = liveLocalsAcross[stateIdx];
        foreach (int local in liveLocalsAtThisPoint)
        {
            if (localToSlotOffset.TryGetValue(local, out int offset))
            {
                int loadTemp = ++maxTemp;
                result.Add(new IrInst.LoadLocal(loadTemp, local));
                result.Add(new IrInst.StoreMemOffset(stateStructTemp, offset, loadTemp));
            }
        }

        // Store the awaited sub-task pointer
        result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.AwaitedTask, awaitTask.TaskTemp));

        // Set next state index
        int nextStateConst = ++maxTemp;
        result.Add(new IrInst.LoadConstInt(nextStateConst, stateIdx + 1));
        result.Add(new IrInst.StoreMemOffset(stateStructTemp, TaskStructLayout.StateIndex, nextStateConst));

        result.Add(new IrInst.Suspend(stateStructTemp, stateIdx + 1, awaitTask.TaskTemp, saveVars));

        // Return 0 (SUSPENDED status)
        result.Add(new IrInst.LoadConstInt(statusTemp, 0));
        result.Add(new IrInst.Return(statusTemp));
    }

    /// <summary>
    /// Asserts the structured-parallelism invariant that a <c>both</c>'s fork → join → cleanup group
    /// (and likewise a queued reduce's <see cref="IrInst.ParallelQueueStart"/> →
    /// <see cref="IrInst.ParallelQueueCleanup"/> group) stays within a single coroutine segment: no
    /// <see cref="IrInst.AwaitTask"/> may appear between
    /// a <see cref="IrInst.ParallelFork"/> and its matching <see cref="IrInst.ParallelCleanup"/>.
    /// The worker descriptor and worker arena are not serialized into the coroutine state struct, so
    /// a fork straddling an <c>await</c> would leak the worker and dangle its arena on resume.
    /// <see cref="Lowering"/>'s <c>LowerParallelBoth</c> emits fork+join+cleanup contiguously with no
    /// intervening <c>await</c>, so this holds by construction — this check turns that contract into a
    /// hard invariant so a future lowering change can't silently break it.
    /// </summary>
    private static void AssertParallelForkJoinWithinSegment(List<IrInst> instructions)
    {
        var openForks = new HashSet<int>();
        foreach (var inst in instructions)
        {
            switch (inst)
            {
                case IrInst.ParallelFork fork:
                    openForks.Add(fork.DescTarget);
                    break;
                case IrInst.ParallelCleanup cleanup:
                    openForks.Remove(cleanup.DescTemp);
                    break;
                case IrInst.ParallelQueueStart queueStart:
                    openForks.Add(queueStart.DescTarget);
                    break;
                case IrInst.ParallelQueueCleanup queueCleanup:
                    openForks.Remove(queueCleanup.DescTemp);
                    break;
                case IrInst.AwaitTask when openForks.Count > 0:
                    throw new InvalidOperationException(
                        "Structured-parallelism invariant violated: an `await` appears between a " +
                        "ParallelFork and its ParallelCleanup. A `both` fork/join/cleanup must stay " +
                        "within a single coroutine segment because the worker descriptor and arena " +
                        "are not part of the serialized coroutine state.");
            }
        }
    }

    /// <summary>
    /// LoadEnv instructions in lambdas load from env_ptr at offset index*8.
    /// In a coroutine, captures are in the state struct at HeaderSize + index*8.
    /// This adjusts LoadEnv to LoadMemOffset with the correct base temp and offset.
    /// </summary>
    private static IrInst AdjustLoadEnvForStateStruct(IrInst inst, int stateStructTemp, int captureCount)
    {
        if (inst is IrInst.LoadEnv loadEnv)
        {
            // Replace LoadEnv with LoadMemOffset from the state struct temp.
            // Captures are at HeaderSize + index*8 in the state struct.
            return new IrInst.LoadMemOffset(loadEnv.Target, stateStructTemp,
                TaskStructLayout.HeaderSize + loadEnv.Index * 8)
            {
                Location = inst.Location
            };
        }
        return inst;
    }

    /// <summary>
    /// Splits the instruction list into segments separated at AwaitTask positions.
    /// Segment 0: instructions[0..await0] (including the AwaitTask)
    /// Segment 1: instructions[await0+1..await1] (including the AwaitTask)
    /// ...
    /// Segment N: instructions[awaitN-1+1..end] (no AwaitTask, ends with Return)
    /// </summary>
    private static List<List<IrInst>> SplitAtAwaits(List<IrInst> instructions, List<int> awaitPositions)
    {
        var segments = new List<List<IrInst>>();
        int start = 0;
        foreach (int pos in awaitPositions)
        {
            var segment = new List<IrInst>();
            for (int i = start; i <= pos; i++)
            {
                segment.Add(instructions[i]);
            }
            segments.Add(segment);
            start = pos + 1;
        }
        // Final segment (after last await to end)
        var lastSegment = new List<IrInst>();
        for (int i = start; i < instructions.Count; i++)
        {
            lastSegment.Add(instructions[i]);
        }
        segments.Add(lastSegment);
        return segments;
    }

    /// <summary>
    /// For each await point, computes which temps are live across that point
    /// (defined before and used after the await).
    /// </summary>
    private static List<HashSet<int>> ComputeLiveTempsAcrossAwaits(
        List<IrInst> instructions, List<int> awaitPositions)
    {
        var result = new List<HashSet<int>>();

        foreach (int awaitPos in awaitPositions)
        {
            // Collect temps defined before the await point
            var definedBefore = new HashSet<int>();
            for (int i = 0; i < awaitPos; i++)
            {
                foreach (int t in GetDefinedTemps(instructions[i]))
                {
                    definedBefore.Add(t);
                }
            }

            // Collect temps used after the await point (not including the AwaitTask itself)
            var usedAfter = new HashSet<int>();
            for (int i = awaitPos + 1; i < instructions.Count; i++)
            {
                foreach (int t in GetUsedTemps(instructions[i]))
                {
                    usedAfter.Add(t);
                }
            }

            // Live across = defined before AND used after
            var live = new HashSet<int>(definedBefore);
            live.IntersectWith(usedAfter);

            // Don't save temp 0 (state struct pointer) — it's always available
            live.Remove(0);

            result.Add(live);
        }

        return result;
    }

    /// <summary>
    /// Whether any jump in the body targets a label defined at or before the jump's own position —
    /// a loop back-edge. Emitted only by the async tail-recursive-loop lowering (the helper
    /// coroutine's restart edge); plain async bodies are forward-only.
    /// </summary>
    private static bool HasBackwardJump(List<IrInst> instructions)
    {
        var labelPositions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is IrInst.Label label)
            {
                labelPositions[label.Name] = i;
            }
        }

        for (int i = 0; i < instructions.Count; i++)
        {
            string? target = instructions[i] switch
            {
                IrInst.Jump j => j.Target,
                IrInst.JumpIfFalse jf => jf.Target,
                _ => null
            };
            if (target is not null && labelPositions.TryGetValue(target, out int pos) && pos <= i)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// For each await point, computes which local slots are live across that point
    /// (written before and read after the await). Local slot 0 (state struct) and
    /// slot 1 (dummy arg) are excluded since they're function parameters.
    /// </summary>
    private static List<HashSet<int>> ComputeLiveLocalsAcrossAwaits(
        List<IrInst> instructions, List<int> awaitPositions, bool hasBackEdge)
    {
        var result = new List<HashSet<int>>();

        if (hasBackEdge)
        {
            // A back-edge makes any written-and-read local potentially live across any await
            // (reads positionally before the await are reachable after a resume via the loop).
            // Save/restore the full written∩read set at every suspend point.
            var writtenAnywhere = new HashSet<int>();
            var readAnywhere = new HashSet<int>();
            foreach (var inst in instructions)
            {
                foreach (int slot in GetWrittenLocalSlots(inst)) writtenAnywhere.Add(slot);
                foreach (int slot in GetReadLocalSlots(inst)) readAnywhere.Add(slot);
            }

            writtenAnywhere.IntersectWith(readAnywhere);
            writtenAnywhere.Remove(0);
            writtenAnywhere.Remove(1);
            foreach (int _ in awaitPositions)
            {
                result.Add(new HashSet<int>(writtenAnywhere));
            }

            return result;
        }

        foreach (int awaitPos in awaitPositions)
        {
            // Collect locals written before the await point
            var writtenBefore = new HashSet<int>();
            for (int i = 0; i < awaitPos; i++)
            {
                foreach (int slot in GetWrittenLocalSlots(instructions[i]))
                    writtenBefore.Add(slot);
            }

            // Collect locals read after the await point
            var readAfter = new HashSet<int>();
            for (int i = awaitPos + 1; i < instructions.Count; i++)
            {
                foreach (int slot in GetReadLocalSlots(instructions[i]))
                    readAfter.Add(slot);
            }

            // Live across = written before AND read after
            var live = new HashSet<int>(writtenBefore);
            live.IntersectWith(readAfter);

            // Exclude slots 0 and 1 (function parameters)
            live.Remove(0);
            live.Remove(1);

            result.Add(live);
        }

        return result;
    }

    /// <summary>
    /// Returns all local slots written (defined) by an instruction.
    /// Includes explicit StoreLocal and implicit writes by arena instructions.
    /// </summary>
    private static IEnumerable<int> GetWrittenLocalSlots(IrInst inst)
    {
        return inst switch
        {
            IrInst.StoreLocal s => [s.Slot],
            IrInst.SaveArenaState s => [s.CursorLocalSlot, s.EndLocalSlot],
            IrInst.RestoreArenaState r => [r.PreRestoreEndSlot],
            _ => []
        };
    }

    /// <summary>
    /// Returns all local slots read (used) by an instruction.
    /// Includes explicit LoadLocal and implicit reads by arena instructions.
    /// </summary>
    private static IEnumerable<int> GetReadLocalSlots(IrInst inst)
    {
        return inst switch
        {
            IrInst.LoadLocal l => [l.Slot],
            IrInst.RestoreArenaState r => [r.CursorLocalSlot, r.EndLocalSlot],
            IrInst.ReclaimArenaChunks r => [r.SavedEndSlot, r.PreRestoreEndSlot],
            _ => []
        };
    }
    /// <summary>
    /// Returns all temps defined (written to) by an instruction.
    /// IMPORTANT: When adding new IrInst types, you MUST add a case here (or in one of the
    /// continuation helpers chained via the default arm) and in GetUsedTemps to ensure
    /// correct liveness analysis across await points.
    /// </summary>
    internal static IEnumerable<int> GetDefinedTemps(IrInst inst)
    {
        return inst switch
        {
            IrInst.LoadConstInt i => [i.Target],
            IrInst.LoadConstFloat i => [i.Target],
            IrInst.LoadConstBool i => [i.Target],
            IrInst.LoadConstStr i => [i.Target],
            IrInst.LoadProgramArgs i => [i.Target],
            IrInst.LoadLocal i => [i.Target],
            IrInst.LoadEnv i => [i.Target],
            IrInst.LoadMemOffset i => [i.Target],
            IrInst.AddInt i => [i.Target],
            IrInst.SubInt i => [i.Target],
            IrInst.MulInt i => [i.Target],
            IrInst.DivInt i => [i.Target],
            IrInst.DivUInt i => [i.Target],
            IrInst.AndInt i => [i.Target],
            IrInst.OrInt i => [i.Target],
            IrInst.XorInt i => [i.Target],
            IrInst.ShlInt i => [i.Target],
            IrInst.ShrInt i => [i.Target],
            IrInst.AddFloat i => [i.Target],
            IrInst.SubFloat i => [i.Target],
            IrInst.MulFloat i => [i.Target],
            IrInst.DivFloat i => [i.Target],
            IrInst.CmpIntGt i => [i.Target],
            IrInst.CmpIntGe i => [i.Target],
            IrInst.CmpIntLt i => [i.Target],
            IrInst.CmpIntLe i => [i.Target],
            IrInst.CmpIntEq i => [i.Target],
            IrInst.CmpIntNe i => [i.Target],
            IrInst.CmpFloatGt i => [i.Target],
            IrInst.CmpFloatGe i => [i.Target],
            IrInst.CmpFloatLt i => [i.Target],
            IrInst.CmpFloatLe i => [i.Target],
            IrInst.CmpFloatEq i => [i.Target],
            IrInst.CmpFloatNe i => [i.Target],
            IrInst.CmpStrEq i => [i.Target],
            IrInst.CmpStrNe i => [i.Target],
            IrInst.ConcatStr i => [i.Target],
            IrInst.ConcatStrTip i => [i.Target],
            IrInst.RegexCompile i => [i.Target],
            IrInst.RegexCompileError i => [i.Target],
            IrInst.RegexFind i => [i.Target],
            IrInst.RegexCaptures i => [i.Target],
            IrInst.RegexSubstitute i => [i.Target],
            IrInst.MakeClosure i => [i.Target],
            IrInst.MakeClosureStack i => [i.Target],
            IrInst.CallClosure i => [i.Target],
            IrInst.CallKnown i => [i.Target],
            IrInst.ToCString i => [i.Target],
            IrInst.CallExternal i => [i.Target],
            _ => GetDefinedTempsAllocFileText(inst)
        };
    }

    /// <summary>
    /// Continues <see cref="GetDefinedTemps"/> for allocation, ADT, file, text, and
    /// big-integer instructions.
    /// </summary>
    private static IEnumerable<int> GetDefinedTempsAllocFileText(IrInst inst)
    {
        return inst switch
        {
            IrInst.Alloc i => [i.Target],
            IrInst.AllocStack i => [i.Target],
            IrInst.AllocAdt i => [i.Target],
            IrInst.AllocAdtToSpace i => [i.Target],
            IrInst.AllocAdtStack i => [i.Target],
            IrInst.GetAdtTag i => [i.Target],
            IrInst.GetAdtField i => [i.Target],
            IrInst.ReadLine i => [i.Target],
            IrInst.FileReadText i => [i.Target],
            IrInst.FileWriteText i => [i.Target],
            IrInst.FileExists i => [i.Target],
            IrInst.FileOpen i => [i.Target],
            IrInst.FileReadChunk i => [i.Target],
            IrInst.FileReadLine i => [i.Target],
            IrInst.FileClose i => [i.Target],
            IrInst.TextUncons i => [i.Target],
            IrInst.TextParseInt i => [i.Target],
            IrInst.TextParseFloat i => [i.Target],
            IrInst.TextFromInt i => [i.Target],
            IrInst.TextFromFloat i => [i.Target],
            IrInst.TextFormatFloat i => [i.Target],
            IrInst.BigIntFromInt i => [i.Target],
            IrInst.BigIntToString i => [i.Target],
            IrInst.BigIntToInt i => [i.Target],
            IrInst.BigIntFromString i => [i.Target],
            IrInst.BigIntBinary i => [i.Target],
            IrInst.BigIntCompare i => [i.Target],
            IrInst.TextToHex i => [i.Target],
            IrInst.TextAsciiCase i => [i.Target],
            _ => GetDefinedTempsNetAndBytes(inst)
        };
    }

    /// <summary>
    /// Continues <see cref="GetDefinedTemps"/> for HTTP, TCP, bytes, and byte-file
    /// instructions.
    /// </summary>
    private static IEnumerable<int> GetDefinedTempsNetAndBytes(IrInst inst)
    {
        return inst switch
        {
            IrInst.HttpGet i => [i.Target],
            IrInst.HttpPost i => [i.Target],
            IrInst.NetTcpConnect i => [i.Target],
            IrInst.NetTcpSend i => [i.Target],
            IrInst.NetTcpReceive i => [i.Target],
            IrInst.NetTcpClose i => [i.Target],
            IrInst.NetTcpListen i => [i.Target],
            IrInst.NetTcpAccept i => [i.Target],
            IrInst.BytesEmpty i => [i.Target],
            IrInst.BytesSingleton i => [i.Target],
            IrInst.BytesLength i => [i.Target],
            IrInst.BytesGet i => [i.Target],
            IrInst.BytesIndexOf i => [i.Target],
            IrInst.BytesCompare i => [i.Target],
            IrInst.BytesScanHash i => [i.Target],
            IrInst.BytesSubText i => [i.Target],
            IrInst.BytesSubView i => [i.Target],
            IrInst.BytesAppend i => [i.Target],
            IrInst.BytesAppendByte i => [i.Target],
            IrInst.BytesFromList i => [i.Target],
            IrInst.BytesHash i => [i.Target],
            IrInst.BytesU16Le i => [i.Target],
            IrInst.BytesU32Le i => [i.Target],
            IrInst.BytesU64Le i => [i.Target],
            IrInst.BytesGetU16Le i => [i.Target],
            IrInst.BytesGetU32Le i => [i.Target],
            IrInst.BytesGetU64Le i => [i.Target],
            IrInst.FileWriteBytes i => [i.Target],
            _ => GetDefinedTempsTaskAndParallel(inst)
        };
    }

    /// <summary>
    /// Continues <see cref="GetDefinedTemps"/> for borrow, task, and structured-parallelism
    /// instructions; the default arm here is the final "defines no temps" fallback.
    /// </summary>
    private static IEnumerable<int> GetDefinedTempsTaskAndParallel(IrInst inst)
    {
        return inst switch
        {
            IrInst.Borrow i => [i.Target],
            IrInst.RcDup i => [i.Target],
            IrInst.CreateTask i => [i.Target],
            IrInst.CreateCompletedTask i => [i.Target],
            IrInst.AwaitTask i => [i.Target],
            IrInst.RunTask i => [i.Target],
            IrInst.SpawnTask i => [i.Target],
            IrInst.AsyncSleep i => [i.Target],
            IrInst.CreateTcpConnectTask i => [i.Target],
            IrInst.CreateTcpSendTask i => [i.Target],
            IrInst.CreateTcpReceiveTask i => [i.Target],
            IrInst.CreateTcpCloseTask i => [i.Target],
            IrInst.CreateTcpListenTask i => [i.Target],
            IrInst.CreateTcpAcceptTask i => [i.Target],
            IrInst.CreateHttpGetTask i => [i.Target],
            IrInst.CreateHttpPostTask i => [i.Target],
            IrInst.CreateTlsConnectTask i => [i.Target],
            IrInst.CreateTlsHandshakeTask i => [i.Target],
            IrInst.CreateTlsServerHandshakeTask sth => [sth.Target],
            IrInst.CreateTlsSendTask i => [i.Target],
            IrInst.CreateTlsReceiveTask i => [i.Target],
            IrInst.CreateTlsCloseTask i => [i.Target],
            IrInst.AsyncAll i => [i.Target],
            IrInst.AsyncRace i => [i.Target],
            // Structured parallelism (`both`). A `both`'s worker descriptor or joined result may be
            // live across an `await` split, so the transform must know these instructions define
            // temps — otherwise the value would be dropped from the coroutine save/restore set and
            // read back as garbage on resume.
            IrInst.ParallelFork i => [i.DescTarget],
            IrInst.ParallelJoin i => [i.ResultTarget],
            IrInst.ParallelQueueStart i => [i.DescTarget],
            IrInst.ParallelQueueAwait i => [i.ResultTarget],
            _ => []
        };
    }

    /// <summary>
    /// Returns all temps used (read) by an instruction.
    /// IMPORTANT: When adding new IrInst types, you MUST add a case here (or in one of the
    /// continuation helpers chained via the default arm) and in GetDefinedTemps to ensure
    /// correct liveness analysis across await points.
    /// </summary>
    private static IEnumerable<int> GetUsedTemps(IrInst inst)
    {
        return inst switch
        {
            IrInst.StoreLocal s => [s.Source],
            IrInst.StoreMemOffset s => [s.BasePtr, s.Source],
            IrInst.LoadMemOffset l => [l.BasePtr],
            IrInst.AddInt a => [a.Left, a.Right],
            IrInst.SubInt s => [s.Left, s.Right],
            IrInst.MulInt m => [m.Left, m.Right],
            IrInst.DivInt d => [d.Left, d.Right],
            IrInst.DivUInt d => [d.Left, d.Right],
            IrInst.AndInt a => [a.Left, a.Right],
            IrInst.OrInt o => [o.Left, o.Right],
            IrInst.XorInt x => [x.Left, x.Right],
            IrInst.ShlInt s => [s.Left, s.Right],
            IrInst.ShrInt s => [s.Left, s.Right],
            IrInst.AddFloat a => [a.Left, a.Right],
            IrInst.SubFloat s => [s.Left, s.Right],
            IrInst.MulFloat m => [m.Left, m.Right],
            IrInst.DivFloat d => [d.Left, d.Right],
            IrInst.CmpIntGt c => [c.Left, c.Right],
            IrInst.CmpIntGe c => [c.Left, c.Right],
            IrInst.CmpIntLt c => [c.Left, c.Right],
            IrInst.CmpIntLe c => [c.Left, c.Right],
            IrInst.CmpIntEq c => [c.Left, c.Right],
            IrInst.CmpIntNe c => [c.Left, c.Right],
            IrInst.CmpFloatGt c => [c.Left, c.Right],
            IrInst.CmpFloatGe c => [c.Left, c.Right],
            IrInst.CmpFloatLt c => [c.Left, c.Right],
            IrInst.CmpFloatLe c => [c.Left, c.Right],
            IrInst.CmpFloatEq c => [c.Left, c.Right],
            IrInst.CmpFloatNe c => [c.Left, c.Right],
            IrInst.CmpStrEq c => [c.Left, c.Right],
            IrInst.CmpStrNe c => [c.Left, c.Right],
            IrInst.ConcatStr c => [c.Left, c.Right],
            IrInst.ConcatStrTip c => [c.Left, c.Right],
            IrInst.RegexCompile c => [c.Pattern],
            IrInst.RegexCompileError c => [c.Pattern],
            IrInst.RegexFind c => [c.Code, c.Subject, c.Start],
            IrInst.RegexCaptures c => [c.Code, c.Subject, c.Start],
            IrInst.RegexSubstitute c => [c.Code, c.Subject, c.Replacement],
            IrInst.MakeClosure mc => [mc.EnvPtrTemp],
            IrInst.MakeClosureStack mc => [mc.EnvPtrTemp],
            IrInst.CallClosure cc => [cc.ClosureTemp, cc.ArgTemp],
            IrInst.CallKnown ck => [ck.EnvTemp, ck.ArgTemp],
            IrInst.ToCString c => [c.StrTemp],
            IrInst.CallExternal c => c.ArgTemps,
            _ => GetUsedTempsAdtFileText(inst)
        };
    }

    /// <summary>
    /// Continues <see cref="GetUsedTemps"/> for ADT, print, file, text, and big-integer
    /// instructions.
    /// </summary>
    private static IEnumerable<int> GetUsedTempsAdtFileText(IrInst inst)
    {
        return inst switch
        {
            IrInst.SetAdtField sf => [sf.Ptr, sf.Source],
            IrInst.GetAdtTag gt => [gt.Ptr],
            IrInst.GetAdtField gf => [gf.Ptr],
            IrInst.PrintInt p => [p.Source],
            IrInst.PrintStr p => [p.Source],
            IrInst.PrintBool p => [p.Source],
            IrInst.WriteStr w => [w.Source],
            IrInst.FileReadText f => [f.PathTemp],
            IrInst.FileWriteText f => [f.PathTemp, f.TextTemp],
            IrInst.FileExists f => [f.PathTemp],
            IrInst.FileOpen f => [f.PathTemp],
            IrInst.FileReadChunk f => [f.HandleTemp, f.CountTemp],
            IrInst.FileReadLine f => [f.HandleTemp],
            IrInst.FileClose f => [f.HandleTemp],
            IrInst.TextUncons t => [t.TextTemp],
            IrInst.TextParseInt t => [t.TextTemp],
            IrInst.TextParseFloat t => [t.TextTemp],
            IrInst.TextFromInt t => [t.ValueTemp],
            IrInst.TextFromFloat t => [t.ValueTemp],
            IrInst.TextFormatFloat t => [t.ValueTemp, t.DecimalsTemp],
            IrInst.BigIntFromInt t => [t.ValueTemp],
            IrInst.BigIntToString t => [t.ValueTemp],
            IrInst.BigIntToInt t => [t.ValueTemp],
            IrInst.BigIntFromString t => [t.ValueTemp],
            IrInst.BigIntBinary t => [t.Left, t.Right],
            IrInst.BigIntCompare t => [t.Left, t.Right],
            IrInst.TextToHex t => [t.ValueTemp],
            IrInst.TextAsciiCase t => [t.SourceTemp],
            _ => GetUsedTempsNetAndBytes(inst)
        };
    }

    /// <summary>
    /// Continues <see cref="GetUsedTemps"/> for HTTP, TCP, bytes, and byte-file
    /// instructions.
    /// </summary>
    private static IEnumerable<int> GetUsedTempsNetAndBytes(IrInst inst)
    {
        return inst switch
        {
            IrInst.HttpGet h => [h.UrlTemp],
            IrInst.HttpPost h => [h.UrlTemp, h.BodyTemp],
            IrInst.NetTcpConnect n => [n.HostTemp, n.PortTemp],
            IrInst.NetTcpSend n => [n.SocketTemp, n.TextTemp],
            IrInst.NetTcpReceive n => [n.SocketTemp, n.MaxBytesTemp],
            IrInst.NetTcpClose n => [n.SocketTemp],
            IrInst.NetTcpListen n => [n.PortTemp],
            IrInst.NetTcpAccept n => [n.SocketTemp],
            IrInst.BytesEmpty => [],
            IrInst.BytesSingleton i => [i.ByteTemp],
            IrInst.BytesLength i => [i.BytesTemp],
            IrInst.BytesGet i => [i.BytesTemp, i.IndexTemp],
            IrInst.BytesIndexOf i => [i.BytesTemp, i.NeedleTemp, i.FromTemp],
            IrInst.BytesCompare i => [i.LeftTemp, i.RightTemp],
            IrInst.BytesScanHash i => [i.BytesTemp, i.NeedleTemp, i.FromTemp],
            IrInst.BytesSubText i => [i.BytesTemp, i.StartTemp, i.LenTemp],
            IrInst.BytesSubView i => [i.BytesTemp, i.StartTemp, i.LenTemp],
            IrInst.BytesAppend i => [i.LeftTemp, i.RightTemp],
            IrInst.BytesAppendByte i => [i.BytesTemp, i.ByteTemp],
            IrInst.BytesFromList i => [i.ListTemp],
            IrInst.BytesHash i => [i.BytesTemp],
            IrInst.BytesU16Le i => [i.ValueTemp],
            IrInst.BytesU32Le i => [i.ValueTemp],
            IrInst.BytesU64Le i => [i.ValueTemp],
            IrInst.BytesGetU16Le i => [i.BytesTemp, i.OffsetTemp],
            IrInst.BytesGetU32Le i => [i.BytesTemp, i.OffsetTemp],
            IrInst.BytesGetU64Le i => [i.BytesTemp, i.OffsetTemp],
            IrInst.FileWriteBytes i => [i.PathTemp, i.BytesTemp],
            _ => GetUsedTempsTaskAndParallel(inst)
        };
    }

    /// <summary>
    /// Continues <see cref="GetUsedTemps"/> for lifetime/resource cleanup, task, structured-parallelism, and
    /// control-flow instructions; the default arm here is the final "uses no temps" fallback.
    /// </summary>
    private static IEnumerable<int> GetUsedTempsTaskAndParallel(IrInst inst)
    {
        return inst switch
        {
            IrInst.CleanupResource d => [d.SourceTemp],
            IrInst.RcDrop d => [d.SourceTemp],
            IrInst.RcDup d => [d.SourceTemp],
            IrInst.Borrow b => [b.SourceTemp],
            IrInst.CreateTask ct => [ct.ClosureTemp],
            IrInst.CreateCompletedTask ct => [ct.ResultTemp],
            IrInst.AwaitTask at => [at.TaskTemp],
            IrInst.RunTask rt => [rt.TaskTemp],
            IrInst.SpawnTask st => [st.TaskTemp],
            IrInst.AsyncSleep sl => [sl.MillisecondsTemp],
            IrInst.CreateTcpConnectTask t => [t.HostTemp, t.PortTemp],
            IrInst.CreateTcpSendTask t => [t.SocketTemp, t.TextTemp],
            IrInst.CreateTcpReceiveTask t => [t.SocketTemp, t.MaxBytesTemp],
            IrInst.CreateTcpCloseTask t => [t.SocketTemp],
            IrInst.CreateTcpListenTask t => [t.PortTemp],
            IrInst.CreateTcpAcceptTask t => [t.SocketTemp],
            IrInst.CreateHttpGetTask t => [t.UrlTemp],
            IrInst.CreateHttpPostTask t => [t.UrlTemp, t.BodyTemp],
            IrInst.CreateTlsConnectTask t => [t.HostTemp, t.PortTemp],
            IrInst.CreateTlsHandshakeTask t => [t.SocketTemp, t.HostTemp],
            IrInst.CreateTlsServerHandshakeTask sth => [sth.SocketTemp, sth.CertTemp, sth.KeyTemp],
            IrInst.CreateTlsSendTask t => [t.SslTemp, t.TextTemp],
            IrInst.CreateTlsReceiveTask t => [t.SslTemp, t.MaxBytesTemp],
            IrInst.CreateTlsCloseTask t => [t.SslTemp],
            IrInst.AsyncAll aa => [aa.TaskListTemp],
            IrInst.AsyncRace ar => [ar.TaskListTemp],
            // Structured parallelism (`both`). The fork reads its right-thunk closure, and the join
            // and cleanup read the worker descriptor; any of these temps defined before an `await`
            // and read after it must be recognised as used or it would be dropped from the coroutine
            // save/restore set.
            IrInst.ParallelFork p => [p.RightClosureTemp],
            IrInst.ParallelJoin p => [p.DescTemp],
            IrInst.ParallelCleanup p => [p.DescTemp],
            IrInst.ParallelQueueStart p => [p.FClosureTemp, p.CombineClosureTemp, p.ListTemp],
            IrInst.ParallelQueueAwait p => [p.DescTemp],
            IrInst.ParallelQueueCleanup p => [p.DescTemp],
            IrInst.PanicStr p => [p.Source],
            IrInst.JumpIfFalse j => [j.CondTemp],
            IrInst.Return r => [r.Source],
            _ => []
        };
    }

    /// <summary>
    /// Returns all temps referenced (both defined and used) by an instruction.
    /// </summary>
    private static IEnumerable<int> GetAllTemps(IrInst inst)
    {
        return GetDefinedTemps(inst).Concat(GetUsedTemps(inst));
    }
}
