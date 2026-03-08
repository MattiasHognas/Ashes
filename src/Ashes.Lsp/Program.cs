using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ashes.Formatter;

namespace Ashes.Lsp;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Dictionary<string, string> Documents = new(StringComparer.OrdinalIgnoreCase);

    public static int Main()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();

        var shutdownRequested = false;

        while (TryReadMessage(input, out var payload))
        {
            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;

            if (!root.TryGetProperty("method", out var methodElement))
            {
                continue;
            }

            var method = methodElement.GetString() ?? string.Empty;
            var hasId = root.TryGetProperty("id", out var id);
            root.TryGetProperty("params", out var parameters);

            switch (method)
            {
                case "initialize":
                    if (hasId)
                    {
                        SendResponse(output, id, new
                        {
                            capabilities = new
                            {
                                textDocumentSync = 1,
                                documentFormattingProvider = true,
                                hoverProvider = true,
                                definitionProvider = true,
                                semanticTokensProvider = new
                                {
                                    legend = new
                                    {
                                        tokenTypes = DocumentService.SemanticTokenTypes,
                                        tokenModifiers = Array.Empty<string>()
                                    },
                                    full = true
                                },
                                completionProvider = new { }
                            }
                        });
                    }
                    break;

                case "shutdown":
                    shutdownRequested = true;
                    if (hasId)
                    {
                        SendResponse(output, id, (object?)null);
                    }

                    break;

                case "exit":
                    return shutdownRequested ? 0 : 1;

                case "textDocument/didOpen":
                    HandleDidOpen(parameters, output);
                    break;

                case "textDocument/didChange":
                    HandleDidChange(parameters, output);
                    break;

                case "textDocument/didClose":
                    HandleDidClose(parameters, output);
                    break;

                case "textDocument/formatting":
                    if (hasId)
                    {
                        HandleFormatting(parameters, output, id);
                    }

                    break;

                case "textDocument/semanticTokens/full":
                    if (hasId)
                    {
                        HandleSemanticTokens(parameters, output, id);
                    }

                    break;

                case "textDocument/completion":
                    if (hasId)
                    {
                        HandleCompletion(parameters, output, id);
                    }

                    break;

                case "textDocument/hover":
                    if (hasId)
                    {
                        HandleHover(parameters, output, id);
                    }

                    break;

                case "textDocument/definition":
                    if (hasId)
                    {
                        HandleDefinition(parameters, output, id);
                    }

                    break;

                default:
                    if (hasId)
                    {
                        SendResponse(output, id, (object?)null);
                    }

                    break;
            }
        }

        return 0;
    }

    private static void HandleDidOpen(JsonElement parameters, Stream output)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        var text = textDocument.GetProperty("text").GetString() ?? string.Empty;
        if (uri is null)
        {
            return;
        }

        Documents[uri] = text;
        PublishDiagnostics(output, uri, text);
    }

    private static void HandleDidChange(JsonElement parameters, Stream output)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        if (uri is null)
        {
            return;
        }

        if (!parameters.TryGetProperty("contentChanges", out var changes) || changes.GetArrayLength() == 0)
        {
            return;
        }

        var text = changes[changes.GetArrayLength() - 1].GetProperty("text").GetString() ?? string.Empty;
        Documents[uri] = text;
        PublishDiagnostics(output, uri, text);
    }

    private static void HandleDidClose(JsonElement parameters, Stream output)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        if (uri is null)
        {
            return;
        }

        Documents.Remove(uri);
        SendNotification(output, "textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<object>() });
    }

    private static string? UriToFilePath(string? uri)
    {
        if (uri is null)
        {
            return null;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            return null;
        }

        return parsed.LocalPath;
    }

    private static void HandleFormatting(JsonElement parameters, Stream output, JsonElement id)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        if (uri is null || !Documents.TryGetValue(uri, out var source))
        {
            SendResponse(output, id, Array.Empty<object>());
            return;
        }

        var filePath = UriToFilePath(uri);
        var formattingOptions = EditorConfigFormattingOptionsResolver.ResolveForPath(filePath);

        if (parameters.TryGetProperty("options", out var requestOptions))
        {
            if (requestOptions.TryGetProperty("insertSpaces", out var insertSpacesElement) &&
                insertSpacesElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var insertSpaces = insertSpacesElement.GetBoolean();
                formattingOptions = formattingOptions with { UseTabs = !insertSpaces };
            }

            if (requestOptions.TryGetProperty("tabSize", out var tabSizeElement) &&
                tabSizeElement.TryGetInt32(out var tabSize) &&
                tabSize > 0)
            {
                formattingOptions = formattingOptions with { IndentSize = tabSize };
            }
        }

        var formatted = DocumentService.Format(source, filePath, formattingOptions);
        if (formatted is null)
        {
            SendResponse(output, id, Array.Empty<object>());
            return;
        }

        var range = FullDocumentRange(source);
        var edits = new[] { new { range, newText = formatted } };
        SendResponse(output, id, edits);
    }

    private static object FullDocumentRange(string text)
    {
        var lines = text.Split('\n');
        var line = Math.Max(lines.Length - 1, 0);
        var character = lines.Length == 0 ? 0 : lines[^1].Length;
        return new
        {
            start = new { line = 0, character = 0 },
            end = new { line, character }
        };
    }

    private static void HandleSemanticTokens(JsonElement parameters, Stream output, JsonElement id)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        if (uri is null || !Documents.TryGetValue(uri, out var source))
        {
            SendResponse(output, id, new { data = Array.Empty<int>() });
            return;
        }

        var tokens = DocumentService.GetSemanticTokens(source, UriToFilePath(uri));
        var data = EncodeSemanticTokens(tokens);
        SendResponse(output, id, new { data });
    }

    private static void HandleCompletion(JsonElement parameters, Stream output, JsonElement id)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        if (uri is null || !Documents.TryGetValue(uri, out var source))
        {
            SendResponse(output, id, Array.Empty<object>());
            return;
        }

        var constructors = DocumentService.GetCompletions(source, UriToFilePath(uri));
        var items = constructors
            .Select(name => new { label = name, kind = 20 }) // kind 20 = EnumMember in LSP
            .ToArray();
        SendResponse(output, id, items);
    }

    private static void HandleHover(JsonElement parameters, Stream output, JsonElement id)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        if (uri is null || !Documents.TryGetValue(uri, out var source))
        {
            SendResponse(output, id, (object?)null);
            return;
        }

        var position = parameters.GetProperty("position");
        var line = position.GetProperty("line").GetInt32();
        var character = position.GetProperty("character").GetInt32();
        var lineStarts = LspTextUtils.GetLineStarts(source);
        var absolutePosition = LspTextUtils.FromLineCharacter(lineStarts, source.Length, line, character);

        var hover = DocumentService.GetHover(source, absolutePosition, UriToFilePath(uri));
        if (hover is null)
        {
            SendResponse(output, id, (object?)null);
            return;
        }

        var (startLine, startCharacter) = LspTextUtils.ToLineCharacter(lineStarts, source.Length, hover.Value.Start);
        var (endLine, endCharacter) = LspTextUtils.ToLineCharacter(lineStarts, source.Length, hover.Value.End);
        SendResponse(output, id, new
        {
            contents = hover.Value.Contents,
            range = new
            {
                start = new { line = startLine, character = startCharacter },
                end = new { line = endLine, character = endCharacter }
            }
        });
    }

    private static void HandleDefinition(JsonElement parameters, Stream output, JsonElement id)
    {
        var textDocument = parameters.GetProperty("textDocument");
        var uri = textDocument.GetProperty("uri").GetString();
        if (uri is null || !Documents.TryGetValue(uri, out var source))
        {
            SendResponse(output, id, (object?)null);
            return;
        }

        var position = parameters.GetProperty("position");
        var line = position.GetProperty("line").GetInt32();
        var character = position.GetProperty("character").GetInt32();
        var lineStarts = LspTextUtils.GetLineStarts(source);
        var absolutePosition = LspTextUtils.FromLineCharacter(lineStarts, source.Length, line, character);

        var currentFilePath = UriToFilePath(uri);
        var definition = DocumentService.GetDefinition(source, absolutePosition, currentFilePath);
        if (definition is null)
        {
            SendResponse(output, id, (object?)null);
            return;
        }

        var definitionUri = definition.Value.FilePath is null
            ? uri
            : new Uri(definition.Value.FilePath).AbsoluteUri;
        var definitionSource = definition.Value.FilePath is not null
            && !string.Equals(definition.Value.FilePath, currentFilePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(definition.Value.FilePath)
                ? File.ReadAllText(definition.Value.FilePath)
                : source;
        var definitionLineStarts = LspTextUtils.GetLineStarts(definitionSource);
        var (startLine, startCharacter) = LspTextUtils.ToLineCharacter(definitionLineStarts, definitionSource.Length, definition.Value.Start);
        var (endLine, endCharacter) = LspTextUtils.ToLineCharacter(definitionLineStarts, definitionSource.Length, definition.Value.End);

        SendResponse(output, id, new
        {
            uri = definitionUri,
            range = new
            {
                start = new { line = startLine, character = startCharacter },
                end = new { line = endLine, character = endCharacter }
            }
        });
    }

    private static int[] EncodeSemanticTokens(IReadOnlyList<DocumentService.SemanticTokenItem> tokens)
    {
        var data = new List<int>(tokens.Count * 5);
        var prevLine = 0;
        var prevChar = 0;

        foreach (var tok in tokens.OrderBy(t => t.Line).ThenBy(t => t.Character))
        {
            var deltaLine = tok.Line - prevLine;
            var deltaChar = deltaLine == 0 ? tok.Character - prevChar : tok.Character;
            data.Add(deltaLine);
            data.Add(deltaChar);
            data.Add(tok.Length);
            data.Add(tok.TokenType);
            data.Add(tok.TokenModifiers);
            prevLine = tok.Line;
            prevChar = tok.Character;
        }

        return data.ToArray();
    }

    private static void PublishDiagnostics(Stream output, string uri, string source)
    {
        var filePath = UriToFilePath(uri);
        var lineStarts = LspTextUtils.GetLineStarts(source);
        var diagnostics = DocumentService.Analyze(source, filePath)
            .Select(d =>
            {
                var (startLine, startCharacter) = LspTextUtils.ToLineCharacter(lineStarts, source.Length, d.Start);
                var (endLine, endCharacter) = LspTextUtils.ToLineCharacter(lineStarts, source.Length, d.End);
                return new
                {
                    range = new
                    {
                        start = new { line = startLine, character = startCharacter },
                        end = new { line = endLine, character = endCharacter }
                    },
                    severity = 1,
                    source = "Ashes",
                    code = d.Code,
                    message = d.Message
                };
            })
            .ToArray();

        SendNotification(output, "textDocument/publishDiagnostics", new { uri, diagnostics });
    }

    private static bool TryReadMessage(Stream input, out string payload)
    {
        payload = string.Empty;

        var contentLength = -1;
        while (true)
        {
            var headerLine = ReadHeaderLine(input);
            if (headerLine is null)
            {
                return false;
            }

            if (headerLine.Length == 0)
            {
                break;
            }

            if (headerLine.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = headerLine["Content-Length:".Length..].Trim();
                if (int.TryParse(value, out var parsed))
                {
                    contentLength = parsed;
                }
            }
        }

        if (contentLength <= 0)
        {
            return false;
        }

        var body = new byte[contentLength];
        var read = 0;
        while (read < contentLength)
        {
            var n = input.Read(body, read, contentLength - read);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        payload = Encoding.UTF8.GetString(body);
        return true;
    }

    private static string? ReadHeaderLine(Stream input)
    {
        using var ms = new MemoryStream();

        while (true)
        {
            var b = input.ReadByte();
            if (b < 0)
            {
                return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray());
            }

            if (b == '\r')
            {
                var next = input.ReadByte();
                if (next == '\n')
                {
                    return Encoding.ASCII.GetString(ms.ToArray());
                }

                if (next >= 0)
                {
                    ms.WriteByte((byte)next);
                }
            }
            else
            {
                ms.WriteByte((byte)b);
            }
        }
    }

    private static void SendNotification(Stream output, string method, object @params)
    {
        WriteMessage(output, new
        {
            jsonrpc = "2.0",
            method,
            @params
        });
    }

    private static void SendResponse(Stream output, JsonElement id, object? result)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            JsonSerializer.Serialize(writer, result, JsonOptions);
            writer.WriteEndObject();
        }

        WritePayload(output, ms.ToArray());
    }

    private static void WriteMessage(Stream output, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        WritePayload(output, json);
    }

    private static void WritePayload(Stream output, byte[] json)
    {
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
        output.Write(header, 0, header.Length);
        output.Write(json, 0, json.Length);
        output.Flush();
    }
}
