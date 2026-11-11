namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private readonly Dictionary<IrInst.CallClosure, string> _curryStageCallLabels =
        new(ReferenceEqualityComparer.Instance);

    // Reuse fallback calls already have a lowered callee. Resolve its actual lexical slot or
    // capture, without adding a label to the shared ownership-summary tables. Bound the local
    // alias search: an older or unsupported definition simply keeps conservative placement.
    private string? ArenaCurryPlacementLabel(int closureTemp)
    {
        int source = closureTemp;
        int lowerBound = Math.Max(0, _inst.Count - 256);
        for (int index = _inst.Count - 1; index >= lowerBound; index--)
        {
            IrInst instruction = _inst[index];
            if (instruction is IrInst.Label or IrInst.Jump or IrInst.JumpIfFalse or IrInst.SwitchTag)
            {
                return null;
            }

            if (!StateMachineTransform.GetDefinedTemps(instruction).Contains(source))
            {
                continue;
            }

            switch (instruction)
            {
                case IrInst.Borrow borrow:
                    source = borrow.SourceTemp;
                    break;
                case IrInst.LoadEnv environment:
                    return _knownFunctionLabelsByEnvIndex.GetValueOrDefault(environment.Index);
                case IrInst.LoadLocal local:
                    return _knownFunctionLabelsBySlot.GetValueOrDefault(local.Slot);
                case IrInst.MakeClosure closure:
                    return closure.FuncLabel;
                case IrInst.MakeClosureStack closure:
                    return closure.FuncLabel;
                default:
                    return null;
            }
        }

        return null;
    }

    private void RecordCurryStageCall(string? label)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRC_NO_ARENACURRYBORROW"))
            && label is not null
            && _inst[^1] is IrInst.CallClosure call)
        {
            _curryStageCallLabels[call] = label;
        }
    }

    // Classify completed bodies after deferred ownership placement has resolved. The strict
    // existing curry recognizer accepts only a fresh arena environment and returned arena
    // closure: no calls, retained fields, owned captures, releases, or pending instructions.
    // Such a stage borrows its argument through its result. Perceus already follows that
    // result's aliases; its call duplicate would create a reference nobody consumes.
    private IrProgram PlaceWithArenaCurryBorrows(IrProgram program)
    {
        if (_curryStageCallLabels.Count == 0)
        {
            return PerceusLifetimePlacement.Place(program, _borrowedArgumentCalls);
        }

        HashSet<string> borrowingLabels = new(StringComparer.Ordinal);
        foreach (IrFunction function in program.Functions)
        {
            if (IrOptimizer.IsBorrowingArenaCurryingStage(function))
            {
                borrowingLabels.Add(function.Label);
            }
        }

        foreach ((IrInst.CallClosure call, string label) in _curryStageCallLabels)
        {
            if (borrowingLabels.Contains(label))
            {
                _borrowedArgumentCalls.Add(call);
            }
        }

        return PerceusLifetimePlacement.Place(program, _borrowedArgumentCalls);
    }
}
