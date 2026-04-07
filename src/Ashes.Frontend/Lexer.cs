using System.Globalization;
using System.Text;

namespace Ashes.Frontend;

public sealed class Lexer
{
    private readonly string _text;
    private readonly Diagnostics _diag;
    private int _pos;

    public Lexer(string text, Diagnostics diag)
    {
        _text = text ?? "";
        _diag = diag;
    }

    public int SavePosition() => _pos;
    public void RestorePosition(int pos) => _pos = pos;

    public Token Next()
    {
        SkipWhite();

        if (_pos >= _text.Length)
        {
            return new Token(TokenKind.EOF, "", 0, _pos, 0);
        }

        int start = _pos;
        char c = _text[_pos];

        if (TryReadDoubleCharacterToken(start, out var token))
        {
            return token;
        }

        if (TryReadSingleCharacterToken(c, start, out token))
        {
            return token;
        }

        if (c == '"')
        {
            return ReadString(start);
        }

        if (char.IsDigit(c))
        {
            return ReadNumber(start);
        }

        if (char.IsLetter(c) || c == '_')
        {
            return ReadIdentifierOrKeyword(start);
        }

        return ReadBadToken(start, c);
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

    private bool TryReadDoubleCharacterToken(int start, out Token token)
    {
        if (TryMatch('|', '?', '>'))
        {
            _pos += 3;
            token = new Token(TokenKind.PipeQuestionGreater, "|?>", 0, start, 3);
            return true;
        }

        if (TryMatch('|', '!', '>'))
        {
            _pos += 3;
            token = new Token(TokenKind.PipeBangGreater, "|!>", 0, start, 3);
            return true;
        }

        if (TryMatch('-', '>'))
        {
            _pos += 2;
            token = new Token(TokenKind.Arrow, "->", 0, start, 2);
            return true;
        }

        if (TryMatch('>', '='))
        {
            _pos += 2;
            token = new Token(TokenKind.GreaterEquals, ">=", 0, start, 2);
            return true;
        }

        if (TryMatch('<', '='))
        {
            _pos += 2;
            token = new Token(TokenKind.LessEquals, "<=", 0, start, 2);
            return true;
        }

        if (TryMatch('=', '='))
        {
            _pos += 2;
            token = new Token(TokenKind.EqualsEquals, "==", 0, start, 2);
            return true;
        }

        if (TryMatch('!', '='))
        {
            _pos += 2;
            token = new Token(TokenKind.BangEquals, "!=", 0, start, 2);
            return true;
        }

        if (TryMatch(':', ':'))
        {
            _pos += 2;
            token = new Token(TokenKind.ColonColon, "::", 0, start, 2);
            return true;
        }

        if (TryMatch('|', '>'))
        {
            _pos += 2;
            token = new Token(TokenKind.PipeGreater, "|>", 0, start, 2);
            return true;
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
            '=' => new Token(TokenKind.Equals, "=", 0, start, 1),
            ',' => new Token(TokenKind.Comma, ",", 0, start, 1),
            '|' => new Token(TokenKind.Pipe, "|", 0, start, 1),
            '(' => new Token(TokenKind.LParen, "(", 0, start, 1),
            ')' => new Token(TokenKind.RParen, ")", 0, start, 1),
            '[' => new Token(TokenKind.LBracket, "[", 0, start, 1),
            ']' => new Token(TokenKind.RBracket, "]", 0, start, 1),
            '.' => new Token(TokenKind.Dot, ".", 0, start, 1),
            _ => default
        };

        if (token.Kind == default)
        {
            return false;
        }

        _pos++;
        return true;
    }

    private bool TryMatch(char first, char second)
    {
        return _pos + 1 < _text.Length && _text[_pos] == first && _text[_pos + 1] == second;
    }

    private bool TryMatch(char first, char second, char third)
    {
        return _pos + 2 < _text.Length
            && _text[_pos] == first
            && _text[_pos + 1] == second
            && _text[_pos + 2] == third;
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

        _diag.Error(start, _pos, "Unterminated string literal.", DiagnosticCodes.ParseError);
        return new Token(TokenKind.String, sb.ToString(), 0, start, _pos - start);
    }

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
                _diag.Error(start, _pos, $"Invalid float literal: {floatText}.", DiagnosticCodes.ParseError);
            }

            return new Token(TokenKind.Float, floatText, 0, floatValue, start, _pos - start);
        }

        var text = _text[start.._pos];
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            _diag.Error(start, _pos, $"Invalid integer literal: {text}.", DiagnosticCodes.ParseError);
        }

        return new Token(TokenKind.Int, text, value, start, _pos - start);
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
            "as" => TokenKind.As,
            "let" => TokenKind.Let,
            "rec" => TokenKind.Rec,
            "in" => TokenKind.In,
            "if" => TokenKind.If,
            "then" => TokenKind.Then,
            "else" => TokenKind.Else,
            "match" => TokenKind.Match,
            "with" => TokenKind.With,
            "fun" => TokenKind.Fun,
            "true" => TokenKind.True,
            "false" => TokenKind.False,
            "type" => TokenKind.Type,
            "async" => TokenKind.Async,
            "await" => TokenKind.Await,
            _ => TokenKind.Ident
        };
    }

    private Token ReadBadToken(int start, char c)
    {
        _diag.Error(start, start + 1, $"Unexpected character: '{c}'.", DiagnosticCodes.ParseError);
        _pos++;
        return new Token(TokenKind.Bad, _text[start.._pos], 0, start, _pos - start);
    }
}
