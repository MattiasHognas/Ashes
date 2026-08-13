using System.Globalization;
using System.Text;

namespace Ashes.Frontend;

/// <summary>
/// Forward-only tokenizer that turns source text into a stream of <see cref="Token"/> values, one
/// per <see cref="Next"/> call. It skips whitespace and <c>//</c> line comments, recognizes keywords,
/// operators, and literals (integer, unsigned-suffixed, float, big-int, string), and reports lexical
/// errors into a <see cref="Diagnostics"/> sink. <see cref="SavePosition"/> / <see cref="RestorePosition"/>
/// give the parser bounded backtracking.
/// </summary>
public sealed class Lexer
{
    private readonly string _text;
    private readonly Diagnostics _diag;
    private readonly SourceTextIndex _sourceIndex;
    private int _pos;

    /// <summary>
    /// Creates a lexer over <paramref name="text"/> (a null source is treated as empty), reporting
    /// lexical errors into <paramref name="diag"/>.
    /// </summary>
    public Lexer(string text, Diagnostics diag)
    {
        _text = text ?? "";
        _diag = diag;
        _sourceIndex = new SourceTextIndex(_text);
    }

    /// <summary>Captures the current byte offset so it can later be handed back to
    /// <see cref="RestorePosition(int)"/> for bounded lookahead/backtracking.</summary>
    public int SavePosition() => _sourceIndex.ToUtf8Offset(_pos);

    /// <summary>Rewinds the lexer to a position previously returned by <see cref="SavePosition"/>.</summary>
    public void RestorePosition(int pos) => _pos = _sourceIndex.GetUtf16Offset(pos);

    /// <summary>
    /// Scans and returns the next token, advancing past it. Whitespace and <c>//</c> line comments are
    /// skipped first; at end of input a <see cref="TokenKind.EOF"/> token is returned.
    /// </summary>
    public Token Next()
    {
        SkipWhite();

        if (_pos >= _text.Length)
        {
            return ToSourceToken(new Token(TokenKind.EOF, "", 0, _pos, 0));
        }

        int start = _pos;
        char c = _text[_pos];

        if (TryReadDoubleCharacterToken(start, out var token))
        {
            return ToSourceToken(token);
        }

        if (TryReadSingleCharacterToken(c, start, out token))
        {
            return ToSourceToken(token);
        }

        if (c == '"')
        {
            return ToSourceToken(ReadString(start));
        }

        if (c == '\'')
        {
            return ToSourceToken(ReadRune(start));
        }

        if (char.IsDigit(c))
        {
            return ToSourceToken(ReadNumber(start));
        }

        if (char.IsLetter(c) || c == '_')
        {
            return ToSourceToken(ReadIdentifierOrKeyword(start));
        }

        return ToSourceToken(ReadBadToken(start, c));
    }

    private Token ToSourceToken(Token token)
    {
        int start = _sourceIndex.ToUtf8Offset(token.Position);
        int end = _sourceIndex.ToUtf8Offset(token.End);
        return token with { Position = start, Length = end - start };
    }

    private void Error(int utf16Start, int utf16End, string message)
    {
        _diag.Error(
            _sourceIndex.ToUtf8Offset(utf16Start),
            _sourceIndex.ToUtf8Offset(utf16End),
            message,
            DiagnosticCodes.ParseError);
    }

    private void SkipWhite()
    {
        while (_pos < _text.Length)
        {
            if (char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
                continue;
            }

            if (_pos + 1 < _text.Length && _text[_pos] == '/' && _text[_pos + 1] == '/')
            {
                _pos += 2;
                while (_pos < _text.Length && _text[_pos] != '\n')
                {
                    _pos++;
                }
                continue;
            }

            break;
        }
    }

