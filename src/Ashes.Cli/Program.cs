using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ashes.Backend.Backends;
using Ashes.Cli.Registry;
using Ashes.Formatter;
using Ashes.Frontend;
using Ashes.Semantics;
using Ashes.TestRunner;
using Spectre.Console;

static int Usage(int exitCode = 2)
{
    AnsiConsole.Write(new Rule("[bold]Ashes[/]").RuleStyle("grey").LeftJustified());
    AnsiConsole.MarkupLine("[grey]Commands:[/]");
    AnsiConsole.MarkupLine("  [bold]ashes compile[/] [[--project <ashes.json>]] [[--target linux-x64|linux-arm64|win-x64|win-arm64]] [[-O0|-O1|-O2|-O3]] [[--target-cpu <cpu>]] [[--debug|-g]] <input.ash | --expr \"...\" > [[-o <output>]]");
    AnsiConsole.MarkupLine("  [bold]ashes run[/]     [[--project <ashes.json>]] [[--target linux-x64|linux-arm64|win-x64|win-arm64]] [[-O0|-O1|-O2|-O3]] [[--target-cpu <cpu>]] [[--debug|-g]] <input.ash | --expr \"...\" > [[-- <args...>]]");
    AnsiConsole.MarkupLine("  [bold]ashes repl[/]    [[--target linux-x64|linux-arm64|win-x64|win-arm64]] [[-O0|-O1|-O2|-O3]] [[--target-cpu <cpu>]]");
    AnsiConsole.MarkupLine("  [bold]ashes test[/]    [[--project <ashes.json>]] [[--target linux-x64|linux-arm64|win-x64|win-arm64]] [[-O0|-O1|-O2|-O3]] [[--target-cpu <cpu>]] [[paths...]]");
    AnsiConsole.MarkupLine("  [bold]ashes fmt[/]     <file|dir> [[-w]]");
    AnsiConsole.MarkupLine("  [bold]ashes init[/]");
    AnsiConsole.MarkupLine("  [bold]ashes add[/]     <package> [[--path <dir>]] [[--dev]]");
    AnsiConsole.MarkupLine("  [bold]ashes remove[/]  <package>");
    AnsiConsole.MarkupLine("  [bold]ashes restore[/] [[--frozen]] [[--offline]]");
    AnsiConsole.MarkupLine("  [bold]ashes tree[/]");
    AnsiConsole.MarkupLine("  [bold]ashes why[/]    <namespace>");
    AnsiConsole.MarkupLine("  [bold]ashes --version[/]");
    AnsiConsole.WriteLine();

    var table = new Table().RoundedBorder().BorderColor(Color.Grey);
    table.AddColumn("[grey]Option[/]");
    table.AddColumn("[grey]Description[/]");
    table.AddRow("[yellow]--target[/]", "Select output target. Defaults to current OS.");
    table.AddRow("[yellow]--project[/]", "Use a specific ashes.json project file.");
    table.AddRow("[yellow]-o[/], [yellow]--out[/]", "Output path (compile only). If omitted, derived from input name.");
    table.AddRow("[yellow]--expr[/]", "Use inline source instead of reading a .ash file.");
    table.AddRow("[yellow]-O0[/]..[yellow]-O3[/]", "Select optimization level.");
    table.AddRow("[yellow]--target-cpu[/]", "Target a specific CPU (e.g. skylake, native). Defaults to x86-64 on x86-64 targets and generic on ARM64.");
    table.AddRow("[yellow]--parallel-stack-size[/]", "Per-worker stack size for structured parallelism (e.g. 2M, 1048576). Defaults to 1M.");
    table.AddRow("[yellow]--parallel-workers[/]", "Max concurrent parallel workers. Defaults to the machine's core count, detected at program start.");
    table.AddRow("[yellow]--debug[/], [yellow]-g[/]", "Emit DWARF debug info. Defaults to -O0; an explicit -O1/-O2/-O3 is honored.");
    table.AddRow("[yellow]-w[/]", "Write formatted output back to file(s) (fmt only).");
    table.AddRow("[yellow]--version[/], [yellow]-v[/]", "Print the compiler version and exit.");
    AnsiConsole.Write(table);

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[grey]Examples:[/]");
    AnsiConsole.MarkupLine("  [bold]ashes compile[/] examples/hello.ash");
    AnsiConsole.MarkupLine("  [bold]ashes run[/] examples/closures.ash");
    AnsiConsole.MarkupLine("  [bold]ashes run[/] --expr [italic]\"Ashes.IO.print(40 + 2)\"[/] -- arg1 arg2");
    AnsiConsole.MarkupLine("  [bold]ashes test[/] tests");
    AnsiConsole.MarkupLine("  [bold]ashes fmt[/] examples -w");
    return exitCode;
}

static string DeriveOutputPath(string inputPath, string targetId)
{
    var dir = Path.GetDirectoryName(inputPath);
    var name = Path.GetFileNameWithoutExtension(inputPath);

    var outName = TargetIds.IsWindows(targetId) ? name + ".exe" : name;
    return string.IsNullOrWhiteSpace(dir) ? outName : Path.Combine(dir!, outName);
}

static AshesProject? ResolveProject(string? projectOption, string? inputFile, string? expr)
{
    if (!string.IsNullOrEmpty(projectOption))
    {
        return ProjectSupport.LoadProject(projectOption);
    }

    if (string.IsNullOrEmpty(inputFile) && string.IsNullOrEmpty(expr))
    {
        var discovered = ProjectSupport.DiscoverProjectFile(Directory.GetCurrentDirectory());
        if (!string.IsNullOrEmpty(discovered))
        {
            return ProjectSupport.LoadProject(discovered);
        }
    }

    return null;
}

// Auto-restore before build/run/test: if a project declares registry dependencies and its lock is
// missing or a locked package is not cached, restore against the default registry (skipped for
// standalone files/expressions and projects with only path dependencies). Explicit registry selection
// or --frozen/--offline is done via `ashes restore`.
static async Task AutoRestoreProjectAsync(string? projectOption, string? inputFile, string? expr)
{
    var projectFile = projectOption;
    if (string.IsNullOrEmpty(projectFile) && string.IsNullOrEmpty(inputFile) && string.IsNullOrEmpty(expr))
    {
        projectFile = ProjectSupport.DiscoverProjectFile(Directory.GetCurrentDirectory());
    }

    if (string.IsNullOrEmpty(projectFile))
    {
        return;
    }

    var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!;
    var roots = CollectRegistryRoots(projectFile);
    if (roots.Count == 0)
    {
        return;
    }

    var lockFile = Ashes.Cli.Package.LockFile.Read(projectDirectory);
    var cache = new Ashes.Cli.Package.PackageCache();
    var needsRestore = lockFile is null || lockFile.Package.Any(p => !cache.Has(p.Namespace, p.Version, p.Hash));
    if (!needsRestore)
    {
        return;
    }

    AnsiConsole.MarkupLine("[grey]Restoring dependencies...[/]");
    await RestoreRegistryDependenciesAsync(projectFile, projectDirectory, null, frozen: false, offline: false, CancellationToken.None)
        .ConfigureAwait(false);
}

static string DeriveProjectOutputPath(AshesProject project, string targetId)
{
    var outputName = !string.IsNullOrWhiteSpace(project.Name)
        ? project.Name!
        : Path.GetFileNameWithoutExtension(project.EntryPath);

    if (TargetIds.IsWindows(targetId))
    {
        outputName += ".exe";
    }

    return Path.Combine(project.OutDir, outputName);
}

static async Task<string> ReadSourceAsync(string? inputFile, string? expr)
{
    if (!string.IsNullOrEmpty(expr))
    {
        return expr;
    }

    if (string.IsNullOrEmpty(inputFile))
    {
        throw new CliUserException("Missing input file or --expr.");
    }

    if (!inputFile.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
    {
        throw new CliUserException("Input file must have .ash extension (or use --expr).");
    }

    if (!File.Exists(inputFile))
    {
        throw new CliUserException($"File not found: {inputFile}");
    }

    return await File.ReadAllTextAsync(inputFile).ConfigureAwait(false);
}

static byte[] CompileToImage(
    string source,
    string targetId,
    BackendCompileOptions? backendOptions = null,
    IReadOnlySet<string>? importedStdModules = null,
    IReadOnlyDictionary<string, string>? moduleAliases = null,
    CombinedCompilationLayout? sourceLayout = null)
{
    var diag = new Diagnostics();
    var program = new Parser(source, diag).ParseProgram();
    diag.ThrowIfAny();

    var lowering = new Lowering(diag, importedStdModules, moduleAliases);
    if (sourceLayout is { } layout)
    {
        lowering.SetSourceContext(layout);
    }

    var ir = lowering.Lower(program);
    diag.ThrowIfAny();

    // Run IR-level optimization passes before backend codegen
    ir = IrOptimizer.Optimize(ir);

    var effectiveOptions = backendOptions ?? BackendCompileOptions.Default;
    var backend = BackendFactory.Create(targetId);
    return backend.Compile(ir, effectiveOptions);
}

static (CombinedCompilationLayout Layout, IReadOnlySet<string>? ImportedStdModules, IReadOnlyDictionary<string, string>? ModuleAliases) PrepareStandaloneCompilationSource(string source, string displayPath)
{
    var parsed = ProjectSupport.ParseImportHeader(source, displayPath);
    var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames, displayPath);
    var importedStdModules = parsed.ImportNames
        .Where(ProjectSupport.IsStdModule)
        .ToHashSet(StringComparer.Ordinal);

    return (layout, importedStdModules.Count == 0 ? null : importedStdModules, parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases);
}

