namespace Ashes.Semantics;

/// <summary>The physical representation currently selected for one lowered temp.</summary>
internal enum LoweredTempRepresentation
{
    Unknown = 0,
    ArenaRegion,
    RuntimeRc,
    BorrowedView,
}

/// <summary>How the lowered temp participates in ownership.</summary>
internal enum LoweredTempOwnershipKind
{
    Unknown = 0,
    Borrowed,
    Transferred,
    NewlyProduced,
}

/// <summary>The drop capability known at the point where a temp fact was recorded.</summary>
internal enum LoweredTempDropKind
{
    Unknown = 0,
    NoRuntimeDrop,
    RuntimeRcHeader,
    BorrowedViewNoDrop,
}

/// <summary>
/// Stable outer layout category known without rerunning type inference or inspecting emitted IR.
/// Detailed recursive copy/drop capability remains the responsibility of the layout work in 3.3.
/// </summary>
internal enum LoweredTempLayoutKind
{
    Unknown = 0,
    BigInt,
    String,
    Bytes,
    ListCell,
    Adt,
    Tuple,
    Closure,
}

/// <summary>Stable reason for the latest representation or ownership transition.</summary>
internal enum LoweredTempOwnershipReason
{
    Unknown = 0,
    InstructionProducer,
    BorrowForward,
    RcDupForward,
    ControlFlowJoin,
    KnownCallResult,
    UnknownCallResult,
    CopyOutNormalization,
    TcoParameterInstall,
    FrameRestore,
    OwnershipTransfer,
    ExplicitLoweringDecision,
}

/// <summary>The operation that produced this temp identity.</summary>
internal enum LoweredTempProducerKind
{
    Unknown = 0,
    Instruction,
    ConcatStrTip,
    Borrow,
    RcDup,
    ControlFlowJoin,
    FrameRestore,
}

/// <summary>
/// Canonical forward fact for one function-local IR temp. Type and drop details are refined as
/// inference and layout information become available; the representation and ownership transition
/// never require a scan of already-emitted instructions.
/// </summary>
internal sealed record LoweredTempOwnershipFact(
    int Temp,
    LoweredTempRepresentation Representation,
    int? OwnerTemp,
    int? SourceTemp,
    TypeRef? Type,
    LoweredTempLayoutKind Layout,
    LoweredTempDropKind DropKind,
    LoweredTempOwnershipKind Ownership,
    LoweredTempProducerKind Producer,
    IrFunctionOrigin? FunctionOrigin,
    SourceLocation? Location,
    LoweredTempOwnershipReason Reason);

public sealed partial class Lowering
{
    private readonly Dictionary<int, LoweredTempOwnershipFact> _tempOwnershipFacts = [];

    private HashSet<int> SnapshotRuntimeManagedTemps()
    {
        return _tempOwnershipFacts
            .Where(pair => pair.Value.Representation == LoweredTempRepresentation.RuntimeRc)
            .Select(pair => pair.Key)
            .ToHashSet();
    }

    private Dictionary<int, LoweredTempOwnershipFact> SnapshotTempOwnershipFacts()
    {
        return new Dictionary<int, LoweredTempOwnershipFact>(_tempOwnershipFacts);
    }

    private void RestoreTempOwnershipFacts(
        IReadOnlyDictionary<int, LoweredTempOwnershipFact> facts)
    {
        _tempOwnershipFacts.Clear();
        foreach ((int temp, LoweredTempOwnershipFact fact) in facts)
        {
            _tempOwnershipFacts[temp] = fact;
        }
    }

    private void RecordEmittedTempOwnership(IrInst instruction)
    {
        switch (instruction)
        {
            case IrInst.RcDup duplicate:
                PropagateRcDupOwnership(duplicate, instruction.Location);
                break;
            case IrInst.CallKnown knownCall:
                RecordUnknownProducedTemp(
                    knownCall.Target,
                    LoweredTempOwnershipReason.KnownCallResult,
                    instruction.Location);
                break;
            case IrInst.CallClosure closureCall:
                RecordUnknownProducedTemp(
                    closureCall.Target,
                    LoweredTempOwnershipReason.UnknownCallResult,
                    instruction.Location);
                break;
            case IrInst.IRuntimeManagedTargetResult result:
                RecordProducedTempOwnership(
                    result.Target,
                    result.RuntimeManaged,
                    LayoutForInstruction(instruction),
                    instruction is IrInst.ConcatStrTip
                        ? LoweredTempProducerKind.ConcatStrTip
                        : LoweredTempProducerKind.Instruction,
                    instruction.Location);
                break;
            case IrInst.IRuntimeManagedDestinationResult result:
                RecordProducedTempOwnership(
                    result.DestTemp,
                    result.RuntimeManaged,
                    LayoutForInstruction(instruction),
                    LoweredTempProducerKind.Instruction,
                    instruction.Location,
                    CopyOutReason(instruction));
                break;
            case IrInst.BytesSubView view:
                RecordBorrowedViewTemp(view.Target, view.BytesTemp, instruction.Location);
                break;
            case IrInst.Borrow borrow:
                PropagateTempOwnership(
                    borrow.Target,
                    borrow.SourceTemp,
                    LoweredTempOwnershipKind.Borrowed,
                    LoweredTempOwnershipReason.BorrowForward,
                    instruction.Location);
                break;
        }
    }

