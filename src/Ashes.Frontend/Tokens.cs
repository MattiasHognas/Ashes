namespace Ashes.Frontend;

public enum TokenKind
{
    EOF,
    Bad,

    Int,
    Float,
    String,
    Ident,

    Let,
    LetQuestion,
    Recursive,
    In,
    And,

    If,
    Then,
    Else,
    Match,
    With,
    When,

    Given,
    True,
    False,
    Type,

    Plus,
    Minus,
    Star,
    Slash,
    Tilde,
    Ampersand,
    Caret,
    LessLess,
    GreaterGreater,
    GreaterThan,
    LessThan,
    GreaterEquals,
    LessEquals,
    EqualsEquals,
    BangEquals,
    Equals,
    Comma,
    Pipe,
    PipeGreater,
    PipeQuestionGreater,
    PipeBangGreater,
    ColonColon,
    LParen,
    RParen,
    LBracket,
    RBracket,
    Arrow, // ->
    Dot, // .
    Colon, // :
    LBrace, // {
    RBrace, // }
    Await,
    LetBang,
    External,
    Capability,
    Needs,
    Provide,
    Perform,
    Handle,

    // Legacy spellings retained only to emit a rename diagnostic (renamed to Capability / Needs).
    RenamedEffect,
    RenamedUses,
}

public readonly record struct Token(
    TokenKind Kind,
    string Text,
    long IntValue,
    double FloatValue,
    int Position,
    int Length
)
{
    public Token(TokenKind kind, string text, long intValue, int position, int length)
        : this(kind, text, intValue, 0, position, length)
    {
    }

    public int End => Position + Length;

    public TextSpan Span => TextSpan.FromBounds(Position, End);
}