static byte[] CompileProjectToImage(AshesProject project, string targetId, BackendCompileOptions? backendOptions = null)
{
    var plan = ProjectSupport.BuildCompilationPlan(project);
    var layout = ProjectSupport.BuildCompilationLayout(plan);
    return CompileToImage(layout.Source, targetId, backendOptions, plan.ImportedStdModules, plan.MergedAliases.Count == 0 ? null : plan.MergedAliases, layout);
}

static bool TryParseOptimizationFlag(string arg, out BackendOptimizationLevel level)
{
    switch (arg)
    {
        case "-O0":
            level = BackendOptimizationLevel.O0;
            return true;
        case "-O1":
            level = BackendOptimizationLevel.O1;
            return true;
        case "-O2":
            level = BackendOptimizationLevel.O2;
            return true;
        case "-O3":
            level = BackendOptimizationLevel.O3;
            return true;
        default:
            level = BackendCompileOptions.Default.OptimizationLevel;
            return false;
    }
}

// Parses the --parallel-stack-size value. Accepts a plain byte count or a K/M/G suffix
// (e.g. "2M", "1048576"). Must be a positive size.
static long ParseParallelStackSize(string value)
{
    string trimmed = value.Trim();
    long multiplier = 1;
    if (trimmed.Length > 0)
    {
        char suffix = char.ToLowerInvariant(trimmed[^1]);
        multiplier = suffix switch
        {
            'k' => 1024L,
            'm' => 1024L * 1024,
            'g' => 1024L * 1024 * 1024,
            _ => 1,
        };
        if (multiplier != 1)
        {
            trimmed = trimmed[..^1];
        }
    }

    if (!long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long amount)
        || amount <= 0)
    {
        throw new CliUsageException("--parallel-stack-size expects a positive byte count (optionally suffixed with K, M, or G).");
    }

    return amount * multiplier;
}

// Parses the --parallel-workers value: a positive worker count. When the flag is absent the
// compiled program detects the machine's core count at startup instead.
static long ParseParallelWorkers(string value)
{
    if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long count)
        || count <= 0)
    {
        throw new CliUsageException("--parallel-workers expects a positive worker count.");
    }

    return count;
}

static void SetUnixExecutableModeIfSupported(string exePath)
{
    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        try
        {
            File.SetUnixFileMode(exePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { }
    }
}

static async Task<(int ExitCode, string Stdout, string Stderr)> RunImageCaptureAsync(byte[] image, string targetId, IReadOnlyList<string>? programArgs = null)
{
    var tmpDir = Path.Combine(Path.GetTempPath(), "ashes");
    Directory.CreateDirectory(tmpDir);

    var name = "ashes_" + Guid.NewGuid().ToString("N");
    var exePath = Path.Combine(tmpDir, TargetIds.IsWindows(targetId) ? name + ".exe" : name);

    await File.WriteAllBytesAsync(exePath, image).ConfigureAwait(false);
    SetUnixExecutableModeIfSupported(exePath);

    var psi = new ProcessStartInfo(exePath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    foreach (var arg in programArgs ?? [])
    {
        psi.ArgumentList.Add(arg);
    }

    using var p = StartCompiledProcess(psi);

    var stdoutTask = p.StandardOutput.ReadToEndAsync();
    var stderrTask = p.StandardError.ReadToEndAsync();
    await Task.WhenAll(stdoutTask, stderrTask, p.WaitForExitAsync()).ConfigureAwait(false);

    return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
}

static async Task<int> RunImageWithInheritedStdioAsync(byte[] image, string targetId, IReadOnlyList<string>? programArgs = null)
{
    var tmpDir = Path.Combine(Path.GetTempPath(), "ashes");
    Directory.CreateDirectory(tmpDir);

    var name = "ashes_" + Guid.NewGuid().ToString("N");
    var exePath = Path.Combine(tmpDir, TargetIds.IsWindows(targetId) ? name + ".exe" : name);

    await File.WriteAllBytesAsync(exePath, image).ConfigureAwait(false);
    SetUnixExecutableModeIfSupported(exePath);

    var psi = new ProcessStartInfo(exePath)
    {
        UseShellExecute = false,
    };

    foreach (var arg in programArgs ?? [])
    {
        psi.ArgumentList.Add(arg);
    }

    using var p = StartCompiledProcess(psi);

    await p.WaitForExitAsync().ConfigureAwait(false);
    return p.ExitCode;
}

static Process StartCompiledProcess(ProcessStartInfo startInfo)
{
    const int maxAttempts = 20;

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start compiled executable.");
        }
        catch (Win32Exception ex) when (attempt < maxAttempts && IsTextFileBusy(ex))
        {
            Thread.Sleep(25);
        }
    }
}

static bool IsTextFileBusy(Win32Exception ex)
{
    return OperatingSystem.IsLinux()
        && (ex.NativeErrorCode == 26
            || ex.Message.Contains("Text file busy", StringComparison.OrdinalIgnoreCase));
}

static string BuildReplSessionSource(IReadOnlyList<ReplBinding> bindings, string exprSource)
{
    var current = exprSource.Trim();
    for (var i = bindings.Count - 1; i >= 0; i--)
    {
        current = WrapWithBinding(bindings[i], current);
    }

    return current;
}

static string WrapWithBinding(ReplBinding binding, string bodySource)
{
    var keyword = binding.IsRecursive ? "let recursive" : "let";
    return $"{keyword} {binding.Name} = {binding.ValueSource}\nin {bodySource}";
}

static bool LooksIncomplete(string input)
{
    int depth = 0;
    foreach (var ch in input)
    {
        if (ch == '(')
        {
            depth++;
        }
        else if (ch == ')')
        {
            depth = Math.Max(0, depth - 1);
        }
    }
    return depth != 0;
}

static bool TryAnalyzeReplSubmission(IReadOnlyList<ReplBinding> bindings, string source, out ReplSubmissionAnalysis? analysis, out CompileDiagnosticException? diagnostics, out string? error)
{
    try
    {
        var compositeSource = BuildReplSessionSource(bindings, source);
        var diag = new Diagnostics();
        var program = new Parser(compositeSource, diag).ParseProgram();
        diag.ThrowIfAny();

        var lowering = new Lowering(diag);
        lowering.Lower(program);
        diag.ThrowIfAny();

        var type = lowering.LastLoweredType ?? new TypeRef.TNever();
        analysis = new ReplSubmissionAnalysis(
            lowering.FormatType(type),
            IsPrintableReplType(type),
            TryExtractPersistentBinding(source, out var binding) ? binding.Name : null);
        diagnostics = null;
        error = null;
        return true;
    }
    catch (CompileDiagnosticException ex)
    {
        analysis = null;
        diagnostics = ex;
        error = ex.Message;
        return false;
    }
    catch (Exception ex)
    {
        analysis = null;
        diagnostics = null;
        error = ex.Message;
        return false;
    }
}

static bool TryCompileReplSubmission(IReadOnlyList<ReplBinding> bindings, string source, bool autoPrint, string targetId, BackendCompileOptions backendOptions, out byte[]? image, out CompileDiagnosticException? diagnostics, out string? error)
{
    try
    {
        var exprSource = autoPrint ? $"Ashes.IO.print({source.Trim()})" : source.Trim();
        image = CompileToImage(BuildReplSessionSource(bindings, exprSource), targetId, backendOptions);
        diagnostics = null;
        error = null;
        return true;
    }
    catch (CompileDiagnosticException ex)
    {
        image = null;
        diagnostics = ex;
        error = ex.Message;
        return false;
    }
    catch (Exception ex)
    {
        image = null;
        diagnostics = null;
        error = ex.Message;
        return false;
    }
}

static bool TryExtractPersistentBinding(string source, out ReplBinding binding)
{
    var diag = new Diagnostics();
    var expr = new Parser(source.Trim(), diag).ParseExpression();
    if (diag.StructuredErrors.Count > 0)
    {
        binding = null!;
        return false;
    }

    switch (expr)
    {
        case Expr.Let letExpr when letExpr.Body is Expr.Var bodyVar && string.Equals(bodyVar.Name, letExpr.Name, StringComparison.Ordinal):
            binding = new ReplBinding(letExpr.Name, Formatter.Format(letExpr.Value).Trim(), false);
            return true;

        case Expr.LetRecursive letRecursiveExpr when letRecursiveExpr.Body is Expr.Var recursiveBodyVar && string.Equals(recursiveBodyVar.Name, letRecursiveExpr.Name, StringComparison.Ordinal):
            binding = new ReplBinding(letRecursiveExpr.Name, Formatter.Format(letRecursiveExpr.Value).Trim(), true);
            return true;

        default:
            binding = null!;
            return false;
    }
}

