using System.Text;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LinuxArm64BackendCoverageTests
{
    private const string HttpsProgram = """match Ashes.Async.run(async await Ashes.Http.get("https://localhost/")) with | Ok(text) -> text | Error(msg) -> msg""";

    [Test]
    public void Linux_arm64_backend_compile_should_link_hermetic_rustls_payload_for_https_programs()
    {
        var bytes = CompileForLinuxArm64(HttpsProgram);

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
        ContainsAscii(bytes, "rustls_client_connection_new").ShouldBeTrue();
        ContainsAscii(bytes, "rustls_platform_server_cert_verifier").ShouldBeTrue();
    }

    [Test]
    public void Linux_arm64_backend_compile_should_not_link_hermetic_rustls_payload_for_plain_programs()
    {
        var bytes = CompileForLinuxArm64("Ashes.IO.print(42)");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
        ContainsAscii(bytes, "rustls_client_connection_new").ShouldBeFalse();
        ContainsAscii(bytes, "rustls_platform_server_cert_verifier").ShouldBeFalse();
    }

    private static byte[] CompileForLinuxArm64(string source)
    {
        var ir = LowerExpression(source);
        return new LinuxArm64LlvmBackend().Compile(ir);
    }

    private static IrProgram LowerExpression(string source)
    {
        var diagnostics = new Diagnostics();
        var ast = new Parser(source, diagnostics).ParseExpression();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static bool ContainsAscii(byte[] bytes, string text)
    {
        byte[] needle = Encoding.ASCII.GetBytes(text);
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }
}