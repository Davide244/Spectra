namespace SpectraShade.Compiler.Lexing;

public enum TokenKind
{
    // Literals
    IntLiteral,
    FloatLiteral,
    BoolLiteral,
    StringLiteral,

    // Identifiers
    Identifier,

    // Top-level keywords
    Shader,
    Import,
    Struct,
    CBuffer,

    // Type inference
    Var,

    // Qualifiers
    In,
    Out,
    Const,

    // Control flow
    If,
    Else,
    For,
    While,
    Return,
    Break,
    Continue,
    Discard,

    // Scalar types
    Void,
    Bool,
    Int,
    UInt,
    Float,
    Double,

    // Vector types
    Vec2,
    Vec3,
    Vec4,
    IVec2,
    IVec3,
    IVec4,
    UVec2,
    UVec3,
    UVec4,
    BVec2,
    BVec3,
    BVec4,

    // Matrix types
    Mat2,
    Mat3,
    Mat4,

    // Sampler types
    Sampler2D,
    Sampler2DArray,
    Sampler3D,
    SamplerCube,

    // Object keywords
    New,

    // Operators
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Equals,
    NotEquals,
    Less,
    LessEquals,
    Greater,
    GreaterEquals,
    And,
    Or,
    Not,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    BitwiseNot,
    ShiftLeft,
    ShiftRight,

    // Assignment
    Assign,
    PlusAssign,
    MinusAssign,
    StarAssign,
    SlashAssign,

    // Punctuation
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    LeftBracket,
    RightBracket,
    Semicolon,
    Comma,
    Dot,
    Colon,
    Arrow,

    // Special
    EndOfFile,
    Invalid,
}
