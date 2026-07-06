using Ashes.Backend.Llvm.Interop;
using Ashes.Semantics;

namespace Ashes.Backend.Llvm;

internal static partial class LlvmCodegen
{
    /// <summary>
    /// Holds DWARF debug info state for a single compilation.
    /// Null when <c>EmitDebugInfo</c> is false.
    /// </summary>
    private sealed class DebugInfoContext : IDisposable
    {
        public LlvmDIBuilderHandle DIBuilder { get; }
        public LlvmMetadataHandle CompileUnit { get; }
        public LlvmMetadataHandle DefaultFile { get; }
        public LlvmMetadataHandle SubroutineType { get; }
        public LlvmMetadataHandle IntType { get; }
        public LlvmMetadataHandle FloatType { get; }
        public LlvmMetadataHandle BoolType { get; }
        public LlvmContextHandle LlvmContext { get; }
        public bool IsOptimized { get; }

        private readonly Dictionary<string, LlvmMetadataHandle> _fileCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (LlvmMetadataHandle Subprogram, string? FilePath)> _subprograms = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Label, string FilePath), LlvmMetadataHandle> _lexicalBlockFiles = new();
        private readonly Dictionary<string, LlvmMetadataHandle> _typeCache = new(StringComparer.Ordinal);

        public DebugInfoContext(
            LlvmTargetContext target,
            string defaultFileName,
            string defaultDirectory,
            bool isOptimized)
        {
            LlvmContext = target.Context;
            IsOptimized = isOptimized;
            DIBuilder = LlvmApi.CreateDIBuilder(target.Module);

            // Module flags: Dwarf Version = 5, Debug Info Version = 3
            var dwarfVersionValue = LlvmApi.ValueAsMetadata(
                LlvmApi.ConstInt(LlvmApi.Int32TypeInContext(target.Context), 5, 0));
            LlvmApi.AddModuleFlag(target.Module, LlvmApi.ModuleFlagBehaviorWarning,
                "Dwarf Version", dwarfVersionValue);

            var debugInfoVersionValue = LlvmApi.ValueAsMetadata(
                LlvmApi.ConstInt(LlvmApi.Int32TypeInContext(target.Context), 3, 0));
            LlvmApi.AddModuleFlag(target.Module, LlvmApi.ModuleFlagBehaviorWarning,
                "Debug Info Version", debugInfoVersionValue);

            // Create default file
            DefaultFile = LlvmApi.DIBuilderCreateFile(DIBuilder, defaultFileName, defaultDirectory);
            _fileCache[Path.Combine(defaultDirectory, defaultFileName)] = DefaultFile;

            // Compile unit
            CompileUnit = LlvmApi.DIBuilderCreateCompileUnit(
                DIBuilder,
                LlvmApi.DwarfLangAshes,
                DefaultFile,
                "Ashes Compiler",
                isOptimized: isOptimized);

            // Subroutine type (no parameters — all Ashes functions use i64 calling convention)
            SubroutineType = LlvmApi.DIBuilderCreateSubroutineType(DIBuilder, DefaultFile);

            // Basic debug types. Ashes names are emitted as typedefs over
            // anonymous-named base types with standard encoding/size pairs:
            // GDB reports the typedef name directly, and LLDB's clang-based
            // type system resolves the underlying base type by encoding and
            // size (a custom-named base type would come back as "long", and
            // an 8-byte boolean base type does not map at all).
            IntType = CreateNamedType("Int", "__ashes_i64", LlvmApi.DwarfAteSigned);
            FloatType = CreateNamedType("Float", "__ashes_f64", LlvmApi.DwarfAteFloat);
            BoolType = CreateNamedType("Bool", "__ashes_b64", LlvmApi.DwarfAteSigned);

            _typeCache["Int"] = IntType;
            _typeCache["Float"] = FloatType;
            _typeCache["Bool"] = BoolType;
        }

