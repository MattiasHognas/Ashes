using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{
    private sealed class DiagnosticContextScope(List<string> diagnosticContext) : IDisposable
    {
        public void Dispose()
        {
            diagnosticContext.RemoveAt(diagnosticContext.Count - 1);
        }
    }

    private sealed class DiagnosticSpanScope(Stack<TextSpan> diagnosticSpans) : IDisposable
    {
        public void Dispose()
        {
            diagnosticSpans.Pop();
        }
    }

    private sealed class DiagnosticCodeScope(Stack<string> diagnosticCodes) : IDisposable
    {
        public void Dispose()
        {
            diagnosticCodes.Pop();
        }
    }

    // TCO (tail call optimization) state
    private sealed class TcoContext
    {
        public string SelfName { get; init; } = "";
        public string BodyLabel { get; set; } = "";
        public int ParamCount { get; init; }
        public List<string> ParamNames { get; init; } = [];
        public List<int> ParamSlots { get; init; } = [];
        public bool InTailPosition { get; set; }

        // Arena watermark for per-iteration reset in TCO loops.
        // Saved right after the loop body label; restored before jumping back
        // when all tail-call arguments are copy types (no heap pointers escape).
        public int ArenaCursorSlot { get; set; } = -1;
        public int ArenaEndSlot { get; set; } = -1;
    }

    private enum IntrinsicKind
    {
        Print,
        Write,
        WriteLine,
        ReadLine,
        FileReadText,
        FileWriteText,
        FileExists,
        TextUncons,
        TextParseInt,
        TextParseFloat,
        TextFromInt,
        TextFromFloat,
        TextToHex,
        HttpGet,
        HttpPost,
        NetTcpConnect,
        NetTcpSend,
        NetTcpReceive,
        NetTcpClose,
        NetTlsConnect,
        NetTlsSend,
        NetTlsReceive,
        NetTlsClose,
        Panic,
        AsyncRun,
        AsyncTask,
        AsyncFromResult,
        AsyncSleep,
        AsyncAll,
        AsyncRace,
        BytesEmpty,
        BytesSingleton,
        BytesLength,
        BytesGet,
        BytesAppend,
        BytesAppendByte,
        BytesFromList,
        BytesU16Le,
        BytesU32Le,
        BytesU64Le,
        BytesGetU16Le,
        BytesGetU32Le,
        BytesGetU64Le,
        FileWriteBytes,
        ReadExact,
        TextByteLength,
        SpawnProcess,
        ProcessWriteStdin,
        ProcessReadStdoutLine,
        ProcessReadStderrLine,
        ProcessWaitForExit,
        ProcessKill
    }

    private enum PreludeValueKind
    {
        Args
    }

    // Binding kinds: local slot or captured env index
    private abstract record Binding(TypeRef Type)
    {
        public virtual TextSpan? DefinitionSpan => null;

        public sealed record Local(int Slot, TypeRef T, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Env(int Index, TypeRef T, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record EnvScheme(int Index, TypeScheme S, TextSpan? Span = null) : Binding(S.Body)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Self(string FuncLabel, TypeRef T, int EnvSizeBytes, TextSpan? Span = null) : Binding(T)
        {
            public override TextSpan? DefinitionSpan => Span;
        }

        public sealed record Intrinsic(IntrinsicKind Kind, TypeScheme S) : Binding(S.Body);
        public sealed record ExternFunction(IrExternFunction Function, TypeRef T) : Binding(T);
        public sealed record PreludeValue(PreludeValueKind Kind, TypeScheme S) : Binding(S.Body);

        public sealed record Scheme(int Slot, TypeScheme S, TextSpan? Span = null) : Binding(S.Body)
        {
            public override TextSpan? DefinitionSpan => Span;
        }
    }

    // --- Ownership tracking ---
    // Tracks owned bindings and their drop/borrow state.
    // Key: binding name, Value: ownership info (slot, type name, whether dropped, active borrows).
    // Copy types (Int, Float, Bool) are never tracked.
    // Owned types (String, List, ADTs, Closures, resource types) are tracked.
    private sealed class OwnershipInfo(int slot, string typeName, bool isResource, TextSpan? definitionSpan)
    {
        public int Slot { get; } = slot;
        public string TypeName { get; } = typeName;
        public bool IsResource { get; } = isResource;
        public TextSpan? DefinitionSpan { get; } = definitionSpan;
        public bool IsDropped { get; set; }
        /// <summary>
        /// Number of live borrows of this value. The compiler infers borrows when
        /// an owned value is used without consuming ownership. By scope structure,
        /// all borrows are consumed before the owning scope exits and emits Drop —
        /// this count is informational for future optimization passes.
        /// </summary>
        public int ActiveBorrows { get; set; }
    }

    /// <summary>
    /// Describes the kind of arena copy-out to emit for a given result type.
    /// </summary>
    private enum CopyOutKind
    {
        /// <summary>Not eligible for copy-out.</summary>
        None,
        /// <summary>Shallow memcpy of a fixed or dynamic-size object (String, ADT, single cons cell).</summary>
        Shallow,
        /// <summary>Deep cons-chain walk for lists.</summary>
        List,
        /// <summary>Closure struct + env copy.</summary>
        Closure,
        /// <summary>TCO-specific: copy one cons cell + copy/deep-copy its head value.</summary>
        TcoListCell,
    }
}