static bool IsPrintableReplType(TypeRef type)
{
    return type is TypeRef.TInt or TypeRef.TStr or TypeRef.TBool;
}

static void PrintReplTypeEcho(ReplSubmissionAnalysis analysis)
{
    var prefix = string.IsNullOrEmpty(analysis.BindingName)
        ? ": "
        : analysis.BindingName + " : ";
    AnsiConsole.Write(new Text(prefix + analysis.TypeDisplay + Environment.NewLine));
}

static async Task<string?> ReadReplLineAsync(string prompt)
{
    if (Console.IsInputRedirected)
    {
        Console.Write(prompt);
        return await Console.In.ReadLineAsync().ConfigureAwait(false);
    }

    return AnsiConsole.Prompt(new TextPrompt<string>($"[grey]{prompt}[/] ").AllowEmpty());
}

static bool IsLikelyNeedMoreInput(string error)
{
    return error.Contains("EOF", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Expected", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Unterminated", StringComparison.OrdinalIgnoreCase);
}

if (args.Length == 0)
{
    return Usage();
}

var command = args[0].ToLowerInvariant();

if (command is "--help" or "-h")
{
    return Usage(0);
}

try
{
    return command switch
    {
        "compile" => await RunCompileAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
        "run" => await RunRunAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
        "repl" => await RunReplAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
        "test" => await RunTest(args.Skip(1).ToArray()).ConfigureAwait(false),
        "fmt" => await RunFmtAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
        "init" => RunInit(args.Skip(1).ToArray()),
        "add" => RunAdd(args.Skip(1).ToArray()),
        "remove" => RunRemove(args.Skip(1).ToArray()),
        "restore" => await RunRestore(args.Skip(1).ToArray()).ConfigureAwait(false),
        "tree" => RunTree(args.Skip(1).ToArray()),
        "why" => RunWhy(args.Skip(1).ToArray()),
        "install" => throw new CliUserException("`ashes install` has been retired. Use `ashes restore` to materialize dependencies (or `ashes add` to add one)."),
        "login" => await RegistryCommands.LoginAsync(args.Skip(1).ToArray(), CancellationToken.None).ConfigureAwait(false),
        "publish" => await RegistryCommands.PublishAsync(args.Skip(1).ToArray(), CancellationToken.None).ConfigureAwait(false),
        "yank" => await RegistryCommands.YankAsync(args.Skip(1).ToArray(), CancellationToken.None).ConfigureAwait(false),
        "search" => await RegistryCommands.SearchAsync(args.Skip(1).ToArray(), CancellationToken.None).ConfigureAwait(false),
        "info" => await RegistryCommands.InfoAsync(args.Skip(1).ToArray(), CancellationToken.None).ConfigureAwait(false),
        "--version" or "-v" => RunVersion(),
        _ => Usage()
    };
}
catch (CliUsageException ex)
{
    Console.Error.Write(DiagnosticTextRenderer.RenderFailure("error", ex.Message));
    return 2;
}
catch (CliUserException ex)
{
    Console.Error.Write(DiagnosticTextRenderer.RenderFailure("error", ex.Message));
    return 1;
}
catch (CompileDiagnosticException ex)
{
    PrintCompilerDiagnostics(ex, null, "<source>");
    return 1;
}
catch (Exception ex)
{
    Console.Error.Write(DiagnosticTextRenderer.RenderFailure("error", ex.Message));
    return 1;
}

async Task<int> RunCompileAsync(string[] a)
{
    if (a.Length == 1 && (string.Equals(a[0], "--help", StringComparison.Ordinal) || string.Equals(a[0], "-h", StringComparison.Ordinal)))
    {
        return Usage(0);
    }

    var arguments = ParseCompileArguments(a);
    var (project, target, backendOptions) = await ResolveCompileContextAsync(arguments).ConfigureAwait(false);

    var sw = Stopwatch.StartNew();
    var image = await CompileCliInputAsync(project, arguments.InputFile, arguments.Expr, target, backendOptions).ConfigureAwait(false);
    if (image is null)
    {
        return 1;
    }
    sw.Stop();

    var outPath = arguments.OutPath ?? (project is not null
        ? DeriveProjectOutputPath(project, target)
        : arguments.InputFile is not null
            ? DeriveOutputPath(arguments.InputFile, target)
            : "out" + (TargetIds.IsWindows(target) ? ".exe" : ""));

    var outDir = Path.GetDirectoryName(outPath);
    if (!string.IsNullOrWhiteSpace(outDir))
    {
        Directory.CreateDirectory(outDir!);
    }
    await File.WriteAllBytesAsync(outPath, image).ConfigureAwait(false);
    SetUnixExecutableModeIfSupported(outPath);

    AnsiConsole.MarkupLine($"[green]OK[/] Wrote [bold]{Runner.FormatSize(image.Length)}[/] to [italic]{outPath}[/]");
    AnsiConsole.MarkupLine($"     Target: [bold]{target}[/]");
    if (arguments.DebugMode)
    {
        AnsiConsole.MarkupLine($"     Debug:  [bold]yes[/]");
    }
    AnsiConsole.MarkupLine($"     Time:   [bold]{Runner.FormatElapsed(sw.ElapsedMilliseconds)}[/]");
    return 0;
}

static CompileCommandArguments ParseCompileArguments(string[] a)
{
    string? target = null;
    BackendOptimizationLevel optimizationLevel = BackendCompileOptions.Default.OptimizationLevel;
    bool explicitOpt = false;
    bool debugMode = false;
    string? outPath = null;
    string? expr = null;
    string? inputFile = null;
    string? projectPath = null;
    string? targetCpu = null;
    long? parallelStackBytes = null;
    long? parallelWorkers = null;

    for (int i = 0; i < a.Length; i++)
    {
        var arg = a[i];

        if (TryParseTargetOptions(a, ref i, ref target, ref targetCpu, ref parallelStackBytes, ref parallelWorkers)) { continue; }
        if ((string.Equals(arg, "-o", StringComparison.Ordinal) || string.Equals(arg, "--out", StringComparison.Ordinal)) && i + 1 < a.Length) { outPath = a[++i]; continue; }
        if (string.Equals(arg, "--expr", StringComparison.Ordinal) && i + 1 < a.Length) { expr = a[++i]; continue; }
        if (string.Equals(arg, "--project", StringComparison.Ordinal) && i + 1 < a.Length) { projectPath = a[++i]; continue; }
        if (arg is "--debug" or "-g") { debugMode = true; continue; }
        if (TryParseOptimizationFlag(arg, out var parsedOptimizationLevel)) { optimizationLevel = parsedOptimizationLevel; explicitOpt = true; continue; }

        if (!arg.StartsWith("-", StringComparison.Ordinal) && inputFile is null) { inputFile = arg; continue; }

        throw new CliUsageException("Unknown argument.");
    }

    if (debugMode && !explicitOpt)
    {
        // --debug defaults to -O0 (readable single-stepping); an explicit -O1/-O2/-O3 is honored so a
        // profiled debug build matches the optimized binary's inlining (CO-21).
        optimizationLevel = BackendOptimizationLevel.O0;
    }

    return new CompileCommandArguments(target, optimizationLevel, debugMode, outPath, expr, inputFile, projectPath, targetCpu, parallelStackBytes, parallelWorkers);
}

static bool TryParseTargetOptions(string[] a, ref int i, ref string? target, ref string? targetCpu, ref long? parallelStackBytes, ref long? parallelWorkers)
{
    var arg = a[i];
    if (string.Equals(arg, "--target", StringComparison.Ordinal) && i + 1 < a.Length) { target = a[++i]; return true; }
    if (string.Equals(arg, "--target-cpu", StringComparison.Ordinal) && i + 1 < a.Length) { targetCpu = a[++i]; return true; }
    if (string.Equals(arg, "--parallel-stack-size", StringComparison.Ordinal) && i + 1 < a.Length) { parallelStackBytes = ParseParallelStackSize(a[++i]); return true; }
    if (string.Equals(arg, "--parallel-workers", StringComparison.Ordinal) && i + 1 < a.Length) { parallelWorkers = ParseParallelWorkers(a[++i]); return true; }
    return false;
}

static async Task<(AshesProject? Project, string Target, BackendCompileOptions Options)> ResolveCompileContextAsync(CompileCommandArguments arguments)
{
    await AutoRestoreProjectAsync(arguments.ProjectPath, arguments.InputFile, arguments.Expr).ConfigureAwait(false);
    var project = ResolveProject(arguments.ProjectPath, arguments.InputFile, arguments.Expr);
    if (project is not null && (!string.IsNullOrEmpty(arguments.InputFile) || !string.IsNullOrEmpty(arguments.Expr)))
    {
        throw new CliUsageException("Cannot combine --project with input file or --expr.");
    }

    var target = arguments.Target ?? project?.Target ?? BackendFactory.DefaultForCurrentOS();
    var backendOptions = new BackendCompileOptions(arguments.OptimizationLevel, arguments.DebugMode, arguments.TargetCpu, arguments.ParallelStackBytes, arguments.ParallelWorkers);
    return (project, target, backendOptions);
}

