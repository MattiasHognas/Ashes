using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private static bool CopiedCellHeadConstructorEnabled
        => Environment.GetEnvironmentVariable("GRC_NO_COPIEDCELLHEADCONSTRUCTOR") is null;

    // While the head of a successor cell the back edge copies, or may still copy, is lowered, when
    // that head is a constructor written right there (`Known(label) :: acc`).
    private CopiedCellHead _lowersCopiedCellHeadConstructor;

    private enum CopiedCellHead
    {
        None,
        Copied,
        LateCopied,
    }

    // The retains a late-copied cell's head constructor took while its head is lowered, kept per cell
    // until the cell is built: only an arena cell over an arena head is copied by the back edge.
    private List<(int Temp, TypeRef Type)>? _copiedCellHeadRetains;

    private readonly Dictionary<Expr.Cons, List<(int Temp, TypeRef Type)>> _copiedCellHeadRetainsByCell = new(ReferenceEqualityComparer.Instance);

    private LoweredValue LowerConsHead(Expr.Cons cons, LoweredValueRequest cellRequest, TypeRef? expectedElementType)
    {
        CopiedCellHead outer = _lowersCopiedCellHeadConstructor;
        List<(int Temp, TypeRef Type)>? outerRetains = _copiedCellHeadRetains;
        _lowersCopiedCellHeadConstructor = CopiedCellHeadOf(cons);
        _copiedCellHeadRetains = _lowersCopiedCellHeadConstructor == CopiedCellHead.LateCopied ? [] : null;
        try
        {
            return LowerRuntimeManagedListElement(cons.Head, cellRequest, expectedElementType);
        }
        finally
        {
            if (_copiedCellHeadRetains is { Count: > 0 } retains)
            {
                _copiedCellHeadRetainsByCell[cons] = retains;
            }

            _lowersCopiedCellHeadConstructor = outer;
            _copiedCellHeadRetains = outerRetains;
        }
    }

    // Once the cell is built: the head's arena constructors are copied with the cell, by the cell's own
    // normalization or by the back edge, so the retains they took are released after the back edge's copy.
    private void RecordLateCopiedCellHeadConstructorRetains(Expr.Cons cons)
    {
        if (!_copiedCellHeadRetainsByCell.Remove(cons, out List<(int Temp, TypeRef Type)>? retains)
            || _backEdgeArgumentPosition is not (TcoContext loop, int index))
        {
            return;
        }

        foreach ((int temp, TypeRef type) in retains)
        {
            _lateCopiedHeadTupleDuplicates.Add(new LateCopiedHeadTupleDuplicate(loop, index, temp, type) { CellHead = true });
        }
    }

    private CopiedCellHead CopiedCellHeadOf(Expr.Cons cons)
    {
        if (!CopiedCellHeadConstructorEnabled || !GeneralRcEnabled || !IsHeadConstructorShape(cons.Head))
        {
            return CopiedCellHead.None;
        }

        if (IsBackEdgeCopiedCell(cons))
        {
            return CopiedCellHead.Copied;
        }

        return _backEdgeArgumentPosition is not null && _backEdgeLateCopiedCells.Contains(cons)
            ? CopiedCellHead.LateCopied
            : CopiedCellHead.None;
    }

    // A constructor or record literal written in place: copied with the cell it heads, and so is one
    // written as its own argument.
    private bool IsHeadConstructorShape(Expr expression) => expression is Expr.RecordLit || IsConstructorExpression(expression);

    // The copied-cell head state an argument of such a constructor is lowered under.
    private CopiedCellHead CopiedCellHeadForArgument(CopiedCellHead head, Expr argument)
        => head != CopiedCellHead.None && IsHeadConstructorShape(argument) ? head : CopiedCellHead.None;

    // A head constructor kept in the arena after its application's own placement holds its fields'
    // retains without owning them: they go to the cell's list. One placed on the reference-counted heap
    // owns them.
    private void KeepArenaHeadConstructorRetains(List<(int Temp, TypeRef Type)>? retains, bool runtimeManaged)
    {
        if (retains is not null && !runtimeManaged)
        {
            _copiedCellHeadRetains?.AddRange(retains);
        }
    }

    // Whether the constructor whose arguments are about to be lowered is such a head, kept in the
    // arena; a constructor nested inside its arguments is one only when written in place as an argument.
    private CopiedCellHead TakeCopiedCellHeadConstructor(bool runtimeManagedCandidate)
    {
        CopiedCellHead head = runtimeManagedCandidate ? CopiedCellHead.None : _lowersCopiedCellHeadConstructor;
        _lowersCopiedCellHeadConstructor = CopiedCellHead.None;
        return head;
    }

    /// <summary>
    /// A constructor written as the head of a successor cell the back edge copies is copied with the
    /// cell, and the copy takes a reference of its own to each field before the iteration's owners
    /// are released. Like a head tuple, it borrows its fields: a duplicate or transfer retain stored
    /// in the arena constructor would never be released. A field the iteration produced right there
    /// (a call's owned result) keeps an owner of its own, released once the successor holds its
    /// references.
    /// </summary>
    private int BorrowCopiedCellHeadConstructorArgument(Expr argument, int argumentTemp, TypeRef argumentType)
    {
        KeepCopiedCellFreshHeadOwned(argument, CreateLoweredValue(argumentTemp, argumentType));
        return argumentTemp;
    }

    /// <summary>
    /// The same head of a cell the back edge may still copy, once the loop's placements are final:
    /// the retains its arguments took are noted like a late-copied cell's own head duplicate, and
    /// released after the back edge copies the successor onto the reference-counted heap.
    /// </summary>
    // The retains of the constructor whose arguments were lowered last, taken by its application.
    private List<(int Temp, TypeRef Type)>? _loweredConstructorHeadRetains;

    // An argument of a constructor that may head a copied cell: one of a cell the back edge copies
    // borrows; one of a cell it may still copy takes its retains, noted for the constructor.
    private int RetainHeadConstructorArgument(
        CopiedCellHead copiedCellHead,
        List<(int Temp, TypeRef Type)>? headRetains,
        Expr argument,
        int argumentTemp,
        TypeRef argumentType,
        bool runtimeManagedCandidate,
        LoweredValueRequest request,
        int fieldIndex)
    {
        if (copiedCellHead == CopiedCellHead.Copied)
        {
            return BorrowCopiedCellHeadConstructorArgument(argument, argumentTemp, argumentType);
        }

        int retainsStart = _inst.Count;
        int retained = RetainEscapingConstructorArgument(argument, argumentTemp, argumentType, runtimeManagedCandidate, request, fieldIndex);
        if (headRetains is not null)
        {
            NoteCopiedCellHeadConstructorRetains(headRetains, retainsStart, argumentType);
        }

        return retained;
    }

    private void NoteCopiedCellHeadConstructorRetains(List<(int Temp, TypeRef Type)> retains, int firstInstruction, TypeRef argumentType)
    {

        for (int i = firstInstruction; i < _inst.Count; i++)
        {
            if (_inst[i] is IrInst.RcDup duplicate)
            {
                retains.Add((duplicate.Target, Prune(argumentType)));
            }
        }
    }
}
