using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    // A layout anchor with its combined-source coordinates precomputed: the byte range it covers
    // and the combined line/column where it starts, so a position inside it maps by line delta.
    private readonly record struct InstalledSourceAnchor(
        SourceLineAnchor Anchor,
        int ByteStart,
        int ByteEnd,
        int CombinedLine,
        int CombinedColumn);

    private InstalledSourceAnchor[]? _sourceLineAnchors;
    private bool[]? _anchoredRegions;

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
        _sourceLineAnchors = null;
        _anchoredRegions = null;
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

        InstallSourceLineAnchors(layout);
    }

    // A reconstructed module region is not line-for-line identical to its file, so its positions
    // resolve through the fragment anchors the stitcher recorded; a region without anchors (the
    // line-preserving entry body) keeps the region-relative mapping.
    private void InstallSourceLineAnchors(CombinedCompilationLayout layout)
    {
        _sourceLineAnchors = null;
        _anchoredRegions = null;
        if (_sourceIndex is null || layout.SourceLineAnchors is not { Count: > 0 } anchors)
        {
            return;
        }

        var installed = new InstalledSourceAnchor[anchors.Count];
        var anchoredRegions = new bool[layout.ModuleOffsets.Count];
        for (int i = 0; i < anchors.Count; i++)
        {
            SourceLineAnchor anchor = anchors[i];
            int byteStart = _sourceIndex.ToUtf8Offset(anchor.CombinedStart);
            int byteEnd = _sourceIndex.ToUtf8Offset(anchor.CombinedEnd);
            (int line, int column) = _sourceIndex.ToPosition(byteStart, SourcePositionEncoding.UnicodeScalar);
            installed[i] = new InstalledSourceAnchor(anchor, byteStart, byteEnd, line, column);
            for (int region = 0; region < layout.ModuleOffsets.Count; region++)
            {
                var (_, regionStart, regionEnd) = layout.ModuleOffsets[region];
                if (anchor.CombinedStart >= regionStart && anchor.CombinedStart < regionEnd)
                {
                    anchoredRegions[region] = true;
                }
            }
        }

        Array.Sort(installed, (left, right) => left.ByteStart.CompareTo(right.ByteStart));
        _sourceLineAnchors = installed;
        _anchoredRegions = anchoredRegions;
    }

    private bool TryResolveAnchoredLocation(int absolutePosition, out (string FilePath, int Line, int Column) resolved)
    {
        resolved = default;
        if (_sourceLineAnchors is not { Length: > 0 } anchors || _sourceIndex is null)
        {
            return false;
        }

        int low = 0;
        int high = anchors.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (anchors[middle].ByteStart <= absolutePosition)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        InstalledSourceAnchor candidate = anchors[low];
        if (absolutePosition < candidate.ByteStart || absolutePosition >= candidate.ByteEnd)
        {
            return false;
        }

        (int line, int column) = _sourceIndex.ToPosition(absolutePosition, SourcePositionEncoding.UnicodeScalar);
        int lineDelta = line - candidate.CombinedLine;
        int resolvedColumn = lineDelta == 0
            ? candidate.Anchor.Column + (column - candidate.CombinedColumn)
            : column + 1;
        resolved = (candidate.Anchor.FilePath, candidate.Anchor.Line + lineDelta, resolvedColumn);
        return true;
    }

    private void CopySourceContextTo(Lowering target)
    {
        target._currentFilePath = _currentFilePath;
        target._sourceIndex = _sourceIndex;
        target._moduleOffsets = _moduleOffsets;
        target._moduleSourceIndexes = _moduleSourceIndexes;
        target._sourceLineAnchors = _sourceLineAnchors;
        target._anchoredRegions = _anchoredRegions;
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
            if (TryResolveAnchoredLocation(absolutePosition, out var anchored))
            {
                return anchored;
            }

            int stringPosition = sourceIndex.ToUtf16Offset(absolutePosition);
            for (int i = _moduleOffsets.Count - 1; i >= 0; i--)
            {
                var (filePath, startOffset, endOffset) = _moduleOffsets[i];
                if (stringPosition >= startOffset && stringPosition < endOffset)
                {
                    // Inside an anchored region but outside every anchor is stitching glue (the
                    // generated binding name, the wrapping parentheses): unlocated, like the glue
                    // between regions below.
                    if (_anchoredRegions is not null && _anchoredRegions[i])
                    {
                        return null;
                    }

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
