using System.Diagnostics;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

/// <summary>
/// End-to-end coverage for top-level mutual-recursion groups (<c>let recursive X = ... and Y = ...</c>):
/// the group type-checks as one binding group, every member is in scope in every other member's body,
/// in subsequent declarations, and in the trailing expression, and the whole thing lowers to native
/// code that runs and produces the right answer.
/// </summary>
/// <remarks>
/// The trailing expression is introduced with <c>let ... in</c> rather than a bare expression on the
/// following line. A bare trailing expression after a flat top-level value declaration is greedily
/// absorbed into that declaration's value via whitespace application (a known Frontend parser
/// limitation tracked as a separate follow-up); the <c>let ... in</c> form delimits it cleanly, the
/// way every existing top-level test is written. Either way the <c>let ... in</c> body is the
/// program's trailing expression and exercises Model-A visibility of the group's members.
/// </remarks>
public sealed class MutualRecursionTests
{
    [Test]
    public async Task Mutually_recursive_group_runs_and_computes_correct_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // isEven/isOdd reference each other; the trailing expression exercises both directions and an
        // odd argument, so a wrong answer in either function changes the printed value.
        var src = """
            let recursive isEven = given (n) -> if n == 0 then true else isOdd(n - 1)
            and isOdd = given (n) -> if n == 0 then false else isEven(n - 1)
            let answer = if isEven(8) then (if isOdd(8) then 0 else 42) else 0 in Ashes.IO.print(answer)
            """;

        (await CompileRunCaptureProgramAsync(src)).ShouldBe("42\n");
    }

    [Test]
    public async Task Group_members_are_in_scope_in_subsequent_declarations_and_trailing_expr()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // `classify` (a later top-level declaration) references isEven, and the trailing expression
        // references isOdd — both must resolve to the group's members under Model-A scoping.
        var src = """
            let recursive isEven = given (n) -> if n == 0 then true else isOdd(n - 1)
            and isOdd = given (n) -> if n == 0 then false else isEven(n - 1)
            let classify = given (n) -> if isEven(n) then 100 else 200
            let result = classify(4) + (if isOdd(3) then 1 else 0) in Ashes.IO.print(result)
            """;

        // classify(4) = 100 (4 is even); isOdd(3) = true => +1; total 101.
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("101\n");
    }

    [Test]
    public async Task Group_members_capturing_distinct_outer_variables_share_one_env_correctly()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // This is the non-trivial lowering path: each member captures a DIFFERENT outer variable, so
        // the group's shared environment is the union [lo, hi] and every member is compiled against
        // that identical layout. When `foo` reaches a sibling it reconstructs `bar`'s closure from its
        // own env pointer (and vice versa); that is only sound because the layout is shared, and only
        // correct if each member reads its capture from the right union slot. `foo` returns `lo`
        // (slot 0) at its base case and `bar` returns `hi` (slot 1) at its, so a mislaid env, a
        // per-member env instead of the shared one, or a wrong reconstruction index all change the
        // printed value.
        var src = """
            let lo = 100
            let hi = 7
            let recursive foo = given (n) -> if n == 0 then lo else bar(n - 1)
            and bar = given (n) -> if n == 0 then hi else foo(n - 1)
            let r = foo(4) * 1000 + bar(4) in Ashes.IO.print(r)
            """;

        // foo(4): foo->bar->foo->bar->foo(0)=lo=100. bar(4): bar->foo->bar->foo->bar(0)=hi=7.
        // r = 100 * 1000 + 7 = 100007.
        (await CompileRunCaptureProgramAsync(src)).ShouldBe("100007\n");
    }

    [Test]
    public async Task Degenerate_group_of_size_one_lowers_and_runs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // The parser only ever emits a multi-binding RecGroup, so build a one-binding group directly
        // to confirm a degenerate group still type-checks and lowers like an ordinary `let recursive`. The
        // declaration and the trailing call are parsed separately and assembled so the trailing
        // expression is not folded into the recursive function's body.
        var diag = new Diagnostics();
        var parsedDecl = new Parser("let recursive fact = given (n) -> if n == 0 then 1 else n * fact(n - 1)", diag).ParseProgram();
        var trailing = new Parser("Ashes.IO.print(fact(5))", diag).ParseProgram().Body;
        diag.ThrowIfAny();

        var letDecl = parsedDecl.Items.OfType<TopLevelItem.LetDecl>().Single();
        var singletonGroup = new TopLevelItem.RecGroup(new[] { (letDecl.Name, letDecl.Value) });
        var program = new Program(new TopLevelItem[] { singletonGroup }, trailing);

        (await RunProgramAsync(program)).ShouldBe("120\n");
    }

    [Test]
    public void And_without_let_rec_is_rejected()
    {
        // Parser-level guard: `and` is only valid after `let recursive`.
        var diag = new Diagnostics();
        var src = """
            let isEven = given (n) -> if n == 0 then true else isOdd(n - 1)
            and isOdd = given (n) -> if n == 0 then false else isEven(n - 1)
            Ashes.IO.print(0)
            """;
        _ = new Parser(src, diag).ParseProgram();

        diag.StructuredErrors.ShouldNotBeEmpty();
        diag.Errors.ShouldContain(e => e.Contains("'let recursive'", StringComparison.Ordinal));
    }

    private static async Task<string> CompileRunCaptureProgramAsync(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();
        return await RunProgramAsync(program);
    }

    private static async Task<string> RunProgramAsync(Program program)
    {
        var diag = new Diagnostics();
        var ir = new Lowering(diag).Lower(program);
        diag.ThrowIfAny();

        var elfBytes = new Ashes.Backend.Backends.LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);

        var exePath = Path.Combine(tmpDir, $"mutrec_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi);
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