    private void RecordProducedTempOwnership(
        int temp,
        bool runtimeManaged,
        LoweredTempLayoutKind layout,
        LoweredTempProducerKind producer,
        SourceLocation? location,
        LoweredTempOwnershipReason reason =
            LoweredTempOwnershipReason.InstructionProducer)
    {
        LoweredTempRepresentation representation = runtimeManaged
            ? LoweredTempRepresentation.RuntimeRc
            : LoweredTempRepresentation.ArenaRegion;
        LoweredTempDropKind dropKind = runtimeManaged
            ? LoweredTempDropKind.RuntimeRcHeader
            : LoweredTempDropKind.NoRuntimeDrop;
        RecordTempOwnership(
            temp,
            representation,
            ownerTemp: runtimeManaged ? temp : null,
            sourceTemp: null,
            type: null,
            layout,
            dropKind,
            LoweredTempOwnershipKind.NewlyProduced,
            producer,
            location,
            reason);
    }

    private void RecordBorrowedViewTemp(int target, int source, SourceLocation? location)
    {
        _tempOwnershipFacts.TryGetValue(source, out LoweredTempOwnershipFact? sourceFact);
        RecordTempOwnership(
            target,
            LoweredTempRepresentation.BorrowedView,
            sourceFact?.OwnerTemp ?? source,
            source,
            sourceFact?.Type,
            LoweredTempLayoutKind.Bytes,
            LoweredTempDropKind.BorrowedViewNoDrop,
            LoweredTempOwnershipKind.Borrowed,
            LoweredTempProducerKind.Borrow,
            location,
            LoweredTempOwnershipReason.BorrowForward);
    }

    private void PropagateTempOwnership(
        int target,
        int source,
        LoweredTempOwnershipKind ownership,
        LoweredTempOwnershipReason reason,
        SourceLocation? location)
    {
        if (!_tempOwnershipFacts.TryGetValue(source, out LoweredTempOwnershipFact? sourceFact))
        {
            RecordTempOwnership(
                target,
                LoweredTempRepresentation.Unknown,
                ownerTemp: null,
                source,
                type: null,
                LoweredTempLayoutKind.Unknown,
                LoweredTempDropKind.Unknown,
                ownership,
                reason == LoweredTempOwnershipReason.BorrowForward
                    ? LoweredTempProducerKind.Borrow
                    : LoweredTempProducerKind.Unknown,
                location,
                reason);
            return;
        }

        LoweredTempProducerKind producer = ProducerForReason(reason);
        if (producer == LoweredTempProducerKind.Unknown)
        {
            producer = sourceFact.Producer;
        }

        RecordTempOwnership(
            target,
            sourceFact.Representation,
            sourceFact.OwnerTemp,
            source,
            sourceFact.Type,
            sourceFact.Layout,
            sourceFact.DropKind,
            ownership,
            producer,
            location,
            reason);
    }

    private static LoweredTempOwnershipReason CopyOutReason(IrInst instruction)
    {
        return instruction switch
        {
            IrInst.CopyOutArena
            {
                Purpose: IrInst.CopyOutPurpose.RcNormalization
            } or IrInst.CopyOutList
            {
                Purpose: IrInst.CopyOutPurpose.RcNormalization
            } or IrInst.CopyOutClosure
            {
                Purpose: IrInst.CopyOutPurpose.RcNormalization
            } => LoweredTempOwnershipReason.CopyOutNormalization,
            _ => LoweredTempOwnershipReason.InstructionProducer,
        };
    }

    private void PropagateRcDupOwnership(IrInst.RcDup duplicate, SourceLocation? location)
    {
        if (duplicate.RuntimeManaged)
        {
            _tempOwnershipFacts.TryGetValue(
                duplicate.SourceTemp,
                out LoweredTempOwnershipFact? sourceFact);
            RecordTempOwnership(
                duplicate.Target,
                LoweredTempRepresentation.RuntimeRc,
                duplicate.Target,
                duplicate.SourceTemp,
                sourceFact?.Type,
                sourceFact?.Layout ?? LoweredTempLayoutKind.Unknown,
                LoweredTempDropKind.RuntimeRcHeader,
                LoweredTempOwnershipKind.Transferred,
                LoweredTempProducerKind.RcDup,
                location,
                LoweredTempOwnershipReason.RcDupForward);
            return;
        }

        PropagateTempOwnership(
            duplicate.Target,
            duplicate.SourceTemp,
            LoweredTempOwnershipKind.Transferred,
            LoweredTempOwnershipReason.RcDupForward,
            location);
    }

