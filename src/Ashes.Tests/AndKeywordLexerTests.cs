using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class AndKeywordLexerTests
{
    [Test]
    public void Next_should_tokenize_and_as_keyword()
    {
        var tokens = LexAll("and");

        tokens[0].Kind.ShouldBe(TokenKind.And);
        tokens[0].Text.ShouldBe("and");
        tokens[1].Kind.ShouldBe(TokenKind.EOF);
    }

    [Test]
    public void And_should_lex_as_keyword_within_let_rec_group()
    {
        var tokens = LexAll("let recursive f = 1 and g = 2");

        tokens.Select(t => t.Kind).ShouldBe(
        [
            TokenKind.Let,
            TokenKind.Recursive,
            TokenKind.Ident,
            TokenKind.Equals,
            TokenKind.Int,
            TokenKind.And,
            TokenKind.Ident,
            TokenKind.Equals,
            TokenKind.Int,
            TokenKind.EOF
        ]);
    }

    [Test]
    public void And_should_not_lex_as_identifier_elsewhere()
    {
        var tokens = LexAll("and");

        tokens[0].Kind.ShouldNotBe(TokenKind.Ident);
    }

    [Test]
    public void Surrounding_identifiers_containing_and_are_unaffected()
    {
        var tokens = LexAll("android andFoo band");

        tokens.Select(t => t.Kind).ShouldBe(
        [
            TokenKind.Ident,
            TokenKind.Ident,
            TokenKind.Ident,
            TokenKind.EOF
        ]);
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