        public LlvmMetadataHandle GetOrCreateType(TypeRef type)
        {
            var typeName = FormatTypeName(type);
            if (_typeCache.TryGetValue(typeName, out var cached))
            {
                return cached;
            }

            LlvmMetadataHandle created = type switch
            {
                TypeRef.TInt => IntType,
                TypeRef.TUInt => IntType,
                TypeRef.TFloat => FloatType,
                TypeRef.TBool => BoolType,
                TypeRef.TStr => CreateReferenceType(typeName),
                TypeRef.TList => CreateReferenceType(typeName),
                TypeRef.TTuple => CreateReferenceType(typeName),
                TypeRef.TFun => CreateReferenceType(typeName),
                TypeRef.TNamedType => CreateReferenceType(typeName),
                TypeRef.TTypeParam => CreateReferenceType(typeName),
                _ => IntType,
            };

            _typeCache[typeName] = created;
            return created;
        }

        private LlvmMetadataHandle CreateNamedType(string typeName, string underlyingName, uint encoding)
        {
            var underlying = LlvmApi.DIBuilderCreateBasicType(
                DIBuilder, underlyingName, 64, encoding);
            return LlvmApi.DIBuilderCreateTypedef(DIBuilder, underlying, typeName, DefaultFile, CompileUnit);
        }

        private LlvmMetadataHandle CreateReferenceType(string typeName)
        {
            var pointee = CreateNamedType(typeName, "__ashes_word", LlvmApi.DwarfAteSigned);
            return LlvmApi.DIBuilderCreatePointerType(DIBuilder, pointee, 64);
        }

        private static string FormatTypeName(TypeRef type)
        {
            return type switch
            {
                TypeRef.TInt => "Int",
                TypeRef.TUInt { Bits: 8 } => "u8",
                TypeRef.TUInt { Bits: 16 } => "u16",
                TypeRef.TUInt { Bits: 32 } => "u32",
                TypeRef.TUInt { Bits: 64 } => "u64",
                TypeRef.TUInt u => $"u{u.Bits}",
                TypeRef.TFloat => "Float",
                TypeRef.TStr => "Str",
                TypeRef.TBool => "Bool",
                TypeRef.TNever => "Never",
                TypeRef.TList list => $"List<{FormatTypeName(list.Element)}>",
                TypeRef.TTuple tuple => $"({string.Join(", ", tuple.Elements.Select(FormatTypeName))})",
                TypeRef.TFun funType => $"({FormatTypeName(funType.Arg)} -> {FormatTypeName(funType.Ret)})",
                TypeRef.TNamedType named => named.TypeArgs.Count == 0
                    ? named.Symbol.Name
                    : $"{named.Symbol.Name}<{string.Join(", ", named.TypeArgs.Select(FormatTypeName))}>",
                TypeRef.TTypeParam param => param.Symbol.Name,
                TypeRef.TVar => "Int",
                _ => "Int",
            };
        }

        public LlvmMetadataHandle GetOrCreateFile(string? filePath)
        {
            if (filePath is null)
            {
                return DefaultFile;
            }

            if (_fileCache.TryGetValue(filePath, out var cached))
            {
                return cached;
            }

            var fileName = Path.GetFileName(filePath);
            var directory = Path.GetDirectoryName(filePath) ?? ".";
            var file = LlvmApi.DIBuilderCreateFile(DIBuilder, fileName, directory);
            _fileCache[filePath] = file;
            return file;
        }

        public LlvmMetadataHandle CreateSubprogram(
            string name, string linkageName, LlvmMetadataHandle file, uint line, string? filePath)
        {
            var sp = LlvmApi.DIBuilderCreateFunction(
                DIBuilder, CompileUnit,
                name, linkageName, file,
                line, SubroutineType,
                isOptimized: IsOptimized);
            _subprograms[linkageName] = (sp, filePath);
            return sp;
        }

        public LlvmMetadataHandle? GetSubprogram(string linkageName)
        {
            return _subprograms.TryGetValue(linkageName, out var entry) ? entry.Subprogram : null;
        }

