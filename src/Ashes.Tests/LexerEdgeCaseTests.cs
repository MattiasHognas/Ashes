using Ashes.Frontend;
using Shouldly;

namespace Ashes.Tests;

public sealed class LexerEdgeCaseTests
{
    [Test]
    public void Next_should_tokenize_underscore_identifier()
    {
        var tokens = LexAll("_x");

        tokens[0].Kind.ShouldBe(TokenKind.Ident);
        tokens[0].Text.ShouldBe("_x");
    }

    [Test]
    public void Next_should_tokenize_identifier_with_underscore_in_middle()
    {
        var tokens = LexAll("my_var");

        tokens[0].Kind.ShouldBe(TokenKind.Ident);
        tokens[0].Text.ShouldBe("my_var");
    }

    [Test]
    public void Next_should_tokenize_double_underscore_identifier()
    {
        var tokens = LexAll("__test");

        tokens[0].Kind.ShouldBe(TokenKind.Ident);
        tokens[0].Text.ShouldBe("__test");
    }

    [Test]
    public void Next_should_tokenize_identifier_with_digits()
    {
        var tokens = LexAll("x123");

        tokens[0].Kind.ShouldBe(TokenKind.Ident);
        tokens[0].Text.ShouldBe("x123");
    }

    [Test]
    public void Next_should_handle_unknown_escape_in_string_as_literal()
    {
        var tokens = LexAll("\"\\a\"");

        tokens[0].Kind.ShouldBe(TokenKind.String);
        tokens[0].Text.ShouldBe("a");
    }

    [Test]
    public void Next_should_handle_backslash_at_end_of_unterminated_string()
    {
        var diag = new Diagnostics();
        var lexer = new Lexer("\"abc\\", diag);

        var token = lexer.Next();

        token.Kind.ShouldBe(TokenKind.String);
        diag.Errors.ShouldNotBeEmpty();
    }

    [Test]
    public void Next_should_tokenize_empty_string()
    {
        var tokens = LexAll("\"\"");

        tokens[0].Kind.ShouldBe(TokenKind.String);
        tokens[0].Text.ShouldBe("");
    }

    [Test]
    public void Next_should_tokenize_string_with_all_escape_sequences()
    {
        var tokens = LexAll("\"\\n\\r\\t\\\\\\\"\"");

        tokens[0].Kind.ShouldBe(TokenKind.String);
        tokens[0].Text.ShouldBe("\n\r\t\\\"");
    }

    [Test]
    public void Next_should_handle_single_lone_underscore_as_identifier()
    {
        var tokens = LexAll("_");

        tokens[0].Kind.ShouldBe(TokenKind.Ident);
        tokens[0].Text.ShouldBe("_");
    }

    [Test]
    public void Next_should_handle_empty_input()
    {
        var tokens = LexAll("");

        tokens.Count.ShouldBe(1);
        tokens[0].Kind.ShouldBe(TokenKind.EOF);
    }

    [Test]
    public void Next_should_handle_only_whitespace()
    {
        var tokens = LexAll("   \t\n  ");

        tokens.Count.ShouldBe(1);
        tokens[0].Kind.ShouldBe(TokenKind.EOF);
    }

    [Test]
    public void Next_should_handle_only_comment()
    {
        var tokens = LexAll("// this is a comment");

        tokens.Count.ShouldBe(1);
        tokens[0].Kind.ShouldBe(TokenKind.EOF);
    }

    [Test]
    public void Next_should_handle_comment_at_end_without_newline()
    {
        var tokens = LexAll("42 // trailing");

        tokens[0].Kind.ShouldBe(TokenKind.Int);
        tokens[0].Text.ShouldBe("42");
        tokens[1].Kind.ShouldBe(TokenKind.EOF);
    }

