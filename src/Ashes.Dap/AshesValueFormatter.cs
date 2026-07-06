using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ashes.Dap;

internal static class AshesValueFormatter
{
    private const int MaxListItems = 16;
    private const int MaxStringBytes = 64;
    private const int MaxDepth = 4;

    public static async Task<string> FormatAsync(
        string rawValue,
        string? type,
        Func<string, Task<string?>> evaluateExpressionAsync)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return rawValue;
        }

        var normalizedType = NormalizeType(type);
        if (string.Equals(normalizedType, "Bool", StringComparison.Ordinal))
        {
            return FormatBool(rawValue);
        }

        if (string.Equals(normalizedType, "Str*", StringComparison.Ordinal))
        {
            var formatted = await FormatStringAsync(rawValue, evaluateExpressionAsync).ConfigureAwait(false);
            return formatted ?? rawValue;
        }

        if (TryGetListElementType(normalizedType, out var elementType))
        {
            var formatted = await FormatListAsync(rawValue, elementType, evaluateExpressionAsync, 0).ConfigureAwait(false);
            return formatted ?? rawValue;
        }

        return rawValue;
    }

    private static async Task<string?> FormatListAsync(
        string rawValue,
        string elementType,
        Func<string, Task<string?>> evaluateExpressionAsync,
        int depth)
    {
        if (depth >= MaxDepth)
        {
            return "[...]";
        }

        if (!TryParsePointer(rawValue, out var address) || address == 0)
        {
            return "[]";
        }

        var items = new List<string>();
        var current = address;
        var remaining = MaxListItems;
        while (current != 0 && remaining-- > 0)
        {
            var currentHex = ToHexPointer(current);
            var headValue = await evaluateExpressionAsync($"*(long long*){currentHex}").ConfigureAwait(false);
            var tailValue = await evaluateExpressionAsync($"*((long long*){currentHex} + 1)").ConfigureAwait(false);
            if (headValue is null || tailValue is null)
            {
                return null;
            }

            items.Add(await FormatElementAsync(headValue, elementType, evaluateExpressionAsync, depth + 1));
            if (!TryParsePointer(tailValue, out current))
            {
                return null;
            }
        }

        var suffix = current != 0 ? ", ..." : string.Empty;
        return $"[{string.Join(", ", items)}{suffix}]";
    }

    private static async Task<string> FormatElementAsync(
        string rawValue,
        string elementType,
        Func<string, Task<string?>> evaluateExpressionAsync,
        int depth)
    {
        var normalizedType = NormalizeType(elementType);
        if (string.Equals(normalizedType, "Int", StringComparison.Ordinal) || string.Equals(normalizedType, "Float", StringComparison.Ordinal))
        {
            return rawValue;
        }

        if (string.Equals(normalizedType, "Bool", StringComparison.Ordinal))
        {
            return FormatBool(rawValue);
        }

        if (string.Equals(normalizedType, "Str*", StringComparison.Ordinal))
        {
            return await FormatStringAsync(rawValue, evaluateExpressionAsync).ConfigureAwait(false) ?? rawValue;
        }

        if (TryGetListElementType(normalizedType, out var nestedElementType))
        {
            return await FormatListAsync(rawValue, nestedElementType, evaluateExpressionAsync, depth).ConfigureAwait(false) ?? rawValue;
        }

        return rawValue;
    }

    private static async Task<string?> FormatStringAsync(
        string rawValue,
        Func<string, Task<string?>> evaluateExpressionAsync)
    {
        if (!TryParsePointer(rawValue, out var address) || address == 0)
        {
            return JsonSerializer.Serialize(string.Empty);
        }

        var addressHex = ToHexPointer(address);
        var lengthValue = await evaluateExpressionAsync($"*(long long*){addressHex}").ConfigureAwait(false);
        if (!long.TryParse(lengthValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length < 0)
        {
            return null;
        }

        var byteCount = (int)Math.Min(length, MaxStringBytes);
        var bytes = new byte[byteCount];
        for (var index = 0; index < byteCount; index++)
        {
            var byteValue = await evaluateExpressionAsync($"*((unsigned char*){addressHex} + {8 + index})").ConfigureAwait(false);
            if (!byte.TryParse(byteValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out bytes[index]))
            {
                return null;
            }
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (length > MaxStringBytes)
        {
            text += "...";
        }

        return JsonSerializer.Serialize(text);
    }

    private static string FormatBool(string rawValue)
    {
        return rawValue switch
        {
            "0" => "false",
            "1" => "true",
            _ => rawValue,
        };
    }

    private static bool TryGetListElementType(string type, out string elementType)
    {
        elementType = string.Empty;
        var normalized = NormalizeType(type);
        if (!normalized.StartsWith("List<", StringComparison.Ordinal) || !normalized.EndsWith(">*", StringComparison.Ordinal))
        {
            return false;
        }

        var inner = normalized[5..^2];
        var depth = 0;
        for (var index = 0; index < inner.Length; index++)
        {
            var ch = inner[index];
            if (ch == '<')
            {
                depth++;
            }
            else if (ch == '>')
            {
                depth--;
            }
        }

        if (depth != 0)
        {
            return false;
        }

        elementType = inner + "*";
        if (!inner.EndsWith("*", StringComparison.Ordinal)
            && (string.Equals(inner, "Int", StringComparison.Ordinal) || string.Equals(inner, "Float", StringComparison.Ordinal) || string.Equals(inner, "Bool", StringComparison.Ordinal)))
        {
            elementType = inner;
        }
        else if (!inner.EndsWith("*", StringComparison.Ordinal) && string.Equals(inner, "Str", StringComparison.Ordinal))
        {
            elementType = "Str*";
        }
        else if (!inner.EndsWith("*", StringComparison.Ordinal) && inner.StartsWith("List<", StringComparison.Ordinal))
        {
            elementType = inner + "*";
        }

        return true;
    }

    private static string NormalizeType(string type)
    {
        return type.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static bool TryParsePointer(string value, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);
    }

    private static string ToHexPointer(ulong address)
    {
        return $"0x{address:x}";
    }
}
