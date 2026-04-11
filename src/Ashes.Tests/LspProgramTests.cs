using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ashes.Lsp;
using Shouldly;

namespace Ashes.Tests;

public sealed class LspProgramTests
{
    [Test]
    public async Task Lsp_program_should_handle_basic_document_lifecycle_requests()
    {
        using var process = StartLspProcess();

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { }
        });

        var initializeResponse = await ReadMessageAsync(process);
        initializeResponse.GetProperty("id").GetInt32().ShouldBe(1);
        initializeResponse.GetProperty("result").GetProperty("capabilities").GetProperty("documentFormattingProvider").GetBoolean().ShouldBeTrue();

        const string uri = "file:///tmp/test.ash";

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new
                {
                    uri,
                    text = "if true then 1"
                }
            }
        });

        var openDiagnostics = await ReadMessageAsync(process);
        openDiagnostics.GetProperty("method").GetString().ShouldBe("textDocument/publishDiagnostics");
        openDiagnostics.GetProperty("params").GetProperty("diagnostics").GetArrayLength().ShouldBeGreaterThan(0);

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "textDocument/formatting",
            @params = new
            {
                textDocument = new { uri }
            }
        });

        var invalidFormatting = await ReadMessageAsync(process);
        invalidFormatting.GetProperty("id").GetInt32().ShouldBe(2);
        invalidFormatting.GetProperty("result").GetArrayLength().ShouldBe(0);

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new
            {
                textDocument = new { uri },
                contentChanges = new[]
                {
                    new { text = "Ashes.IO.print(40+2)" }
                }
            }
        });

        var changedDiagnostics = await ReadMessageAsync(process);
        changedDiagnostics.GetProperty("method").GetString().ShouldBe("textDocument/publishDiagnostics");
        changedDiagnostics.GetProperty("params").GetProperty("diagnostics").GetArrayLength().ShouldBe(0);

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "textDocument/formatting",
            @params = new
            {
                textDocument = new { uri }
            }
        });

        var formattingResponse = await ReadMessageAsync(process);
        formattingResponse.GetProperty("id").GetInt32().ShouldBe(3);
        var edits = formattingResponse.GetProperty("result");
        edits.GetArrayLength().ShouldBe(1);
        edits[0].GetProperty("newText").GetString().ShouldBe("Ashes.IO.print(40 + 2)\n");

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 4,
            method = "unknown/method",
            @params = new { }
        });

        var unknownResponse = await ReadMessageAsync(process);
        unknownResponse.GetProperty("id").GetInt32().ShouldBe(4);
        unknownResponse.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didClose",
            @params = new
            {
                textDocument = new { uri }
            }
        });

        var closeDiagnostics = await ReadMessageAsync(process);
        closeDiagnostics.GetProperty("method").GetString().ShouldBe("textDocument/publishDiagnostics");
        closeDiagnostics.GetProperty("params").GetProperty("diagnostics").GetArrayLength().ShouldBe(0);

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 5,
            method = "shutdown",
            @params = new { }
        });

        var shutdownResponse = await ReadMessageAsync(process);
        shutdownResponse.GetProperty("id").GetInt32().ShouldBe(5);
        shutdownResponse.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            method = "exit"
        });

        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0);
    }

    [Test]
    public async Task Lsp_program_should_return_failure_exit_code_if_exit_is_sent_before_shutdown()
    {
        using var process = StartLspProcess();

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            method = "exit"
        });

        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(1);
    }

    [Test]
    public async Task Lsp_program_formatting_should_use_editorconfig_defaults_and_lsp_overrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashes_lsp_formatting_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ".editorconfig"), """
                                                               root = true

                                                               [*.ash]
                                                               indent_style = tab
                                                               tab_width = 4
                                                               indent_size = 4
                                                               end_of_line = lf
                                                               """);

            var filePath = Path.Combine(root, "Main.ash");
            var uri = new Uri(filePath).AbsoluteUri;

            using var process = StartLspProcess();

            await WriteMessageAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { }
            });
            _ = await ReadMessageAsync(process);

            await WriteMessageAsync(process, new
            {
                jsonrpc = "2.0",
                method = "textDocument/didOpen",
                @params = new
                {
                    textDocument = new
                    {
                        uri,
                        text = "let x = if true then 1 else 2 in Ashes.IO.print(x)"
                    }
                }
            });
            _ = await ReadMessageAsync(process); // diagnostics

            await WriteMessageAsync(process, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "textDocument/formatting",
                @params = new
                {
                    textDocument = new { uri }
                }
            });

            var defaultFormatting = await ReadMessageAsync(process);
            var defaultText = defaultFormatting.GetProperty("result")[0].GetProperty("newText").GetString()!;
            defaultText.ShouldContain("\tif true");

            await WriteMessageAsync(process, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "textDocument/formatting",
                @params = new
                {
                    textDocument = new { uri },
                    options = new
                    {
                        tabSize = 2,
                        insertSpaces = true
                    }
                }
            });

            var overrideFormatting = await ReadMessageAsync(process);
            var overrideText = overrideFormatting.GetProperty("result")[0].GetProperty("newText").GetString()!;
            overrideText.ShouldContain("  if true");
            overrideText.ShouldNotContain("\tif true");

            await WriteMessageAsync(process, new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
            _ = await ReadMessageAsync(process);
            await WriteMessageAsync(process, new { jsonrpc = "2.0", method = "exit" });
            await process.WaitForExitAsync();
            process.ExitCode.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Lsp_program_should_return_semantic_tokens_and_completions_for_adt_source()
    {
        using var process = StartLspProcess();

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { }
        });

        var initResponse = await ReadMessageAsync(process);
        var caps = initResponse.GetProperty("result").GetProperty("capabilities");
        caps.TryGetProperty("semanticTokensProvider", out _).ShouldBeTrue();
        caps.TryGetProperty("completionProvider", out _).ShouldBeTrue();

        const string uri = "file:///tmp/adt_test.ash";
        const string source = "type Maybe = | None | Some(T)\nAshes.IO.print(1)";

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new { textDocument = new { uri, text = source } }
        });

        _ = await ReadMessageAsync(process); // diagnostics notification

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "textDocument/semanticTokens/full",
            @params = new { textDocument = new { uri } }
        });

        var tokenResponse = await ReadMessageAsync(process);
        tokenResponse.GetProperty("id").GetInt32().ShouldBe(2);
        var data = tokenResponse.GetProperty("result").GetProperty("data");
        data.GetArrayLength().ShouldBeGreaterThan(0);
        var decodedTokens = DecodeSemanticTokens(data, source);
        decodedTokens.ShouldContain(t => t.Text == "Maybe" && t.TokenType == DocumentService.TokenTypeType);
        decodedTokens.ShouldContain(t => t.Text == "None" && t.TokenType == DocumentService.TokenTypeEnumMember);
        decodedTokens.ShouldContain(t => t.Text == "Some" && t.TokenType == DocumentService.TokenTypeEnumMember);
        decodedTokens.ShouldContain(t => t.Text == "T" && t.TokenType == DocumentService.TokenTypeTypeParameter);

        for (var i = 1; i < decodedTokens.Count; i++)
        {
            var previous = decodedTokens[i - 1];
            var current = decodedTokens[i];

            current.Line.ShouldBeGreaterThanOrEqualTo(previous.Line);
            if (current.Line == previous.Line)
            {
                current.Character.ShouldBeGreaterThanOrEqualTo(previous.Character);
            }
        }

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "textDocument/completion",
            @params = new { textDocument = new { uri }, position = new { line = 1, character = 0 } }
        });

        var completionResponse = await ReadMessageAsync(process);
        completionResponse.GetProperty("id").GetInt32().ShouldBe(3);
        var items = completionResponse.GetProperty("result");
        items.GetArrayLength().ShouldBeGreaterThan(0);
        var labels = Enumerable.Range(0, items.GetArrayLength())
            .Select(i => items[i].GetProperty("label").GetString()!)
            .ToArray();
        labels.ShouldContain("None");
        labels.ShouldContain("Some");

        await WriteMessageAsync(process, new { jsonrpc = "2.0", id = 4, method = "shutdown", @params = new { } });
        _ = await ReadMessageAsync(process);
        await WriteMessageAsync(process, new { jsonrpc = "2.0", method = "exit" });
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0);
    }

    [Test]
    public async Task Lsp_program_should_return_module_member_completions_for_qualified_modules()
    {
        using var process = StartLspProcess();

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { }
        });
        _ = await ReadMessageAsync(process);

        const string uri = "file:///tmp/module_completion.ash";
        const string source = "import Ashes.List\nAshes.IO.";

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new { textDocument = new { uri, text = source } }
        });
        _ = await ReadMessageAsync(process);

        await WriteMessageAsync(process, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "textDocument/completion",
            @params = new { textDocument = new { uri }, position = new { line = 1, character = 9 } }
        });

        var completionResponse = await ReadMessageAsync(process);
        completionResponse.GetProperty("id").GetInt32().ShouldBe(2);
        var items = completionResponse.GetProperty("result");
        var labels = Enumerable.Range(0, items.GetArrayLength())
            .Select(i => items[i].GetProperty("label").GetString()!)
            .ToArray();
        labels.ShouldContain("print");
        labels.ShouldContain("panic");
        labels.ShouldContain("args");

        await WriteMessageAsync(process, new { jsonrpc = "2.0", id = 3, method = "shutdown", @params = new { } });
        _ = await ReadMessageAsync(process);
        await WriteMessageAsync(process, new { jsonrpc = "2.0", method = "exit" });
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0);
    }

    private static Process StartLspProcess()
    {
        var lspAssemblyPath = Path.Combine(AppContext.BaseDirectory, "ashes-lsp.dll");
        File.Exists(lspAssemblyPath).ShouldBeTrue($"Expected LSP assembly at '{lspAssemblyPath}'");

        var startInfo = new ProcessStartInfo("dotnet", $"\"{lspAssemblyPath}\"")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        return Process.Start(startInfo)!;
    }

    private static async Task WriteMessageAsync(Process process, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");

        await process.StandardInput.BaseStream.WriteAsync(header);
        await process.StandardInput.BaseStream.WriteAsync(bytes);
        await process.StandardInput.BaseStream.FlushAsync();
    }

    private static async Task<JsonElement> ReadMessageAsync(Process process)
    {
        int contentLength = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(process.StandardOutput.BaseStream);
            line.ShouldNotBeNull();

            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
            }
        }

        contentLength.ShouldBeGreaterThan(0);
        var body = new byte[contentLength];
        int read = 0;

        while (read < contentLength)
        {
            var chunk = await process.StandardOutput.BaseStream.ReadAsync(body.AsMemory(read, contentLength - read));
            chunk.ShouldBeGreaterThan(0);
            read += chunk;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream stream)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var b = new byte[1];
            var n = await stream.ReadAsync(b);
            if (n == 0)
            {
                return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray());
            }

            if (b[0] == '\r')
            {
                var next = new byte[1];
                var nextRead = await stream.ReadAsync(next);
                if (nextRead == 0)
                {
                    return Encoding.ASCII.GetString(ms.ToArray());
                }

                if (next[0] == '\n')
                {
                    return Encoding.ASCII.GetString(ms.ToArray());
                }

                ms.WriteByte(next[0]);
            }
            else
            {
                ms.WriteByte(b[0]);
            }
        }
    }

    private static List<DecodedSemanticToken> DecodeSemanticTokens(JsonElement data, string source)
    {
        (data.GetArrayLength() % 5).ShouldBe(0);

        var line = 0;
        var character = 0;
        var decoded = new List<DecodedSemanticToken>();

        for (var i = 0; i < data.GetArrayLength(); i += 5)
        {
            var deltaLine = data[i].GetInt32();
            var deltaCharacter = data[i + 1].GetInt32();
            var length = data[i + 2].GetInt32();
            var tokenType = data[i + 3].GetInt32();
            var tokenModifiers = data[i + 4].GetInt32();

            if (deltaLine == 0)
            {
                character += deltaCharacter;
            }
            else
            {
                line += deltaLine;
                character = deltaCharacter;
            }

            var text = LspSemanticTokenTestHelpers.ExtractTokenText(source, line, character, length);
            decoded.Add(new DecodedSemanticToken(line, character, length, tokenType, tokenModifiers, text));
        }

        return decoded;
    }

    private readonly record struct DecodedSemanticToken(
        int Line,
        int Character,
        int Length,
        int TokenType,
        int TokenModifiers,
        string Text);
}