static async Task<byte[]?> CompileCliInputAsync(AshesProject? project, string? inputFile, string? expr, string target, BackendCompileOptions backendOptions)
{
    if (project is null)
    {
        var source = await ReadSourceAsync(inputFile, expr).ConfigureAwait(false);
        var displayPath = inputFile ?? "<expr>";
        CombinedCompilationLayout? diagnosticLayout = null;
        try
        {
            var prepared = PrepareStandaloneCompilationSource(source, displayPath);
            diagnosticLayout = prepared.Layout;
            return CompileToImage(prepared.Layout.Source, target, backendOptions, prepared.ImportedStdModules, prepared.ModuleAliases, prepared.Layout);
        }
        catch (CompileDiagnosticException ex)
        {
            PrintCompilerDiagnostics(ex, source, displayPath, diagnosticLayout);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            PrintCompileFailure(ex.Message, displayPath);
            return null;
        }
    }

    try
    {
        return CompileProjectToImage(project, target, backendOptions);
    }
    catch (CompileDiagnosticException ex)
    {
        PrintCompilerDiagnostics(ex, null, project.EntryPath);
        return null;
    }
    catch (InvalidOperationException ex)
    {
        PrintCompileFailure(ex.Message, project.EntryPath);
        return null;
    }
}

async Task<int> RunRunAsync(string[] a)
{
    if (a.Length == 1 && (string.Equals(a[0], "--help", StringComparison.Ordinal) || string.Equals(a[0], "-h", StringComparison.Ordinal)))
    {
        return Usage(0);
    }

    var idx = Array.IndexOf(a, "--");
    var cliArgs = idx >= 0 ? a[..idx] : a;
    var progArgs = idx >= 0 ? a[(idx + 1)..] : Array.Empty<string>();

    var arguments = ParseRunArguments(cliArgs);
    var (project, target, backendOptions) = await ResolveCompileContextAsync(arguments).ConfigureAwait(false);

    var image = await CompileCliInputAsync(project, arguments.InputFile, arguments.Expr, target, backendOptions).ConfigureAwait(false);
    if (image is null)
    {
        return 1;
    }

    return await RunImageWithInheritedStdioAsync(image, target, progArgs).ConfigureAwait(false);
}

static CompileCommandArguments ParseRunArguments(string[] cliArgs)
{
    string? target = null;
    BackendOptimizationLevel optimizationLevel = BackendCompileOptions.Default.OptimizationLevel;
    bool explicitOpt = false;
    bool debugMode = false;
    string? expr = null;
    string? inputFile = null;
    string? projectPath = null;
    string? targetCpu = null;
    long? parallelStackBytes = null;
    long? parallelWorkers = null;

    for (int i = 0; i < cliArgs.Length; i++)
    {
        var arg = cliArgs[i];
        if (TryParseTargetOptions(cliArgs, ref i, ref target, ref targetCpu, ref parallelStackBytes, ref parallelWorkers)) { continue; }
        if (string.Equals(arg, "--expr", StringComparison.Ordinal) && i + 1 < cliArgs.Length) { expr = cliArgs[++i]; continue; }
        if (string.Equals(arg, "--project", StringComparison.Ordinal) && i + 1 < cliArgs.Length) { projectPath = cliArgs[++i]; continue; }
        if (arg is "--debug" or "-g") { debugMode = true; continue; }
        if (TryParseOptimizationFlag(arg, out var parsedOptimizationLevel)) { optimizationLevel = parsedOptimizationLevel; explicitOpt = true; continue; }

        if (!arg.StartsWith("-", StringComparison.Ordinal) && inputFile is null) { inputFile = arg; continue; }

        throw new CliUsageException("Unknown argument.");
    }

    if (debugMode && !explicitOpt)
    {
        // --debug defaults to -O0 (readable single-stepping); an explicit -O1/-O2/-O3 is honored so a
        // profiled debug build matches the optimized binary's inlining (CO-21).
        optimizationLevel = BackendOptimizationLevel.O0;
    }

    return new CompileCommandArguments(target, optimizationLevel, debugMode, null, expr, inputFile, projectPath, targetCpu, parallelStackBytes, parallelWorkers);
}

async Task<int> RunReplAsync(string[] a)
{
    if (a.Length == 1 && (string.Equals(a[0], "--help", StringComparison.Ordinal) || string.Equals(a[0], "-h", StringComparison.Ordinal)))
    {
        return Usage(0);
    }

    var (target, backendOptions) = ParseReplArguments(a);
    var sessionBindings = new List<ReplBinding>();

    AnsiConsole.Write(new Rule("[bold]Ashes REPL[/]").RuleStyle("grey").LeftJustified());
    AnsiConsole.MarkupLine($"Target: [bold]{target}[/]");
    AnsiConsole.MarkupLine("[grey]Type :help for commands, :quit to exit.[/]");
    AnsiConsole.WriteLine();

    while (true)
    {
        var buffer = new List<string>();

        var first = await ReadReplLineAsync("> ").ConfigureAwait(false);
        if (first is null)
        {
            break;
        }

        var trimmedFirst = first.Trim();
        if (string.IsNullOrEmpty(trimmedFirst))
        {
            continue;
        }

        if (trimmedFirst is ":q" or ":quit" or ":exit")
        {
            break;
        }

        if (trimmedFirst is ":h" or ":help")
        {
            PrintReplHelp();
            continue;
        }

        if (trimmedFirst.StartsWith(":target", StringComparison.Ordinal))
        {
            HandleReplTargetCommand(trimmedFirst, ref target);
            continue;
        }

        buffer.Add(first);

        await ProcessReplSubmissionAsync(sessionBindings, buffer, target, backendOptions).ConfigureAwait(false);
    }

    return 0;
}

static (string Target, BackendCompileOptions Options) ParseReplArguments(string[] a)
{
    string? target = null;
    BackendOptimizationLevel optimizationLevel = BackendCompileOptions.Default.OptimizationLevel;
    string? targetCpu = null;
    long? parallelStackBytes = null;
    long? parallelWorkers = null;

    for (int i = 0; i < a.Length; i++)
    {
        var arg = a[i];
        if (TryParseTargetOptions(a, ref i, ref target, ref targetCpu, ref parallelStackBytes, ref parallelWorkers)) { continue; }
        if (TryParseOptimizationFlag(arg, out var parsedOptimizationLevel)) { optimizationLevel = parsedOptimizationLevel; continue; }
        throw new CliUsageException("Unknown argument.");
    }

    target ??= BackendFactory.DefaultForCurrentOS();
    var backendOptions = new BackendCompileOptions(optimizationLevel, TargetCpu: targetCpu, ParallelWorkerStackBytes: parallelStackBytes, ParallelWorkerCap: parallelWorkers);
    return (target, backendOptions);
}

static void PrintReplHelp()
{
    AnsiConsole.MarkupLine("[grey]REPL commands:[/]");
    AnsiConsole.MarkupLine("  [yellow]:help[/]   Show this help");
    AnsiConsole.MarkupLine("  [yellow]:quit[/]   Exit");
    AnsiConsole.MarkupLine("  [yellow]:target[/] Show current target");
    AnsiConsole.MarkupLine("  [yellow]:target linux-x64|linux-arm64|win-x64|win-arm64[/]  Change target");
    AnsiConsole.MarkupLine("  [yellow]let name = ... in name[/]  Persist a binding in the session");
}

static void HandleReplTargetCommand(string trimmedFirst, ref string target)
{
    var parts = trimmedFirst.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 1)
    {
        AnsiConsole.MarkupLine($"Target: [bold]{target}[/]");
        return;
    }
    if (parts.Length == 2 && TargetIds.IsKnown(parts[1]))
    {
        target = parts[1];
        AnsiConsole.MarkupLine($"Target set to [bold]{target}[/]");
        return;
    }

    AnsiConsole.MarkupLine("[red]Error:[/] Usage: :target linux-x64|linux-arm64|win-x64|win-arm64");
}