        /// <summary>
        /// Returns the debug scope for an instruction at <paramref name="filePath"/> inside the
        /// function <paramref name="linkageName"/>. When the instruction's file differs from the
        /// subprogram's own file (module-stitched compilations mix files within one function), the
        /// subprogram scope is wrapped in a DILexicalBlockFile so the DWARF line table records the
        /// instruction's real file rather than inheriting the subprogram's.
        /// </summary>
        public LlvmMetadataHandle? GetScopeForFile(string linkageName, string? filePath)
        {
            if (!_subprograms.TryGetValue(linkageName, out var entry))
            {
                return null;
            }

            if (string.IsNullOrEmpty(filePath)
                || string.Equals(filePath, entry.FilePath, StringComparison.Ordinal))
            {
                return entry.Subprogram;
            }

            var key = (linkageName, filePath);
            if (_lexicalBlockFiles.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var scope = LlvmApi.DIBuilderCreateLexicalBlockFile(
                DIBuilder, entry.Subprogram, GetOrCreateFile(filePath), 0);
            _lexicalBlockFiles[key] = scope;
            return scope;
        }

        public void SetDebugLocation(LlvmBuilderHandle builder, SourceLocation loc, LlvmMetadataHandle scope)
        {
            var debugLoc = LlvmApi.CreateDebugLocation(
                LlvmContext, (uint)loc.Line, (uint)loc.Column, scope, default);
            LlvmApi.SetCurrentDebugLocation2(builder, debugLoc);
        }

        public void ClearDebugLocation(LlvmBuilderHandle builder)
        {
            LlvmApi.SetCurrentDebugLocation2(builder, default);
        }

        public void FinalizeDebugInfo()
        {
            LlvmApi.DIBuilderFinalize(DIBuilder);
        }

        public void Dispose()
        {
            LlvmApi.DisposeDIBuilder(DIBuilder);
        }
    }

    private static DebugInfoContext? CreateDebugInfoContext(
        LlvmTargetContext target,
        Backends.BackendCompileOptions options,
        IrProgram program)
    {
        if (!options.EmitDebugInfo)
        {
            return null;
        }

        // Determine default file from the first instruction located in a real source file;
        // stitched module pseudo-files ("<std:...>") only name the compile unit when nothing
        // better exists.
        string defaultFileName = "main.ash";
        string defaultDirectory = ".";

        var firstLoc = FindFirstUserSourceLocation(program) ?? FindFirstSourceLocation(program);
        if (firstLoc is not null)
        {
            defaultFileName = Path.GetFileName(firstLoc.Value.FilePath);
            defaultDirectory = Path.GetDirectoryName(firstLoc.Value.FilePath) ?? ".";
        }

        return new DebugInfoContext(target, defaultFileName, defaultDirectory,
            isOptimized: options.OptimizationLevel > Backends.BackendOptimizationLevel.O0);
    }

    private static SourceLocation? FindFirstUserSourceLocation(IrProgram program)
    {
        foreach (var inst in program.EntryFunction.Instructions)
        {
            if (inst.Location is { } loc && !loc.FilePath.StartsWith('<'))
            {
                return loc;
            }
        }

        foreach (var func in program.Functions)
        {
            foreach (var inst in func.Instructions)
            {
                if (inst.Location is { } loc && !loc.FilePath.StartsWith('<'))
                {
                    return loc;
                }
            }
        }

        return null;
    }

    private static SourceLocation? FindFirstSourceLocation(IrProgram program)
    {
        foreach (var inst in program.EntryFunction.Instructions)
        {
            if (inst.Location is not null)
            {
                return inst.Location;
            }
        }

        foreach (var func in program.Functions)
        {
            foreach (var inst in func.Instructions)
            {
                if (inst.Location is not null)
                {
                    return inst.Location;
                }
            }
        }

        return null;
    }

    private static void SetupFunctionDebugInfo(
        DebugInfoContext dbg,
        LlvmValueHandle llvmFunction,
        IrFunction function)
    {
        // Find the first source location in this function
        SourceLocation? firstLoc = null;
        foreach (var inst in function.Instructions)
        {
            if (inst.Location is not null)
            {
                firstLoc = inst.Location;
                break;
            }
        }

        var file = dbg.GetOrCreateFile(firstLoc?.FilePath);
        uint line = firstLoc is not null ? (uint)firstLoc.Value.Line : 0;

        // Use label as both name and linkage name
        var displayName = function.Label.StartsWith("lambda_", StringComparison.Ordinal)
            ? function.Label
            : function.Label.Replace("_start_", "", StringComparison.Ordinal);

        var sp = dbg.CreateSubprogram(displayName, function.Label, file, line, firstLoc?.FilePath);
        LlvmApi.SetSubprogram(llvmFunction, sp);
    }