    // Ordered longest-first so maximal munch holds among shared prefixes
    // (e.g. "|?>" before "|>", "<=" and "<<" before "<").
    private static readonly (string Text, TokenKind Kind)[] MultiCharacterTokens =
    {
        ("|?>", TokenKind.PipeQuestionGreater),
        ("|!>", TokenKind.PipeBangGreater),
        ("->", TokenKind.Arrow),
        (">=", TokenKind.GreaterEquals),
        ("<=", TokenKind.LessEquals),
        ("==", TokenKind.EqualsEquals),
        ("!=", TokenKind.BangEquals),
        ("<<", TokenKind.LessLess),
        (">>", TokenKind.GreaterGreater),
        ("::", TokenKind.ColonColon),
        ("|>", TokenKind.PipeGreater),
        ("<", TokenKind.LessThan),
        (">", TokenKind.GreaterThan),
    };

    private bool TryReadDoubleCharacterToken(int start, out Token token)
    {
        foreach ((string text, TokenKind kind) in MultiCharacterTokens)
        {
            if (_text.AsSpan(_pos).StartsWith(text, StringComparison.Ordinal))
            {
                _pos += text.Length;
                token = new Token(kind, text, 0, start, text.Length);
                return true;
            }
        }

        token = default;
        return false;
    }

    private bool TryReadSingleCharacterToken(char c, int start, out Token token)
    {
        token = c switch
        {
            '+' => new Token(TokenKind.Plus, "+", 0, start, 1),
            '-' => new Token(TokenKind.Minus, "-", 0, start, 1),
            '*' => new Token(TokenKind.Star, "*", 0, start, 1),
            '/' => new Token(TokenKind.Slash, "/", 0, start, 1),
            '%' => new Token(TokenKind.Percent, "%", 0, start, 1),
            '~' => new Token(TokenKind.Tilde, "~", 0, start, 1),
            '&' => new Token(TokenKind.Ampersand, "&", 0, start, 1),
            '^' => new Token(TokenKind.Caret, "^", 0, start, 1),
            '!' => new Token(TokenKind.Bang, "!", 0, start, 1),
            '=' => new Token(TokenKind.Equals, "=", 0, start, 1),
            ',' => new Token(TokenKind.Comma, ",", 0, start, 1),
            '|' => new Token(TokenKind.Pipe, "|", 0, start, 1),
            '(' => new Token(TokenKind.LParen, "(", 0, start, 1),
            ')' => new Token(TokenKind.RParen, ")", 0, start, 1),
            '[' => new Token(TokenKind.LBracket, "[", 0, start, 1),
            ']' => new Token(TokenKind.RBracket, "]", 0, start, 1),
            '.' => new Token(TokenKind.Dot, ".", 0, start, 1),
            ':' => new Token(TokenKind.Colon, ":", 0, start, 1),
            '{' => new Token(TokenKind.LBrace, "{", 0, start, 1),
            '}' => new Token(TokenKind.RBrace, "}", 0, start, 1),
            _ => default
        };

        if (token.Kind == default)
        {
            return false;
        }

        _pos++;
        return true;
    }

    private Token ReadString(int start)
    {
        _pos++;
        var sb = new StringBuilder();

        while (_pos < _text.Length)
        {
            char ch = _text[_pos++];

            if (ch == '"')
            {
                return new Token(TokenKind.String, sb.ToString(), 0, start, _pos - start);
            }

            if (ch == '\\' && _pos < _text.Length)
            {
                char esc = _text[_pos++];
                sb.Append(esc switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => esc
                });
                continue;
            }

            sb.Append(ch);
        }