    [Test]
    public void Next_should_tokenize_multiple_bad_characters()
    {
        var diag = new Diagnostics();
        var lexer = new Lexer("@#$", diag);

        var t1 = lexer.Next();
        var t2 = lexer.Next();
        var t3 = lexer.Next();

        t1.Kind.ShouldBe(TokenKind.Bad);
        t2.Kind.ShouldBe(TokenKind.Bad);
        t3.Kind.ShouldBe(TokenKind.Bad);
        diag.Errors.Count.ShouldBe(3);
    }

    [Test]
    public void Next_should_tokenize_dot_operator()
    {
        var tokens = LexAll(".");

        tokens[0].Kind.ShouldBe(TokenKind.Dot);
        tokens[0].Text.ShouldBe(".");
    }

    [Test]
    public void Next_should_tokenize_let_question()
    {
        var tokens = LexAll("let?");

        tokens[0].Kind.ShouldBe(TokenKind.LetQuestion);
        tokens[0].Text.ShouldBe("let?");
    }

    [Test]
    public void Next_should_tokenize_let_followed_by_non_question_mark_separately()
    {
        var tokens = LexAll("let x");

        tokens[0].Kind.ShouldBe(TokenKind.Let);
        tokens[0].Text.ShouldBe("let");
        tokens[1].Kind.ShouldBe(TokenKind.Ident);
        tokens[1].Text.ShouldBe("x");
    }

    [Test]
    public void Next_should_record_correct_positions()
    {
        var tokens = LexAll("let x = 42");

        tokens[0].Position.ShouldBe(0);
        tokens[0].Length.ShouldBe(3);
        tokens[1].Position.ShouldBe(4);
        tokens[1].Length.ShouldBe(1);
        tokens[2].Position.ShouldBe(6);
        tokens[2].Length.ShouldBe(1);
        tokens[3].Position.ShouldBe(8);
        tokens[3].Length.ShouldBe(2);
    }

    [Test]
    public void Next_should_tokenize_integer_literal_with_correct_value()
    {
        var tokens = LexAll("12345");

        tokens[0].Kind.ShouldBe(TokenKind.Int);
        tokens[0].IntValue.ShouldBe(12345);
    }

    [Test]
    public void Next_should_tokenize_zero()
    {
        var tokens = LexAll("0");

        tokens[0].Kind.ShouldBe(TokenKind.Int);
        tokens[0].IntValue.ShouldBe(0);
    }

    [Test]
    public void Next_should_tokenize_pipe_question_greater_before_pipe_greater()
    {
        var tokens = LexAll("|?>");

        tokens[0].Kind.ShouldBe(TokenKind.PipeQuestionGreater);
        tokens[0].Text.ShouldBe("|?>");
    }

    [Test]
    public void Next_should_tokenize_pipe_bang_greater()
    {
        var tokens = LexAll("|!>");

        tokens[0].Kind.ShouldBe(TokenKind.PipeBangGreater);
        tokens[0].Text.ShouldBe("|!>");
    }

    [Test]
    public void Next_should_tokenize_pipe_when_not_followed_by_greater()
    {
        var tokens = LexAll("| x");

        tokens[0].Kind.ShouldBe(TokenKind.Pipe);
        tokens[0].Text.ShouldBe("|");
    }

    [Test]
    public void Token_End_should_equal_position_plus_length()
    {
        var tokens = LexAll("let");

        tokens[0].End.ShouldBe(tokens[0].Position + tokens[0].Length);
    }

    [Test]
    public void Token_Span_should_match_position_and_end()
    {
        var tokens = LexAll("let");

        tokens[0].Span.Start.ShouldBe(0);
        tokens[0].Span.End.ShouldBe(3);
    }

    [Test]
    public void Next_should_handle_number_followed_by_dot_without_digit_as_int_then_dot()
    {
        var diag = new Diagnostics();
        var lexer = new Lexer("42.x", diag);

        var t1 = lexer.Next();
        var t2 = lexer.Next();
        var t3 = lexer.Next();

        t1.Kind.ShouldBe(TokenKind.Int);
        t1.IntValue.ShouldBe(42);
        t2.Kind.ShouldBe(TokenKind.Dot);
        t3.Kind.ShouldBe(TokenKind.Ident);
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
