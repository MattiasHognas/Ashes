using Ashes.Frontend;

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
/// Recursive copy/drop/reuse details are retained separately by
/// <see cref="OrdinaryHeapLayoutCapability"/>.
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

[Flags]
internal enum LoweredValueRuntimeRepresentation
{
    None = 0,
    String = 1 << 0,
    Bytes = 1 << 1,
    BigInt = 1 << 2,
    Adt = 1 << 3,
    Tuple = 1 << 4,
    List = 1 << 5,
    Record = 1 << 6,
    Closure = 1 << 7,
    ScalarAdtResult = 1 << 8,
    BigIntParseResult = 1 << 9,
    TextUnconsResult = 1 << 10,
    TcoAdt = 1 << 11,
}

/// <summary>
/// Explicit demand passed from an ownership-aware consumer to the expression producing its value.
/// <see cref="ConsumerCanOwn"/> records the semantic proof; <see cref="RuntimeRepresentation"/>
/// records the physical representation the producer is permitted to emit.
/// <see cref="TransfersRuntimeManagedChildren"/> marks a function-return boundary where an arena
/// aggregate must retain borrowed runtime-managed children beyond the producer's lexical cleanup.
/// The returned <see cref="LoweredValue.Ownership"/> records what was actually emitted.
/// </summary>
internal readonly record struct LoweredValueRequest(
    bool ConsumerCanOwn,
    bool TransfersRuntimeManagedChildren,
    LoweredValueRuntimeRepresentation RuntimeRepresentation,
    string? RuntimeListTailBinding,
    bool RuntimeListTailShared,
    int? RuntimeTcoListTailSlot,
    TypeRef.TNamedType? RuntimeReuseAdtType,
    IReadOnlyDictionary<string, bool>? RuntimeAdtChildBindings,
    TypeRef? ExpectedType)
{
    public static LoweredValueRequest None => default;

    public static LoweredValueRequest OwnedRuntime(
        LoweredValueRuntimeRepresentation representation) =>
        new(
            ConsumerCanOwn: true,
            TransfersRuntimeManagedChildren: false,
            representation,
            RuntimeListTailBinding: null,
            RuntimeListTailShared: false,
            RuntimeTcoListTailSlot: null,
            RuntimeReuseAdtType: null,
            RuntimeAdtChildBindings: null,
            ExpectedType: null);

    public bool EmitsRuntime(LoweredValueRuntimeRepresentation representation) =>
        ConsumerCanOwn
        && (RuntimeRepresentation & representation)
            != LoweredValueRuntimeRepresentation.None;

    public LoweredValueRequest AddRuntime(
        bool condition,
        LoweredValueRuntimeRepresentation representation) =>
        condition
            ? this with
            {
                ConsumerCanOwn = true,
                RuntimeRepresentation = RuntimeRepresentation | representation,
            }
            : this;

    public LoweredValueRequest WithExpectedType(TypeRef expectedType) =>
        this with { ExpectedType = expectedType };

    public LoweredValueRequest WithoutExpectedType() =>
        this with { ExpectedType = null };

    public LoweredValueRequest WithRuntimeListContext(
        string? tailBinding,
        bool tailShared,
        int? tcoTailSlot) =>
        this with
        {
            ConsumerCanOwn = true,
            RuntimeRepresentation =
                RuntimeRepresentation | LoweredValueRuntimeRepresentation.List,
            RuntimeListTailBinding = tailBinding,
            RuntimeListTailShared = tailShared,
            RuntimeTcoListTailSlot = tcoTailSlot,
        };

    public LoweredValueRequest WithRuntimeAdtContext(
        IReadOnlyDictionary<string, bool>? childBindings,
        TypeRef.TNamedType? reuseType = null) =>
        this with
        {
            RuntimeReuseAdtType = reuseType ?? RuntimeReuseAdtType,
            RuntimeAdtChildBindings = childBindings ?? RuntimeAdtChildBindings,
        };
}