    private static void EmitInstructionDebugLocation(
        DebugInfoContext? dbg,
        LlvmBuilderHandle builder,
        IrInst instruction,
        string functionLabel)
    {
        if (dbg is null)
        {
            return;
        }

        if (instruction.Location is { } loc && dbg.GetScopeForFile(functionLabel, loc.FilePath) is { } fileScope)
        {
            dbg.SetDebugLocation(builder, loc, fileScope);
            return;
        }

        var scope = dbg.GetSubprogram(functionLabel);
        if (scope is not null)
        {
            // LLVM's verifier and inliner require every call to a function that carries debug info to
            // carry a !dbg location itself; at -O2/-O3 the inliner also stitches each inlined
            // instruction's location into an inlined-at chain rooted at the call site, so a call with
            // no location produces invalid debug info. Many synthetic instructions (a devirtualized
            // CallKnown, arena/runtime helper calls, deep-copy emission) have no source location, so
            // give every unlocated instruction an artificial line-0 location in the enclosing scope
            // rather than clearing — line 0 is the DWARF convention for compiler-generated code and
            // keeps optimized debug builds (CO-21) verifier- and inliner-clean.
            dbg.SetDebugLocation(builder, new SourceLocation("", 0, 0), scope.Value);
        }
        else
        {
            dbg.ClearDebugLocation(builder);
        }
    }

    /// <summary>
    /// Emits DWARF debug variable declarations for named local slots.
    /// For lambda parameters (env slot 0, arg slot 1), emits DW_TAG_formal_parameter.
    /// For other named locals (let bindings, match bindings), emits DW_TAG_auto_variable.
    /// Must be called after allocas are created but before the function body.
    /// </summary>
    private static void EmitLocalVariableDebugInfo(
        DebugInfoContext dbg,
        LlvmBuilderHandle builder,
        IrFunction function,
        LlvmValueHandle[] localSlots)
    {
        if (function.LocalNames is null || function.LocalNames.Count == 0)
        {
            return;
        }

        var scope = dbg.GetSubprogram(function.Label);
        if (scope is null)
        {
            return;
        }

        var sp = scope.Value;
        var file = dbg.GetOrCreateFile(FindFirstFunctionSourcePath(function));
        uint line = FindFirstFunctionLine(function);
        var emptyExpr = LlvmApi.DIBuilderCreateExpression(dbg.DIBuilder);
        var debugLoc = LlvmApi.CreateDebugLocation(dbg.LlvmContext, line, 0, sp, default);
        var currentBlock = LlvmApi.GetInsertBlock(builder);

        foreach (var (slot, name) in function.LocalNames.OrderBy(kv => kv.Key))
        {
            if (slot >= localSlots.Length)
            {
                continue;
            }

            var variableType = function.LocalTypes is not null
                && function.LocalTypes.TryGetValue(slot, out var localType)
                ? dbg.GetOrCreateType(localType)
                : dbg.IntType;

            LlvmMetadataHandle varInfo;
            if (function.HasEnvAndArgParams && slot == 1)
            {
                // Lambda parameter (arg slot) — emit as formal parameter.
                // Ashes lambdas always receive (env, arg) with arg at slot 1;
                // DWARF argNo=1 marks this as the first user-visible parameter.
                varInfo = LlvmApi.DIBuilderCreateParameterVariable(
                    dbg.DIBuilder, sp, name, 1, file, line, variableType);
            }
            else
            {
                // Local variable (let binding, match binding, etc.)
                varInfo = LlvmApi.DIBuilderCreateAutoVariable(
                    dbg.DIBuilder, sp, name, file, line, variableType);
            }

            LlvmApi.DIBuilderInsertDeclareRecordAtEnd(
                dbg.DIBuilder, localSlots[slot], varInfo, emptyExpr, debugLoc, currentBlock);
        }
    }

    private static string? FindFirstFunctionSourcePath(IrFunction function)
    {
        foreach (var inst in function.Instructions)
        {
            if (inst.Location is not null)
            {
                return inst.Location.Value.FilePath;
            }
        }

        return null;
    }

    private static uint FindFirstFunctionLine(IrFunction function)
    {
        foreach (var inst in function.Instructions)
        {
            if (inst.Location is not null)
            {
                return (uint)inst.Location.Value.Line;
            }
        }

        return 0;
    }
}
