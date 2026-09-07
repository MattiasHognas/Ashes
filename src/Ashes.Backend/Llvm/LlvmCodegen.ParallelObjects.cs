using Ashes.Backend.Llvm.Interop;
using Ashes.Semantics;

namespace Ashes.Backend.Llvm;

/// <summary>
/// Splits a large program into partitions that are optimized and turned into object code on
/// separate threads, then linked as one executable. Each partition is its own LLVM module in its
/// own context: it declares every lifted function but defines only its own, and only partition 0
/// defines the entry function; runtime helper functions are defined in every module so they still
/// inline, while every global partition 0 defines becomes an external declaration in the others,
/// so the objects share one set of runtime globals and resolve each other's symbols by name at
/// link time. The partition count depends only on the program's size, which keeps the produced
/// image reproducible across machines.
/// </summary>
internal static partial class LlvmCodegen
{
    private const int ParallelObjectFunctionsPerPartition = 1024;
    private const int ParallelObjectMaximumPartitions = 16;

    // The partition count for a program: 1 (the ordinary single-module path) for a target whose
    // objects cannot be merged yet, for debug builds (whose DWARF is emitted per module), and for
    // small programs; the options' explicit count or ASHES_LLVM_JOBS when given; otherwise one
    // partition per ParallelObjectFunctionsPerPartition functions, capped.
    private static int ResolveParallelObjectPartitions(IrProgram program, Backends.BackendCompileOptions options, string targetId)
    {
        if (options.EmitDebugInfo || !ParallelObjectsSupported(targetId))
        {
            return 1;
        }

        if (options.ObjectPartitions is int explicitPartitions)
        {
            return Math.Clamp(explicitPartitions, 1, ParallelObjectMaximumPartitions);
        }

        string? configured = Environment.GetEnvironmentVariable("ASHES_LLVM_JOBS");
        if (int.TryParse(configured, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int configuredJobs))
        {
            return Math.Clamp(configuredJobs, 1, ParallelObjectMaximumPartitions);
        }

        return Math.Clamp(program.Functions.Count / ParallelObjectFunctionsPerPartition, 1, ParallelObjectMaximumPartitions);
    }

    private static bool ParallelObjectsSupported(string targetId) => targetId switch
    {
        Backends.TargetIds.LinuxX64 => true,
        Backends.TargetIds.LinuxArm64 => ElfRelocatableObjects.MergeSupported,
        Backends.TargetIds.WindowsX64 or Backends.TargetIds.WindowsArm64 => CoffRelocatableObjects.MergeSupported,
        _ => false,
    };

    // Assigns each lifted function to a partition by contiguous ranges balanced on instruction
    // count, so functions lowered together (one source module's) stay together and keep their
    // cross-calls inside one LLVM module.
    private static Dictionary<string, int> AssignParallelObjectPartitions(IrProgram program, int partitions)
    {
        long total = program.EntryFunction.Instructions.Count;
        foreach (IrFunction function in program.Functions)
        {
            total += function.Instructions.Count;
        }

        var assignment = new Dictionary<string, int>(program.Functions.Count, StringComparer.Ordinal);
        long target = (total + partitions - 1) / partitions;
        long accumulated = program.EntryFunction.Instructions.Count;
        int partition = 0;
        foreach (IrFunction function in program.Functions)
        {
            if (accumulated >= target * (partition + 1) && partition < partitions - 1)
            {
                partition++;
            }

            assignment[function.Label] = partition;
            accumulated += function.Instructions.Count;
        }

        return assignment;
    }

    private static byte[] CompileParallelObjects(
        IrProgram program,
        Backends.BackendCompileOptions options,
        string targetId,
        LlvmCodegenFlavor flavor,
        bool usesTlsRuntime,
        int partitions)
    {
        Dictionary<string, int> assignment = AssignParallelObjectPartitions(program, partitions);
        var targets = new LlvmTargetContext[partitions];
        try
        {
            CompilePhaseTiming.Measure("backend.emit-module", () =>
            {
                for (int partition = 0; partition < partitions; partition++)
                {
                    targets[partition] = LlvmTargetSetup.Create(
                        targetId, options.OptimizationLevel, options.TargetCpu,
                        options.ParallelWorkerStackBytes, options.ParallelWorkerCap)
                        with
                    { Partition = new ProgramPartition(partition, assignment) };
                    EmitProgramModule(targets[partition], program, "entry", flavor, options, usesTlsRuntime);
                }
            });
            CompilePhaseTiming.Measure("backend.export-symbols", () => ExportPartitionSymbols(targets, assignment, "entry"));
            CompilePhaseTiming.Measure("backend.verify", () =>
            {
                foreach (LlvmTargetContext target in targets)
                {
                    VerifyModule(target);
                }
            });

            var objects = new byte[partitions][];
            CompilePhaseTiming.Measure("backend.parallel-objects", () =>
                Parallel.For(
                    0,
                    partitions,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    partition => objects[partition] = EmitPartitionObject(targets[partition], program, options, targetId, partition)));

            DumpPartitionObjects(objects);
            return CompilePhaseTiming.Measure("backend.link", () =>
                LinkPartitionObjects(targetId, objects, GetExternalLibraries(program)));
        }
        finally
        {
            foreach (LlvmTargetContext target in targets)
            {
                target?.Dispose();
            }
        }
    }

