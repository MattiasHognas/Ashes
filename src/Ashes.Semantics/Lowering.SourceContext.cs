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
        _lineStarts = SourceTextUtils.GetLineStarts(sourceText);
        _sourceLength = sourceText.Length;
    }

    /// <summary>
    /// Sets multi-file source context using a <see cref="CombinedCompilationLayout"/>
    /// so that emitted IR instructions carry per-file source locations.
    /// </summary>
    public void SetSourceContext(CombinedCompilationLayout layout)
    {
        _lineStarts = SourceTextUtils.GetLineStarts(layout.Source);
        _sourceLength = layout.Source.Length;
        _moduleOffsets = layout.ModuleOffsets;

        // Pre-compute line starts per region (not per file) so disjoint regions
        // for the same file each get correct line/column mappings.
        _moduleLineStarts = new int[layout.ModuleOffsets.Count][];
        for (int i = 0; i < layout.ModuleOffsets.Count; i++)
        {
            var (_, startOffset, endOffset) = layout.ModuleOffsets[i];
            var moduleText = layout.Source[startOffset..endOffset];
            _moduleLineStarts[i] = SourceTextUtils.GetLineStarts(moduleText);
        }

        // Default fallback to entry module file (entry expression region is appended last).
        if (layout.ModuleOffsets.Count > 0)
        {
            _currentFilePath = layout.ModuleOffsets[^1].FilePath;
        }
    }

    /// <summary>
    /// Emits an IR instruction, optionally tagging it with the source
    /// location of <see cref="_currentSourceExpr"/> when debug context is set.
    /// </summary>
    private void Emit(IrInst inst)
    {
        if (_lineStarts is not null && _currentSourceExpr is not null && !IsRuntimeMachinery(inst))
        {
            var span = AstSpans.GetOrDefault(_currentSourceExpr);
            if ((span.Length > 0 || span.Start > 0) && ResolveSourceLocation(span.Start) is { } resolved)
            {
                inst = inst with { Location = new SourceLocation(resolved.FilePath, resolved.Line, resolved.Column) };
            }
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
            or IrInst.Drop
            or IrInst.Borrow;
    }

    private (string FilePath, int Line, int Column)? ResolveSourceLocation(int absolutePosition)
    {
        // Multi-file resolution: find which module the position falls in
        if (_moduleOffsets is not null)
        {
            for (int i = _moduleOffsets.Count - 1; i >= 0; i--)
            {
                var (filePath, startOffset, endOffset) = _moduleOffsets[i];
                if (absolutePosition >= startOffset && absolutePosition < endOffset)
                {
                    var relativePosition = absolutePosition - startOffset;
                    if (_moduleLineStarts is not null)
                    {
                        var moduleLength = endOffset - startOffset;
                        var (line, column) = SourceTextUtils.ToLineColumn(_moduleLineStarts[i], moduleLength, relativePosition);
                        return (filePath, line, column);
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
        var (l, c) = SourceTextUtils.ToLineColumn(_lineStarts!, _sourceLength, absolutePosition);
        return (_currentFilePath ?? "<unknown>", l, c);
    }
}
