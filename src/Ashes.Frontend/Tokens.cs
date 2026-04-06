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
    Rec,
    In,

    If,
    Then,
    Else,
    Match,
    With,

    Fun,
    True,
    False,
    Type,

    Plus,
    Minus,
    Star,
    Slash,
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
    Async,
    Await,
    LetBang,
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
