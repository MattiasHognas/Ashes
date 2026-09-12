namespace Ashes.Semantics;

public static partial class IrOptimizer
{
    // The backend resolves a closure's environment normalizer by name suffix across every lifted
    // function rather than through an instruction operand, so no instruction names one. A normalizer
    // is therefore kept exactly when the closure it normalizes is kept.
    private const string ClosureEnvironmentNormalizerSuffix = "$env_normalize";

    /// <summary>
    /// Drops the functions the entry point cannot reach. Every top-level binding of an imported
    /// module is lowered whether or not the importing program uses it, so importing one standard
    /// library function compiles in that whole module; once every function-producing pass has run,
    /// the functions nothing names are dead weight in the image.
    /// </summary>
    /// <remarks>
    /// This runs over already-lowered IR, so it changes nothing about what the compiler analyzed:
    /// binding, type inference, and lowering have all happened, and every diagnostic they raise is
    /// raised before this point. Pruning before those phases would instead silence the diagnostics
    /// inside an unused declaration.
    /// <para>
    /// It shapes the image only, so it runs after the compiler has reported — past
    /// <see cref="Optimize"/>, whose contract is that it never removes a function, and past the IR
    /// dumps and explain reports, which describe the program the compiler reasoned about. Running it
    /// unconditionally is what keeps a report flag from selecting different generated code.
    /// </para>
    /// </remarks>
    public static IrProgram PruneUnreachableFunctions(IrProgram program)
    {
        // A binding the program never reads still holds its function live through the closure the
        // entry point builds for it, so the dead bindings have to go before reachability is asked.
        IrFunction entry = ElideDeadTopLevelClosureBindings(program.EntryFunction);
        if (!ReferenceEquals(entry, program.EntryFunction))
        {
            program = program with { EntryFunction = entry };
        }

        var functionsByLabel = new Dictionary<string, IrFunction>(StringComparer.Ordinal);
        foreach (IrFunction function in program.Functions)
        {
            functionsByLabel[function.Label] = function;
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<IrFunction>();
        pending.Push(entry);
        while (pending.Count > 0)
        {
            foreach (string label in ReferencedFunctionLabels(pending.Pop()))
            {
                MarkFunctionReachable(label, functionsByLabel, reachable, pending);
            }
        }

        List<IrFunction> kept = program.Functions
            .Where(function => reachable.Contains(function.Label))
            .ToList();
        return kept.Count == program.Functions.Count ? program : program with { Functions = kept };
    }

    private static void MarkFunctionReachable(
        string label,
        Dictionary<string, IrFunction> functionsByLabel,
        HashSet<string> reachable,
        Stack<IrFunction> pending)
    {
        // A label naming no function of this program is a builtin or an external symbol.
        if (!reachable.Add(label) || !functionsByLabel.TryGetValue(label, out IrFunction? referenced))
        {
            return;
        }

        pending.Push(referenced);
        if (functionsByLabel.TryGetValue(label + ClosureEnvironmentNormalizerSuffix, out IrFunction? normalizer)
            && reachable.Add(normalizer.Label))
        {
            pending.Push(normalizer);
        }
    }

    // The instructions that name a lifted function: the closure-construction forms, the
    // devirtualized call, and the two releases that delegate to a generated helper the backend calls
    // directly rather than through a closure.
    private static IEnumerable<string> ReferencedFunctionLabels(IrFunction function)
    {
        foreach (IrInst instruction in function.Instructions)
        {
            switch (instruction)
            {
                case IrInst.CallKnown call:
                    yield return call.FuncLabel;
                    break;
                case IrInst.MakeClosure closure:
                    yield return closure.FuncLabel;
                    break;
                case IrInst.MakeClosureStack stackClosure:
                    yield return stackClosure.FuncLabel;
                    break;
                case IrInst.LoadFuncAddr address:
                    yield return address.FuncLabel;
                    break;
                case IrInst.RcDrop { StructuralDropperLabel: { } structuralDropper }:
                    yield return structuralDropper;
                    break;
                case IrInst.CreateTask { FrameDropperLabel: { } frameDropper }:
                    yield return frameDropper;
                    break;
                default:
                    break;
            }
        }
    }
}
