namespace Ashes.Semantics;

/// <summary>
/// IR-level optimization pass pipeline (Phase 4).
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
        var optimizedEntry = OptimizeFunction(program.EntryFunction);
        var optimizedFuncs = program.Functions.Select(OptimizeFunction).ToList();

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
        instructions = ElideBorrowsForConstants(instructions);
        instructions = FoldConstants(instructions);
        instructions = ReduceIdentitiesAndStrength(instructions);
        instructions = ElideUnreachableCode(instructions);
        instructions = ElideDeadCode(instructions);
        instructions = ElideRedundantDrops(instructions);

        return function with
        {
            Instructions = instructions,
        };
    }

    // ── Pass 1: Elide borrows for constants and copy-type loads ─────────
    // A Borrow of a LoadConstInt/LoadConstFloat/LoadConstBool is a no-op
    // in the backend (simple value copy). The Borrow instruction exists for
    // semantic completeness but copy types have no ownership semantics.
    // Currently this pass is a no-op because the backend already handles
    // Borrow as a trivial value copy that LLVM's codegen optimizes away.
    // When temp aliasing / remapping is added, this pass will eliminate
    // the Borrow instructions entirely for copy-type sources.

    private static List<IrInst> ElideBorrowsForConstants(List<IrInst> instructions)
    {
        // Future: when temp aliasing infrastructure exists, remove Borrow
        // instructions whose source is a copy-type constant and remap uses
        // of the borrow target to the original source.
        return instructions;
    }

    // ── Pass 2: Constant folding ────────────────────────────────────────
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
        var savedIntStates = new Dictionary<string, Dictionary<int, long>>();
        var savedFloatStates = new Dictionary<string, Dictionary<int, double>>();
        var savedBoolStates = new Dictionary<string, Dictionary<int, bool>>();

        var result = new List<IrInst>(instructions.Count);
        bool changed = false;
        bool prevIsTerminator = false; // tracks whether the previous instruction was Jump/Return

        foreach (var inst in instructions)
        {
            switch (inst)
            {
                case IrInst.LoadConstInt lci:
                    knownInts[lci.Target] = lci.Value;
                    result.Add(inst);
                    prevIsTerminator = false;
                    break;

                case IrInst.LoadConstFloat lcf:
                    knownFloats[lcf.Target] = lcf.Value;
                    result.Add(inst);
                    prevIsTerminator = false;
                    break;

                case IrInst.LoadConstBool lcb:
                    knownBools[lcb.Target] = lcb.Value;
                    result.Add(inst);
                    prevIsTerminator = false;
                    break;

                case IrInst.AddInt add when knownInts.ContainsKey(add.Left) && knownInts.ContainsKey(add.Right):
                    {
                        long folded = knownInts[add.Left] + knownInts[add.Right];
                        knownInts[add.Target] = folded;
                        result.Add(new IrInst.LoadConstInt(add.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.SubInt sub when knownInts.ContainsKey(sub.Left) && knownInts.ContainsKey(sub.Right):
                    {
                        long folded = knownInts[sub.Left] - knownInts[sub.Right];
                        knownInts[sub.Target] = folded;
                        result.Add(new IrInst.LoadConstInt(sub.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.MulInt mul when knownInts.ContainsKey(mul.Left) && knownInts.ContainsKey(mul.Right):
                    {
                        long folded = knownInts[mul.Left] * knownInts[mul.Right];
                        knownInts[mul.Target] = folded;
                        result.Add(new IrInst.LoadConstInt(mul.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.DivInt div when knownInts.ContainsKey(div.Left) && knownInts.ContainsKey(div.Right)
                                           && knownInts[div.Right] != 0:
                    {
                        long folded = knownInts[div.Left] / knownInts[div.Right];
                        knownInts[div.Target] = folded;
                        result.Add(new IrInst.LoadConstInt(div.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.AddFloat addF when knownFloats.ContainsKey(addF.Left) && knownFloats.ContainsKey(addF.Right):
                    {
                        double folded = knownFloats[addF.Left] + knownFloats[addF.Right];
                        knownFloats[addF.Target] = folded;
                        result.Add(new IrInst.LoadConstFloat(addF.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.SubFloat subF when knownFloats.ContainsKey(subF.Left) && knownFloats.ContainsKey(subF.Right):
                    {
                        double folded = knownFloats[subF.Left] - knownFloats[subF.Right];
                        knownFloats[subF.Target] = folded;
                        result.Add(new IrInst.LoadConstFloat(subF.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.MulFloat mulF when knownFloats.ContainsKey(mulF.Left) && knownFloats.ContainsKey(mulF.Right):
                    {
                        double folded = knownFloats[mulF.Left] * knownFloats[mulF.Right];
                        knownFloats[mulF.Target] = folded;
                        result.Add(new IrInst.LoadConstFloat(mulF.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.DivFloat divF when knownFloats.ContainsKey(divF.Left) && knownFloats.ContainsKey(divF.Right):
                    {
                        double folded = knownFloats[divF.Left] / knownFloats[divF.Right];
                        knownFloats[divF.Target] = folded;
                        result.Add(new IrInst.LoadConstFloat(divF.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.CmpIntEq cmpEq when knownInts.ContainsKey(cmpEq.Left) && knownInts.ContainsKey(cmpEq.Right):
                    {
                        bool folded = knownInts[cmpEq.Left] == knownInts[cmpEq.Right];
                        knownBools[cmpEq.Target] = folded;
                        result.Add(new IrInst.LoadConstBool(cmpEq.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.CmpIntNe cmpNe when knownInts.ContainsKey(cmpNe.Left) && knownInts.ContainsKey(cmpNe.Right):
                    {
                        bool folded = knownInts[cmpNe.Left] != knownInts[cmpNe.Right];
                        knownBools[cmpNe.Target] = folded;
                        result.Add(new IrInst.LoadConstBool(cmpNe.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.CmpIntGe cmpGe when knownInts.ContainsKey(cmpGe.Left) && knownInts.ContainsKey(cmpGe.Right):
                    {
                        bool folded = knownInts[cmpGe.Left] >= knownInts[cmpGe.Right];
                        knownBools[cmpGe.Target] = folded;
                        result.Add(new IrInst.LoadConstBool(cmpGe.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                case IrInst.CmpIntLe cmpLe when knownInts.ContainsKey(cmpLe.Left) && knownInts.ContainsKey(cmpLe.Right):
                    {
                        bool folded = knownInts[cmpLe.Left] <= knownInts[cmpLe.Right];
                        knownBools[cmpLe.Target] = folded;
                        result.Add(new IrInst.LoadConstBool(cmpLe.Target, folded) { Location = inst.Location });
                        changed = true;
                        break;
                    }

                // Labels, jumps, and control flow invalidate constant knowledge —
                // unless the label has a single predecessor, in which case we can
                // propagate constants from that predecessor.
                case IrInst.Label lbl:
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

                        result.Add(inst);
                        prevIsTerminator = false;
                        break;
                    }

                case IrInst.JumpIfFalse jif:
                    // Save state for the target label — will be used if the label turns
                    // out to be a single-predecessor label (only this branch targets it).
                    savedIntStates[jif.Target] = new Dictionary<int, long>(knownInts);
                    savedFloatStates[jif.Target] = new Dictionary<int, double>(knownFloats);
                    savedBoolStates[jif.Target] = new Dictionary<int, bool>(knownBools);
                    result.Add(inst);
                    prevIsTerminator = false; // JumpIfFalse is conditional, not a terminator
                    break;

                case IrInst.Jump jmp:
                    // Save state for the target label.
                    savedIntStates[jmp.Target] = new Dictionary<int, long>(knownInts);
                    savedFloatStates[jmp.Target] = new Dictionary<int, double>(knownFloats);
                    savedBoolStates[jmp.Target] = new Dictionary<int, bool>(knownBools);
                    result.Add(inst);
                    prevIsTerminator = true; // Jump is an unconditional terminator
                    break;

                default:
                    result.Add(inst);
                    prevIsTerminator = inst is IrInst.Return;
                    break;
            }
        }

        return changed ? result : instructions;
    }

    // ── Pass 2b: Identity elimination and strength reduction ─────────────
    // Simplify arithmetic with known identity values:
    //   x + 0 → x, 0 + x → x, x - 0 → x
    //   x * 1 → x, 1 * x → x, x * 0 → 0, 0 * x → 0
    //   x / 1 → x
    //   x * 2 → x + x (strength reduction)

    private static List<IrInst> ReduceIdentitiesAndStrength(List<IrInst> instructions)
    {
        var knownInts = new Dictionary<int, long>();
        var branchRefs = CountBranchRefsToLabels(instructions);
        var savedIntStates = new Dictionary<string, Dictionary<int, long>>();
        var result = new List<IrInst>(instructions.Count);
        bool changed = false;
        bool prevIsTerminator = false;

        foreach (var inst in instructions)
        {
            switch (inst)
            {
                case IrInst.LoadConstInt lci:
                    knownInts[lci.Target] = lci.Value;
                    result.Add(inst);
                    prevIsTerminator = false;
                    break;

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

                        break;
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

                        break;
                    }

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

                        break;
                    }

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

                        break;
                    }

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
                        prevIsTerminator = false;
                        break;
                    }

                case IrInst.JumpIfFalse jif:
                    savedIntStates[jif.Target] = new Dictionary<int, long>(knownInts);
                    result.Add(inst);
                    prevIsTerminator = false;
                    break;

                case IrInst.Jump jmp:
                    savedIntStates[jmp.Target] = new Dictionary<int, long>(knownInts);
                    result.Add(inst);
                    prevIsTerminator = true;
                    break;

                default:
                    result.Add(inst);
                    prevIsTerminator = inst is IrInst.Return;
                    break;
            }
        }

        return changed ? result : instructions;
    }

    // ── Pass 2c: Unreachable code elimination ───────────────────────────
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

            // Unconditional terminators: Jump and Return make subsequent code unreachable
            // until the next label.
            if (inst is IrInst.Jump or IrInst.Return)
            {
                unreachable = true;
            }
        }

        return changed ? result : instructions;
    }

    // ── Pass 3: Dead code elimination ───────────────────────────────────
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
            // Remove LoadConst* instructions whose target is never read
            if (inst is IrInst.LoadConstInt lci && !usedTemps.Contains(lci.Target))
            {
                changed = true;
                continue;
            }

            if (inst is IrInst.LoadConstFloat lcf && !usedTemps.Contains(lcf.Target))
            {
                changed = true;
                continue;
            }

            if (inst is IrInst.LoadConstBool lcb && !usedTemps.Contains(lcb.Target))
            {
                changed = true;
                continue;
            }

            // Remove StoreLocal instructions whose slot is never loaded
            if (inst is IrInst.StoreLocal sl && !loadedSlots.Contains(sl.Slot))
            {
                changed = true;
                continue;
            }

            result.Add(inst);
        }

        return changed ? result : instructions;
    }

    // ── Pass 4: Drop elision ────────────────────────────────────────────
    // Remove Drop instructions for slots that were never stored to
    // (the value is uninitialized or was already consumed).
    // Currently a no-op — the Drop instructions in the IR are still needed
    // for resource types (Socket). Arena-based deallocation now handles
    // bulk memory reclamation via RestoreArenaState. When per-object free()
    // or more granular arena analysis is added, this pass will skip drops
    // for values proven to be dead or moved.

    private static List<IrInst> ElideRedundantDrops(List<IrInst> instructions)
    {
        // Future: analyze ownership flow to identify drops on values that
        // are dead (never initialized) or already consumed (moved).
        return instructions;
    }

    /// <summary>
    /// Collects all temp indices that are read (used as operands) by an instruction.
    /// This does NOT include target/destination temps — only sources.
    /// </summary>
    private static void CollectUsedTemps(IrInst inst, HashSet<int> usedTemps)
    {
        switch (inst)
        {
            case IrInst.AddInt a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.SubInt s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.MulInt m: usedTemps.Add(m.Left); usedTemps.Add(m.Right); break;
            case IrInst.DivInt d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.AddFloat a: usedTemps.Add(a.Left); usedTemps.Add(a.Right); break;
            case IrInst.SubFloat s: usedTemps.Add(s.Left); usedTemps.Add(s.Right); break;
            case IrInst.MulFloat m: usedTemps.Add(m.Left); usedTemps.Add(m.Right); break;
            case IrInst.DivFloat d: usedTemps.Add(d.Left); usedTemps.Add(d.Right); break;
            case IrInst.CmpIntGe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntLe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpIntNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatGe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatLe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpFloatNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpStrEq c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.CmpStrNe c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.ConcatStr c: usedTemps.Add(c.Left); usedTemps.Add(c.Right); break;
            case IrInst.StoreLocal s: usedTemps.Add(s.Source); break;
            case IrInst.StoreMemOffset s: usedTemps.Add(s.BasePtr); usedTemps.Add(s.Source); break;
            case IrInst.LoadMemOffset l: usedTemps.Add(l.BasePtr); break;
            case IrInst.MakeClosure mc: usedTemps.Add(mc.EnvPtrTemp); break;
            case IrInst.CallClosure cc: usedTemps.Add(cc.ClosureTemp); usedTemps.Add(cc.ArgTemp); break;
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
            case IrInst.HttpGet h: usedTemps.Add(h.UrlTemp); break;
            case IrInst.HttpPost h: usedTemps.Add(h.UrlTemp); usedTemps.Add(h.BodyTemp); break;
            case IrInst.NetTcpConnect n: usedTemps.Add(n.HostTemp); usedTemps.Add(n.PortTemp); break;
            case IrInst.NetTcpSend n: usedTemps.Add(n.SocketTemp); usedTemps.Add(n.TextTemp); break;
            case IrInst.NetTcpReceive n: usedTemps.Add(n.SocketTemp); usedTemps.Add(n.MaxBytesTemp); break;
            case IrInst.NetTcpClose n: usedTemps.Add(n.SocketTemp); break;
            case IrInst.Drop d: usedTemps.Add(d.SourceTemp); break;
            case IrInst.Borrow b: usedTemps.Add(b.SourceTemp); break;
            case IrInst.CreateTask ct: usedTemps.Add(ct.ClosureTemp); break;
            case IrInst.CreateCompletedTask ct: usedTemps.Add(ct.ResultTemp); break;
            case IrInst.AwaitTask at: usedTemps.Add(at.TaskTemp); break;
            case IrInst.RunTask rt: usedTemps.Add(rt.TaskTemp); break;
            case IrInst.AsyncSleep sl: usedTemps.Add(sl.MillisecondsTemp); break;
            case IrInst.AsyncAll aa: usedTemps.Add(aa.TaskListTemp); break;
            case IrInst.AsyncRace ar: usedTemps.Add(ar.TaskListTemp); break;
            case IrInst.Suspend s:
                usedTemps.Add(s.StateStructTemp);
                usedTemps.Add(s.AwaitedTaskTemp);
                foreach (var (_, sourceTemp) in s.SaveVars) usedTemps.Add(sourceTemp);
                break;
            case IrInst.Resume r:
                usedTemps.Add(r.StateStructTemp);
                break;
            case IrInst.PanicStr p: usedTemps.Add(p.Source); break;
            case IrInst.JumpIfFalse j: usedTemps.Add(j.CondTemp); break;
            case IrInst.Return r: usedTemps.Add(r.Source); break;
                // LoadConstInt, LoadConstFloat, LoadConstBool, LoadConstStr, LoadLocal,
                // LoadEnv, LoadProgramArgs, ReadLine, Alloc, AllocAdt, Label, Jump:
                // These either have no source temps or only define targets.
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
