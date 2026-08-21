using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    /// <summary>
    /// Sets source context for debug info tagging. Call before Lower()
    /// so that emitted IR instructions carry source locations.
    /// </summary>
    public void SetSourceContext(string filePath, string sourceText)
    {
        _currentFilePath = filePath;
        _sourceIndex = new SourceTextIndex(sourceText);
        _moduleOffsets = null;
        _moduleSourceIndexes = null;
        _functionSourceNames = null;
        _moduleProvenanceByPath = null;
    }

    /// <summary>
    /// Sets multi-file source context using a <see cref="CombinedCompilationLayout"/>
    /// so that emitted IR instructions carry per-file source locations.
    /// </summary>
    public void SetSourceContext(CombinedCompilationLayout layout)
    {
        _sourceIndex = new SourceTextIndex(layout.Source);
        _moduleOffsets = layout.ModuleOffsets;
        _functionSourceNames = layout.FunctionSourceNames;
        _moduleProvenanceByPath = layout.ModuleProvenanceByPath;

        // Pre-compute line starts per region (not per file) so disjoint regions
        // for the same file each get correct line/column mappings.
        _moduleSourceIndexes = new SourceTextIndex[layout.ModuleOffsets.Count];
        for (int i = 0; i < layout.ModuleOffsets.Count; i++)
        {
            var (_, startOffset, endOffset) = layout.ModuleOffsets[i];
            var moduleText = layout.Source[startOffset..endOffset];
            _moduleSourceIndexes[i] = new SourceTextIndex(moduleText);
        }

        // Default fallback to entry module file (entry expression region is appended last).
        if (layout.ModuleOffsets.Count > 0)
        {
            _currentFilePath = layout.ModuleOffsets[^1].FilePath;
        }
    }

    private void CopySourceContextTo(Lowering target)
    {
        target._currentFilePath = _currentFilePath;
        target._sourceIndex = _sourceIndex;
        target._moduleOffsets = _moduleOffsets;
        target._moduleSourceIndexes = _moduleSourceIndexes;
        target._functionSourceNames = _functionSourceNames;
        target._moduleProvenanceByPath = _moduleProvenanceByPath;
    }

    /// <summary>
    /// Emits an IR instruction, optionally tagging it with the source
    /// location of <see cref="_currentSourceExpr"/> when debug context is set.
    /// </summary>
    private void Emit(IrInst inst)
    {
        if (!_collectInferredTraitElaboration
            && _sourceIndex is not null
            && _currentSourceExpr is not null
            && !IsRuntimeMachinery(inst))
        {
            TextSpan span = AstSpans.GetOrDefault(_currentSourceExpr);
            if (ResolveSourceLocation(span) is { } resolved)
            {
                inst = inst with { Location = resolved };
            }
        }

        if (!_collectInferredTraitElaboration)
        {
            RecordEmittedTempOwnership(inst);
        }
        _inst.Add(inst);
    }

    /// <summary>
    /// Arena and ownership machinery does not correspond to any source statement, so it must not
    /// carry the line of whichever expression happened to be current when it was emitted. The
    /// backend expands several of these into multi-block code (copy-out loops, cold reclaim
    /// paths); a stale user line on them creates line-table entries at addresses the program
    /// never executes, which then shadow the real breakpoint address for that line. Unlocated
    /// instructions get the artificial line-0 location DWARF reserves for compiler-generated
    /// code instead.
    /// </summary>
    private static bool IsRuntimeMachinery(IrInst inst)
    {
        return inst is IrInst.SaveArenaState
            or IrInst.RestoreArenaState
            or IrInst.ReclaimArenaChunks
            or IrInst.CopyOutArena
            or IrInst.CopyOutArenaToSpace
            or IrInst.CleanupResource
            or IrInst.DropReuse
            or IrInst.RcDrop
            or IrInst.RcDup
            or IrInst.RcIsUnique
            or IrInst.Borrow;
    }

    private (string FilePath, int Line, int Column)? ResolveSourceLocation(int absolutePosition)
    {
        SourceTextIndex? sourceIndex = _sourceIndex;
        if (sourceIndex is null)
        {
            return null;
        }

        // Multi-file resolution: find which module the position falls in
        if (_moduleOffsets is not null)
        {
            int stringPosition = sourceIndex.ToUtf16Offset(absolutePosition);
            for (int i = _moduleOffsets.Count - 1; i >= 0; i--)
            {
                var (filePath, startOffset, endOffset) = _moduleOffsets[i];
                if (stringPosition >= startOffset && stringPosition < endOffset)
                {
                    var relativePosition = stringPosition - startOffset;
                    if (_moduleSourceIndexes is not null)
                    {
                        SourceTextIndex moduleIndex = _moduleSourceIndexes[i];
                        int relativeBytePosition = moduleIndex.ToUtf8Offset(relativePosition);
                        (int line, int column) = moduleIndex.ToPosition(
                            relativeBytePosition,
                            SourcePositionEncoding.UnicodeScalar);
                        return (filePath, line + 1, column + 1);
                    }
                }
            }

            // The position falls in stitching glue between module regions (boundary bindings,
            // wrapping parentheses). Mapping it against the combined source would attribute a
            // nonsense line to the entry file, so leave the instruction unlocated — the backend
            // gives it the artificial line-0 location DWARF uses for compiler-generated code.
            return null;
        }

        // Single-file fallback
        (int l, int c) = sourceIndex.ToPosition(absolutePosition, SourcePositionEncoding.UnicodeScalar);
        return (_currentFilePath ?? "<unknown>", l + 1, c + 1);
    }

    private SourceLocation? ResolveSourceLocation(TextSpan span)
    {
        if (_sourceIndex is null || (span.Length == 0 && span.Start == 0))
        {
            return null;
        }

        return ResolveSourceLocation(span.Start) is { } resolved
            ? new SourceLocation(resolved.FilePath, resolved.Line, resolved.Column)
            : null;
    }
}