        Error(start, _pos, "Unterminated string literal.");
        return new Token(TokenKind.String, sb.ToString(), 0, start, _pos - start);
    }

    private Token ReadRune(int start)
    {
        _pos++;
        int value = 0xFFFD;
        bool valid = true;
        if (_pos >= _text.Length || _text[_pos] is '\r' or '\n' or '\'')
        {
            valid = false;
        }
        else if (_text[_pos] == '\\')
        {
            _pos++;
            valid = TryReadRuneEscape(out value);
        }
        else if (char.IsHighSurrogate(_text[_pos]))
        {
            if (_pos + 1 < _text.Length && char.IsLowSurrogate(_text[_pos + 1]))
            {
                value = char.ConvertToUtf32(_text[_pos], _text[_pos + 1]);
                _pos += 2;
            }
            else
            {
                valid = false;
                _pos++;
            }
        }
        else if (char.IsLowSurrogate(_text[_pos]))
        {
            valid = false;
            _pos++;
        }
        else
        {
            value = _text[_pos++];
        }

        if (_pos >= _text.Length || _text[_pos] != '\'')
        {
            valid = false;
            while (_pos < _text.Length && _text[_pos] is not ('\'' or '\r' or '\n'))
            {
                _pos++;
            }
        }

        if (_pos < _text.Length && _text[_pos] == '\'')
        {
            _pos++;
        }

        if (!valid || !IsValidUnicodeScalar(value))
        {
            Error(start, _pos, "A rune literal must contain exactly one valid Unicode scalar value.");
            value = 0xFFFD;
        }

        return new Token(TokenKind.Rune, _text[start.._pos], value, start, _pos - start);
    }

    private bool TryReadRuneEscape(out int value)
    {
        value = 0xFFFD;
        if (_pos >= _text.Length)
        {
            return false;
        }

        char escape = _text[_pos++];
        value = escape switch
        {
            '\\' => '\\',
            '\'' => '\'',
            'n' => '\n',
            'r' => '\r',
            't' => '\t',
            '0' => 0,
            _ => value,
        };
        if (escape != 'u')
        {
            return escape is '\\' or '\'' or 'n' or 'r' or 't' or '0';
        }

        if (_pos >= _text.Length || _text[_pos++] != '{')
        {
            return false;
        }

        int digitsStart = _pos;
        while (_pos < _text.Length && _pos - digitsStart < 6 && Uri.IsHexDigit(_text[_pos]))
        {
            _pos++;
        }

        if (_pos == digitsStart || _pos >= _text.Length || _text[_pos++] != '}')
        {
            return false;
        }

        return int.TryParse(_text[digitsStart..(_pos - 1)], NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture, out value) && IsValidUnicodeScalar(value);
    }

    private static bool IsValidUnicodeScalar(int value) =>
        value is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF);

    private Token ReadNumber(int start)
    {
        while (_pos < _text.Length && char.IsDigit(_text[_pos]))
        {
            _pos++;
        }

        if (_pos + 1 < _text.Length
            && _text[_pos] == '.'
            && char.IsDigit(_text[_pos + 1]))
        {
            _pos++;
            while (_pos < _text.Length && char.IsDigit(_text[_pos]))
            {
                _pos++;
            }

            var floatText = _text[start.._pos];
            if (!double.TryParse(floatText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var floatValue))
            {
                Error(start, _pos, $"Invalid float literal: {floatText}.");
            }

            return new Token(TokenKind.Float, floatText, 0, floatValue, start, _pos - start);
        }

        int digitsEnd = _pos;
        if (_pos < _text.Length && _text[_pos] == 'N')
        {
            _pos++;
            return new Token(TokenKind.BigInt, _text[start..digitsEnd], 0, start, _pos - start);
        }
        int unsignedBits = TryReadUnsignedSuffix();
        var text = _text[start.._pos];
        var numberText = _text[start..digitsEnd];
        if (unsignedBits > 0)
        {
            if (!ulong.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var unsignedValue))
            {
                Error(start, _pos, $"Invalid unsigned integer literal: {text}.");
                unsignedValue = 0;
            }
            else if (unsignedValue > GetUnsignedMax(unsignedBits))
            {
                Error(start, _pos, $"Unsigned integer literal out of range for u{unsignedBits}: {text}.");
                unsignedValue = 0;
            }

            return new Token(TokenKind.Int, text, unchecked((long)unsignedValue), start, _pos - start);
        }

        if (!long.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            Error(start, _pos, $"Invalid integer literal: {numberText}.");
        }

        return new Token(TokenKind.Int, text, value, start, _pos - start);
    }

    private int TryReadUnsignedSuffix()
    {
        if (_pos + 1 >= _text.Length || _text[_pos] != 'u')
        {
            return 0;
        }

        if (TryMatchSuffix("u8"))
        {
            _pos += 2;
            return 8;
        }

        if (TryMatchSuffix("u16"))
        {
            _pos += 3;
            return 16;
        }

        if (TryMatchSuffix("u32"))
        {
            _pos += 3;
            return 32;
        }

        if (TryMatchSuffix("u64"))
        {
            _pos += 3;
            return 64;
        }

        return 0;
    }

    private bool TryMatchSuffix(string suffix)
    {
        if (_pos + suffix.Length > _text.Length)
        {
            return false;
        }

        for (int i = 0; i < suffix.Length; i++)
        {
            if (_text[_pos + i] != suffix[i])
            {
                return false;
            }
        }

        int next = _pos + suffix.Length;
        if (next < _text.Length && (char.IsLetterOrDigit(_text[next]) || _text[next] == '_'))
        {
            return false;
        }

        return true;
    }

    private static ulong GetUnsignedMax(int bits)
    {
        return bits switch
        {
            8 => byte.MaxValue,
            16 => ushort.MaxValue,
            32 => uint.MaxValue,
            64 => ulong.MaxValue,
            _ => 0
        };
    }

    private Token ReadIdentifierOrKeyword(int start)
    {
        while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '_'))
        {
            _pos++;
        }

        var text = _text[start.._pos];
        if (string.Equals(text, "let", StringComparison.Ordinal) && _pos < _text.Length && _text[_pos] == '?')
        {
            _pos++;
            return new Token(TokenKind.LetQuestion, "let?", 0, start, _pos - start);
        }

        if (string.Equals(text, "let", StringComparison.Ordinal) && _pos < _text.Length && _text[_pos] == '!')
        {
            _pos++;
            return new Token(TokenKind.LetBang, "let!", 0, start, _pos - start);
        }

        return new Token(GetIdentifierTokenKind(text), text, 0, start, _pos - start);
    }

    private static TokenKind GetIdentifierTokenKind(string text)
    {
        return text switch
        {
            "let" => TokenKind.Let,
            "recursive" => TokenKind.Recursive,
            "in" => TokenKind.In,
            "and" => TokenKind.And,
            "if" => TokenKind.If,
            "then" => TokenKind.Then,
            "else" => TokenKind.Else,
            "match" => TokenKind.Match,
            "with" => TokenKind.With,
            "when" => TokenKind.When,
            "given" => TokenKind.Given,
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            "type" => TokenKind.Type,
            "await" => TokenKind.Await,
            "external" => TokenKind.External,
            "capability" => TokenKind.Capability,
            "needs" => TokenKind.Needs,
            "provide" => TokenKind.Provide,
            "perform" => TokenKind.Perform,
            "handle" => TokenKind.Handle,
            "trait" => TokenKind.Trait,
            "implement" => TokenKind.Implement,
            "requires" => TokenKind.Requires,
            "deriving" => TokenKind.Deriving,
            _ => TokenKind.Ident
        };
    }

    private Token ReadBadToken(int start, char c)
    {
        int consumed = char.IsHighSurrogate(c)
            && start + 1 < _text.Length
            && char.IsLowSurrogate(_text[start + 1])
                ? 2
                : 1;
        Error(start, start + consumed, $"Unexpected character: '{_text[start..(start + consumed)]}'.");
        _pos += consumed;
        return new Token(TokenKind.Bad, _text[start.._pos], 0, start, _pos - start);
    }
}
