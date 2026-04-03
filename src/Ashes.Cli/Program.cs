using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ashes.Backend.Backends;
using Ashes.Formatter;
using Ashes.Frontend;
using Ashes.Semantics;
using Ashes.TestRunner;
using Spectre.Console;

static int Usage(int exitCode = 2)
{
    AnsiConsole.Write(new Rule("[bold]Ashes[/]").RuleStyle("grey").LeftJustified());
    AnsiConsole.MarkupLine("[grey]Commands:[/]");
    AnsiConsole.MarkupLine("  [bold]ashes compile[/] [[--project <ashes.json>]] [[--target linux-x64|windows-x64]] [[-O0|-O1|-O2|-O3]] <input.ash | --expr \"...\" > [[-o <output>]]");
    AnsiConsole.MarkupLine("  [bold]ashes run[/]     [[--project <ashes.json>]] [[--target linux-x64|windows-x64]] [[-O0|-O1|-O2|-O3]] <input.ash | --expr \"...\" > [[-- <args...>]]");
    AnsiConsole.MarkupLine("  [bold]ashes repl[/]    [[--target linux-x64|windows-x64]] [[-O0|-O1|-O2|-O3]]");
    AnsiConsole.MarkupLine("  [bold]ashes test[/]    [[--project <ashes.json>]] [[--target linux-x64|windows-x64]] [[-O0|-O1|-O2|-O3]] [[paths...]]");
    AnsiConsole.MarkupLine("  [bold]ashes fmt[/]     <file|dir> [[-w]]");
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

    var outName = targetId == TargetIds.WindowsX64 ? name + ".exe" : name;
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

static string DeriveProjectOutputPath(AshesProject project, string targetId)
{
    var outputName = !string.IsNullOrWhiteSpace(project.Name)
        ? project.Name!
        : Path.GetFileNameWithoutExtension(project.EntryPath);

    if (targetId == TargetIds.WindowsX64)
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

    return await File.ReadAllTextAsync(inputFile);
}

static byte[] CompileToImage(string source, string targetId, BackendCompileOptions? backendOptions = null, IReadOnlySet<string>? importedStdModules = null)
{
    var diag = new Diagnostics();
    var program = new Parser(source, diag).ParseProgram();
    diag.ThrowIfAny();

    var ir = new Lowering(diag, importedStdModules).Lower(program);
    diag.ThrowIfAny();

    var effectiveOptions = backendOptions ?? BackendCompileOptions.Default;
    var backend = BackendFactory.Create(targetId);
    return backend.Compile(ir, effectiveOptions);
}

static (string Source, IReadOnlySet<string>? ImportedStdModules) PrepareStandaloneCompilationSource(string source, string displayPath)
{
    var parsed = ProjectSupport.ParseImportHeader(source, displayPath);
    var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
    var importedStdModules = parsed.ImportNames
        .Where(ProjectSupport.IsStdModule)
        .ToHashSet(StringComparer.Ordinal);

    return (layout.Source, importedStdModules.Count == 0 ? null : importedStdModules);
}

static byte[] CompileProjectToImage(AshesProject project, string targetId, BackendCompileOptions? backendOptions = null)
{
    var plan = ProjectSupport.BuildCompilationPlan(project);

    var projectSource = ProjectSupport.BuildCompilationSource(plan);
    return CompileToImage(projectSource, targetId, backendOptions, plan.ImportedStdModules);
}

static bool TryParseOptimizationFlag(string arg, out BackendOptimizationLevel level)
{
    level = arg switch
    {
        "-O0" => BackendOptimizationLevel.O0,
        "-O1" => BackendOptimizationLevel.O1,
        "-O2" => BackendOptimizationLevel.O2,
        "-O3" => BackendOptimizationLevel.O3,
        _ => BackendOptimizationLevel.O2,
    };

    return arg is "-O0" or "-O1" or "-O2" or "-O3";
}

static async Task<(int ExitCode, string Stdout, string Stderr)> RunImageCaptureAsync(byte[] image, string targetId, IReadOnlyList<string>? programArgs = null)
{
    var tmpDir = Path.Combine(Path.GetTempPath(), "ashes");
    Directory.CreateDirectory(tmpDir);

    var name = "ashes_" + Guid.NewGuid().ToString("N");
    var exePath = Path.Combine(tmpDir, targetId == TargetIds.WindowsX64 ? name + ".exe" : name);

    await File.WriteAllBytesAsync(exePath, image);

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
    await Task.WhenAll(stdoutTask, stderrTask, p.WaitForExitAsync());

    return (p.ExitCode, stdoutTask.Result, stderrTask.Result);
}

static async Task<int> RunImageWithInheritedStdioAsync(byte[] image, string targetId, IReadOnlyList<string>? programArgs = null)
{
    var tmpDir = Path.Combine(Path.GetTempPath(), "ashes");
    Directory.CreateDirectory(tmpDir);

    var name = "ashes_" + Guid.NewGuid().ToString("N");
    var exePath = Path.Combine(tmpDir, targetId == TargetIds.WindowsX64 ? name + ".exe" : name);

    await File.WriteAllBytesAsync(exePath, image);

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

    var psi = new ProcessStartInfo(exePath)
    {
        UseShellExecute = false,
    };

    foreach (var arg in programArgs ?? [])
    {
        psi.ArgumentList.Add(arg);
    }

    using var p = StartCompiledProcess(psi);

    await p.WaitForExitAsync();
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
    var keyword = binding.IsRecursive ? "let rec" : "let";
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

        case Expr.LetRec letRecExpr when letRecExpr.Body is Expr.Var recBodyVar && string.Equals(recBodyVar.Name, letRecExpr.Name, StringComparison.Ordinal):
            binding = new ReplBinding(letRecExpr.Name, Formatter.Format(letRecExpr.Value).Trim(), true);
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
        return await Console.In.ReadLineAsync();
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
        "compile" => await RunCompileAsync(args.Skip(1).ToArray()),
        "run" => await RunRunAsync(args.Skip(1).ToArray()),
        "repl" => await RunReplAsync(args.Skip(1).ToArray()),
        "test" => RunTest(args.Skip(1).ToArray()),
        "fmt" => await RunFmtAsync(args.Skip(1).ToArray()),
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
    if (a.Length == 1 && (a[0] == "--help" || a[0] == "-h"))
    {
        return Usage(0);
    }

    string? target = null;
    BackendOptimizationLevel optimizationLevel = BackendCompileOptions.Default.OptimizationLevel;
    string? outPath = null;
    string? expr = null;
    string? inputFile = null;
    string? projectPath = null;

    for (int i = 0; i < a.Length; i++)
    {
        var arg = a[i];

        if (arg == "--target" && i + 1 < a.Length) { target = a[++i]; continue; }
        if ((arg == "-o" || arg == "--out") && i + 1 < a.Length) { outPath = a[++i]; continue; }
        if (arg == "--expr" && i + 1 < a.Length) { expr = a[++i]; continue; }
        if (arg == "--project" && i + 1 < a.Length) { projectPath = a[++i]; continue; }
        if (TryParseOptimizationFlag(arg, out var parsedOptimizationLevel)) { optimizationLevel = parsedOptimizationLevel; continue; }

        if (!arg.StartsWith("-", StringComparison.Ordinal) && inputFile is null) { inputFile = arg; continue; }

        throw new CliUsageException("Unknown argument.");
    }

    var project = ResolveProject(projectPath, inputFile, expr);
    if (project is not null && (!string.IsNullOrEmpty(inputFile) || !string.IsNullOrEmpty(expr)))
    {
        throw new CliUsageException("Cannot combine --project with input file or --expr.");
    }

    target ??= project?.Target ?? BackendFactory.DefaultForCurrentOS();
    var backendOptions = new BackendCompileOptions(optimizationLevel);

    var sw = Stopwatch.StartNew();
    byte[] image;
    if (project is null)
    {
        var source = await ReadSourceAsync(inputFile, expr);
        var displayPath = inputFile ?? "<expr>";
        try
        {
            var prepared = PrepareStandaloneCompilationSource(source, displayPath);
            image = CompileToImage(prepared.Source, target, backendOptions, prepared.ImportedStdModules);
        }
        catch (CompileDiagnosticException ex)
        {
            PrintCompilerDiagnostics(ex, source, displayPath);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            PrintCompileFailure(ex.Message, displayPath);
            return 1;
        }
    }
    else
    {
        try
        {
            image = CompileProjectToImage(project, target, backendOptions);
        }
        catch (CompileDiagnosticException ex)
        {
            PrintCompilerDiagnostics(ex, null, project.EntryPath);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            PrintCompileFailure(ex.Message, project.EntryPath);
            return 1;
        }
    }
    sw.Stop();

    outPath ??= project is not null
        ? DeriveProjectOutputPath(project, target)
        : inputFile is not null
            ? DeriveOutputPath(inputFile, target)
            : "out" + (target == TargetIds.WindowsX64 ? ".exe" : "");

    var outDir = Path.GetDirectoryName(outPath);
    if (!string.IsNullOrWhiteSpace(outDir))
    {
        Directory.CreateDirectory(outDir!);
    }
    await File.WriteAllBytesAsync(outPath, image);

    AnsiConsole.MarkupLine($"[green]OK[/] Wrote [bold]{Runner.FormatSize(image.Length)}[/] to [italic]{outPath}[/]");
    AnsiConsole.MarkupLine($"     Target: [bold]{target}[/]");
    AnsiConsole.MarkupLine($"     Time:   [bold]{Runner.FormatElapsed(sw.ElapsedMilliseconds)}[/]");
    return 0;
}

async Task<int> RunRunAsync(string[] a)
{
    if (a.Length == 1 && (a[0] == "--help" || a[0] == "-h"))
    {
        return Usage(0);
    }

    var idx = Array.IndexOf(a, "--");
    var cliArgs = idx >= 0 ? a[..idx] : a;
    var progArgs = idx >= 0 ? a[(idx + 1)..] : Array.Empty<string>();

    string? target = null;
    BackendOptimizationLevel optimizationLevel = BackendCompileOptions.Default.OptimizationLevel;
    string? expr = null;
    string? inputFile = null;
    string? projectPath = null;

    for (int i = 0; i < cliArgs.Length; i++)
    {
        var arg = cliArgs[i];
        if (arg == "--target" && i + 1 < cliArgs.Length) { target = cliArgs[++i]; continue; }
        if (arg == "--expr" && i + 1 < cliArgs.Length) { expr = cliArgs[++i]; continue; }
        if (arg == "--project" && i + 1 < cliArgs.Length) { projectPath = cliArgs[++i]; continue; }
        if (TryParseOptimizationFlag(arg, out var parsedOptimizationLevel)) { optimizationLevel = parsedOptimizationLevel; continue; }

        if (!arg.StartsWith("-", StringComparison.Ordinal) && inputFile is null) { inputFile = arg; continue; }

        throw new CliUsageException("Unknown argument.");
    }

    var project = ResolveProject(projectPath, inputFile, expr);
    if (project is not null && (!string.IsNullOrEmpty(inputFile) || !string.IsNullOrEmpty(expr)))
    {
        throw new CliUsageException("Cannot combine --project with input file or --expr.");
    }

    target ??= project?.Target ?? BackendFactory.DefaultForCurrentOS();
    var backendOptions = new BackendCompileOptions(optimizationLevel);

    byte[] image;
    if (project is null)
    {
        var source = await ReadSourceAsync(inputFile, expr);
        var displayPath = inputFile ?? "<expr>";
        try
        {
            var prepared = PrepareStandaloneCompilationSource(source, displayPath);
            image = CompileToImage(prepared.Source, target, backendOptions, prepared.ImportedStdModules);
        }
        catch (CompileDiagnosticException ex)
        {
            PrintCompilerDiagnostics(ex, source, displayPath);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            PrintCompileFailure(ex.Message, displayPath);
            return 1;
        }
    }
    else
    {
        try
        {
            image = CompileProjectToImage(project, target, backendOptions);
        }
        catch (CompileDiagnosticException ex)
        {
            PrintCompilerDiagnostics(ex, null, project.EntryPath);
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            PrintCompileFailure(ex.Message, project.EntryPath);
            return 1;
        }
    }

    return await RunImageWithInheritedStdioAsync(image, target, progArgs);
}

async Task<int> RunReplAsync(string[] a)
{
    if (a.Length == 1 && (a[0] == "--help" || a[0] == "-h"))
    {
        return Usage(0);
    }

    string? target = null;
    BackendOptimizationLevel optimizationLevel = BackendCompileOptions.Default.OptimizationLevel;

    for (int i = 0; i < a.Length; i++)
    {
        var arg = a[i];
        if (arg == "--target" && i + 1 < a.Length) { target = a[++i]; continue; }
        if (TryParseOptimizationFlag(arg, out var parsedOptimizationLevel)) { optimizationLevel = parsedOptimizationLevel; continue; }
        throw new CliUsageException("Unknown argument.");
    }

    target ??= BackendFactory.DefaultForCurrentOS();
    var backendOptions = new BackendCompileOptions(optimizationLevel);
    var sessionBindings = new List<ReplBinding>();

    AnsiConsole.Write(new Rule("[bold]Ashes REPL[/]").RuleStyle("grey").LeftJustified());
    AnsiConsole.MarkupLine($"Target: [bold]{target}[/]");
    AnsiConsole.MarkupLine("[grey]Type :help for commands, :quit to exit.[/]");
    AnsiConsole.WriteLine();

    while (true)
    {
        var buffer = new List<string>();

        var first = await ReadReplLineAsync("> ");
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
            AnsiConsole.MarkupLine("[grey]REPL commands:[/]");
            AnsiConsole.MarkupLine("  [yellow]:help[/]   Show this help");
            AnsiConsole.MarkupLine("  [yellow]:quit[/]   Exit");
            AnsiConsole.MarkupLine("  [yellow]:target[/] Show current target");
            AnsiConsole.MarkupLine("  [yellow]:target linux-x64|windows-x64[/]  Change target");
            AnsiConsole.MarkupLine("  [yellow]let name = ... in name[/]  Persist a binding in the session");
            continue;
        }

        if (trimmedFirst.StartsWith(":target", StringComparison.Ordinal))
        {
            var parts = trimmedFirst.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 1)
            {
                AnsiConsole.MarkupLine($"Target: [bold]{target}[/]");
                continue;
            }
            if (parts.Length == 2 && (parts[1] == TargetIds.LinuxX64 || parts[1] == TargetIds.WindowsX64))
            {
                target = parts[1];
                AnsiConsole.MarkupLine($"Target set to [bold]{target}[/]");
                continue;
            }

            AnsiConsole.MarkupLine("[red]Error:[/] Usage: :target linux-x64|windows-x64");
            continue;
        }

        buffer.Add(first);

        while (true)
        {
            var candidate = string.Join("\n", buffer).TrimEnd();

            if (LooksIncomplete(candidate))
            {
                var more = await ReadReplLineAsync("| ");
                if (more is null)
                {
                    break;
                }

                buffer.Add(more);
                continue;
            }

            if (TryAnalyzeReplSubmission(sessionBindings, candidate, out var analysis, out var compileDiagnostics, out var compileError))
            {
                var isBindingSubmission = TryExtractPersistentBinding(candidate, out var persistedBinding);
                if (!TryCompileReplSubmission(sessionBindings, candidate, autoPrint: analysis!.IsPrintable && !isBindingSubmission, target, backendOptions, out var image, out compileDiagnostics, out compileError))
                {
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

                var (exit, stdout, stderr) = await RunImageCaptureAsync(image!, target);

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

                    PrintReplTypeEcho(analysis!);

                    break;
                }

                PrintRuntimeFailure(exit, stdout, stderr);
                break;
            }

            if (compileError is not null && IsLikelyNeedMoreInput(compileError))
            {
                var more = await ReadReplLineAsync("| ");
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

    return 0;
}

int RunTest(string[] a)
{
    if (a.Length == 1 && (a[0] == "--help" || a[0] == "-h"))
    {
        return Usage(0);
    }

    // ashes test [--project ...] [--target ...] [paths...]
    string? target = null;
    BackendOptimizationLevel optimizationLevel = BackendCompileOptions.Default.OptimizationLevel;
    string? projectPath = null;
    var paths = new List<string>();

    for (int i = 0; i < a.Length; i++)
    {
        var arg = a[i];
        if (arg == "--target" && i + 1 < a.Length) { target = a[++i]; continue; }
        if (arg == "--project" && i + 1 < a.Length) { projectPath = a[++i]; continue; }
        if (TryParseOptimizationFlag(arg, out var parsedOptimizationLevel)) { optimizationLevel = parsedOptimizationLevel; continue; }
        if (arg.StartsWith("-", StringComparison.Ordinal))
        {
            throw new CliUsageException("Unknown argument.");
        }

        paths.Add(arg);
    }

    var project = ResolveProject(projectPath, null, null);
    target ??= project?.Target ?? BackendFactory.DefaultForCurrentOS();
    var backendOptions = new BackendCompileOptions(optimizationLevel);

    return Runner.RunTests(paths, target, AnsiConsole.Console, project, backendOptions);
}

async Task<int> RunFmtAsync(string[] a)
{
    if (a.Length == 1 && (a[0] == "--help" || a[0] == "-h"))
    {
        return Usage(0);
    }

    // ashes fmt <file|dir> [-w]
    if (a.Length == 0)
    {
        throw new CliUserException("Missing file or directory.");
    }

    var writeInPlace = a.Contains("-w", StringComparer.Ordinal) || a.Contains("--write", StringComparer.Ordinal);
    var targets = a.Where(x => x != "-w" && x != "--write").ToArray();
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
    if (files.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No .ash files found.[/]");
        return 0;
    }

    var sw = Stopwatch.StartNew();
    foreach (var file in files)
    {
        var src = await File.ReadAllTextAsync(file);
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
        var formattedWithoutComments = imports.Count == 0
            ? formattedBody
            : string.Join(lineEnding, imports) + lineEnding + formattedBody;
        var formatted = leadingComments.Count == 0
            ? formattedWithoutComments
            : string.Join(lineEnding, leadingComments) + lineEnding + formattedWithoutComments;

        if (writeInPlace)
        {
            if (formatted != src)
            {
                await File.WriteAllTextAsync(file, formatted);
            }
        }
        else
        {
            if (files.Count > 1)
            {
                AnsiConsole.Write(new Rule(file).RuleStyle("grey").LeftJustified());
            }

            AnsiConsole.Write(new Text(formatted));
        }
    }
    sw.Stop();

    if (writeInPlace)
    {
        AnsiConsole.MarkupLine($"[green]OK[/] Formatted {files.Count} file(s) in [bold]{Runner.FormatElapsed(sw.ElapsedMilliseconds)}[/].");
    }

    return 0;
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
        RegexOptions.Compiled | RegexOptions.CultureInvariant
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
            imports.Add($"import {match.Groups[1].Value}");
            continue;
        }

        if (line.TrimStart().StartsWith("import ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invalid import syntax in {filePath}:{lineIndex}. Expected 'import Foo' or 'import Foo.Bar'.");
        }

        sourceLines.Add(line);
    }

    return (imports, string.Join(lineEnding, sourceLines));
}

static void PrintCompilerDiagnostics(CompileDiagnosticException ex, string? source, string displayPath)
{
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
    sb.AppendLine(exitCode.ToString());

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