static async Task ProcessReplSubmissionAsync(List<ReplBinding> sessionBindings, List<string> buffer, string target, BackendCompileOptions backendOptions)
{
    while (true)
    {
        var candidate = string.Join("\n", buffer).TrimEnd();

        if (LooksIncomplete(candidate))
        {
            var more = await ReadReplLineAsync("| ").ConfigureAwait(false);
            if (more is null)
            {
                break;
            }

            buffer.Add(more);
            continue;
        }

        if (TryAnalyzeReplSubmission(sessionBindings, candidate, out var analysis, out var compileDiagnostics, out var compileError))
        {
            await ExecuteReplSubmissionAsync(sessionBindings, candidate, analysis!, target, backendOptions).ConfigureAwait(false);
            break;
        }

        if (compileError is not null && IsLikelyNeedMoreInput(compileError))
        {
            var more = await ReadReplLineAsync("| ").ConfigureAwait(false);
            if (more is null)
            {
                break;
            }

            buffer.Add(more);
            continue;
        }

        if (compileDiagnostics is not null)
        {
            PrintCompilerDiagnostics(compileDiagnostics, null, "<repl>");
        }
        else
        {
            PrintCompileFailure(compileError ?? "Unknown compile error", "<repl>");
        }

        break;
    }
}

static async Task ExecuteReplSubmissionAsync(List<ReplBinding> sessionBindings, string candidate, ReplSubmissionAnalysis analysis, string target, BackendCompileOptions backendOptions)
{
    var isBindingSubmission = TryExtractPersistentBinding(candidate, out var persistedBinding);
    if (!TryCompileReplSubmission(sessionBindings, candidate, autoPrint: analysis.IsPrintable && !isBindingSubmission, target, backendOptions, out var image, out var compileDiagnostics, out var compileError))
    {
        if (compileDiagnostics is not null)
        {
            PrintCompilerDiagnostics(compileDiagnostics, null, "<repl>");
        }
        else
        {
            PrintCompileFailure(compileError ?? "Unknown compile error", "<repl>");
        }

        return;
    }

    var (exit, stdout, stderr) = await RunImageCaptureAsync(image!, target).ConfigureAwait(false);

    if (exit == 0)
    {
        if (!string.IsNullOrEmpty(stdout))
        {
            AnsiConsole.Write(new Text(stdout));
        }

        if (!string.IsNullOrEmpty(stderr))
        {
            AnsiConsole.Write(new Text(stderr));
        }

        if (isBindingSubmission)
        {
            sessionBindings.Add(persistedBinding);
        }

        PrintReplTypeEcho(analysis);

        return;
    }

    PrintRuntimeFailure(exit, stdout, stderr);
}

async Task<int> RunTest(string[] a)
{
    if (a.Length == 1 && (string.Equals(a[0], "--help", StringComparison.Ordinal) || string.Equals(a[0], "-h", StringComparison.Ordinal)))
    {
        return Usage(0);
    }

    // ashes test [--project ...] [--target ...] [paths...]
    string? target = null;
    BackendOptimizationLevel optimizationLevel = BackendCompileOptions.Default.OptimizationLevel;
    string? projectPath = null;
    string? targetCpu = null;
    long? parallelStackBytes = null;
    long? parallelWorkers = null;
    var paths = new List<string>();

    for (int i = 0; i < a.Length; i++)
    {
        var arg = a[i];
        if (string.Equals(arg, "--target", StringComparison.Ordinal) && i + 1 < a.Length) { target = a[++i]; continue; }
        if (string.Equals(arg, "--target-cpu", StringComparison.Ordinal) && i + 1 < a.Length) { targetCpu = a[++i]; continue; }
        if (string.Equals(arg, "--parallel-stack-size", StringComparison.Ordinal) && i + 1 < a.Length) { parallelStackBytes = ParseParallelStackSize(a[++i]); continue; }
        if (string.Equals(arg, "--parallel-workers", StringComparison.Ordinal) && i + 1 < a.Length) { parallelWorkers = ParseParallelWorkers(a[++i]); continue; }
        if (string.Equals(arg, "--project", StringComparison.Ordinal) && i + 1 < a.Length) { projectPath = a[++i]; continue; }
        if (TryParseOptimizationFlag(arg, out var parsedOptimizationLevel)) { optimizationLevel = parsedOptimizationLevel; continue; }
        if (arg.StartsWith("-", StringComparison.Ordinal))
        {
            throw new CliUsageException("Unknown argument.");
        }

        paths.Add(arg);
    }

    await AutoRestoreProjectAsync(projectPath, null, null).ConfigureAwait(false);
    var project = ResolveProject(projectPath, null, null);
    target ??= project?.Target ?? BackendFactory.DefaultForCurrentOS();
    var backendOptions = new BackendCompileOptions(optimizationLevel, TargetCpu: targetCpu, ParallelWorkerStackBytes: parallelStackBytes, ParallelWorkerCap: parallelWorkers);

    return Runner.RunTests(paths, target, AnsiConsole.Console, project, backendOptions);
}

async Task<int> RunFmtAsync(string[] a)
{
    if (a.Length == 1 && (string.Equals(a[0], "--help", StringComparison.Ordinal) || string.Equals(a[0], "-h", StringComparison.Ordinal)))
    {
        return Usage(0);
    }

    var (writeInPlace, files) = ParseFmtArguments(a);
    if (files.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No .ash files found.[/]");
        return 0;
    }

    var sw = Stopwatch.StartNew();
    foreach (var file in files)
    {
        await FormatSingleFileAsync(file, writeInPlace, files.Count).ConfigureAwait(false);
    }
    sw.Stop();

    if (writeInPlace)
    {
        AnsiConsole.MarkupLine($"[green]OK[/] Formatted {files.Count} file(s) in [bold]{Runner.FormatElapsed(sw.ElapsedMilliseconds)}[/].");
    }

    return 0;
}

static (bool WriteInPlace, List<string> Files) ParseFmtArguments(string[] a)
{
    // ashes fmt <file|dir> [-w]
    if (a.Length == 0)
    {
        throw new CliUserException("Missing file or directory.");
    }

    var writeInPlace = a.Contains("-w", StringComparer.Ordinal) || a.Contains("--write", StringComparer.Ordinal);
    var targets = a.Where(x => !string.Equals(x, "-w", StringComparison.Ordinal) && !string.Equals(x, "--write", StringComparison.Ordinal)).ToArray();
    if (targets.Length != 1)
    {
        throw new CliUsageException("Provide exactly one file or directory.");
    }

    var path = targets[0];

    var files = new List<string>();
    if (File.Exists(path))
    {
        if (!path.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliUserException("Input file must be .ash");
        }

        files.Add(path);
    }
    else if (Directory.Exists(path))
    {
        files.AddRange(Directory.EnumerateFiles(path, "*.ash", SearchOption.AllDirectories));
    }
    else
    {
        throw new CliUserException($"Path not found: {path}");
    }

    files = files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    return (writeInPlace, files);
}

static async Task FormatSingleFileAsync(string file, bool writeInPlace, int fileCount)
{
    var src = await File.ReadAllTextAsync(file).ConfigureAwait(false);
    // Inline `module` blocks are a compile-time stitching construct with no AST node, so the
    // formatter cannot model them. Leave such files untouched (the author's layout is
    // authoritative) rather than error or mangle; full formatting fidelity is future work.
    if (ProjectSupport.ContainsInlineModule(src))
    {
        if (!writeInPlace)
        {
            if (fileCount > 1)
            {
                AnsiConsole.Write(new Rule(file).RuleStyle("grey").LeftJustified());
            }

            AnsiConsole.Write(new Text(src));
        }

        return;
    }

    var formatted = FormatAshSource(src, file);

    if (writeInPlace)
    {
        if (!string.Equals(formatted, src, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(file, formatted).ConfigureAwait(false);
        }
    }
    else
    {
        if (fileCount > 1)
        {
            AnsiConsole.Write(new Rule(file).RuleStyle("grey").LeftJustified());
        }

        AnsiConsole.Write(new Text(formatted));
    }
}

static string FormatAshSource(string src, string file)
{
    var formattingOptions = EditorConfigFormattingOptionsResolver.ResolveForPath(file);
    var lineEnding = formattingOptions.NewLine;
    var (leadingComments, sourceWithoutComments) = ExtractLeadingComments(src, lineEnding);
    var (imports, sourceWithoutImports) = ExtractImports(sourceWithoutComments, file, lineEnding);
    var diag = new Diagnostics();
    var program = new Parser(sourceWithoutImports, diag).ParseProgram();
    diag.ThrowIfAny();

    var formattedBody = Formatter.Format(
        program,
        preferPipelines: sourceWithoutImports.Contains("|>", StringComparison.Ordinal)
            || sourceWithoutImports.Contains("|?>", StringComparison.Ordinal)
            || sourceWithoutImports.Contains("|!>", StringComparison.Ordinal),
        options: formattingOptions);
    // The AST carries no trivia, so the formatter alone would drop every non-leading comment;
    // reinsert standalone comment lines at their anchored positions (same as LSP formatting).
    formattedBody = CommentReinserter.ReinsertStandaloneCommentLines(sourceWithoutImports, formattedBody, lineEnding);
    var formattedWithoutComments = imports.Count == 0
        ? formattedBody
        : string.Join(lineEnding, imports) + lineEnding + formattedBody;
    return leadingComments.Count == 0
        ? formattedWithoutComments
        : string.Join(lineEnding, leadingComments) + lineEnding + formattedWithoutComments;
}

static (IReadOnlyList<string> LeadingComments, string SourceWithoutLeadingComments) ExtractLeadingComments(string source, string lineEnding)
{
    var leadingComments = new List<string>();
    using var reader = new StringReader(source);
    string? line;
    bool inLeadingCommentsSection = true;
    var remainingLines = new List<string>();
    while ((line = reader.ReadLine()) is not null)
    {
        if (inLeadingCommentsSection && line.StartsWith("//", StringComparison.Ordinal))
        {
            leadingComments.Add(line);
            continue;
        }

        if (inLeadingCommentsSection && string.IsNullOrWhiteSpace(line))
        {
            leadingComments.Add(line);
            continue;
        }

        inLeadingCommentsSection = false;
        remainingLines.Add(line);
    }

    return (leadingComments, string.Join(lineEnding, remainingLines));
}

static (IReadOnlyList<string> Imports, string SourceWithoutImports) ExtractImports(string source, string filePath, string lineEnding)
{
    var importLine = new Regex(
        ProjectSupport.ImportModulePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1)
    );
    var imports = new List<string>();
    var sourceLines = new List<string>();
    using var reader = new StringReader(source);
    string? line;
    var lineIndex = 0;
    while ((line = reader.ReadLine()) is not null)
    {
        lineIndex++;
        var match = importLine.Match(line);
        if (match.Success)
        {
            // Groups: 1 = module path, 2 = lowercase selector binding (the `.name`),
            // 3 = `as` alias. A selector keeps its `.name`; any form may carry an alias.
            var rendered = match.Groups[2].Success
                ? $"import {match.Groups[1].Value}.{match.Groups[2].Value}"
                : $"import {match.Groups[1].Value}";
            if (match.Groups[3].Success)
            {
                rendered += $" as {match.Groups[3].Value}";
            }

            imports.Add(rendered);
            continue;
        }

        if (line.TrimStart().StartsWith("import ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invalid import syntax in {filePath}:{lineIndex}. Expected 'import Foo' or 'import Foo.Bar' or 'import Foo.Bar as Alias'.");
        }

        sourceLines.Add(line);
    }

    return (imports, string.Join(lineEnding, sourceLines));
}

