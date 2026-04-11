using System.Diagnostics;
using Ashes.Semantics;
using Shouldly;
using Ashes.Frontend;

namespace Ashes.Tests;

public sealed class EndToEndNativeBackendTests
{
    [Test]
    public async Task Int_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.print(40 + 2)");
        stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task String_concat_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.print(\"hello \" + \"world\")");
        stdout.ShouldBe("hello world\n");
    }

    [Test]
    public async Task String_comparison_program_runs_and_prints_expected_output()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("if (\"he\" + \"llo\") == \"hello\" then if \"hello\" != \"world\" then Ashes.IO.print(\"ok\") else Ashes.IO.print(\"bad\") else Ashes.IO.print(\"bad\")");
        stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Write_program_runs_without_trailing_newline()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("let _ = Ashes.IO.write(\"he\") in Ashes.IO.write(\"llo\")");
        stdout.ShouldBe("hello");
    }

    [Test]
    public async Task Write_line_program_runs_with_newline()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var stdout = await CompileRunCaptureAsync("Ashes.IO.writeLine(\"hello\")");
        stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Read_line_returns_some_for_input()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = "match Ashes.IO.readLine() with | None -> Ashes.IO.print(\"none\") | Some(text) -> Ashes.IO.print(text)";
        (await CompileRunCaptureAsync(source, stdin: "hello\n")).ShouldBe("hello\n");
    }

    [Test]
    public async Task Read_line_returns_none_at_eof()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var source = "match Ashes.IO.readLine() with | None -> Ashes.IO.print(\"none\") | Some(text) -> Ashes.IO.print(text)";
        (await CompileRunCaptureAsync(source, stdin: "")).ShouldBe("none\n");
    }

    [Test]
    public async Task Bool_prints_true_false()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        (await CompileRunCaptureAsync("Ashes.IO.print(true)")).ShouldBe("true\n");
        (await CompileRunCaptureAsync("Ashes.IO.print(false)")).ShouldBe("false\n");
    }

    [Test]
    public async Task If_expression_works()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        (await CompileRunCaptureAsync("if true then Ashes.IO.print(\"yes\") else Ashes.IO.print(\"no\")")).ShouldBe("yes\n");
        (await CompileRunCaptureAsync("if false then Ashes.IO.print(\"yes\") else Ashes.IO.print(\"no\")")).ShouldBe("no\n");
    }

    [Test]
    public async Task Lambda_no_capture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "let inc = fun (x) -> x + 1 in Ashes.IO.print(inc(41))";
        (await CompileRunCaptureAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Closure_capture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "let z = 20 in let f = fun (x) -> x + z in Ashes.IO.print(f(22))";
        (await CompileRunCaptureAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Curried_add()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "let add = fun (x) -> fun (y) -> x + y in let add10 = add(10) in Ashes.IO.print(add10(32))";
        (await CompileRunCaptureAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Let_rec_loop_works()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "let rec loop = fun (i) -> if i >= 10 then i else loop(i + 1) in Ashes.IO.print(loop(0))";
        (await CompileRunCaptureAsync(src)).ShouldBe("10\n");
    }

    [Test]
    public async Task Less_or_equal_works()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "let rec loop = fun (i) -> if i <= 10 then loop(i + 1) else i in Ashes.IO.print(loop(0))";
        (await CompileRunCaptureAsync(src)).ShouldBe("11\n");
    }

    [Test]
    public async Task Arithmetic_subtract_multiply_divide_work()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "Ashes.IO.print((10 - 3) * 2 / 7)";
        (await CompileRunCaptureAsync(src)).ShouldBe("2\n");
    }

    [Test]
    public async Task Float_arithmetic_and_comparisons_work()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "if ((1.5 + 2.5) * 2.0 / 2.0) == 4.0 then if 4.0 >= 4.0 then if 3.0 <= 4.0 then if 3.0 != 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0) else Ashes.IO.print(0) else Ashes.IO.print(0) else Ashes.IO.print(0)";
        (await CompileRunCaptureAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Match_with_list_literal_works()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "let rec sum = fun (xs) -> match xs with | [] -> 0 | x :: rest -> x + sum(rest) in Ashes.IO.print(sum([1, 2, 3]))";
        (await CompileRunCaptureAsync(src)).ShouldBe("6\n");
    }

    [Test]
    public async Task Cons_operator_is_right_associative()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "let rec len = fun (xs) -> match xs with | [] -> 0 | _ :: rest -> 1 + len(rest) in Ashes.IO.print(len(1 :: 2 :: []))";
        (await CompileRunCaptureAsync(src)).ShouldBe("2\n");
    }

    [Test]
    public async Task Program_args_are_available_as_prelude_list_without_executable_name()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")";
        (await CompileRunCaptureAsync(src, ["first", "second"])).ShouldBe("first:second\n");
    }

    [Test]
    public async Task Tuple_literal_and_match_pattern_work()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = "let p = (1, 2) in match p with | (a, b) -> Ashes.IO.print(a + b)";
        (await CompileRunCaptureAsync(src)).ShouldBe("3\n");
    }

    [Test]
    public async Task Adt_nullary_constructor_and_match()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = """
            type Color = | Red | Green | Blue
            let c = Green
            in match c with
            | Red -> Ashes.IO.print(1)
            | Green -> Ashes.IO.print(2)
            | Blue -> Ashes.IO.print(3)
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("2\n");
    }

    [Test]
    public async Task Adt_constructor_with_payload_and_match()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = """
            type LocalMaybe = | None | Some(T)
            let unwrapOr = fun (opt, def) ->
            match opt with
            | None -> def
            | Some(x) -> x
            in Ashes.IO.print(unwrapOr(Some(42), 0))
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Adt_tag_distinguishes_constructors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = """
            type Outcome = | Left(T) | Right(T)
            let tag = fun (value) ->
                match value with
                | Left(x) -> 1
                | Right(x) -> 2
            in Ashes.IO.print(tag(Left(0)) + tag(Right(0)))
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("3\n");
    }

    [Test]
    public async Task Adt_constructor_with_multiple_fields_and_match()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var src = """
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> Ashes.IO.print(a + b)
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("42\n");
    }

    // --- TCO arena reset end-to-end tests ---

    [Test]
    public async Task TCO_loop_with_arena_reset_produces_correct_result()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Sum 1..10000 using tail-recursive accumulator — all args are copy types,
        // so arena reset fires on every iteration, keeping heap usage constant.
        var src = """
            let rec sum = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else sum (n - 1) (acc + n)
            in Ashes.IO.print(sum 10000 0)
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("50005000\n");
    }

    [Test]
    public async Task TCO_loop_with_string_intermediates_and_copy_args_runs_correctly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Even though the body creates intermediate heap allocations (let binding
        // with string), the tail-call args are all Int → arena resets safely.
        var src = """
            let rec count = fun (n) -> fun (acc) ->
                if n == 0 then acc
                else
                    let tag = "iter" in
                    count (n - 1) (acc + 1)
            in Ashes.IO.print(count 1000 0)
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("1000\n");
    }

    // --- Per-call arena watermark end-to-end tests ---

    [Test]
    public async Task Function_call_returning_int_produces_correct_result()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Curried call add(10)(32) returns Int — arena reset reclaims
        // intermediate closures, result must still be correct.
        var src = """
            let add = fun (x) -> fun (y) -> x + y
            in Ashes.IO.print(add(10)(32))
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Nested_function_calls_returning_int_produce_correct_result()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // f(g(x)) where both return Int — nested per-call watermarks.
        var src = """
            let double = fun (x) -> x + x
            in let inc = fun (x) -> x + 1
            in Ashes.IO.print(double(inc(20)))
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Function_call_returning_string_produces_correct_result()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Function returning String — per-call arena reset + copy-out.
        var src = """
            let greet = fun (name) -> "hello " + name
            in Ashes.IO.print(greet("world"))
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("hello world\n");
    }

    [Test]
    public async Task Function_call_returning_list_produces_correct_result()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Function returning List — per-call arena skip (conservative);
        // list must survive and be usable.
        var src = """
            let makeList = fun (x) -> x :: [2, 3]
            in match makeList(1) with
                | hd :: _ -> Ashes.IO.print(hd)
                | [] -> Ashes.IO.print(0)
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("1\n");
    }

    [Test]
    public async Task Multiple_calls_in_expression_produce_correct_result()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // f(x) + g(y) — two independent call watermarks in one expression.
        var src = """
            let f = fun (x) -> x * 2
            in let g = fun (y) -> y * 3
            in Ashes.IO.print(f(7) + g(7))
            """;
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("35\n");
    }

    private static async Task<string> CompileRunCaptureAsync(string source, string[]? programArgs = null, string? stdin = null)
    {
        var diag = new Diagnostics();
        var ast = new Parser(source, diag).ParseExpression();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(ast);
        diag.ThrowIfAny();

        return await RunElfAsync(ir, programArgs, stdin);
    }

    private static async Task<string> CompileRunCaptureProgramAsync(string source, string[]? programArgs = null, string? stdin = null)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(program);
        diag.ThrowIfAny();

        return await RunElfAsync(ir, programArgs, stdin);
    }

    private static async Task<string> RunElfAsync(IrProgram ir, string[]? programArgs, string? stdin)
    {
        var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"mf_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in programArgs ?? [])
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = await TestProcessHelper.StartProcessAsync(psi); ;
        if (stdin is not null)
        {
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
        }
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
