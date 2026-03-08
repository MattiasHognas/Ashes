using Ashes.Semantics;
using Ashes.Frontend;

namespace Ashes.Tests;

public sealed class SnapshotTests
{
    private readonly VerifySettings _settings;

    public SnapshotTests()
    {
        _settings = new VerifySettings();
        _settings.ScrubMachineName();
        _settings.ScrubUserName();
    }

    [Test]
    public async Task Snapshot_pipeline_for_int_program()
    {
        var source = "let x = 40 + 2 in Ashes.IO.print(x + 1)";
        var snapshot = CompileToSnapshot(source);

        await Verifier.Verify(snapshot, _settings)
            .UseMethodName(nameof(Snapshot_pipeline_for_int_program));
    }

    [Test]
    public async Task Snapshot_pipeline_for_string_program()
    {
        var source = "Ashes.IO.print(\"hello \" + \"world\")";
        var snapshot = CompileToSnapshot(source);

        await Verifier.Verify(snapshot, _settings)
            .UseMethodName(nameof(Snapshot_pipeline_for_string_program));
    }

    [Test]
    public async Task Snapshot_pipeline_for_lambda_program()
    {
        var source = "let add = fun (x) -> fun (y) -> x + y in Ashes.IO.print(add(10)(32))";
        var snapshot = CompileToSnapshot(source);

        await Verifier.Verify(snapshot, _settings)
            .UseMethodName(nameof(Snapshot_pipeline_for_lambda_program));
    }

    private static Snapshot CompileToSnapshot(string source)
    {
        var diag = new Diagnostics();

        // Lex
        var lexer = new Lexer(source, diag);
        var tokens = new List<SnapshotToken>();
        while (true)
        {
            var t = lexer.Next();
            tokens.Add(new SnapshotToken(t.Kind, t.Text, t.IntValue, t.Position));
            if (t.Kind == TokenKind.EOF)
            {
                break;
            }
        }

        // Parse
        var ast = new Parser(source, diag).ParseExpression();

        IrProgram? ir = null;
        string? elfSha256 = null;

        if (diag.Errors.Count == 0)
        {
            ir = new Lowering(diag).Lower(ast);

            if (diag.Errors.Count == 0)
            {
                var elf = new Ashes.Backend.Backends.LinuxX64ElfBackend().Compile(ir);
                elfSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(elf));
            }
        }

        return new Snapshot(
            Source: source,
            Tokens: tokens,
            Diagnostics: diag.Errors,
            Ast: ast,
            Ir: ir,
            ElfSha256: elfSha256
        );
    }

    record struct Snapshot(
        string Source,
        List<SnapshotToken> Tokens,
        IReadOnlyList<string> Diagnostics,
        Expr Ast,
        IrProgram? Ir,
        string? ElfSha256
    );

    private readonly record struct SnapshotToken(
        TokenKind Kind,
        string Text,
        long IntValue,
        int Position
    );
}
