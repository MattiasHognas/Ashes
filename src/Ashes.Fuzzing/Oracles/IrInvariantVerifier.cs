using System.Collections;
using System.Reflection;
using Ashes.Semantics;

namespace Ashes.Fuzzing.Oracles;

internal sealed class IrInvariantVerifier
{
    private static readonly IReadOnlySet<string> TempPropertyNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Arg",
        "BasePtr",
        "Code",
        "Cond",
        "Left",
        "Pattern",
        "Ptr",
        "Replacement",
        "Right",
        "Source",
        "Start",
        "Subject",
        "Target",
    };
    private static readonly IReadOnlySet<string> DestinationPropertyNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "DescTarget",
        "DestTemp",
        "ResultTarget",
        "Target",
    };
    private static readonly IReadOnlySet<string> LocalSlotPropertyNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "CursorLocalSlot",
        "EndLocalSlot",
        "OwnerSlot",
        "PreRestoreEndSlot",
        "ResvEndSlot",
        "ResvStartSlot",
        "SavedEndSlot",
        "Slot",
    };

    internal IReadOnlyList<string> Verify(IrProgram program)
    {
        List<string> errors = [];
        IrFunction[] functions = [program.EntryFunction, .. program.Functions];
        HashSet<string> functionLabels = new(StringComparer.Ordinal);
        HashSet<string> stringLabels = new(StringComparer.Ordinal);
        foreach (IrStringLiteral literal in program.StringLiterals)
        {
            if (string.IsNullOrWhiteSpace(literal.Label) || !stringLabels.Add(literal.Label))
            {
                errors.Add($"Duplicate or invalid string literal label '{literal.Label}'.");
            }
        }
        foreach (IrFunction function in functions)
        {
            if (!functionLabels.Add(function.Label))
            {
                errors.Add($"Duplicate function label '{function.Label}'.");
            }
        }

        foreach (IrFunction function in functions)
        {
            VerifyFunction(function, functionLabels, stringLabels, program.ExternalFunctions, errors);
        }
        return errors;
    }

    private static void VerifyFunction(
        IrFunction function,
        IReadOnlySet<string> functionLabels,
        IReadOnlySet<string> stringLabels,
        IReadOnlyList<IrExternalFunction> externalFunctions,
        List<string> errors)
    {
        if (function.LocalCount < 0 || function.TempCount < 0)
        {
            errors.Add($"Function '{function.Label}' has a negative frame size.");
            return;
        }
        if (function.Instructions.Count == 0)
        {
            errors.Add($"Function '{function.Label}' has no instructions.");
            return;
        }

        Dictionary<string, int> labels = new(StringComparer.Ordinal);
        HashSet<int> reuseTokens = [];
        Dictionary<int, int> reuseTokenConsumers = [];
        for (int index = 0; index < function.Instructions.Count; index++)
        {
            IrInst instruction = function.Instructions[index];
            if (instruction is IrInst.Label label && !labels.TryAdd(label.Name, index))
            {
                errors.Add($"Function '{function.Label}' contains duplicate block label '{label.Name}'.");
            }
            if (instruction is IrInst.DropReuse dropReuse && !reuseTokens.Add(dropReuse.Target))
            {
                errors.Add($"Function '{function.Label}' produces reuse token %{dropReuse.Target} more than once.");
            }
            ValidateTempProperties(function, instruction, errors);
            ValidateLocalSlotProperties(function, instruction, errors);
            ValidateFunctionReference(function, instruction, functionLabels, errors);
            ValidateInstructionShape(function, instruction, stringLabels, externalFunctions, errors);
        }

        foreach (IrInst instruction in function.Instructions)
        {
            switch (instruction)
            {
                case IrInst.Jump jump when !labels.ContainsKey(jump.Target):
                    errors.Add($"Function '{function.Label}' jumps to missing label '{jump.Target}'.");
                    break;
                case IrInst.JumpIfFalse jump when !labels.ContainsKey(jump.Target):
                    errors.Add($"Function '{function.Label}' conditionally jumps to missing label '{jump.Target}'.");
                    break;
                case IrInst.SwitchTag dispatch:
                    if (!labels.ContainsKey(dispatch.DefaultLabel))
                    {
                        errors.Add($"Function '{function.Label}' switches to missing default label '{dispatch.DefaultLabel}'.");
                    }
                    foreach ((long _, string caseLabel) in dispatch.Cases)
                    {
                        if (!labels.ContainsKey(caseLabel))
                        {
                            errors.Add($"Function '{function.Label}' switches to missing case label '{caseLabel}'.");
                        }
                    }
                    break;
                case IrInst.AllocReusing reuse when !reuseTokens.Contains(reuse.TokenTemp):
                    errors.Add($"Function '{function.Label}' consumes unknown reuse token %{reuse.TokenTemp}.");
                    break;
                case IrInst.AllocReusing reuse:
                    reuseTokenConsumers[reuse.TokenTemp] = reuseTokenConsumers.GetValueOrDefault(reuse.TokenTemp) + 1;
                    break;
            }
        }

        foreach (int token in reuseTokens)
        {
            int consumers = reuseTokenConsumers.GetValueOrDefault(token);
            if (consumers > 1)
            {
                errors.Add($"Function '{function.Label}' reuse token %{token} has {consumers} consumers; expected at most one.");
            }
        }

        VerifyLocalMetadata(function, function.LocalNames?.Keys, "name", errors);
        VerifyLocalMetadata(function, function.LocalTypes?.Keys, "type", errors);
        VerifyDefinedTemps(function, labels, errors);
        VerifyNoUseAfterConsume(function, labels, errors);
    }

    private static void VerifyNoUseAfterConsume(
        IrFunction function,
        IReadOnlyDictionary<string, int> labels,
        List<string> errors)
    {
        int count = function.Instructions.Count;
        List<int>[] successors = BuildSuccessors(function, labels);
        bool[] reachable = Reachable(successors);
        List<int>[] predecessors = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (int index = 0; index < count; index++)
        {
            foreach (int successor in successors[index])
            {
                predecessors[successor].Add(index);
            }
        }

        HashSet<int>[] input = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        HashSet<int>[] output = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        bool changed;
        do
        {
            changed = false;
            for (int index = 0; index < count; index++)
            {
                if (!reachable[index])
                {
                    continue;
                }
                HashSet<int> nextInput = [];
                foreach (int predecessor in predecessors[index].Where(predecessor => reachable[predecessor]))
                {
                    nextInput.UnionWith(output[predecessor]);
                }
                HashSet<int> nextOutput = new(nextInput);
                nextOutput.ExceptWith(Destinations(function.Instructions[index]));
                if (ConsumedSource(function.Instructions[index]) is int consumed)
                {
                    nextOutput.Add(consumed);
                }
                if (!input[index].SetEquals(nextInput) || !output[index].SetEquals(nextOutput))
                {
                    input[index] = nextInput;
                    output[index] = nextOutput;
                    changed = true;
                }
            }
        }
        while (changed);

        for (int index = 0; index < count; index++)
        {
            if (!reachable[index])
            {
                continue;
            }
            foreach (int used in Sources(function.Instructions[index]))
            {
                if (input[index].Contains(used))
                {
                    errors.Add($"Function '{function.Label}' instruction {index} '{function.Instructions[index].GetType().Name}' uses consumed temp %{used}.");
                }
            }
        }
    }

    private static int? ConsumedSource(IrInst instruction) => instruction switch
    {
        IrInst.RcDrop drop => drop.SourceTemp,
        IrInst.CleanupResource cleanup => cleanup.SourceTemp,
        IrInst.DropReuse reuse => reuse.SourceTemp,
        _ => null,
    };

    private static void VerifyDefinedTemps(IrFunction function, IReadOnlyDictionary<string, int> labels, List<string> errors)
    {
        int count = function.Instructions.Count;
        List<int>[] successors = BuildSuccessors(function, labels);
        bool[] reachable = Reachable(successors);

        List<int>[] predecessors = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (int index = 0; index < count; index++)
        {
            foreach (int successor in successors[index])
            {
                predecessors[successor].Add(index);
            }
        }

        HashSet<int> allTemps = Enumerable.Range(0, function.TempCount).ToHashSet();
        HashSet<int>[] input = Enumerable.Range(0, count)
            .Select(index => index == 0 ? new HashSet<int>() : new HashSet<int>(allTemps))
            .ToArray();
        HashSet<int>[] output = input.Select(set => new HashSet<int>(set)).ToArray();
        bool changed;
        do
        {
            changed = false;
            for (int index = 0; index < count; index++)
            {
                if (!reachable[index])
                {
                    continue;
                }
                HashSet<int> nextInput = index == 0
                    ? []
                    : IntersectOutputs(predecessors[index].Where(predecessor => reachable[predecessor]), output);
                HashSet<int> nextOutput = new(nextInput);
                nextOutput.UnionWith(Destinations(function.Instructions[index]));
                if (!input[index].SetEquals(nextInput) || !output[index].SetEquals(nextOutput))
                {
                    input[index] = nextInput;
                    output[index] = nextOutput;
                    changed = true;
                }
            }
        }
        while (changed);

        for (int index = 0; index < count; index++)
        {
            if (!reachable[index])
            {
                continue;
            }
            foreach (int used in Sources(function.Instructions[index]))
            {
                if (used >= 0 && used < function.TempCount && !input[index].Contains(used))
                {
                    errors.Add($"Function '{function.Label}' instruction {index} '{function.Instructions[index].GetType().Name}' uses undefined temp %{used}.");
                }
            }
        }
    }

    private static List<int>[] BuildSuccessors(IrFunction function, IReadOnlyDictionary<string, int> labels)
    {
        int count = function.Instructions.Count;
        List<int>[] successors = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (int index = 0; index < count; index++)
        {
            IrInst instruction = function.Instructions[index];
            switch (instruction)
            {
                case IrInst.Jump jump:
                    AddLabelSuccessor(jump.Target, labels, successors[index]);
                    break;
                case IrInst.JumpIfFalse jump:
                    AddLabelSuccessor(jump.Target, labels, successors[index]);
                    AddFallthrough(index, count, successors[index]);
                    break;
                case IrInst.SwitchTag dispatch:
                    AddLabelSuccessor(dispatch.DefaultLabel, labels, successors[index]);
                    foreach ((long _, string label) in dispatch.Cases)
                    {
                        AddLabelSuccessor(label, labels, successors[index]);
                    }
                    break;
                case IrInst.Return:
                    break;
                default:
                    AddFallthrough(index, count, successors[index]);
                    break;
            }
        }
        return successors;
    }

    private static bool[] Reachable(IReadOnlyList<List<int>> successors)
    {
        bool[] reachable = new bool[successors.Count];
        Queue<int> pending = new();
        pending.Enqueue(0);
        while (pending.TryDequeue(out int index))
        {
            if (reachable[index])
            {
                continue;
            }
            reachable[index] = true;
            foreach (int successor in successors[index])
            {
                pending.Enqueue(successor);
            }
        }
        return reachable;
    }

    private static HashSet<int> IntersectOutputs(IEnumerable<int> predecessors, IReadOnlyList<HashSet<int>> outputs)
    {
        using IEnumerator<int> iterator = predecessors.GetEnumerator();
        if (!iterator.MoveNext())
        {
            return [];
        }
        HashSet<int> intersection = new(outputs[iterator.Current]);
        while (iterator.MoveNext())
        {
            intersection.IntersectWith(outputs[iterator.Current]);
        }
        return intersection;
    }

    private static IEnumerable<int> Destinations(IrInst instruction) => TempProperties(instruction, destinations: true);

    private static IEnumerable<int> Sources(IrInst instruction) => TempProperties(instruction, destinations: false);

    private static IEnumerable<int> TempProperties(IrInst instruction, bool destinations)
    {
        foreach (PropertyInfo property in instruction.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            bool destination = DestinationPropertyNames.Contains(property.Name);
            if (destination != destinations)
            {
                continue;
            }
            object? value = property.GetValue(instruction);
            bool isTemp = TempPropertyNames.Contains(property.Name) || property.Name.EndsWith("Temp", StringComparison.Ordinal);
            if (isTemp && value is int temp && temp >= 0)
            {
                yield return temp;
            }
            else if (!destinations && (property.Name.EndsWith("Temps", StringComparison.Ordinal) || property.Name is "Args" or "UsedTemps") && value is IEnumerable values)
            {
                foreach (object? element in values)
                {
                    if (element is int listedTemp && listedTemp >= 0)
                    {
                        yield return listedTemp;
                    }
                }
            }
        }
    }

    private static void AddLabelSuccessor(string label, IReadOnlyDictionary<string, int> labels, ICollection<int> successors)
    {
        if (labels.TryGetValue(label, out int target))
        {
            successors.Add(target);
        }
    }

    private static void AddFallthrough(int index, int count, ICollection<int> successors)
    {
        if (index + 1 < count)
        {
            successors.Add(index + 1);
        }
    }

    private static void ValidateFunctionReference(IrFunction function, IrInst instruction, IReadOnlySet<string> functionLabels, List<string> errors)
    {
        string? referencedLabel = instruction switch
        {
            IrInst.MakeClosure closure => closure.FuncLabel,
            IrInst.MakeClosureStack closure => closure.FuncLabel,
            IrInst.CallKnown call => call.FuncLabel,
            IrInst.LoadFuncAddr address => address.FuncLabel,
            _ => null,
        };
        if (referencedLabel is not null && !functionLabels.Contains(referencedLabel))
        {
            errors.Add($"Function '{function.Label}' references missing function '{referencedLabel}'.");
        }
    }

    private static void ValidateTempProperties(IrFunction function, IrInst instruction, List<string> errors)
    {
        foreach (PropertyInfo property in instruction.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            object? value = property.GetValue(instruction);
            bool isTemp = TempPropertyNames.Contains(property.Name) || property.Name.EndsWith("Temp", StringComparison.Ordinal);
            if (isTemp && value is int temp)
            {
                ValidateTemp(function, instruction, property.Name, temp, errors);
            }
            else if (property.Name.EndsWith("Temps", StringComparison.Ordinal) && value is IEnumerable values)
            {
                foreach (object? element in values)
                {
                    if (element is int listedTemp)
                    {
                        ValidateTemp(function, instruction, property.Name, listedTemp, errors);
                    }
                }
            }
        }
    }

    private static void ValidateTemp(IrFunction function, IrInst instruction, string propertyName, int temp, List<string> errors)
    {
        if (temp < -1 || temp >= function.TempCount)
        {
            errors.Add($"Function '{function.Label}' instruction '{instruction.GetType().Name}' has invalid {propertyName} %{temp} for {function.TempCount} temps.");
        }
    }

    private static void ValidateLocalSlotProperties(IrFunction function, IrInst instruction, List<string> errors)
    {
        foreach (PropertyInfo property in instruction.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            object? value = property.GetValue(instruction);
            if (LocalSlotPropertyNames.Contains(property.Name) && value is int slot)
            {
                ValidateLocalSlot(function, instruction, property.Name, slot, errors);
            }
            else if (string.Equals(property.Name, "ReadLocalSlots", StringComparison.Ordinal) && value is IEnumerable slots)
            {
                foreach (object? element in slots)
                {
                    if (element is int listedSlot)
                    {
                        ValidateLocalSlot(function, instruction, property.Name, listedSlot, errors);
                    }
                }
            }
        }
    }

    private static void ValidateLocalSlot(
        IrFunction function,
        IrInst instruction,
        string propertyName,
        int slot,
        List<string> errors)
    {
        if (slot < -1 || slot >= function.LocalCount)
        {
            errors.Add($"Function '{function.Label}' instruction '{instruction.GetType().Name}' has invalid {propertyName} local slot {slot} for {function.LocalCount} locals.");
        }
    }

    private static void ValidateInstructionShape(
        IrFunction function,
        IrInst instruction,
        IReadOnlySet<string> stringLabels,
        IReadOnlyList<IrExternalFunction> externalFunctions,
        List<string> errors)
    {
        switch (instruction)
        {
            case IrInst.LoadConstStr load when !stringLabels.Contains(load.StrLabel):
                errors.Add($"Function '{function.Label}' loads missing string literal '{load.StrLabel}'.");
                break;
            case IrInst.CallExternal call:
                ValidateExternalCall(function, call, externalFunctions, errors);
                break;
            case IrInst.MakeClosure closure when closure.EnvSizeBytes < 0:
                errors.Add($"Function '{function.Label}' creates a closure with negative environment size {closure.EnvSizeBytes}.");
                break;
            case IrInst.MakeClosureStack closure when closure.EnvSizeBytes < 0:
                errors.Add($"Function '{function.Label}' creates a stack closure with negative environment size {closure.EnvSizeBytes}.");
                break;
            case IrInst.Alloc allocation when allocation.SizeBytes < 0:
                errors.Add($"Function '{function.Label}' allocates a negative byte size {allocation.SizeBytes}.");
                break;
            case IrInst.AllocStack allocation when allocation.SizeBytes < 0:
                errors.Add($"Function '{function.Label}' stack-allocates a negative byte size {allocation.SizeBytes}.");
                break;
            case IrInst.AllocAdt allocation when allocation.FieldCount < 0:
                errors.Add($"Function '{function.Label}' allocates an ADT with negative field count {allocation.FieldCount}.");
                break;
            case IrInst.AllocAdtStack allocation when allocation.FieldCount < 0:
                errors.Add($"Function '{function.Label}' stack-allocates an ADT with negative field count {allocation.FieldCount}.");
                break;
            case IrInst.AllocAdtToSpace allocation when allocation.FieldCount < 0:
                errors.Add($"Function '{function.Label}' allocates a to-space ADT with negative field count {allocation.FieldCount}.");
                break;
            case IrInst.DropReuse reuse when reuse.FieldCount < 0:
                errors.Add($"Function '{function.Label}' produces a reuse token with negative field count {reuse.FieldCount}.");
                break;
            case IrInst.AllocReusing reuse when reuse.FieldCount < 0:
                errors.Add($"Function '{function.Label}' consumes a reuse token with negative field count {reuse.FieldCount}.");
                break;
            case IrInst.SetAdtField field when field.FieldIndex < 0:
                errors.Add($"Function '{function.Label}' writes negative ADT field index {field.FieldIndex}.");
                break;
            case IrInst.GetAdtField field when field.FieldIndex < 0:
                errors.Add($"Function '{function.Label}' reads negative ADT field index {field.FieldIndex}.");
                break;
            case IrInst.SwitchTag dispatch when dispatch.Cases.Select(item => item.Tag).Distinct().Count() != dispatch.Cases.Count:
                errors.Add($"Function '{function.Label}' contains duplicate switch tags.");
                break;
            case IrInst.CleanupResource cleanup when string.IsNullOrWhiteSpace(cleanup.TypeName):
                errors.Add($"Function '{function.Label}' cleans up a resource without a type name.");
                break;
            case IrInst.RcDrop drop when string.IsNullOrWhiteSpace(drop.TypeName):
                errors.Add($"Function '{function.Label}' drops an owned value without a type name.");
                break;
        }
    }

    private static void ValidateExternalCall(
        IrFunction function,
        IrInst.CallExternal call,
        IReadOnlyList<IrExternalFunction> externalFunctions,
        List<string> errors)
    {
        if (call.ArgTemps.Count != call.ParameterTypes.Count)
        {
            errors.Add($"Function '{function.Label}' external call '{call.SymbolName}' has {call.ArgTemps.Count} arguments but {call.ParameterTypes.Count} parameter types.");
        }
        IrExternalFunction? declaration = externalFunctions.FirstOrDefault(candidate =>
            string.Equals(candidate.SymbolName, call.SymbolName, StringComparison.Ordinal) &&
            string.Equals(candidate.LibraryName, call.LibraryName, StringComparison.Ordinal));
        if (declaration is null)
        {
            errors.Add($"Function '{function.Label}' calls undeclared external symbol '{call.SymbolName}'.");
            return;
        }
        if (!declaration.ParameterTypes.SequenceEqual(call.ParameterTypes) || declaration.ReturnType != call.ReturnType)
        {
            errors.Add($"Function '{function.Label}' external call '{call.SymbolName}' does not match its declared FFI signature.");
        }
    }

    private static void VerifyLocalMetadata(IrFunction function, IEnumerable<int>? slots, string kind, List<string> errors)
    {
        if (slots is null)
        {
            return;
        }
        foreach (int slot in slots)
        {
            if (slot < 0 || slot >= function.LocalCount)
            {
                errors.Add($"Function '{function.Label}' has {kind} metadata for invalid local slot {slot}.");
            }
        }
    }
}