/// <summary>
/// One expression-lowering result paired with the canonical ownership fact for its temp.
/// The fact is a snapshot of the decision at this hand-off; later rewrites return a new
/// <see cref="LoweredValue"/> rather than mutating the value already handed to a consumer.
/// </summary>
internal readonly record struct LoweredValue(
    int Temp,
    TypeRef Type,
    LoweredTempOwnershipFact Ownership)
{
    public void Deconstruct(out int temp, out TypeRef type)
    {
        temp = Temp;
        type = Type;
    }

    public (int Temp, TypeRef Type) AsPair() => (Temp, Type);
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
    OrdinaryHeapLayoutCapability? LayoutCapability,
    LoweredTempDropKind DropKind,
    LoweredTempOwnershipKind Ownership,
    LoweredTempProducerKind Producer,
    IrFunctionOrigin? FunctionOrigin,
    SourceLocation? Location,
    LoweredTempOwnershipReason Reason,
    BuiltinRegistry.BytesOwnershipProvenance BytesProvenance =
        BuiltinRegistry.BytesOwnershipProvenance.Unknown);

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
            case IrInst.LoadLocal load:
                // A load of a single-use armed affine-append binding carries the bound ConcatStrTip
                // result's fact — recorded here so the reset-resolution replay (which re-derives
                // facts from instructions alone) re-establishes it. No-op for every other slot.
                TryStampAffineAppendLoad(load.Target, load.Slot);
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
            reason,
            bytesProvenance: layout == LoweredTempLayoutKind.Bytes
                ? BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer
                : BuiltinRegistry.BytesOwnershipProvenance.Unknown);
    }

    private void RecordBorrowedViewTemp(int target, int source, SourceLocation? location)
    {
        _tempOwnershipFacts.TryGetValue(source, out LoweredTempOwnershipFact? sourceFact);
        RecordTempOwnership(
            target,
            LoweredTempRepresentation.BorrowedView,
            sourceFact?.OwnerTemp ?? source,
            source,
            new TypeRef.TBytes(),
            LoweredTempLayoutKind.Bytes,
            LoweredTempDropKind.BorrowedViewNoDrop,
            LoweredTempOwnershipKind.Borrowed,
            LoweredTempProducerKind.Borrow,
            location,
            LoweredTempOwnershipReason.BorrowForward,
            bytesProvenance: BuiltinRegistry.BytesOwnershipProvenance.BorrowedView);
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
            reason,
            sourceFact.LayoutCapability,
            sourceFact.BytesProvenance);
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
                LoweredTempOwnershipReason.RcDupForward,
                sourceFact?.LayoutCapability,
                sourceFact?.BytesProvenance
                    ?? BuiltinRegistry.BytesOwnershipProvenance.Unknown);
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

    private void RecordUnknownBorrowedTemp(
        int temp,
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
            LoweredTempOwnershipKind.Borrowed,
            LoweredTempProducerKind.Borrow,
            location,
            LoweredTempOwnershipReason.BorrowForward);
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

    private void MarkFrameOwnedResourceTemp(int temp, TypeRef type)
    {
        _tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? existing);
        RecordTempOwnership(
            temp,
            LoweredTempRepresentation.Unknown,
            ownerTemp: temp,
            sourceTemp: existing?.SourceTemp,
            type: type,
            layout: LayoutForType(type),
            dropKind: LoweredTempDropKind.Unknown,
            ownership: LoweredTempOwnershipKind.Transferred,
            producer: existing?.Producer ?? LoweredTempProducerKind.Borrow,
            location: existing?.Location,
            reason: LoweredTempOwnershipReason.OwnershipTransfer);
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
            effectiveReason,
            type is null ? existing?.LayoutCapability : null,
            existing?.BytesProvenance
                ?? (type is TypeRef.TBytes
                    ? BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer
                    : BuiltinRegistry.BytesOwnershipProvenance.Unknown));
    }

    private void RecordCallResultTempOwnership(
        int temp,
        TypeRef resultType,
        bool runtimeManagedResult,
        bool normalizedRuntimeManagedResult,
        BuiltinRegistry.BytesOwnershipProvenance bytesProvenance)
    {
        if (runtimeManagedResult || normalizedRuntimeManagedResult)
        {
            MarkRuntimeManagedTemp(
                temp,
                runtimeManagedResult
                    ? LoweredTempOwnershipReason.KnownCallResult
                    : LoweredTempOwnershipReason.UnknownCallResult,
                type: resultType);
            RefineTempBytesProvenance(
                temp,
                normalizedRuntimeManagedResult && Prune(resultType) is TypeRef.TBytes
                    ? BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer
                    : bytesProvenance);
            return;
        }

        if (_tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? existing))
        {
            _tempOwnershipFacts[temp] = existing with
            {
                Type = resultType,
                Layout = LayoutForType(resultType),
                LayoutCapability = GetOrdinaryHeapLayoutCapability(
                    resultType,
                    existing.Representation),
                Reason = LoweredTempOwnershipReason.UnknownCallResult,
                BytesProvenance = bytesProvenance,
            };
            return;
        }

        RecordUnknownProducedTemp(
            temp,
            LoweredTempOwnershipReason.UnknownCallResult,
            location: null,
            resultType);
        RefineTempBytesProvenance(temp, bytesProvenance);
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
                LayoutCapability = GetOrdinaryHeapLayoutCapability(
                    type,
                    existing.Representation),
            };
        }
    }

    private void RefineTempBytesProvenance(
        int temp,
        BuiltinRegistry.BytesOwnershipProvenance provenance)
    {
        if (_tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? existing))
        {
            _tempOwnershipFacts[temp] = existing with { BytesProvenance = provenance };
        }
    }

    private void RecordProgramLifetimeBytesView(int temp)
    {
        if (!_tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? existing))
        {
            RecordUnknownProducedTemp(
                temp,
                LoweredTempOwnershipReason.InstructionProducer,
                location: null);
            existing = _tempOwnershipFacts[temp];
        }

        _tempOwnershipFacts[temp] = existing with
        {
            DropKind = LoweredTempDropKind.NoRuntimeDrop,
            BytesProvenance = BuiltinRegistry.BytesOwnershipProvenance.ProgramLifetimeView,
        };
    }

    private void RecordBytesReinterpretTemp(int temp)
    {
        if (_tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? existing))
        {
            _tempOwnershipFacts[temp] = existing with
            {
                Type = new TypeRef.TBytes(),
                Layout = LoweredTempLayoutKind.Bytes,
                BytesProvenance = BuiltinRegistry.BytesOwnershipProvenance.BorrowedView,
            };
            return;
        }

        RecordTempOwnership(
            temp,
            LoweredTempRepresentation.BorrowedView,
            ownerTemp: temp,
            sourceTemp: temp,
            new TypeRef.TBytes(),
            LoweredTempLayoutKind.Bytes,
            LoweredTempDropKind.BorrowedViewNoDrop,
            LoweredTempOwnershipKind.Borrowed,
            LoweredTempProducerKind.Borrow,
            location: null,
            LoweredTempOwnershipReason.BorrowForward,
            bytesProvenance: BuiltinRegistry.BytesOwnershipProvenance.BorrowedView);
    }

    private void RecordLocalBytesProvenance(int slot, int temp)
    {
        if (_tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? fact)
            && fact.BytesProvenance != BuiltinRegistry.BytesOwnershipProvenance.Unknown)
        {
            _localBytesProvenance[slot] = fact.BytesProvenance;
        }
        else
        {
            _localBytesProvenance.Remove(slot);
        }
    }

    private void RecordAggregateBytesProvenance(
        int aggregateTemp,
        IReadOnlyList<LoweredValue> children)
    {
        if (!_tempOwnershipFacts.TryGetValue(
                aggregateTemp,
                out LoweredTempOwnershipFact? aggregateFact))
        {
            return;
        }

        LoweredValue[] bytesChildren = children
            .Where(child => ContainsBytesLayout(
                child.Type,
                new HashSet<TypeSymbol>()))
            .ToArray();
        if (bytesChildren.Length == 0)
        {
            return;
        }

        BuiltinRegistry.BytesOwnershipProvenance provenance = bytesChildren.All(child =>
            child.Ownership.BytesProvenance
                == BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer)
                    ? BuiltinRegistry.BytesOwnershipProvenance.FreshOwnedBuffer
                    : bytesChildren.Any(child => child.Ownership.BytesProvenance
                        == BuiltinRegistry.BytesOwnershipProvenance.Unknown)
                        ? BuiltinRegistry.BytesOwnershipProvenance.Unknown
                        : bytesChildren.Any(child => child.Ownership.BytesProvenance
                            == BuiltinRegistry.BytesOwnershipProvenance.ProgramLifetimeView)
                            ? BuiltinRegistry.BytesOwnershipProvenance.ProgramLifetimeView
                            : BuiltinRegistry.BytesOwnershipProvenance.BorrowedView;
        _tempOwnershipFacts[aggregateTemp] = aggregateFact with
        {
            BytesProvenance = provenance,
        };
    }

    private LoweredValue CreateLoweredValue(int temp, TypeRef type)
    {
        TypeRef prunedType = Prune(type);
        RefineTempOwnershipType(temp, prunedType);
        if (!_tempOwnershipFacts.TryGetValue(temp, out LoweredTempOwnershipFact? fact))
        {
            SourceLocation? location = _currentSourceExpr is null
                ? null
                : ResolveSourceLocation(AstSpans.GetOrDefault(_currentSourceExpr));
            RecordTempOwnership(
                temp,
                LoweredTempRepresentation.Unknown,
                ownerTemp: null,
                sourceTemp: null,
                prunedType,
                LayoutForType(prunedType),
                LoweredTempDropKind.Unknown,
                LoweredTempOwnershipKind.Unknown,
                LoweredTempProducerKind.Unknown,
                location,
                LoweredTempOwnershipReason.Unknown);
            fact = _tempOwnershipFacts[temp];
        }

        return new LoweredValue(temp, prunedType, fact);
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
        LoweredTempOwnershipReason reason,
        OrdinaryHeapLayoutCapability? layoutCapability = null,
        BuiltinRegistry.BytesOwnershipProvenance bytesProvenance =
            BuiltinRegistry.BytesOwnershipProvenance.Unknown)
    {
        _tempOwnershipFacts[temp] = new LoweredTempOwnershipFact(
            temp,
            representation,
            ownerTemp,
            sourceTemp,
            type,
            layout,
            layoutCapability
                ?? GetOrdinaryHeapLayoutCapability(type, representation),
            dropKind,
            ownership,
            producer,
            _activeFunctionOrigin,
            location,
            reason,
            bytesProvenance);
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
                or IrInst.BytesAllocate
                or IrInst.BytesCopyRange
                or IrInst.BytesSet
                or IrInst.BytesSetU16Le
                or IrInst.BytesSetU32Le
                or IrInst.BytesSetU64Le
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

    private LoweredTempLayoutKind LayoutForType(TypeRef? type)
    {
        TypeRef? represented = type is null ? null : EraseZeroCostTypeRepresentation(type);
        return represented switch
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
