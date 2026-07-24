namespace Ashes.Frontend;

/// <summary>The lexical category of a <see cref="Token"/>: keywords, operators, punctuation, literal
/// kinds, and the end-of-input and error sentinels.</summary>
public enum TokenKind
{
    /// <summary>End of input; produced once the source is exhausted.</summary>
    EOF,
    /// <summary>An unrecognized character the lexer could not classify.</summary>
    Bad,

    /// <summary>An integer literal (also the resolved kind of an unsigned-suffixed literal).</summary>
    Int,
    /// <summary>A floating-point literal.</summary>
    Float,
    /// <summary>A double-quoted string literal.</summary>
    String,
    /// <summary>An identifier that is not a reserved keyword.</summary>
    Ident,

    /// <summary>The <c>let</c> keyword.</summary>
    Let,
    /// <summary>The <c>let?</c> result-binding form.</summary>
    LetQuestion,
    /// <summary>The <c>recursive</c> keyword marking a recursive binding.</summary>
    Recursive,
    /// <summary>The <c>in</c> keyword ending a <c>let ... in</c> binding.</summary>
    In,
    /// <summary>The <c>and</c> keyword joining mutual-recursion bindings.</summary>
    And,

    /// <summary>The <c>if</c> keyword.</summary>
    If,
    /// <summary>The <c>then</c> keyword.</summary>
    Then,
    /// <summary>The <c>else</c> keyword.</summary>
    Else,
    /// <summary>The <c>match</c> keyword.</summary>
    Match,
    /// <summary>The <c>with</c> keyword (match cases and record updates).</summary>
    With,
    /// <summary>The <c>when</c> keyword introducing a match-case guard.</summary>
    When,

    /// <summary>The <c>given</c> keyword introducing a lambda.</summary>
    Given,
    /// <summary>The <c>true</c> boolean literal.</summary>
    True,
    /// <summary>The <c>false</c> boolean literal.</summary>
    False,
    /// <summary>The <c>type</c> keyword.</summary>
    Type,

    /// <summary>The <c>+</c> addition operator.</summary>
    Plus,
    /// <summary>The <c>-</c> subtraction / unary-negation operator.</summary>
    Minus,
    /// <summary>The <c>*</c> multiplication operator.</summary>
    Star,
    /// <summary>The <c>/</c> division operator.</summary>
    Slash,
    /// <summary>The <c>%</c> modulo operator.</summary>
    Percent,
    /// <summary>A big-integer literal (an <c>N</c>-suffixed integer).</summary>
    BigInt,
    /// <summary>The <c>~</c> bitwise-not operator.</summary>
    Tilde,
    /// <summary>The <c>&amp;</c> bitwise-and operator.</summary>
    Ampersand,
    /// <summary>The <c>^</c> bitwise-xor operator.</summary>
    Caret,
    /// <summary>The <c>&lt;&lt;</c> left-shift operator.</summary>
    LessLess,
    /// <summary>The <c>&gt;&gt;</c> right-shift operator.</summary>
    GreaterGreater,
    /// <summary>The <c>&gt;</c> greater-than operator.</summary>
    GreaterThan,
    /// <summary>The <c>&lt;</c> less-than operator.</summary>
    LessThan,
    /// <summary>The <c>&gt;=</c> greater-or-equal operator.</summary>
    GreaterEquals,
    /// <summary>The <c>&lt;=</c> less-or-equal operator.</summary>
    LessEquals,
    /// <summary>The <c>==</c> equality operator.</summary>
    EqualsEquals,
    /// <summary>The <c>!=</c> inequality operator.</summary>
    BangEquals,
    /// <summary>The <c>=</c> binding/assignment token.</summary>
    Equals,
    /// <summary>The <c>,</c> separator.</summary>
    Comma,
    /// <summary>The <c>|</c> token (bitwise-or, match/type alternatives).</summary>
    Pipe,
    /// <summary>The <c>|&gt;</c> pipe-forward operator.</summary>
    PipeGreater,
    /// <summary>The <c>|?&gt;</c> result-pipe operator.</summary>
    PipeQuestionGreater,
    /// <summary>The <c>|!&gt;</c> result-map-error pipe operator.</summary>
    PipeBangGreater,
    /// <summary>The <c>::</c> cons operator.</summary>
    ColonColon,
    /// <summary>The <c>(</c> opening parenthesis.</summary>
    LParen,
    /// <summary>The <c>)</c> closing parenthesis.</summary>
    RParen,
    /// <summary>The <c>[</c> opening bracket.</summary>
    LBracket,
    /// <summary>The <c>]</c> closing bracket.</summary>
    RBracket,
    /// <summary>The <c>-&gt;</c> arrow (lambda bodies, function types, match arms).</summary>
    Arrow, // ->
    /// <summary>The <c>.</c> member/qualifier dot.</summary>
    Dot, // .
    /// <summary>The <c>:</c> type-annotation colon.</summary>
    Colon, // :
    /// <summary>The <c>{</c> opening brace (record literals and rows).</summary>
    LBrace, // {
    /// <summary>The <c>}</c> closing brace.</summary>
    RBrace, // }
    /// <summary>The <c>await</c> keyword.</summary>
    Await,
    /// <summary>The <c>let!</c> await-binding form.</summary>
    LetBang,
    /// <summary>The <c>external</c> keyword.</summary>
    External,
    /// <summary>The <c>capability</c> keyword.</summary>
    Capability,
    /// <summary>The <c>needs</c> keyword introducing a capability row.</summary>
    Needs,
    /// <summary>The <c>provide</c> keyword introducing a static provider.</summary>
    Provide,
    /// <summary>The <c>perform</c> keyword marking an explicit capability operation.</summary>
    Perform,
    /// <summary>The <c>handle</c> keyword installing a handler.</summary>
    Handle,
}

/// <summary>
/// A single lexical token: its <paramref name="Kind"/>, the exact source <paramref name="Text"/> it
/// spanned, decoded numeric payloads, and the source position it occupied.
/// </summary>
/// <param name="Kind">The lexical category of the token.</param>
/// <param name="Text">The raw source slice the token was lexed from.</param>
/// <param name="IntValue">The decoded value for an integer/big-int token; zero otherwise.</param>
/// <param name="FloatValue">The decoded value for a float token; zero otherwise.</param>
/// <param name="Position">The inclusive start offset in the source.</param>
/// <param name="Length">The number of characters the token spans.</param>
public readonly record struct Token(
    TokenKind Kind,
    string Text,
    long IntValue,
    double FloatValue,
    int Position,
    int Length
)
{
    /// <summary>Convenience constructor for non-float tokens, defaulting
    /// <see cref="FloatValue"/> to zero.</summary>
    public Token(TokenKind kind, string text, long intValue, int position, int length)
        : this(kind, text, intValue, 0, position, length)
    {
    }

    /// <summary>The exclusive end offset, <see cref="Position"/> plus <see cref="Length"/>.</summary>
    public int End => Position + Length;

    /// <summary>The source span this token occupies.</summary>
    public TextSpan Span => TextSpan.FromBounds(Position, End);
}