    private void RecordUnknownProducedTemp(
        int temp,
        LoweredTempOwnershipReason reason,
        SourceLocation? location,
        TypeRef? type = null)
    {
        RecordTempOwnership(
            temp,
            LoweredTempRepresentation.Unknown,
            ownerTemp: null,
            sourceTemp: null,
            type,
            LayoutForType(type),
            LoweredTempDropKind.Unknown,
            LoweredTempOwnershipKind.NewlyProduced,
            LoweredTempProducerKind.Instruction,
            location,
            reason);
    }

    private void RecordFrameRestoreTemp(int target, int source, TypeRef type)
    {
        PropagateTempOwnership(
            target,
            source,
            LoweredTempOwnershipKind.Transferred,
            LoweredTempOwnershipReason.FrameRestore,
            location: null);
        RefineTempOwnershipType(target, type);
    }

    private void MarkRuntimeManagedTemp(
        int temp,
        LoweredTempOwnershipReason reason = LoweredTempOwnershipReason.ExplicitLoweringDecision,
        int? sourceTemp = null,
        TypeRef? type = null,
        SourceLocation? location = null)
    {
        _tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? existing);
        LoweredTempOwnershipReason effectiveReason =
            reason == LoweredTempOwnershipReason.ExplicitLoweringDecision
                && existing?.Representation == LoweredTempRepresentation.RuntimeRc
                    ? existing.Reason
                    : reason;
        LoweredTempLayoutKind layout =
            existing is { Layout: not LoweredTempLayoutKind.Unknown }
                ? existing.Layout
                : LayoutForType(type ?? existing?.Type);
        RecordTempOwnership(
            temp,
            LoweredTempRepresentation.RuntimeRc,
            existing?.OwnerTemp ?? temp,
            sourceTemp ?? existing?.SourceTemp,
            type ?? existing?.Type,
            layout,
            LoweredTempDropKind.RuntimeRcHeader,
            existing?.Ownership ?? LoweredTempOwnershipKind.NewlyProduced,
            existing?.Producer ?? ProducerForReason(effectiveReason),
            location ?? existing?.Location,
            effectiveReason);
    }

    private void RecordCallResultTempOwnership(
        int temp,
        TypeRef resultType,
        bool runtimeManagedResult,
        bool normalizedRuntimeManagedResult)
    {
        if (runtimeManagedResult || normalizedRuntimeManagedResult)
        {
            MarkRuntimeManagedTemp(
                temp,
                runtimeManagedResult
                    ? LoweredTempOwnershipReason.KnownCallResult
                    : LoweredTempOwnershipReason.UnknownCallResult,
                type: resultType);
            return;
        }

        if (_tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? existing))
        {
            _tempOwnershipFacts[temp] = existing with
            {
                Type = resultType,
                Layout = LayoutForType(resultType),
                Reason = LoweredTempOwnershipReason.UnknownCallResult,
            };
            return;
        }

        RecordUnknownProducedTemp(
            temp,
            LoweredTempOwnershipReason.UnknownCallResult,
            location: null,
            resultType);
    }

    private void RecordControlFlowJoinTemp(
        int temp,
        TypeRef resultType,
        bool runtimeManaged)
    {
        RecordTempOwnership(
            temp,
            runtimeManaged
                ? LoweredTempRepresentation.RuntimeRc
                : LoweredTempRepresentation.Unknown,
            ownerTemp: null,
            sourceTemp: null,
            resultType,
            LayoutForType(resultType),
            runtimeManaged
                ? LoweredTempDropKind.RuntimeRcHeader
                : LoweredTempDropKind.Unknown,
            LoweredTempOwnershipKind.Transferred,
            LoweredTempProducerKind.ControlFlowJoin,
            location: null,
            LoweredTempOwnershipReason.ControlFlowJoin);
    }

    private void RefineTempOwnershipType(int temp, TypeRef type)
    {
        if (_tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? existing))
        {
            _tempOwnershipFacts[temp] = existing with
            {
                Type = type,
                Layout = LayoutForType(type),
            };
        }
    }

    private void ReplaceEmittedTempOwnership(IrInst oldInstruction, IrInst newInstruction)
    {
        int? oldResultTemp = RuntimeManagedResultTemp(oldInstruction);
        int? newResultTemp = RuntimeManagedResultTemp(newInstruction);
        if (oldResultTemp is int oldTemp
            && oldTemp == newResultTemp
            && _tempOwnershipFacts.TryGetValue(oldTemp, out LoweredTempOwnershipFact? existing)
            && existing.Reason == LoweredTempOwnershipReason.InstructionProducer)
        {
            _tempOwnershipFacts.Remove(oldTemp);
        }

        RecordEmittedTempOwnership(newInstruction);
    }

    private static int? RuntimeManagedResultTemp(IrInst instruction)
    {
        return instruction switch
        {
            IrInst.IRuntimeManagedTargetResult result => result.Target,
            IrInst.IRuntimeManagedDestinationResult result => result.DestTemp,
            _ => null,
        };
    }

    private void RecordTempOwnership(
        int temp,
        LoweredTempRepresentation representation,
        int? ownerTemp,
        int? sourceTemp,
        TypeRef? type,
        LoweredTempLayoutKind layout,
        LoweredTempDropKind dropKind,
        LoweredTempOwnershipKind ownership,
        LoweredTempProducerKind producer,
        SourceLocation? location,
        LoweredTempOwnershipReason reason)
    {
        _tempOwnershipFacts[temp] = new LoweredTempOwnershipFact(
            temp,
            representation,
            ownerTemp,
            sourceTemp,
            type,
            layout,
            dropKind,
            ownership,
            producer,
            _activeFunctionOrigin,
            location,
            reason);
    }

    private static LoweredTempLayoutKind LayoutForInstruction(IrInst instruction)
    {
        return instruction switch
        {
            IrInst.BigIntFromInt or IrInst.BigIntBinary => LoweredTempLayoutKind.BigInt,
            IrInst.BigIntToString
                or IrInst.ConcatStr
                or IrInst.ConcatStrTip
                or IrInst.TextFromInt
                or IrInst.TextFromFloat
                or IrInst.TextFormatFloat
                or IrInst.TextToHex
                or IrInst.TextAsciiCase => LoweredTempLayoutKind.String,
            IrInst.BigIntToInt
                or IrInst.BigIntFromString
                or IrInst.TextUncons
                or IrInst.TextParseInt
                or IrInst.TextParseFloat => LoweredTempLayoutKind.Adt,
            IrInst.MakeClosure or IrInst.CopyOutClosure => LoweredTempLayoutKind.Closure,
            IrInst.AllocAdt => LoweredTempLayoutKind.Adt,
            IrInst.AllocReusing { ListCell: true } => LoweredTempLayoutKind.ListCell,
            IrInst.AllocReusing => LoweredTempLayoutKind.Adt,
            IrInst.BytesEmpty
                or IrInst.BytesSingleton
                or IrInst.BytesSubText
                or IrInst.BytesAppend
                or IrInst.BytesAppendByte
                or IrInst.BytesFromList
                or IrInst.BytesU16Le
                or IrInst.BytesU32Le
                or IrInst.BytesU64Le => LoweredTempLayoutKind.Bytes,
            IrInst.CopyOutList => LoweredTempLayoutKind.ListCell,
            IrInst.CopyOutArena { StaticSizeBytes: IrInst.CopyOutArena.BigIntSize } =>
                LoweredTempLayoutKind.BigInt,
            _ => LoweredTempLayoutKind.Unknown,
        };
    }

    private static LoweredTempLayoutKind LayoutForType(TypeRef? type)
    {
        return type switch
        {
            TypeRef.TBigInt => LoweredTempLayoutKind.BigInt,
            TypeRef.TStr => LoweredTempLayoutKind.String,
            TypeRef.TBytes => LoweredTempLayoutKind.Bytes,
            TypeRef.TList => LoweredTempLayoutKind.ListCell,
            TypeRef.TNamedType => LoweredTempLayoutKind.Adt,
            TypeRef.TTuple => LoweredTempLayoutKind.Tuple,
            TypeRef.TFun => LoweredTempLayoutKind.Closure,
            _ => LoweredTempLayoutKind.Unknown,
        };
    }

    private static LoweredTempProducerKind ProducerForReason(
        LoweredTempOwnershipReason reason)
    {
        return reason switch
        {
            LoweredTempOwnershipReason.ControlFlowJoin =>
                LoweredTempProducerKind.ControlFlowJoin,
            LoweredTempOwnershipReason.FrameRestore =>
                LoweredTempProducerKind.FrameRestore,
            LoweredTempOwnershipReason.BorrowForward =>
                LoweredTempProducerKind.Borrow,
            LoweredTempOwnershipReason.RcDupForward =>
                LoweredTempProducerKind.RcDup,
            _ => LoweredTempProducerKind.Unknown,
        };
    }
}
