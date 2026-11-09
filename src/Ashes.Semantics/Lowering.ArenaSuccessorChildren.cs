using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static bool ArenaSuccessorChildCopyEnabled
        => Environment.GetEnvironmentVariable("GRC_NO_ARENASUCCESSORCHILDCOPY") is null;

    // The depth of constructor argument lists being lowered: a child is recorded only for the
    // constructor a tail self-call's argument is itself, never for one nested inside it.
    private int _constructorArgumentDepth;

    // While a tail self-call's argument is lowered: the references its constructor retained for the
    // transfer out of an iteration-local owner, with the field each one is stored in.
    private List<(int FieldIndex, int Temp, TypeRef Type)>? _transferDuplicatedChildren;

    // Per function body, the arena constructor successors of tail self-calls and the transfer
    // references their fields hold.
    private readonly Dictionary<List<IrInst>, Dictionary<int, IReadOnlyList<(int FieldIndex, int Temp, TypeRef Type)>>>
        _transferDuplicatedChildrenByBody = new(ReferenceEqualityComparer.Instance);

    private void RecordTransferDuplicatedChild(int fieldIndex, int borrowedTemp, int retainedTemp, TypeRef type)
    {
        if (fieldIndex >= 0
            && retainedTemp != borrowedTemp
            && _constructorArgumentDepth == 1
            && _transferDuplicatedChildren is { } children)
        {
            children.Add((fieldIndex, retainedTemp, Prune(type)));
        }
    }

    // Opens the collection for one tail self-call argument; the previous collector is returned so a
    // nested call's argument restores it.
    private (List<(int FieldIndex, int Temp, TypeRef Type)>? Children, int Depth) BeginTransferDuplicatedChildren()
    {
        var outer = (_transferDuplicatedChildren, _constructorArgumentDepth);
        _transferDuplicatedChildren = ArenaSuccessorChildCopyEnabled && GeneralRcEnabled ? [] : null;
        _constructorArgumentDepth = 0;
        return outer;
    }

    // Closes the collection: the children are kept for the successor only when it is the arena cell
    // its constructor allocated.
    private void EndTransferDuplicatedChildren((List<(int FieldIndex, int Temp, TypeRef Type)>? Children, int Depth) outer, int successorTemp)
    {
        if (_transferDuplicatedChildren is { Count: > 0 } children
            && _inst.FindLast(instruction => instruction is IrInst.AllocAdt { } alloc && alloc.Target == successorTemp)
                is IrInst.AllocAdt { RuntimeManaged: false })
        {
            if (!_transferDuplicatedChildrenByBody.TryGetValue(_inst, out var bySuccessor))
            {
                bySuccessor = [];
                _transferDuplicatedChildrenByBody[_inst] = bySuccessor;
            }

            bySuccessor[successorTemp] = children.ToArray();
        }

        _transferDuplicatedChildren = outer.Children;
        _constructorArgumentDepth = outer.Depth;
    }

    private IReadOnlyList<(int FieldIndex, int Temp, TypeRef Type)> TransferDuplicatedChildrenOf(int successorTemp)
        => _transferDuplicatedChildrenByBody.TryGetValue(_inst, out var bySuccessor)
            && bySuccessor.TryGetValue(successorTemp, out var children)
                ? children
                : [];

    /// <summary>
    /// A back edge that keeps the iteration's arena hands an arena parameter a constructor cell built
    /// for it, whose fields hold references retained for the transfer out of an owner the iteration
    /// releases (`Some(label)` of a label bound out of a call's result). The arena cell releases
    /// nothing and neither does the parameter, so each such field takes an arena copy of its value,
    /// stored back into the cell, and the retained reference is released after the copy. The copy
    /// lives as long as the loop's arena does, which no reset on this edge shortens.
    /// </summary>
    private void CopyTransferDuplicatedChildrenIntoArena(PendingTcoReset info)
    {
        for (int i = 0; i < info.TransferDuplicatedChildren.Length && i < info.ParamSlots.Length; i++)
        {
            if (info.ParamPlacements[i]?.Representation == TcoPlacementRepresentation.RuntimeRc
                || info.TransferDuplicatedChildren[i].Count == 0)
            {
                continue;
            }

            foreach ((int fieldIndex, int temp, TypeRef type) in info.TransferDuplicatedChildren[i])
            {
                if (CanArenaReset(Prune(type))
                    || ContainsUnresolvedLayoutType(Prune(type), [])
                    || !HasBoundedCopy(type, []))
                {
                    continue;
                }

                int copiedTemp = EmitArenaCopyOfReferenceCounted(temp, Prune(type), releasesSource: true);
                Emit(new IrInst.SetAdtField(info.ArgTemps[i], fieldIndex, copiedTemp));
            }
        }
    }

    private (int Temp, TypeRef Type) LowerTcoArgumentCollectingTransferChildren(
        TcoContext tco,
        int index,
        Expr argument,
        LoweredValueRequest request)
    {
        var outer = BeginTransferDuplicatedChildren();
        (int Temp, TypeRef Type) lowered = LowerTcoArgumentForSlot(tco, index, argument, request);
        EndTransferDuplicatedChildren(outer, lowered.Temp);
        return lowered;
    }
}
