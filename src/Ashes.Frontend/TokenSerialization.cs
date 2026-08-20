using System.Globalization;
using System.Text;

namespace Ashes.Frontend;

/// <summary>
/// Serializes the public token stream into the versioned, implementation-neutral parity format used
/// by bootstrap tests. Text is represented as lowercase UTF-8 hex so every record remains one line.
/// </summary>
public static class TokenSerialization
{
    /// <summary>The schema marker written as the first line of every serialized token stream.</summary>
    public const string Schema = "ashes-token-v1";

    /// <summary>
    /// Serializes <paramref name="tokens"/> as tab-separated kind, text hex, integer payload, float
    /// payload, UTF-8 byte position, and UTF-8 byte length fields. Every line, including the schema
    /// and final token, ends in a line-feed byte.
    /// </summary>
    public static string Serialize(IReadOnlyList<Token> tokens)
    {
        var builder = new StringBuilder();
        builder.Append(Schema).Append('\n');

        foreach (Token token in tokens)
        {
            builder.Append(token.Kind)
                .Append('\t')
                .Append(ToUtf8Hex(token.Text))
                .Append('\t')
                .Append(token.IntValue.ToString(CultureInfo.InvariantCulture))
                .Append('\t')
                .Append(SerializeFloatPayload(token))
                .Append('\t')
                .Append(token.Position.ToString(CultureInfo.InvariantCulture))
                .Append('\t')
                .Append(token.Length.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string ToUtf8Hex(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte valueByte in bytes)
        {
            builder.Append(valueByte.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string SerializeFloatPayload(Token token)
    {
        if (token.Kind == TokenKind.Float
            && double.TryParse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double decoded)
            && BitConverter.DoubleToInt64Bits(decoded) == BitConverter.DoubleToInt64Bits(token.FloatValue))
        {
            return token.Text;
        }

        return BitConverter.DoubleToInt64Bits(token.FloatValue) == 0
            ? "0"
            : token.FloatValue.ToString("R", CultureInfo.InvariantCulture);
    }
}
