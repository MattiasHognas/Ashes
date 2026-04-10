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
        public LlvmContextHandle LlvmContext { get; }
        public bool IsOptimized { get; }

        private readonly Dictionary<string, LlvmMetadataHandle> _fileCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, LlvmMetadataHandle> _subprograms = new(StringComparer.Ordinal);

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
                LlvmApi.DwarfLangC99,
                DefaultFile,
                "Ashes Compiler",
                isOptimized: isOptimized);

            // Subroutine type (no parameters — all Ashes functions use i64 calling convention)
            SubroutineType = LlvmApi.DIBuilderCreateSubroutineType(DIBuilder, DefaultFile);

            // Basic debug types
            IntType = LlvmApi.DIBuilderCreateBasicType(
                DIBuilder, "Int", 64, LlvmApi.DwarfAteSigned);
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
            string name, string linkageName, LlvmMetadataHandle file, uint line)
        {
            var sp = LlvmApi.DIBuilderCreateFunction(
                DIBuilder, CompileUnit,
                name, linkageName, file,
                line, SubroutineType,
                isOptimized: IsOptimized);
            _subprograms[linkageName] = sp;
            return sp;
        }

        public LlvmMetadataHandle? GetSubprogram(string linkageName)
        {
            return _subprograms.TryGetValue(linkageName, out var sp) ? sp : null;
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

        // Determine default file from first instruction with a source location
        string defaultFileName = "main.ash";
        string defaultDirectory = ".";

        var firstLoc = FindFirstSourceLocation(program);
        if (firstLoc is not null)
        {
            defaultFileName = Path.GetFileName(firstLoc.Value.FilePath);
            defaultDirectory = Path.GetDirectoryName(firstLoc.Value.FilePath) ?? ".";
        }

        return new DebugInfoContext(target, defaultFileName, defaultDirectory,
            isOptimized: options.OptimizationLevel > Backends.BackendOptimizationLevel.O0);
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

        var sp = dbg.CreateSubprogram(displayName, function.Label, file, line);
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

        if (instruction.Location is { } loc)
        {
            var scope = dbg.GetSubprogram(functionLabel);
            if (scope is not null)
            {
                dbg.SetDebugLocation(builder, loc, scope.Value);
            }
        }
        else
        {
            dbg.ClearDebugLocation(builder);
        }
    }
}
