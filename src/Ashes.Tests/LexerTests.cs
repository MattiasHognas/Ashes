using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class LexerTests
{
    [Test]
    public void Next_should_tokenize_keywords_operators_and_literals()
    {
        var tokens = LexAll("let let? let! rec in print if then else match with fun true false type async await foo >= <= == != -> :: |> |?> |!> + - * / = , | ( ) [ ] 123 1.5");

        tokens.Select(t => t.Kind).ShouldBe(
        [
            TokenKind.Let,
            TokenKind.LetQuestion,
            TokenKind.LetBang,
            TokenKind.Rec,
            TokenKind.In,
            TokenKind.Ident,
            TokenKind.If,
            TokenKind.Then,
            TokenKind.Else,
            TokenKind.Match,
            TokenKind.With,
            TokenKind.Fun,
            TokenKind.True,
            TokenKind.False,
            TokenKind.Type,
            TokenKind.Async,
            TokenKind.Await,
            TokenKind.Ident,
            TokenKind.GreaterEquals,
            TokenKind.LessEquals,
            TokenKind.EqualsEquals,
            TokenKind.BangEquals,
            TokenKind.Arrow,
            TokenKind.ColonColon,
            TokenKind.PipeGreater,
            TokenKind.PipeQuestionGreater,
            TokenKind.PipeBangGreater,
            TokenKind.Plus,
            TokenKind.Minus,
            TokenKind.Star,
            TokenKind.Slash,
            TokenKind.Equals,
            TokenKind.Comma,
            TokenKind.Pipe,
            TokenKind.LParen,
            TokenKind.RParen,
            TokenKind.LBracket,
            TokenKind.RBracket,
            TokenKind.Int,
            TokenKind.Float,
            TokenKind.EOF
        ]);
    }

    [Test]
    public void Next_should_tokenize_float_literal_with_invariant_value()
    {
        var tokens = LexAll("3.14");

        tokens[0].Kind.ShouldBe(TokenKind.Float);
        tokens[0].Text.ShouldBe("3.14");
        tokens[0].FloatValue.ShouldBe(3.14);
        tokens[1].Kind.ShouldBe(TokenKind.EOF);
    }

    [Test]
    public void Next_should_unescape_string_literal()
    {
        var tokens = LexAll("\"a\\\\b\\\"c\\nd\\re\\tf\"");

        tokens[0].Kind.ShouldBe(TokenKind.String);
        tokens[0].Text.ShouldBe("a\\b\"c\nd\re\tf");
        tokens[1].Kind.ShouldBe(TokenKind.EOF);
    }

    [Test]
    public void Next_should_report_unterminated_string_literal()
    {
        var diag = new Diagnostics();
        var lexer = new Lexer("\"abc", diag);

        var token = lexer.Next();

        token.Kind.ShouldBe(TokenKind.String);
        token.Text.ShouldBe("abc");
        diag.Errors.ShouldBe(["[pos 0] Unterminated string literal."]);
    }

    [Test]
    public void Next_should_report_bad_character()
    {
        var diag = new Diagnostics();
        var lexer = new Lexer("@", diag);

        var token = lexer.Next();

        token.Kind.ShouldBe(TokenKind.Bad);
        token.Text.ShouldBe("@");
        diag.Errors.ShouldBe(["[pos 0] Unexpected character: '@'."]);
    }

    [Test]
    public void Next_should_report_invalid_integer_with_period()
    {
        var diag = new Diagnostics();
        var lexer = new Lexer("999999999999999999999999999999999999999", diag);

        _ = lexer.Next();

        diag.Errors.ShouldContain(x => x.Contains("Invalid integer literal:", StringComparison.Ordinal));
        diag.Errors.ShouldContain(x => x.EndsWith(".", StringComparison.Ordinal));
    }

    [Test]
    public void Next_should_skip_line_comments()
    {
        var tokens = LexAll("// comment\nlet // inline\nx = 1");

        tokens.Select(t => t.Kind).ShouldBe(
        [
            TokenKind.Let,
            TokenKind.Ident,
            TokenKind.Equals,
            TokenKind.Int,
            TokenKind.EOF
        ]);
    }

    [Test]
    public void Next_should_tokenize_async_keyword()
    {
        var tokens = LexAll("async");
        tokens[0].Kind.ShouldBe(TokenKind.Async);
        tokens[0].Text.ShouldBe("async");
    }

    [Test]
    public void Next_should_tokenize_await_keyword()
    {
        var tokens = LexAll("await");
        tokens[0].Kind.ShouldBe(TokenKind.Await);
        tokens[0].Text.ShouldBe("await");
    }

    [Test]
    public void Next_should_tokenize_let_bang()
    {
        var tokens = LexAll("let! x = 1 in x");
        tokens[0].Kind.ShouldBe(TokenKind.LetBang);
        tokens[0].Text.ShouldBe("let!");
        tokens[1].Kind.ShouldBe(TokenKind.Ident);
        tokens[1].Text.ShouldBe("x");
    }

    [Test]
    public void Next_should_treat_async_prefix_identifier_as_ident()
    {
        var tokens = LexAll("asyncFoo");
        tokens[0].Kind.ShouldBe(TokenKind.Ident);
        tokens[0].Text.ShouldBe("asyncFoo");
    }

    private static List<Token> LexAll(string source)
    {
        var diag = new Diagnostics();
        var lexer = new Lexer(source, diag);
        var tokens = new List<Token>();

        while (true)
        {
            var token = lexer.Next();
            tokens.Add(token);
            if (token.Kind == TokenKind.EOF)
            {
                break;
            }
        }

        diag.Errors.ShouldBeEmpty();
        return tokens;
    }
}