static void PrintCompilerDiagnostics(CompileDiagnosticException ex, string? source, string displayPath, CombinedCompilationLayout? layout = null)
{
    // Compilation runs on the combined (stitched) source, so diagnostic spans are offsets into it.
    // With the layout at hand, map them back to the entry file's own coordinates before rendering;
    // spans inside stitched module regions render header-only, attributed to the owning file.
    if (layout is { } combinedLayout && source is not null)
    {
        string stripped;
        try
        {
            stripped = ProjectSupport.ParseImportHeader(source, displayPath).SourceWithoutImports;
        }
        catch (InvalidOperationException)
        {
            stripped = source;
        }

        var mapped = ProjectSupport.MapDiagnosticsToOriginal(combinedLayout, ex.StructuredErrors, displayPath, source, stripped)
            .OrderBy(m => m.HasPosition ? 0 : 1)
            .ThenBy(m => m.Entry.Start)
            .ToArray();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < mapped.Length; i++)
        {
            sb.Append(DiagnosticTextRenderer.RenderCompilerDiagnostics(
                [mapped[i].Entry],
                mapped[i].HasPosition ? source : null,
                mapped[i].FilePath));
            if (i < mapped.Length - 1)
            {
                sb.AppendLine();
            }
        }

        Console.Error.Write(sb.ToString());
        return;
    }

    Console.Error.Write(DiagnosticTextRenderer.RenderCompilerDiagnostics(ex, source, displayPath));
}

static void PrintCompileFailure(string message, string displayPath)
{
    Console.Error.Write(DiagnosticTextRenderer.RenderFailure("compile error", message, displayPath));
}

static void PrintRuntimeFailure(int exitCode, string stdout, string stderr)
{
    var sb = new System.Text.StringBuilder();
    sb.Append("runtime error: process exited with code ");
    sb.AppendLine(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

    if (!string.IsNullOrWhiteSpace(stdout))
    {
        sb.AppendLine("stdout:");
        sb.AppendLine(stdout.TrimEnd());
    }

    if (!string.IsNullOrWhiteSpace(stderr))
    {
        sb.AppendLine("stderr:");
        sb.AppendLine(stderr.TrimEnd());
    }

    AnsiConsole.Write(new Text(sb.ToString()));
}

static System.Text.Json.JsonSerializerOptions CreateProjectJsonOptions() => new()
{
    WriteIndented = true,
    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
};

static (Dictionary<string, object?> Fields, Dictionary<string, object?> Dependencies) ReadProjectJson(System.Text.Json.JsonElement root)
{
    var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
    foreach (var prop in root.EnumerateObject())
    {
        if (string.Equals(prop.Name, "dependencies", StringComparison.Ordinal))
        {
            continue;
        }

        obj[prop.Name] = DeserializeJsonElement(prop.Value);
    }

    var deps = new Dictionary<string, object?>(StringComparer.Ordinal);
    if (root.TryGetProperty("dependencies", out var existingDeps) && existingDeps.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        foreach (var dep in existingDeps.EnumerateObject())
        {
            deps[dep.Name] = DeserializeJsonElement(dep.Value);
        }
    }

    return (obj, deps);
}

static System.Text.Json.JsonDocument ParseProjectJson(string projectFilePath)
{
    var text = File.ReadAllText(projectFilePath);
    try
    {
        var doc = System.Text.Json.JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            doc.Dispose();
            throw new CliUserException($"Invalid ashes.json ({projectFilePath}): root must be a JSON object.");
        }

        return doc;
    }
    catch (System.Text.Json.JsonException ex)
    {
        throw new CliUserException($"Invalid ashes.json ({projectFilePath}): {ex.Message}");
    }
}

static void WriteProjectJson(string projectFilePath, Dictionary<string, object?> obj)
{
    var json = System.Text.Json.JsonSerializer.Serialize(obj, CreateProjectJsonOptions());
    File.WriteAllText(projectFilePath, json + Environment.NewLine);
}

static int RunInit(string[] a)
{
    if (a.Length == 1 && (string.Equals(a[0], "--help", StringComparison.Ordinal) || string.Equals(a[0], "-h", StringComparison.Ordinal)))
    {
        return Usage(0);
    }

    if (a.Length > 0)
    {
        throw new CliUsageException("Unknown argument.");
    }

    var cwd = Directory.GetCurrentDirectory();
    var projectFilePath = Path.Combine(cwd, "ashes.json");

    if (File.Exists(projectFilePath))
    {
        throw new CliUserException("ashes.json already exists in this directory.");
    }

    var projectName = new DirectoryInfo(cwd).Name;
    var entryRelative = Path.Combine("src", "Main.ash");

    var projectJson = new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["name"] = projectName,
        ["entry"] = entryRelative.Replace('\\', '/'),
        ["sourceRoots"] = new[] { "src" }
    };

    var json = System.Text.Json.JsonSerializer.Serialize(projectJson, CreateProjectJsonOptions());
    File.WriteAllText(projectFilePath, json + Environment.NewLine);
    AnsiConsole.MarkupLine("[green]Created[/] ashes.json");

    var srcDir = Path.Combine(cwd, "src");
    Directory.CreateDirectory(srcDir);

    var mainPath = Path.Combine(srcDir, "Main.ash");
    if (!File.Exists(mainPath))
    {
        File.WriteAllText(mainPath, "Ashes.IO.print(\"hello, ashes!\")" + "\n");
        AnsiConsole.MarkupLine("[green]Created[/] src/Main.ash");
    }

    return 0;
}

static int RunAdd(string[] a)
{
    var opts = Ashes.Cli.Registry.ArgScanner.Parse(a);
    if (opts.Flag("help"))
    {
        return Usage(0);
    }

    if (opts.Positionals.Count == 0)
    {
        throw new CliUserException("Missing package name.");
    }

    var packageName = opts.Positionals[0];
    var path = opts.Value("path");
    var isDev = opts.Flag("dev");
    var field = isDev ? "devDependencies" : "dependencies";

    var projectFilePath = ProjectSupport.DiscoverProjectFile(Directory.GetCurrentDirectory());
    if (string.IsNullOrEmpty(projectFilePath))
    {
        throw new CliUserException("No ashes.json found. Run 'ashes init' first.");
    }

    using var doc = ParseProjectJson(projectFilePath);
    var (obj, deps) = ReadProjectJson(doc.RootElement);

    // A path dependency writes an object value; a registry dependency writes a SemVer string (default *).
    object value = path is not null
        ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["path"] = path.Replace('\\', '/') }
        : "*";

    // `dependencies` is returned separately by ReadProjectJson; `devDependencies` stays inside `obj`.
    var map = isDev ? GetOrCreateDependencyMap(obj, "devDependencies") : deps;
    map[packageName] = value;
    obj[field] = map;

    WriteProjectJson(projectFilePath, obj);

    AnsiConsole.MarkupLine($"[green]Added[/] {Markup.Escape(packageName)} to {field}.");
    return 0;
}