    // Links the partitions' objects into the target's executable: the linux-x64 linker takes the
    // objects directly; the other targets merge them into one relocatable object first and link
    // that as usual.
    private static byte[] LinkPartitionObjects(string targetId, byte[][] objects, IReadOnlyDictionary<string, string>? externalLibraries) =>
        targetId switch
        {
            Backends.TargetIds.LinuxX64 => LlvmImageLinker.LinkLinuxExecutable(objects, "entry", null, externalLibraries),
            Backends.TargetIds.LinuxArm64 => LlvmImageLinker.LinkLinuxArm64Executable(ElfRelocatableObjects.Merge(objects), "entry", null, externalLibraries),
            Backends.TargetIds.WindowsX64 => LlvmImageLinker.LinkWindowsExecutable(CoffRelocatableObjects.Merge(objects), "entry", null, externalLibraries),
            Backends.TargetIds.WindowsArm64 => LlvmImageLinker.LinkWindowsArm64Executable(CoffRelocatableObjects.Merge(objects), "entry", null, externalLibraries),
            _ => throw new ArgumentOutOfRangeException(nameof(targetId), $"Unknown target '{targetId}'."),
        };

    // Writes each partition's object next to each other under the directory ASHES_DUMP_OBJECTS
    // names, for inspecting what the linker receives.
    private static void DumpPartitionObjects(byte[][] objects)
    {
        string? directory = Environment.GetEnvironmentVariable("ASHES_DUMP_OBJECTS");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        for (int partition = 0; partition < objects.Length; partition++)
        {
            File.WriteAllBytes(Path.Combine(directory, $"partition{partition}.o"), objects[partition]);
        }
    }

    // Optimizes one partition's module and emits its object code on the calling thread. The
    // vendored bitcode payloads are linked into partition 0 alone; the other partitions reach
    // them through the link.
    private static byte[] EmitPartitionObject(
        LlvmTargetContext target,
        IrProgram program,
        Backends.BackendCompileOptions options,
        string targetId,
        int partition)
    {
        CompilePhaseTiming.Measure($"backend.partition{partition}.llvm-passes", () =>
            RunLlvmOptimizationPasses(target, options.OptimizationLevel));
        if (partition == 0)
        {
            LinkOpenlibmBitcodeIfNeeded(target, program, targetId);
            LinkPcre2BitcodeIfNeeded(target, program, targetId);
            LinkMbedTlsBitcodeIfNeeded(target, program, targetId);
        }

        return CompilePhaseTiming.Measure($"backend.partition{partition}.object-code", () => EmitObjectCode(target));
    }

    // Exports every lifted function and the entry from the module that defines it and declares it
    // externally in the others; exports partition 0's globals and replaces the other partitions'
    // definitions of the same names by declarations, so the runtime state and the string literals
    // partition 0 holds are shared. A global only a later partition defines (a literal partition 0
    // never uses) stays that partition's own.
    private static void ExportPartitionSymbols(
        LlvmTargetContext[] targets,
        IReadOnlyDictionary<string, int> assignment,
        string entryFunctionName)
    {
        var sharedGlobals = new HashSet<string>(StringComparer.Ordinal);
        for (LlvmValueHandle global = LlvmApi.GetFirstGlobal(targets[0].Module); global.Ptr != 0; global = LlvmApi.GetNextGlobal(global))
        {
            if (LlvmApi.IsDeclaration(global) == 0)
            {
                string name = LlvmApi.GetValueName(global);
                if (name.Length == 0)
                {
                    name = $"__ashes_shared_global_{sharedGlobals.Count}";
                    LlvmApi.SetValueName(global, name);
                }

                LlvmApi.SetLinkage(global, LlvmLinkage.External);
                sharedGlobals.Add(name);
            }
        }

        foreach (LlvmTargetContext target in targets)
        {
            for (LlvmValueHandle function = LlvmApi.GetFirstFunction(target.Module); function.Ptr != 0; function = LlvmApi.GetNextFunction(function))
            {
                string name = LlvmApi.GetValueName(function);
                if (assignment.ContainsKey(name) || string.Equals(name, entryFunctionName, StringComparison.Ordinal))
                {
                    LlvmApi.SetLinkage(function, LlvmLinkage.External);
                }
            }
        }

        for (int partition = 1; partition < targets.Length; partition++)
        {
            var duplicates = new List<LlvmValueHandle>();
            for (LlvmValueHandle global = LlvmApi.GetFirstGlobal(targets[partition].Module); global.Ptr != 0; global = LlvmApi.GetNextGlobal(global))
            {
                if (LlvmApi.IsDeclaration(global) == 0 && sharedGlobals.Contains(LlvmApi.GetValueName(global)))
                {
                    duplicates.Add(global);
                }
            }

            foreach (LlvmValueHandle global in duplicates)
            {
                ReplaceGlobalWithDeclaration(targets[partition].Module, global);
            }
        }
    }

    // Replaces a defined global by an external declaration of the same name, type, constness,
    // and alignment.
    private static void ReplaceGlobalWithDeclaration(LlvmModuleHandle module, LlvmValueHandle global)
    {
        string name = LlvmApi.GetValueName(global);
        LlvmTypeHandle type = LlvmApi.GlobalGetValueType(global);
        LlvmApi.SetValueName(global, name + "$stripped");
        LlvmValueHandle declaration = LlvmApi.AddGlobal(module, type, name);
        LlvmApi.SetLinkage(declaration, LlvmLinkage.External);
        LlvmApi.SetGlobalConstant(declaration, LlvmApi.IsGlobalConstant(global));
        LlvmApi.SetAlignment(declaration, LlvmApi.GetAlignment(global));
        LlvmApi.ReplaceAllUsesWith(global, declaration);
        LlvmApi.DeleteGlobal(global);
    }
}