static Dictionary<string, object?> GetOrCreateDependencyMap(Dictionary<string, object?> obj, string field)
{
    if (obj.TryGetValue(field, out var existing) && existing is Dictionary<string, object?> map)
    {
        return map;
    }

    var created = new Dictionary<string, object?>(StringComparer.Ordinal);
    obj[field] = created;
    return created;
}

static int RunRemove(string[] a)
{
    var opts = Ashes.Cli.Registry.ArgScanner.Parse(a);
    if (opts.Flag("help"))
    {
        return Usage(0);
    }

    if (opts.Positionals.Count == 0)
    {
        throw new CliUserException("Missing package name.");
    }

    var packageName = opts.Positionals[0];

    var projectFilePath = ProjectSupport.DiscoverProjectFile(Directory.GetCurrentDirectory());
    if (string.IsNullOrEmpty(projectFilePath))
    {
        throw new CliUserException("No ashes.json found. Run 'ashes init' first.");
    }

    using var doc = ParseProjectJson(projectFilePath);
    var (obj, deps) = ReadProjectJson(doc.RootElement);

    var removed = deps.Remove(packageName);
    if (obj.TryGetValue("devDependencies", out var dev) && dev is Dictionary<string, object?> devMap)
    {
        removed |= devMap.Remove(packageName);
        if (devMap.Count == 0)
        {
            obj.Remove("devDependencies");
        }
    }

    if (!removed)
    {
        throw new CliUserException($"Package '{packageName}' is not a dependency.");
    }

    if (deps.Count > 0)
    {
        obj["dependencies"] = deps;
    }

    WriteProjectJson(projectFilePath, obj);

    AnsiConsole.MarkupLine($"[green]Removed[/] {Markup.Escape(packageName)}.");
    return 0;
}

static async Task<int> RunRestore(string[] a)
{
    var opts = Ashes.Cli.Registry.ArgScanner.Parse(a);
    if (opts.Flag("help"))
    {
        return Usage(0);
    }

    var projectFilePath = ProjectSupport.DiscoverProjectFile(Directory.GetCurrentDirectory());
    if (string.IsNullOrEmpty(projectFilePath))
    {
        throw new CliUserException("No ashes.json found. Run 'ashes init' first.");
    }

    var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

    // Registry dependencies resolve, download, and cache first (writing ashes.lock) so that the
    // subsequent project load — which reads the lock — finds them materialized.
    await RestoreRegistryDependenciesAsync(
        projectFilePath, projectDirectory, opts.Value("registry"), opts.Flag("frozen"), opts.Flag("offline"), CancellationToken.None)
        .ConfigureAwait(false);

    // Loading resolves + validates path dependencies and now-cached registry dependencies.
    var project = ProjectSupport.LoadProject(projectFilePath);

    if (project.Dependencies.Count == 0)
    {
        AnsiConsole.MarkupLine("[grey]No dependencies to restore.[/]");
    }
    else
    {
        AnsiConsole.MarkupLine($"[green]Restored[/] {project.Dependencies.Count} dependenc{(project.Dependencies.Count == 1 ? "y" : "ies")}:");
        foreach (var d in project.Dependencies)
        {
            var dev = d.IsDev ? " [grey](dev)[/]" : "";
            AnsiConsole.MarkupLine($"  {Markup.Escape(d.Name)} [grey]->[/] {Markup.Escape(d.Namespace)} [grey]({Markup.Escape(d.ProjectDirectory)})[/]{dev}");
        }
    }

    return 0;
}

static int RunTree(string[] a)
{
    var opts = Ashes.Cli.Registry.ArgScanner.Parse(a);
    if (opts.Flag("help"))
    {
        return Usage(0);
    }

    var manifestPath = ProjectSupport.DiscoverProjectFile(Directory.GetCurrentDirectory())
        ?? throw new CliUserException("No ashes.json found. Run 'ashes init' first.");
    var projectDirectory = Path.GetDirectoryName(manifestPath)!;
    var project = ProjectSupport.LoadProject(manifestPath);

    var (edges, versions) = ReadLockGraph(projectDirectory);
    var namespaceByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in project.Dependencies)
    {
        namespaceByDir[d.ProjectDirectory] = d.Namespace;
    }

    var tree = new Tree($"[bold]{Markup.Escape(project.Name ?? "project")}[/]");
    foreach (var (ns, isPath) in DirectDependencyNamespaces(manifestPath, projectDirectory, namespaceByDir))
    {
        var suffix = isPath ? " [grey](path)[/]" : $" [grey]{Markup.Escape(versions.GetValueOrDefault(ns, "?"))}[/]";
        var node = tree.AddNode(Markup.Escape(ns) + suffix);
        AddLockChildren(node, ns, edges, versions, [ns]);
    }

    AnsiConsole.Write(tree);
    return 0;
}

static int RunWhy(string[] a)
{
    var opts = Ashes.Cli.Registry.ArgScanner.Parse(a);
    if (opts.Flag("help"))
    {
        return Usage(0);
    }

    if (opts.Positionals.Count == 0)
    {
        throw new CliUsageException("Usage: ashes why <namespace>");
    }

    var target = ProjectSupport.PascalCase(opts.Positionals[0]);
    var manifestPath = ProjectSupport.DiscoverProjectFile(Directory.GetCurrentDirectory())
        ?? throw new CliUserException("No ashes.json found. Run 'ashes init' first.");
    var projectDirectory = Path.GetDirectoryName(manifestPath)!;
    var project = ProjectSupport.LoadProject(manifestPath);

    var (edges, _) = ReadLockGraph(projectDirectory);
    var namespaceByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in project.Dependencies)
    {
        namespaceByDir[d.ProjectDirectory] = d.Namespace;
    }

    var roots = DirectDependencyNamespaces(manifestPath, projectDirectory, namespaceByDir).Select(x => x.Namespace).ToList();
    var path = FindPath(roots, target, edges);
    if (path is null)
    {
        AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(target)}' is not a dependency of this project.[/]");
        return 0;
    }

    AnsiConsole.MarkupLine(string.Join(" [grey]->[/] ", path.Select(Markup.Escape)));
    return 0;
}

static (Dictionary<string, IReadOnlyList<string>> Edges, Dictionary<string, string> Versions) ReadLockGraph(string projectDirectory)
{
    var lockFile = Ashes.Cli.Package.LockFile.Read(projectDirectory);
    var edges = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
    var versions = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var p in lockFile?.Package ?? [])
    {
        edges[p.Namespace] = p.Dependencies;
        versions[p.Namespace] = p.Version;
    }

    return (edges, versions);
}

static IEnumerable<(string Namespace, bool IsPath)> DirectDependencyNamespaces(
    string manifestPath, string projectDirectory, Dictionary<string, string> namespaceByDir)
{
    using var doc = ParseProjectJson(manifestPath);
    foreach (var field in (string[])["dependencies", "devDependencies"])
    {
        if (!doc.RootElement.TryGetProperty(field, out var deps) || deps.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            continue;
        }

        foreach (var dep in deps.EnumerateObject())
        {
            if (dep.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                dep.Value.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var depDir = Path.GetFullPath(Path.Combine(projectDirectory, pathEl.GetString()!));
                yield return (namespaceByDir.GetValueOrDefault(depDir, ProjectSupport.PascalCase(dep.Name)), true);
            }
            else
            {
                yield return (ProjectSupport.PascalCase(dep.Name), false);
            }
        }
    }
}

static void AddLockChildren(
    TreeNode node, string ns, Dictionary<string, IReadOnlyList<string>> edges, Dictionary<string, string> versions, HashSet<string> ancestors)
{
    if (!edges.TryGetValue(ns, out var children))
    {
        return;
    }

    foreach (var child in children)
    {
        if (ancestors.Contains(child))
        {
            node.AddNode(Markup.Escape(child) + " [grey](cycle)[/]");
            continue;
        }

        var childNode = node.AddNode(Markup.Escape(child) + $" [grey]{Markup.Escape(versions.GetValueOrDefault(child, "?"))}[/]");
        AddLockChildren(childNode, child, edges, versions, [.. ancestors, child]);
    }
}

static IReadOnlyList<string>? FindPath(IReadOnlyList<string> roots, string target, Dictionary<string, IReadOnlyList<string>> edges)
{
    var queue = new Queue<List<string>>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var root in roots)
    {
        queue.Enqueue([root]);
    }

    while (queue.Count > 0)
    {
        var path = queue.Dequeue();
        var last = path[^1];
        if (string.Equals(last, target, StringComparison.Ordinal))
        {
            return path;
        }

        if (!seen.Add(last) || !edges.TryGetValue(last, out var children))
        {
            continue;
        }

        foreach (var child in children)
        {
            queue.Enqueue([.. path, child]);
        }
    }

    return null;
}

static async Task RestoreRegistryDependenciesAsync(
    string manifestPath, string projectDirectory, string? registryOption, bool frozen, bool offline, CancellationToken ct)
{
    var roots = CollectRegistryRoots(manifestPath);
    if (roots.Count == 0)
    {
        return;
    }

    var cache = new Ashes.Cli.Package.PackageCache();

    // --offline: never touch the network. Trust the committed lock and only verify its packages are cached.
    if (offline)
    {
        VerifyOfflineRegistryCache(projectDirectory, cache);
        return;
    }

    var baseUrl = Ashes.Cli.Registry.RegistryConfig.ResolveBaseUrl(registryOption);
    using var client = new Ashes.Cli.Registry.RegistryClient();
    var resolved = await new Ashes.Cli.Package.DependencyResolver(new Ashes.Cli.Package.RegistryPackageIndex(client, baseUrl))
        .ResolveAsync(roots, ct).ConfigureAwait(false);

    var pinned = await PinResolvedVersionsAsync(client, baseUrl, resolved, ct).ConfigureAwait(false);

    var locked = pinned
        .Select(p => new Ashes.Cli.Package.LockedPackage(
            p.Namespace, p.Version.Version, $"registry+{baseUrl}", p.Version.Hash, p.Version.Dependencies.Select(d => d.Namespace).ToList()))
        .OrderBy(p => p.Namespace, StringComparer.Ordinal)
        .ToList();

    // --frozen: fail if a fresh resolution would change the committed lock.
    if (frozen)
    {
        var existing = Ashes.Cli.Package.LockFile.Read(projectDirectory);
        if (existing is null || !SameLock(existing.Package, locked))
        {
            throw new CliUserException("ASH033: dependency resolution differs from ashes.lock (--frozen).");
        }
    }

    await DownloadAndVerifyRegistryPackagesAsync(cache, client, baseUrl, pinned, ct).ConfigureAwait(false);

    if (!frozen)
    {
        new Ashes.Cli.Package.LockFile { Package = locked }.Write(projectDirectory);
    }

    AnsiConsole.MarkupLine($"[green]Resolved[/] {locked.Count} registry dependenc{(locked.Count == 1 ? "y" : "ies")}{(frozen ? " (frozen)" : " into ashes.lock")}.");
}

static void VerifyOfflineRegistryCache(string projectDirectory, Ashes.Cli.Package.PackageCache cache)
{
    var existing = Ashes.Cli.Package.LockFile.Read(projectDirectory)
        ?? throw new CliUserException("Cannot restore --offline: no ashes.lock. Run 'ashes restore' online first.");
    foreach (var p in existing.Package)
    {
        if (!cache.Has(p.Namespace, p.Version, p.Hash))
        {
            throw new CliUserException($"ASH033: '{p.Namespace}@{p.Version}' is not in the cache (--offline).");
        }

        VerifyCacheHash(cache.PathFor(p.Namespace, p.Version, p.Hash), p.Hash, p.Namespace, p.Version);
    }
}

static async Task<List<(string Namespace, Ashes.Cli.Registry.VersionDto Version)>> PinResolvedVersionsAsync(
    Ashes.Cli.Registry.RegistryClient client, string baseUrl, IReadOnlyList<Ashes.Cli.Package.ResolvedPackage> resolved, CancellationToken ct)
{
    // Resolve version metadata (hashes/deps) before downloading, so --frozen can compare cheaply.
    var pinned = new List<(string Namespace, Ashes.Cli.Registry.VersionDto Version)>();
    foreach (var package in resolved)
    {
        var meta = await client.GetPackageAsync(baseUrl, package.Namespace, ct).ConfigureAwait(false)
            ?? throw new CliUserException($"Package '{package.Namespace}' not found on {baseUrl}.");
        pinned.Add((package.Namespace, meta.Versions.First(v => string.Equals(v.Version, package.Version.ToString(), StringComparison.Ordinal))));
    }

    return pinned;
}

static async Task DownloadAndVerifyRegistryPackagesAsync(
    Ashes.Cli.Package.PackageCache cache, Ashes.Cli.Registry.RegistryClient client, string baseUrl, List<(string Namespace, Ashes.Cli.Registry.VersionDto Version)> pinned, CancellationToken ct)
{
    foreach (var (ns, version) in pinned)
    {
        if (!cache.Has(ns, version.Version, version.Hash))
        {
            var tarball = await client.DownloadSourceAsync(baseUrl, ns, version.Version, ct).ConfigureAwait(false);
            await cache.StoreAsync(ns, version.Version, version.Hash, tarball, ct).ConfigureAwait(false);
        }

        // The registry computed this hash at publish; verifying the cached tree catches a corrupt cache
        // or a lying mirror before the compiler ever reads it.
        VerifyCacheHash(cache.PathFor(ns, version.Version, version.Hash), version.Hash, ns, version.Version);
    }
}

static void VerifyCacheHash(string cacheDir, string expectedHash, string ns, string version)
{
    var actual = Ashes.Cli.Package.PackageCache.ComputeTreeHash(cacheDir);
    if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
    {
        throw new CliUserException(
            $"ASH034: cached content of '{ns}@{version}' does not match its lock hash (expected {expectedHash}, got {actual}).");
    }
}

static bool SameLock(IReadOnlyList<Ashes.Cli.Package.LockedPackage> a, IReadOnlyList<Ashes.Cli.Package.LockedPackage> b)
{
    if (a.Count != b.Count)
    {
        return false;
    }

    var left = a.OrderBy(p => p.Namespace, StringComparer.Ordinal).ToList();
    var right = b.OrderBy(p => p.Namespace, StringComparer.Ordinal).ToList();
    for (var i = 0; i < left.Count; i++)
    {
        if (!string.Equals(left[i].Namespace, right[i].Namespace, StringComparison.Ordinal) ||
            !string.Equals(left[i].Version, right[i].Version, StringComparison.Ordinal) ||
            !string.Equals(left[i].Hash, right[i].Hash, StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static List<Ashes.Cli.Package.DependencyReq> CollectRegistryRoots(string manifestPath)
{
    // Registry constraints come from the root manifest AND every transitive path dependency's manifest,
    // so a source dependency's own registry deps are resolved into the unified lock.
    var roots = new List<Ashes.Cli.Package.DependencyReq>();
    CollectRegistryRootsRecursive(Path.GetFullPath(manifestPath), roots, new HashSet<string>(StringComparer.OrdinalIgnoreCase), topLevel: true);
    return roots;
}

static void CollectRegistryRootsRecursive(
    string manifestPath, List<Ashes.Cli.Package.DependencyReq> roots, HashSet<string> visited, bool topLevel)
{
    if (!File.Exists(manifestPath))
    {
        return;
    }

    var dir = Path.GetDirectoryName(manifestPath)!;
    using var doc = ParseProjectJson(manifestPath);

    // A dependency's devDependencies are not transitive, so only the top-level manifest follows them.
    foreach (var field in topLevel ? (string[])["dependencies", "devDependencies"] : ["dependencies"])
    {
        if (!doc.RootElement.TryGetProperty(field, out var deps) || deps.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            continue;
        }

        foreach (var dep in deps.EnumerateObject())
        {
            string? constraint = null;
            if (dep.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                constraint = dep.Value.GetString();
            }
            else if (dep.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (dep.Value.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var depDir = Path.GetFullPath(Path.Combine(dir, pathEl.GetString()!));
                    if (visited.Add(depDir))
                    {
                        CollectRegistryRootsRecursive(Path.Combine(depDir, "ashes.json"), roots, visited, topLevel: false);
                    }

                    continue;
                }

                if (dep.Value.TryGetProperty("git", out _))
                {
                    continue; // git dependencies are resolved separately
                }

                if (dep.Value.TryGetProperty("version", out var ver) && ver.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    constraint = ver.GetString();
                }
            }

            if (constraint is not null)
            {
                roots.Add(new Ashes.Cli.Package.DependencyReq(
                    ProjectSupport.PascalCase(dep.Name), Ashes.Cli.Package.VersionConstraint.Parse(constraint), "manifest"));
            }
        }
    }
}

static object? DeserializeJsonElement(System.Text.Json.JsonElement element)
{
    return element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number => element.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null => null,
        System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(DeserializeJsonElement).ToArray(),
        System.Text.Json.JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => DeserializeJsonElement(p.Value), StringComparer.Ordinal),
        _ => element.GetRawText()
    };
}

static int RunVersion()
{
    // Version is set at publish time via -p:Version=<tag>.
    // Local development builds that do not pass -p:Version will report "0.0.0" by design.
    var version = System.Reflection.Assembly.GetEntryAssembly()!
        .GetName()
        .Version
        ?.ToString(3) ?? "0.0.0";
    Console.WriteLine(version);
    return 0;
}

sealed class CliUsageException(string message) : Exception(message);
sealed class CliUserException(string message) : Exception(message);

sealed record CompileCommandArguments(
    string? Target,
    BackendOptimizationLevel OptimizationLevel,
    bool DebugMode,
    string? OutPath,
    string? Expr,
    string? InputFile,
    string? ProjectPath,
    string? TargetCpu,
    long? ParallelStackBytes,
    long? ParallelWorkers);
