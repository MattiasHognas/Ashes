namespace Ashes.Semantics;

// Named type variable used in type scheme quantifiers (forall a. body).
// Id is the original TVar ID used for instantiation; Name is kept for display.
/// <summary>A named type variable bound by a <see cref="TypeScheme"/> quantifier.</summary>
/// <param name="Id">Original <see cref="TypeRef.TVar"/> id used when the scheme is instantiated.</param>
/// <param name="Name">Human-readable name retained for diagnostics and formatting.</param>
public sealed record TypeVar(int Id, string Name);

// Type scheme: forall [Quantified]. Body (polytype representation for let-polymorphism).
/// <summary>A polytype for let-polymorphism: <c>forall Quantified. Body</c>.</summary>
/// <param name="Quantified">The type variables universally quantified over <paramref name="Body"/>.</param>
/// <param name="Body">The quantified monotype instantiated afresh at each use site.</param>
public sealed record TypeScheme(IReadOnlyList<TypeVar> Quantified, TypeRef Body);

/// <summary>Base of the resolved type representation used throughout semantics, lowering, and the
/// backend. Concrete cases model primitive, aggregate, function, capability, and type-variable
/// forms.</summary>
public abstract record TypeRef
{
    /// <summary>The signed 64-bit integer type (<c>Int</c>).</summary>
    public sealed record TInt : TypeRef;
    // Unsigned integer: Bits ∈ {8, 16, 32, 64}. Values are stored as i64 internally
    // but wrap at their declared bit width for arithmetic, matching C unsigned semantics.
    /// <summary>An unsigned integer type whose width is <paramref name="Bits"/>.</summary>
    /// <param name="Bits">Declared bit width, one of 8, 16, 32, or 64.</param>
    public sealed record TUInt(int Bits) : TypeRef;
    /// <summary>The 64-bit IEEE-754 floating-point type (<c>Float</c>).</summary>
    public sealed record TFloat : TypeRef;
    // Arbitrary-precision signed integer. Native heap value:
    // pointer to { i64 header = (negFlag<<32)|limbCount, i64 limb[...] }, sign-magnitude, base 2^64,
    // normalized (zero = header 0, no limbs). Immutable; each op allocates a fresh value. The
    // arithmetic is emitted as LLVM-IR runtime helpers by the backend, on demand.
    /// <summary>The arbitrary-precision signed integer type (<c>BigInt</c>).</summary>
    public sealed record TBigInt : TypeRef;
    /// <summary>The immutable UTF-8 string type (<c>Str</c>).</summary>
    public sealed record TStr : TypeRef;
    // Immutable byte buffer: layout is identical to TStr → {length:i64, data:u8[length]}.
    /// <summary>The immutable byte-buffer type (<c>Bytes</c>), sharing <see cref="TStr"/>'s heap layout.</summary>
    public sealed record TBytes : TypeRef;
    /// <summary>The boolean type (<c>Bool</c>).</summary>
    public sealed record TBool : TypeRef;
    /// <summary>The uninhabited bottom type, the result of a diverging or <c>panic</c> expression.</summary>
    public sealed record TNever : TypeRef;
    /// <summary>An immutable singly-linked list type with element type <paramref name="Element"/>.</summary>
    /// <param name="Element">The element type of the list.</param>
    public sealed record TList(TypeRef Element) : TypeRef;
    /// <summary>A fixed-arity tuple type over <paramref name="Elements"/>.</summary>
    /// <param name="Elements">The component types, in positional order.</param>
    public sealed record TTuple(IReadOnlyList<TypeRef> Elements) : TypeRef;
    /// <summary>A function (arrow) type from <paramref name="Arg"/> to <paramref name="Ret"/>, optionally
    /// carrying a capability <see cref="Row"/>.</summary>
    /// <param name="Arg">The single argument type (curried functions nest arrows).</param>
    /// <param name="Ret">The result type.</param>
    public sealed record TFun(TypeRef Arg, TypeRef Ret) : TypeRef
    {
        /// <summary>
        /// The arrow's capability row: a <see cref="TRow"/> (or a <see cref="TVar"/> row variable), or
        /// null for the pure closed empty row. Kept as an init-only property so the ubiquitous
        /// two-argument construction stays pure by default.
        /// </summary>
        public TypeRef? Row { get; init; }
    }

    /// <summary>An unresolved unification variable produced during Hindley-Milner inference, identified
    /// by <paramref name="Id"/>. Also serves as a row variable when it appears as a row tail.</summary>
    /// <param name="Id">Unique identifier of the inference variable.</param>
    public sealed record TVar(int Id) : TypeRef;

    /// <summary>
    /// One capability instance inside a row: the declared capability plus its type arguments
    /// (e.g. <c>Clock</c> or <c>State(Int)</c>). Only ever appears inside <see cref="TRow"/>.
    /// </summary>
    public sealed record TCapability(CapabilitySymbol Symbol, IReadOnlyList<TypeRef> Args) : TypeRef;

    /// <summary>
    /// An capability row: a set of capabilities plus a tail. <see cref="Tail"/> is a <see cref="TVar"/>
    /// row variable (open row), another <see cref="TRow"/> produced by substitution (flattened on
    /// normalization), or null (closed row).
    /// </summary>
    public sealed record TRow(IReadOnlyList<TCapability> Capabilities, TypeRef? Tail) : TypeRef;
    /// <summary>A reference to a user-declared or built-in named type applied to <paramref name="TypeArgs"/>
    /// (e.g. <c>Option(Int)</c>).</summary>
    /// <param name="Symbol">The declared type being referenced.</param>
    /// <param name="TypeArgs">The type arguments applied to <paramref name="Symbol"/>.</param>
    public sealed record TNamedType(TypeSymbol Symbol, IReadOnlyList<TypeRef> TypeArgs) : TypeRef;
    /// <summary>A rigid (universally-quantified) type parameter bound in an enclosing declaration.</summary>
    /// <param name="Symbol">The type parameter symbol.</param>
    public sealed record TTypeParam(TypeParameterSymbol Symbol) : TypeRef;
    /// <summary>An opaque externally-defined type identified only by <paramref name="Name"/>, used for
    /// FFI handles the compiler treats abstractly.</summary>
    /// <param name="Name">The opaque type's name.</param>
    public sealed record TOpaque(string Name) : TypeRef;
    /// <summary>A raw pointer to <paramref name="Pointee"/>, used at the FFI boundary and in low-level
    /// intrinsics.</summary>
    /// <param name="Pointee">The pointed-to type.</param>
    public sealed record TPtr(TypeRef Pointee) : TypeRef;
}

/// <summary>A source position attached to IR for debug-info (DWARF) emission.</summary>
/// <param name="FilePath">Path of the source file the instruction originates from.</param>
/// <param name="Line">1-based source line.</param>
/// <param name="Column">1-based source column.</param>
public readonly record struct SourceLocation(string FilePath, int Line, int Column);

/// <summary>Base of the linear intermediate representation. Each nested case is one IR instruction,
/// most producing a value into a numbered <c>Target</c> temp and reading operand temps; the backend
/// lowers the instruction stream to LLVM. Semantics lowering emits these, and
/// <c>IrOptimizer</c>/<c>StateMachineTransform</c> rewrite them.</summary>
public abstract record IrInst
{
    /// <summary>
    /// Optional source location for debug info emission (DWARF).
    /// Init-only so that Location is set once (via <c>with</c>) before the
    /// instruction is added to the IR list, keeping record equality stable.
    /// </summary>
    public SourceLocation? Location { get; init; }

    /// <summary>Materializes the integer literal <paramref name="Value"/> into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the constant.</param>
    /// <param name="Value">The literal integer value.</param>
    public sealed record LoadConstInt(int Target, long Value) : IrInst;
    /// <summary>Materializes the float literal <paramref name="Value"/> into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the constant.</param>
    /// <param name="Value">The literal double value.</param>
    public sealed record LoadConstFloat(int Target, double Value) : IrInst;
    /// <summary>Materializes the boolean literal <paramref name="Value"/> into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the constant.</param>
    /// <param name="Value">The literal boolean value.</param>
    public sealed record LoadConstBool(int Target, bool Value) : IrInst;
    /// <summary>Loads a pointer to the interned string literal named <paramref name="StrLabel"/> into
    /// <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the string pointer.</param>
    /// <param name="StrLabel">Label of the <see cref="IrStringLiteral"/> to load.</param>
    public sealed record LoadConstStr(int Target, string StrLabel) : IrInst;
    /// <summary>Loads the program's command-line arguments (as a list of strings) into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the argument list.</param>
    public sealed record LoadProgramArgs(int Target) : IrInst;

    /// <summary>Reads the local variable in <paramref name="Slot"/> into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the loaded value.</param>
    /// <param name="Slot">Local variable slot index.</param>
    public sealed record LoadLocal(int Target, int Slot) : IrInst;
    /// <summary>Writes the value in <paramref name="Source"/> into local variable <paramref name="Slot"/>.</summary>
    /// <param name="Slot">Local variable slot index to write.</param>
    /// <param name="Source">Temp holding the value to store.</param>
    public sealed record StoreLocal(int Slot, int Source) : IrInst;

    /// <summary>Reads the captured environment entry at <paramref name="Index"/> (via the function's implicit
    /// environment pointer) into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the captured value.</param>
    /// <param name="Index">Zero-based index into the closure environment.</param>
    public sealed record LoadEnv(int Target, int Index) : IrInst; // uses env ptr implicit in function
    /// <summary>Stores <paramref name="Source"/> to memory at <c>[BasePtr + OffsetBytes]</c>.</summary>
    /// <param name="BasePtr">Temp holding the base address.</param>
    /// <param name="OffsetBytes">Constant byte offset from the base.</param>
    /// <param name="Source">Temp holding the value to write.</param>
    public sealed record StoreMemOffset(int BasePtr, int OffsetBytes, int Source) : IrInst; // [base+off]=src
    /// <summary>Loads from memory at <c>[BasePtr + OffsetBytes]</c> into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the loaded value.</param>
    /// <param name="BasePtr">Temp holding the base address.</param>
    /// <param name="OffsetBytes">Constant byte offset from the base.</param>
    public sealed record LoadMemOffset(int Target, int BasePtr, int OffsetBytes) : IrInst;  // tgt=[base+off]

    // DeferredType is non-null only for a provisional '+' whose operand type was still unresolved at
    // lowering time; ResolveDeferredAdds patches such adds to ConcatStr/AddFloat (or a plain AddInt)
    // once inference finishes. It is carried on the record so the TCO `with`-based remap preserves it.
    /// <summary>Integer addition <c>Target = Left + Right</c>, or a provisional <c>+</c> whose operand type
    /// was still unresolved (see <paramref name="DeferredType"/>).</summary>
    /// <param name="Target">Temp receiving the sum.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    /// <param name="DeferredType">Non-null only for a provisional <c>+</c>; once inference finishes
    /// <c>ResolveDeferredAdds</c> patches this to <see cref="ConcatStr"/>/<see cref="AddFloat"/> or a plain
    /// integer add.</param>
    public sealed record AddInt(int Target, int Left, int Right, TypeRef? DeferredType = null) : IrInst
    {
        /// <summary>When >= 0 (both): this deferred `+` was armed as an affine accumulator append;
        /// if it resolves to Str, ResolveDeferredAdds patches it to ConcatStrTip carrying the
        /// loop's reservation slots instead of a plain ConcatStr.</summary>
        public int AffineResvStartSlot { get; init; } = -1;

        /// <summary>The reservation's end-cursor local slot paired with <see cref="AffineResvStartSlot"/>;
        /// -1 when this add was not armed as an affine append.</summary>
        public int AffineResvEndSlot { get; init; } = -1;
    }
    /// <summary>Integer subtraction <c>Target = Left - Right</c>.</summary>
    /// <param name="Target">Temp receiving the difference.</param>
    /// <param name="Left">Temp holding the minuend.</param>
    /// <param name="Right">Temp holding the subtrahend.</param>
    public sealed record SubInt(int Target, int Left, int Right) : IrInst;
    // DeferredType mirrors AddInt: non-null only for a provisional '*' whose operand type was still
    // unresolved at lowering time; ResolveDeferredMuls patches such muls to MulFloat / BigIntBinary
    // (or a plain MulInt) once inference finishes.
    /// <summary>Integer multiplication <c>Target = Left * Right</c>, or a provisional <c>*</c> whose operand
    /// type was still unresolved (see <paramref name="DeferredType"/>).</summary>
    /// <param name="Target">Temp receiving the product.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    /// <param name="DeferredType">Non-null only for a provisional <c>*</c>; <c>ResolveDeferredMuls</c> later
    /// patches this to <see cref="MulFloat"/>/<see cref="BigIntBinary"/> or a plain integer multiply.</param>
    public sealed record MulInt(int Target, int Left, int Right, TypeRef? DeferredType = null) : IrInst;
    /// <summary>Signed integer division <c>Target = Left / Right</c>.</summary>
    /// <param name="Target">Temp receiving the quotient.</param>
    /// <param name="Left">Temp holding the dividend.</param>
    /// <param name="Right">Temp holding the divisor.</param>
    public sealed record DivInt(int Target, int Left, int Right) : IrInst;
    /// <summary>Unsigned integer division <c>Target = Left / Right</c>.</summary>
    /// <param name="Target">Temp receiving the quotient.</param>
    /// <param name="Left">Temp holding the dividend.</param>
    /// <param name="Right">Temp holding the divisor.</param>
    public sealed record DivUInt(int Target, int Left, int Right) : IrInst;
    /// <summary>Bitwise AND <c>Target = Left &amp; Right</c>.</summary>
    /// <param name="Target">Temp receiving the result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record AndInt(int Target, int Left, int Right) : IrInst;
    /// <summary>Bitwise OR <c>Target = Left | Right</c>.</summary>
    /// <param name="Target">Temp receiving the result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record OrInt(int Target, int Left, int Right) : IrInst;
    /// <summary>Bitwise XOR <c>Target = Left ^ Right</c>.</summary>
    /// <param name="Target">Temp receiving the result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record XorInt(int Target, int Left, int Right) : IrInst;
    /// <summary>Logical left shift <c>Target = Left &lt;&lt; Right</c>.</summary>
    /// <param name="Target">Temp receiving the result.</param>
    /// <param name="Left">Temp holding the value to shift.</param>
    /// <param name="Right">Temp holding the shift amount.</param>
    public sealed record ShlInt(int Target, int Left, int Right) : IrInst;
    /// <summary>Logical right shift <c>Target = Left &gt;&gt; Right</c>.</summary>
    /// <param name="Target">Temp receiving the result.</param>
    /// <param name="Left">Temp holding the value to shift.</param>
    /// <param name="Right">Temp holding the shift amount.</param>
    public sealed record ShrInt(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point addition <c>Target = Left + Right</c>.</summary>
    /// <param name="Target">Temp receiving the sum.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record AddFloat(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point subtraction <c>Target = Left - Right</c>.</summary>
    /// <param name="Target">Temp receiving the difference.</param>
    /// <param name="Left">Temp holding the minuend.</param>
    /// <param name="Right">Temp holding the subtrahend.</param>
    public sealed record SubFloat(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point multiplication <c>Target = Left * Right</c>.</summary>
    /// <param name="Target">Temp receiving the product.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record MulFloat(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point division <c>Target = Left / Right</c>.</summary>
    /// <param name="Target">Temp receiving the quotient.</param>
    /// <param name="Left">Temp holding the dividend.</param>
    /// <param name="Right">Temp holding the divisor.</param>
    public sealed record DivFloat(int Target, int Left, int Right) : IrInst;
    /// <summary>Signed integer "greater than" comparison <c>Target = Left &gt; Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpIntGt(int Target, int Left, int Right) : IrInst;
    /// <summary>Signed integer "greater than or equal" comparison <c>Target = Left &gt;= Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpIntGe(int Target, int Left, int Right) : IrInst;
    /// <summary>Signed integer "less than" comparison <c>Target = Left &lt; Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpIntLt(int Target, int Left, int Right) : IrInst;
    /// <summary>Signed integer "less than or equal" comparison <c>Target = Left &lt;= Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpIntLe(int Target, int Left, int Right) : IrInst;
    /// <summary>Unsigned integer "greater than" comparison <c>Target = Left &gt; Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpUIntGt(int Target, int Left, int Right) : IrInst;
    /// <summary>Unsigned integer "greater than or equal" comparison <c>Target = Left &gt;= Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpUIntGe(int Target, int Left, int Right) : IrInst;
    /// <summary>Unsigned integer "less than" comparison <c>Target = Left &lt; Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpUIntLt(int Target, int Left, int Right) : IrInst;
    /// <summary>Unsigned integer "less than or equal" comparison <c>Target = Left &lt;= Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpUIntLe(int Target, int Left, int Right) : IrInst;
    // DeferredType is non-null only for a provisional '==' / '!=' whose operand type was still
    // unresolved at lowering time; ResolveDeferredEqs patches such comparisons to CmpStrEq/CmpStrNe
    // or CmpFloatEq/CmpFloatNe (or leaves a plain CmpIntEq/CmpIntNe) once inference finishes.
    /// <summary>Integer equality <c>Target = Left == Right</c>, or a provisional <c>==</c> whose operand type
    /// was still unresolved (see <paramref name="DeferredType"/>).</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    /// <param name="DeferredType">Non-null only for a provisional <c>==</c>; <c>ResolveDeferredEqs</c> later
    /// patches this to <see cref="CmpStrEq"/>/<see cref="CmpFloatEq"/> or leaves a plain integer compare.</param>
    public sealed record CmpIntEq(int Target, int Left, int Right, TypeRef? DeferredType = null) : IrInst;
    /// <summary>Integer inequality <c>Target = Left != Right</c>, or a provisional <c>!=</c> whose operand type
    /// was still unresolved (see <paramref name="DeferredType"/>).</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    /// <param name="DeferredType">Non-null only for a provisional <c>!=</c>; <c>ResolveDeferredEqs</c> later
    /// patches this to <see cref="CmpStrNe"/>/<see cref="CmpFloatNe"/> or leaves a plain integer compare.</param>
    public sealed record CmpIntNe(int Target, int Left, int Right, TypeRef? DeferredType = null) : IrInst;
    /// <summary>Floating-point "greater than" comparison <c>Target = Left &gt; Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpFloatGt(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point "greater than or equal" comparison <c>Target = Left &gt;= Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpFloatGe(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point "less than" comparison <c>Target = Left &lt; Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpFloatLt(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point "less than or equal" comparison <c>Target = Left &lt;= Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpFloatLe(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point equality <c>Target = Left == Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpFloatEq(int Target, int Left, int Right) : IrInst;
    /// <summary>Floating-point inequality <c>Target = Left != Right</c>.</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record CmpFloatNe(int Target, int Left, int Right) : IrInst;

    // Ashes.Number.Math numeric conversions and Float unary intrinsics (Layer 1).
    // IntToFloat is sitofp; FloatToInt is fptosi (truncates toward zero). FloatUnaryIntrinsic
    // lowers to a call to the named LLVM math intrinsic (e.g. "llvm.sqrt.f64").
    /// <summary>Converts an integer to a float (<c>sitofp</c>).</summary>
    /// <param name="Target">Temp receiving the float result.</param>
    /// <param name="ValueTemp">Temp holding the integer to convert.</param>
    public sealed record IntToFloat(int Target, int ValueTemp) : IrInst;
    /// <summary>Converts a float to an integer, truncating toward zero (<c>fptosi</c>).</summary>
    /// <param name="Target">Temp receiving the integer result.</param>
    /// <param name="ValueTemp">Temp holding the float to convert.</param>
    public sealed record FloatToInt(int Target, int ValueTemp) : IrInst;
    /// <summary>Applies a unary LLVM math intrinsic to a float operand.</summary>
    /// <param name="Target">Temp receiving the float result.</param>
    /// <param name="ValueTemp">Temp holding the float operand.</param>
    /// <param name="LlvmIntrinsic">Name of the LLVM intrinsic to call (e.g. <c>llvm.sqrt.f64</c>).</param>
    public sealed record FloatUnaryIntrinsic(int Target, int ValueTemp, string LlvmIntrinsic) : IrInst;

    // Ashes.Number.Math Layer-2 transcendental: a call to an openlibm symbol (e.g. "sin", "pow"). All
    // arguments and the result are Float (f64). The openlibm bitcode is linked into the module when
    // the program uses any of these (ProgramUsesMathRuntimeAbi), so the symbol resolves internally.
    /// <summary>Calls an openlibm transcendental symbol (e.g. <c>sin</c>, <c>pow</c>); all arguments and the
    /// result are Float. The openlibm bitcode is linked in when the program uses any such call.</summary>
    /// <param name="Target">Temp receiving the float result.</param>
    /// <param name="Symbol">Name of the openlibm symbol to call.</param>
    /// <param name="Args">Temps holding the float arguments, in order.</param>
    public sealed record CallLibm(int Target, string Symbol, IReadOnlyList<int> Args) : IrInst;

    // Ashes.Number.BigInt operations, backed by emitted LLVM-IR runtime helpers.
    // BigInt values are heap pointers (i64). The codegen pre-sizes result buffers from operand limb
    // counts and calls the allocation-free C helpers.
    /// <summary>Widens an <c>Int</c> to a <c>BigInt</c>.</summary>
    /// <param name="Target">Temp receiving the BigInt result.</param>
    /// <param name="ValueTemp">Temp holding the integer to widen.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BigIntFromInt(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst; // Int -> BigInt
    /// <summary>Renders a <c>BigInt</c> to its decimal <c>Str</c>.</summary>
    /// <param name="Target">Temp receiving the string result.</param>
    /// <param name="ValueTemp">Temp holding the BigInt to render.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BigIntToString(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst; // BigInt -> Str
    /// <summary>Narrows a <c>BigInt</c> to <c>Result(Str, Int)</c>, erroring when it does not fit an <c>Int</c>.</summary>
    /// <param name="Target">Temp receiving the result value.</param>
    /// <param name="ValueTemp">Temp holding the BigInt to narrow.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BigIntToInt(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst; // BigInt -> Result(Str, Int)
    /// <summary>Parses a <c>Str</c> into <c>Result(Str, BigInt)</c>.</summary>
    /// <param name="Target">Temp receiving the result value.</param>
    /// <param name="ValueTemp">Temp holding the string to parse.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BigIntFromString(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst; // Str -> Result(Str, BigInt)
    // Op ∈ { "add", "sub", "mul", "div", "mod" }: BigInt -> BigInt -> BigInt.
    /// <summary>Binary <c>BigInt</c> arithmetic selected by <paramref name="Op"/>.</summary>
    /// <param name="Target">Temp receiving the BigInt result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    /// <param name="Op">Operation name, one of <c>add</c>, <c>sub</c>, <c>mul</c>, <c>div</c>, <c>mod</c>.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BigIntBinary(int Target, int Left, int Right, string Op, bool RuntimeManaged = false) : IrInst;
    /// <summary>Three-way <c>BigInt</c> comparison yielding a negative, zero, or positive <c>Int</c>.</summary>
    /// <param name="Target">Temp receiving the comparison result.</param>
    /// <param name="Left">Temp holding the left operand.</param>
    /// <param name="Right">Temp holding the right operand.</param>
    public sealed record BigIntCompare(int Target, int Left, int Right) : IrInst; // BigInt -> BigInt -> Int

    /// <summary>String equality <c>Target = Left == Right</c> (byte-wise).</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left string.</param>
    /// <param name="Right">Temp holding the right string.</param>
    public sealed record CmpStrEq(int Target, int Left, int Right) : IrInst;
    /// <summary>String inequality <c>Target = Left != Right</c> (byte-wise).</summary>
    /// <param name="Target">Temp receiving the boolean result.</param>
    /// <param name="Left">Temp holding the left string.</param>
    /// <param name="Right">Temp holding the right string.</param>
    public sealed record CmpStrNe(int Target, int Left, int Right) : IrInst;
    /// <summary>String concatenation <c>Target = Left ++ Right</c>, allocating a fresh buffer. See
    /// <see cref="ConcatStrTip"/> for the affine-accumulator variant.</summary>
    /// <param name="Target">Temp receiving the concatenated string.</param>
    /// <param name="Left">Temp holding the left string.</param>
    /// <param name="Right">Temp holding the right string.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record ConcatStr(int Target, int Left, int Right, bool RuntimeManaged = false) : IrInst;
    // Affine-accumulator string append: semantically identical to ConcatStr, but the accumulator
    // grows inside a RESERVATION instead of being copied per append. The loop keeps two local
    // slots (zeroed at loop entry): the reservation's start and end. When Left == *ResvStartSlot
    // and the appended bytes fit below *ResvEndSlot, Right's bytes are copied onto Left's end and
    // only the length header grows — the cursor is untouched, so per-iteration scratch allocated
    // above the reservation is irrelevant. Otherwise the fallback concatenates into a NEW
    // allocation with doubling headroom (capacity = 2x the result) and records it in the slots —
    // fallbacks are geometric, so total copy work is linear in appended bytes. A runtime-managed
    // form CONSUMES Left: the extend path transfers that reference unchanged, while the fallback
    // copies into a fresh RC allocation and releases Left. The identity check makes mutation safe:
    // only a string this loop itself reserved can match ResvStart (a caller-passed seed never does),
    // and the static affine analysis guarantees the loop holds no other reference to it.
    /// <summary>Affine-accumulator string append: semantically identical to <see cref="ConcatStr"/>, but the
    /// accumulator grows inside a loop-held reservation (start/end slots) instead of being copied per
    /// append, keeping total copy work linear.</summary>
    /// <param name="Target">Temp receiving the appended string.</param>
    /// <param name="Left">Temp holding the accumulator (the reservation candidate).</param>
    /// <param name="Right">Temp holding the bytes to append.</param>
    /// <param name="ResvStartSlot">Local slot holding the reservation's start pointer.</param>
    /// <param name="ResvEndSlot">Local slot holding the reservation's end (capacity) pointer.</param>
    /// <param name="RuntimeManaged">True when the append consumes and produces a reference-counted string.</param>
    public sealed record ConcatStrTip(
        int Target,
        int Left,
        int Right,
        int ResvStartSlot,
        int ResvEndSlot,
        bool RuntimeManaged = false) : IrInst;

    // Ashes.Text.Regex (PCRE2) intrinsics. The 8-bit PCRE2 bitcode is linked into the module when the
    // program uses any of these (ProgramUsesRegexRuntimeAbi), so the pcre2_* symbols resolve
    // internally. A compiled pattern (pcre2_code*) lives in a persistent mmap-backed region that the
    // arena never relocates, so a Regex value is a stable i64 handle to it. Per-match scratch is
    // bracketed by a region save/restore inside the match/substitute emitters, keeping streaming
    // matches memory-bounded. PCRE2's malloc/free route to that region; memcpy/memset are the
    // module's own builtins.
    /// <summary>Compiles a PCRE2 pattern, yielding a stable <c>pcre2_code*</c> handle (0 on error).</summary>
    /// <param name="Target">Temp receiving the compiled-pattern handle.</param>
    /// <param name="Pattern">Temp holding the pattern string.</param>
    public sealed record RegexCompile(int Target, int Pattern) : IrInst;          // Str -> Int (pcre2_code*, 0 on error)
    /// <summary>Compiles a PCRE2 pattern for validation only, yielding the empty string when valid or the
    /// diagnostic message otherwise.</summary>
    /// <param name="Target">Temp receiving the error message (empty if valid).</param>
    /// <param name="Pattern">Temp holding the pattern string.</param>
    public sealed record RegexCompileError(int Target, int Pattern) : IrInst;     // Str -> Str ("" if valid, else message)
    /// <summary>Finds the first match of a compiled pattern at or after <paramref name="Start"/>, yielding
    /// <c>Option((Int, Int))</c> (match start/end offsets).</summary>
    /// <param name="Target">Temp receiving the optional match span.</param>
    /// <param name="Code">Temp holding the compiled-pattern handle from <see cref="RegexCompile"/>.</param>
    /// <param name="Subject">Temp holding the subject string.</param>
    /// <param name="Start">Temp holding the byte offset to start searching from.</param>
    public sealed record RegexFind(int Target, int Code, int Subject, int Start) : IrInst;      // -> Option((Int, Int))
    /// <summary>Finds the first match and returns its capture groups as <c>Option(List(Option(Str)))</c>.</summary>
    /// <param name="Target">Temp receiving the optional capture list.</param>
    /// <param name="Code">Temp holding the compiled-pattern handle from <see cref="RegexCompile"/>.</param>
    /// <param name="Subject">Temp holding the subject string.</param>
    /// <param name="Start">Temp holding the byte offset to start searching from.</param>
    public sealed record RegexCaptures(int Target, int Code, int Subject, int Start) : IrInst;  // -> Option(List(Option(Str)))
    /// <summary>Substitutes all matches of a compiled pattern in the subject, yielding the resulting <c>Str</c>.</summary>
    /// <param name="Target">Temp receiving the substituted string.</param>
    /// <param name="Code">Temp holding the compiled-pattern handle from <see cref="RegexCompile"/>.</param>
    /// <param name="Subject">Temp holding the subject string.</param>
    /// <param name="Replacement">Temp holding the replacement template.</param>
    public sealed record RegexSubstitute(int Target, int Code, int Subject, int Replacement) : IrInst; // -> Str

    /// <summary>Heap-allocates a closure object <c>{code, env, packed env_size/result ownership, dropper}</c>
    /// over the lifted function <paramref name="FuncLabel"/>. See <see cref="MakeClosureStack"/> for the
    /// stack-allocated variant and <see cref="CallClosure"/>/<see cref="CallKnown"/> for invocation.</summary>
    /// <param name="Target">Temp receiving the closure pointer.</param>
    /// <param name="FuncLabel">Label of the lifted function the closure invokes.</param>
    /// <param name="EnvPtrTemp">Temp holding the packed environment pointer (0 when there are no captures).</param>
    /// <param name="EnvSizeBytes">Size in bytes of the captured environment.</param>
    /// <param name="RuntimeManaged">True when the closure object itself is reference-counted.</param>
    /// <param name="ReturnsRuntimeManaged">True when the closure's result is reference-counted.</param>
    /// <param name="AcceptsRuntimeManagedArgument">True when the closure may adopt a reference-counted argument.</param>
    public sealed record MakeClosure(
        int Target,
        string FuncLabel,
        int EnvPtrTemp,
        int EnvSizeBytes,
        bool RuntimeManaged = false,
        bool ReturnsRuntimeManaged = false,
        bool AcceptsRuntimeManagedArgument = false
    ) : IrInst; // alloc 32 bytes: {code, env, packed env_size/result ownership, dropper}
    /// <summary>Stack-allocated form of <see cref="MakeClosure"/>, used for a non-escaping closure whose
    /// lifetime is bounded by the current frame.</summary>
    /// <param name="Target">Temp receiving the closure pointer.</param>
    /// <param name="FuncLabel">Label of the lifted function the closure invokes.</param>
    /// <param name="EnvPtrTemp">Temp holding the packed environment pointer (0 when there are no captures).</param>
    /// <param name="EnvSizeBytes">Size in bytes of the captured environment.</param>
    /// <param name="ReturnsRuntimeManaged">True when the closure's result is reference-counted.</param>
    /// <param name="AcceptsRuntimeManagedArgument">True when the closure may adopt a reference-counted argument.</param>
    public sealed record MakeClosureStack(
        int Target,
        string FuncLabel,
        int EnvPtrTemp,
        int EnvSizeBytes,
        bool ReturnsRuntimeManaged = false,
        bool AcceptsRuntimeManagedArgument = false
    ) : IrInst; // stack alloc 32 bytes: {code, env, packed env_size/result ownership, dropper}

    /// <summary>Loads the address of a lifted function as an i64. Used to store a resource dropper
    /// into an escaping closure's dropper slot, so a resource a closure captured is closed
    /// deterministically when the closure is dropped.</summary>
    public sealed record LoadFuncAddr(int Target, string FuncLabel) : IrInst;
    /// <summary>Invokes the closure in <paramref name="ClosureTemp"/> with a single argument through its
    /// stored code pointer and environment. See <see cref="CallKnown"/> for the devirtualized form.</summary>
    /// <param name="Target">Temp receiving the call result.</param>
    /// <param name="ClosureTemp">Temp holding the closure object to invoke.</param>
    /// <param name="ArgTemp">Temp holding the argument value.</param>
    /// <param name="RuntimeManagedArgumentFlagTemp">Temp holding the ownership-transfer flag for the argument;
    /// -1 when unused.</param>
    public sealed record CallClosure(
        int Target,
        int ClosureTemp,
        int ArgTemp,
        int RuntimeManagedArgumentFlagTemp = -1
    ) : IrInst;
    // Devirtualized closure call: the callee label is statically known (the closure temp was
    // produced by a MakeClosure with this label), so codegen emits a direct call the LLVM
    // inliner can see through. Produced only by IrOptimizer.DevirtualizeKnownClosureCalls.
    /// <summary>Devirtualized closure call: the callee label is statically known, so codegen emits a direct
    /// call the LLVM inliner can see through. Produced only by <c>IrOptimizer.DevirtualizeKnownClosureCalls</c>
    /// from a <see cref="CallClosure"/> whose closure came from a <see cref="MakeClosure"/> with this label.</summary>
    /// <param name="Target">Temp receiving the call result.</param>
    /// <param name="FuncLabel">Label of the statically-known callee.</param>
    /// <param name="EnvTemp">Temp holding the environment pointer to pass.</param>
    /// <param name="ArgTemp">Temp holding the argument value.</param>
    /// <param name="RuntimeManagedArgumentFlagTemp">Temp holding the ownership-transfer flag for the argument;
    /// -1 when unused.</param>
    public sealed record CallKnown(
        int Target,
        string FuncLabel,
        int EnvTemp,
        int ArgTemp,
        int RuntimeManagedArgumentFlagTemp = -1
    ) : IrInst;
    /// <summary>
    /// Loads the hidden closure-call ownership flag. A true value means the caller transferred an
    /// already runtime-managed argument, so an RC-normalizing function entry may adopt it instead
    /// of deep-copying it. Unknown and arena-managed calls pass false.
    /// </summary>
    public sealed record LoadArgumentOwnership(int Target) : IrInst;

    // Generic fixed-size allocation. RuntimeManaged is used for selected list cells and closure
    // environments; tuples and other runtime buffers remain arena-managed.
    /// <summary>Allocates a fixed-size buffer of <paramref name="SizeBytes"/> and yields its pointer.</summary>
    /// <param name="Target">Temp receiving the allocation pointer.</param>
    /// <param name="SizeBytes">Number of bytes to allocate.</param>
    /// <param name="RuntimeManaged">True to allocate a reference-counted cell (with an RC header); false for
    /// arena-managed memory.</param>
    public sealed record Alloc(int Target, int SizeBytes, bool RuntimeManaged = false) : IrInst;
    /// <summary>Stack-allocates a fixed-size buffer of <paramref name="SizeBytes"/> and yields its pointer.</summary>
    /// <param name="Target">Temp receiving the allocation pointer.</param>
    /// <param name="SizeBytes">Number of bytes to allocate on the stack.</param>
    public sealed record AllocStack(int Target, int SizeBytes) : IrInst;

    // ADT heap cell: layout is described by HeapLayouts.Adt. Runtime-managed cells carry an
    // RcHeader immediately before the returned value pointer.
    /// <summary>Allocates a tagged ADT heap cell (layout per <c>HeapLayouts.Adt</c>) and yields its pointer.
    /// See <see cref="AllocAdtStack"/> and <see cref="AllocAdtToSpace"/> for the stack and to-space variants.</summary>
    /// <param name="Target">Temp receiving the cell pointer.</param>
    /// <param name="Tag">Constructor tag written into the cell.</param>
    /// <param name="FieldCount">Number of payload fields the cell holds.</param>
    /// <param name="RuntimeManaged">True to allocate a reference-counted cell (with an RC header).</param>
    public sealed record AllocAdt(int Target, int Tag, int FieldCount, bool RuntimeManaged = false) : IrInst;
    /// <summary>Stack-allocated form of <see cref="AllocAdt"/> for a non-escaping ADT cell.</summary>
    /// <param name="Target">Temp receiving the cell pointer.</param>
    /// <param name="Tag">Constructor tag written into the cell.</param>
    /// <param name="FieldCount">Number of payload fields the cell holds.</param>
    public sealed record AllocAdtStack(int Target, int Tag, int FieldCount) : IrInst;

    /// <summary>
    /// Like <see cref="AllocAdt"/> but allocates in the persistent "to-space" arena instead of the
    /// main per-iteration arena. Emitted for a genuinely-new cell (no reuse token) inside an in-place
    /// reuse specialization — e.g. the fresh node a <c>Map.set</c> creates for a new key. The main
    /// arena's TCO back-edge reset never reclaims to-space, so the new cell survives the reset while the
    /// reset still reclaims the iteration's scaffolding/scratch. To-space is never reset during the loop
    /// (it holds part of the live accumulator); it is bounded by the number of genuinely-new cells
    /// (≈distinct keys), not by iterations. See <see cref="IrInst.AllocAdt"/>.
    /// </summary>
    public sealed record AllocAdtToSpace(int Target, int Tag, int FieldCount) : IrInst;

    /// <summary>
    /// Converts a dead ADT cell into an explicit reuse token. <c>FieldCount</c> describes the
    /// compatible allocation layout. The arena-backed path is statically unique, so this is an
    /// identity operation in codegen. For runtime-managed values, codegen consumes the source
    /// ownership: a unique cell becomes the token, while a shared cell is decremented and produces
    /// a null token.
    /// </summary>
    public sealed record DropReuse(
        int Target,
        int SourceTemp,
        int FieldCount,
        bool RuntimeManaged = false
    ) : IrInst;

    /// <summary>
    /// In-place reuse: yields the cell at <c>TokenTemp</c>'s address as <c>Target</c>, instead of
    /// allocating. ADT reuse writes <c>Tag</c> and uses a (1 + FieldCount)-word payload. List-cell
    /// reuse leaves the untagged two-word payload for the following stores and ignores
    /// <c>Tag</c>/<c>FieldCount</c>. Arena-backed tokens are emitted only for provably-dead,
    /// uniquely-owned cells of the compatible layout. A null runtime-managed token instead
    /// allocates a fresh RC cell of that layout.
    /// </summary>
    public sealed record AllocReusing(
        int Target,
        int Tag,
        int FieldCount,
        int TokenTemp,
        bool RuntimeManaged = false,
        bool ListCell = false
    ) : IrInst;
    // SetAdtField uses HeapLayouts.Adt.PayloadWordOffsetBytes(FieldIndex).
    /// <summary>Writes <paramref name="Source"/> into field <paramref name="FieldIndex"/> of the ADT cell at
    /// <paramref name="Ptr"/> (offset via <c>HeapLayouts.Adt.PayloadWordOffsetBytes</c>).</summary>
    /// <param name="Ptr">Temp holding the ADT cell pointer.</param>
    /// <param name="FieldIndex">Zero-based payload field index to write.</param>
    /// <param name="Source">Temp holding the value to store.</param>
    public sealed record SetAdtField(int Ptr, int FieldIndex, int Source) : IrInst;
    // Save the current stack pointer into a local slot at a TCO loop header; RestoreStackPointer resets to
    // it at each back-edge so dynamic stack allocations in the loop body (e.g. per-iteration string/syscall
    // scratch buffers) are freed every iteration instead of accumulating until the stack overflows.
    /// <summary>Saves the current stack pointer into a local slot at a TCO loop header, paired with
    /// <see cref="RestoreStackPointer"/> at each back-edge to free per-iteration stack scratch.</summary>
    /// <param name="Slot">Local slot receiving the saved stack pointer.</param>
    public sealed record SaveStackPointer(int Slot) : IrInst;
    /// <summary>Resets the stack pointer to the value previously saved by <see cref="SaveStackPointer"/> into
    /// <paramref name="Slot"/>.</summary>
    /// <param name="Slot">Local slot holding the saved stack pointer.</param>
    public sealed record RestoreStackPointer(int Slot) : IrInst;
    // GetAdtTag uses the descriptor's tag offset.
    /// <summary>Reads the constructor tag from the ADT cell at <paramref name="Ptr"/> into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the tag.</param>
    /// <param name="Ptr">Temp holding the ADT cell pointer.</param>
    public sealed record GetAdtTag(int Target, int Ptr) : IrInst;
    // GetAdtField uses HeapLayouts.Adt.PayloadWordOffsetBytes(FieldIndex).
    /// <summary>Reads field <paramref name="FieldIndex"/> of the ADT cell at <paramref name="Ptr"/> into
    /// <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the field value.</param>
    /// <param name="Ptr">Temp holding the ADT cell pointer.</param>
    /// <param name="FieldIndex">Zero-based payload field index to read.</param>
    public sealed record GetAdtField(int Target, int Ptr, int FieldIndex) : IrInst;

    /// <summary>Writes the integer in <paramref name="Source"/> to standard output, followed by a newline.</summary>
    /// <param name="Source">Temp holding the integer to print.</param>
    public sealed record PrintInt(int Source) : IrInst;
    /// <summary>Writes the string in <paramref name="Source"/> to standard output, followed by a newline.</summary>
    /// <param name="Source">Temp holding the string to print.</param>
    public sealed record PrintStr(int Source) : IrInst;
    /// <summary>Writes the boolean in <paramref name="Source"/> to standard output, followed by a newline.</summary>
    /// <param name="Source">Temp holding the boolean to print.</param>
    public sealed record PrintBool(int Source) : IrInst;
    /// <summary>Writes the string in <paramref name="Source"/> to standard output with no trailing newline.</summary>
    /// <param name="Source">Temp holding the string to write.</param>
    public sealed record WriteStr(int Source) : IrInst;
    /// <summary>Reads one line from standard input into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the line read.</param>
    public sealed record ReadLine(int Target) : IrInst;
    /// <summary>Reads exactly <paramref name="CountTemp"/> bytes from standard input into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the bytes read.</param>
    /// <param name="CountTemp">Temp holding the number of bytes to read.</param>
    public sealed record ReadExact(int Target, int CountTemp) : IrInst;
    /// <summary>Puts the terminal into raw mode, yielding a status/handle into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    public sealed record ConsoleEnableRaw(int Target) : IrInst;
    /// <summary>Restores the terminal from raw mode, yielding a status into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    public sealed record ConsoleRestore(int Target) : IrInst;
    /// <summary>Polls for terminal input up to <paramref name="TimeoutTemp"/> milliseconds, yielding the
    /// available input into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the polled input.</param>
    /// <param name="TimeoutTemp">Temp holding the poll timeout in milliseconds.</param>
    public sealed record ConsolePoll(int Target, int TimeoutTemp) : IrInst;
    /// <summary>Reads a monotonic clock value in milliseconds into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the millisecond timestamp.</param>
    public sealed record MonotonicMillis(int Target) : IrInst;
    /// <summary>Yields the byte length of the string in <paramref name="TextTemp"/> into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the byte length.</param>
    /// <param name="TextTemp">Temp holding the string to measure.</param>
    public sealed record TextByteLength(int Target, int TextTemp) : IrInst;
    /// <summary>Reads a whole file as text into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the file contents.</param>
    /// <param name="PathTemp">Temp holding the file path.</param>
    public sealed record FileReadText(int Target, int PathTemp) : IrInst;

    /// <summary>Reads a whole file as raw bytes into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the file bytes.</param>
    /// <param name="PathTemp">Temp holding the file path.</param>
    public sealed record FileReadAllBytes(int Target, int PathTemp) : IrInst;

    /// <summary>Memory-maps a file, yielding a bytes view into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the mapped bytes.</param>
    /// <param name="PathTemp">Temp holding the file path.</param>
    public sealed record FileMmap(int Target, int PathTemp) : IrInst;
    /// <summary>Writes text to a file, yielding a status into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    /// <param name="PathTemp">Temp holding the file path.</param>
    /// <param name="TextTemp">Temp holding the text to write.</param>
    public sealed record FileWriteText(int Target, int PathTemp, int TextTemp) : IrInst;
    /// <summary>Tests whether a file exists, yielding a boolean into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the existence flag.</param>
    /// <param name="PathTemp">Temp holding the file path.</param>
    public sealed record FileExists(int Target, int PathTemp) : IrInst;
    /// <summary>Opens a file, yielding a file handle into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the file handle.</param>
    /// <param name="PathTemp">Temp holding the file path.</param>
    public sealed record FileOpen(int Target, int PathTemp) : IrInst;
    /// <summary>Reads up to <paramref name="CountTemp"/> bytes from an open file handle into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the chunk read.</param>
    /// <param name="HandleTemp">Temp holding the open file handle.</param>
    /// <param name="CountTemp">Temp holding the maximum number of bytes to read.</param>
    public sealed record FileReadChunk(int Target, int HandleTemp, int CountTemp) : IrInst;
    /// <summary>Reads one line from an open file handle into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the line read.</param>
    /// <param name="HandleTemp">Temp holding the open file handle.</param>
    public sealed record FileReadLine(int Target, int HandleTemp) : IrInst;
    /// <summary>Closes an open file handle, yielding a status into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    /// <param name="HandleTemp">Temp holding the open file handle.</param>
    public sealed record FileClose(int Target, int HandleTemp) : IrInst;
    /// <summary>Splits a string into its first character and remainder, yielding an option into
    /// <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the optional (head, tail) result.</param>
    /// <param name="TextTemp">Temp holding the string to deconstruct.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record TextUncons(int Target, int TextTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Parses a string as an integer, yielding a result option into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the parse result.</param>
    /// <param name="TextTemp">Temp holding the string to parse.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record TextParseInt(int Target, int TextTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Parses a string as a float, yielding a result option into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the parse result.</param>
    /// <param name="TextTemp">Temp holding the string to parse.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record TextParseFloat(int Target, int TextTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Renders an integer to its decimal string into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the rendered string.</param>
    /// <param name="ValueTemp">Temp holding the integer to render.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record TextFromInt(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Renders a float to its default string form into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the rendered string.</param>
    /// <param name="ValueTemp">Temp holding the float to render.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record TextFromFloat(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Renders a float to a string with a fixed number of decimals into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the rendered string.</param>
    /// <param name="ValueTemp">Temp holding the float to render.</param>
    /// <param name="DecimalsTemp">Temp holding the number of decimal places.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record TextFormatFloat(int Target, int ValueTemp, int DecimalsTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Renders an integer to its hexadecimal string into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the rendered string.</param>
    /// <param name="ValueTemp">Temp holding the integer to render.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record TextToHex(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst;
    // ASCII-only case map (a-z <-> A-Z by flipping bit 0x20); multibyte UTF-8 (>= 0x80) untouched.
    /// <summary>Maps ASCII letter case (upper/lower) across a string, leaving multibyte UTF-8 untouched.</summary>
    /// <param name="Target">Temp receiving the case-mapped string.</param>
    /// <param name="SourceTemp">Temp holding the string to map.</param>
    /// <param name="Upper">True to uppercase, false to lowercase.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record TextAsciiCase(int Target, int SourceTemp, bool Upper, bool RuntimeManaged = false) : IrInst;
    /// <summary>Performs a blocking HTTP GET, yielding the response into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the response.</param>
    /// <param name="UrlTemp">Temp holding the request URL.</param>
    public sealed record HttpGet(int Target, int UrlTemp) : IrInst;
    /// <summary>Performs a blocking HTTP POST, yielding the response into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the response.</param>
    /// <param name="UrlTemp">Temp holding the request URL.</param>
    /// <param name="BodyTemp">Temp holding the request body.</param>
    public sealed record HttpPost(int Target, int UrlTemp, int BodyTemp) : IrInst;
    /// <summary>Opens a blocking TCP connection, yielding a socket into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the connected socket.</param>
    /// <param name="HostTemp">Temp holding the host.</param>
    /// <param name="PortTemp">Temp holding the port.</param>
    public sealed record NetTcpConnect(int Target, int HostTemp, int PortTemp) : IrInst;
    /// <summary>Sends text over a TCP socket, yielding a status into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    /// <param name="SocketTemp">Temp holding the socket.</param>
    /// <param name="TextTemp">Temp holding the text to send.</param>
    public sealed record NetTcpSend(int Target, int SocketTemp, int TextTemp) : IrInst;
    /// <summary>Receives up to <paramref name="MaxBytesTemp"/> bytes from a TCP socket into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the bytes read.</param>
    /// <param name="SocketTemp">Temp holding the socket.</param>
    /// <param name="MaxBytesTemp">Temp holding the maximum number of bytes to receive.</param>
    public sealed record NetTcpReceive(int Target, int SocketTemp, int MaxBytesTemp) : IrInst;
    /// <summary>Closes a TCP socket, yielding a status into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    /// <param name="SocketTemp">Temp holding the socket to close.</param>
    public sealed record NetTcpClose(int Target, int SocketTemp) : IrInst;
    /// <summary>Binds and listens on a local port, yielding a listener socket into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the listener socket.</param>
    /// <param name="PortTemp">Temp holding the port to bind.</param>
    public sealed record NetTcpListen(int Target, int PortTemp) : IrInst;
    /// <summary>Accepts one connection from a listener socket, yielding the accepted socket into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the accepted socket.</param>
    /// <param name="SocketTemp">Temp holding the listener socket.</param>
    public sealed record NetTcpAccept(int Target, int SocketTemp) : IrInst;

    // Ashes.Byte operations.  TBytes layout: {length:i64, data:u8[length]} — identical to TStr.
    /// <summary>Yields the empty byte buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the empty bytes value.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesEmpty(int Target, bool RuntimeManaged = false) : IrInst;
    /// <summary>Builds a one-byte buffer from <paramref name="ByteTemp"/> into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the singleton bytes value.</param>
    /// <param name="ByteTemp">Temp holding the byte value.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesSingleton(int Target, int ByteTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Yields the length of a byte buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the length.</param>
    /// <param name="BytesTemp">Temp holding the byte buffer.</param>
    public sealed record BytesLength(int Target, int BytesTemp) : IrInst;
    /// <summary>Reads the byte at <paramref name="IndexTemp"/> from a buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the byte value.</param>
    /// <param name="BytesTemp">Temp holding the byte buffer.</param>
    /// <param name="IndexTemp">Temp holding the byte index.</param>
    public sealed record BytesGet(int Target, int BytesTemp, int IndexTemp) : IrInst;
    /// <summary>Finds the first occurrence of a needle in a buffer at or after <paramref name="FromTemp"/>,
    /// yielding the index (or a sentinel) into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the match index.</param>
    /// <param name="BytesTemp">Temp holding the haystack buffer.</param>
    /// <param name="NeedleTemp">Temp holding the needle buffer.</param>
    /// <param name="FromTemp">Temp holding the start offset.</param>
    public sealed record BytesIndexOf(int Target, int BytesTemp, int NeedleTemp, int FromTemp) : IrInst;
    /// <summary>Three-way lexicographic comparison of two byte buffers into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the comparison result.</param>
    /// <param name="LeftTemp">Temp holding the left buffer.</param>
    /// <param name="RightTemp">Temp holding the right buffer.</param>
    public sealed record BytesCompare(int Target, int LeftTemp, int RightTemp) : IrInst;
    /// <summary>Scans for a needle in a buffer using a rolling hash from <paramref name="FromTemp"/>, yielding
    /// the match index into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the match index.</param>
    /// <param name="BytesTemp">Temp holding the haystack buffer.</param>
    /// <param name="NeedleTemp">Temp holding the needle buffer.</param>
    /// <param name="FromTemp">Temp holding the start offset.</param>
    public sealed record BytesScanHash(int Target, int BytesTemp, int NeedleTemp, int FromTemp) : IrInst;
    /// <summary>Extracts a copied sub-range of a buffer as an owned value into <paramref name="Target"/>. See
    /// <see cref="BytesSubView"/> for the non-copying view.</summary>
    /// <param name="Target">Temp receiving the sub-buffer.</param>
    /// <param name="BytesTemp">Temp holding the source buffer.</param>
    /// <param name="StartTemp">Temp holding the start offset.</param>
    /// <param name="LenTemp">Temp holding the sub-range length.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesSubText(int Target, int BytesTemp, int StartTemp, int LenTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Produces a non-copying view over a sub-range of a buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the sub-buffer view.</param>
    /// <param name="BytesTemp">Temp holding the source buffer.</param>
    /// <param name="StartTemp">Temp holding the start offset.</param>
    /// <param name="LenTemp">Temp holding the sub-range length.</param>
    public sealed record BytesSubView(int Target, int BytesTemp, int StartTemp, int LenTemp) : IrInst;
    /// <summary>Concatenates two byte buffers into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the concatenated buffer.</param>
    /// <param name="LeftTemp">Temp holding the left buffer.</param>
    /// <param name="RightTemp">Temp holding the right buffer.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesAppend(int Target, int LeftTemp, int RightTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Appends a single byte to a buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the extended buffer.</param>
    /// <param name="BytesTemp">Temp holding the source buffer.</param>
    /// <param name="ByteTemp">Temp holding the byte to append.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesAppendByte(int Target, int BytesTemp, int ByteTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Builds a byte buffer from a list of byte values into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the built buffer.</param>
    /// <param name="ListTemp">Temp holding the source list.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesFromList(int Target, int ListTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Yields a hash of a byte buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the hash value.</param>
    /// <param name="BytesTemp">Temp holding the buffer to hash.</param>
    public sealed record BytesHash(int Target, int BytesTemp) : IrInst;
    /// <summary>Encodes a value as two little-endian bytes into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the encoded bytes.</param>
    /// <param name="ValueTemp">Temp holding the value to encode.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesU16Le(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Encodes a value as four little-endian bytes into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the encoded bytes.</param>
    /// <param name="ValueTemp">Temp holding the value to encode.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesU32Le(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Encodes a value as eight little-endian bytes into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the encoded bytes.</param>
    /// <param name="ValueTemp">Temp holding the value to encode.</param>
    /// <param name="RuntimeManaged">True when the result participates in reference-counted ownership.</param>
    public sealed record BytesU64Le(int Target, int ValueTemp, bool RuntimeManaged = false) : IrInst;
    /// <summary>Reads a little-endian 16-bit value at <paramref name="OffsetTemp"/> from a buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the decoded value.</param>
    /// <param name="BytesTemp">Temp holding the source buffer.</param>
    /// <param name="OffsetTemp">Temp holding the byte offset to read from.</param>
    public sealed record BytesGetU16Le(int Target, int BytesTemp, int OffsetTemp) : IrInst;
    /// <summary>Reads a little-endian 32-bit value at <paramref name="OffsetTemp"/> from a buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the decoded value.</param>
    /// <param name="BytesTemp">Temp holding the source buffer.</param>
    /// <param name="OffsetTemp">Temp holding the byte offset to read from.</param>
    public sealed record BytesGetU32Le(int Target, int BytesTemp, int OffsetTemp) : IrInst;
    /// <summary>Reads a little-endian 64-bit value at <paramref name="OffsetTemp"/> from a buffer into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the decoded value.</param>
    /// <param name="BytesTemp">Temp holding the source buffer.</param>
    /// <param name="OffsetTemp">Temp holding the byte offset to read from.</param>
    public sealed record BytesGetU64Le(int Target, int BytesTemp, int OffsetTemp) : IrInst;
    /// <summary>Writes a byte buffer to a file, yielding a status into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    /// <param name="PathTemp">Temp holding the file path.</param>
    /// <param name="BytesTemp">Temp holding the bytes to write.</param>
    public sealed record FileWriteBytes(int Target, int PathTemp, int BytesTemp) : IrInst;

    // Ashes.IO.Process operations. ProcessRef is a pointer to {stdin_fd, stdout_fd, stderr_fd, pid} (32 bytes).
    /// <summary>Spawns a child process, yielding a process handle into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the process handle.</param>
    /// <param name="ExeTemp">Temp holding the executable path.</param>
    /// <param name="ArgsTemp">Temp holding the argument list.</param>
    public sealed record SpawnProcess(int Target, int ExeTemp, int ArgsTemp) : IrInst;
    /// <summary>Writes text to a child process's standard input, yielding a status into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    /// <param name="ProcessTemp">Temp holding the process handle.</param>
    /// <param name="TextTemp">Temp holding the text to write.</param>
    public sealed record ProcessWriteStdin(int Target, int ProcessTemp, int TextTemp) : IrInst;
    /// <summary>Reads one line from a child process's standard output into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the line read.</param>
    /// <param name="ProcessTemp">Temp holding the process handle.</param>
    public sealed record ProcessReadStdoutLine(int Target, int ProcessTemp) : IrInst;
    /// <summary>Reads one line from a child process's standard error into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the line read.</param>
    /// <param name="ProcessTemp">Temp holding the process handle.</param>
    public sealed record ProcessReadStderrLine(int Target, int ProcessTemp) : IrInst;
    /// <summary>Waits for a child process to exit, yielding its exit code into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the exit code.</param>
    /// <param name="ProcessTemp">Temp holding the process handle.</param>
    public sealed record ProcessWaitForExit(int Target, int ProcessTemp) : IrInst;
    /// <summary>Kills a child process, yielding a status into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the operation result.</param>
    /// <param name="ProcessTemp">Temp holding the process handle.</param>
    public sealed record ProcessKill(int Target, int ProcessTemp) : IrInst;

    /// <summary>
    /// Deterministic cleanup of a resource value. Unlike ordinary heap lifetime markers, this has
    /// observable runtime behavior: files, sockets, processes, and resource-owning closures must be
    /// released even while ordinary heap values remain arena-managed.
    /// </summary>
    public sealed record CleanupResource(int SourceTemp, string TypeName) : IrInst;

    /// <summary>
    /// Perceus lifetime marker for an ordinary owned heap value whose ownership dies here. During
    /// the erased-marker migration stage this is a backend no-op and is removed by the optimizer;
    /// arena restoration remains responsible for actual memory reclamation.
    /// </summary>
    public sealed record RcDrop(
        int SourceTemp,
        string TypeName,
        int OwnerSlot = -1, // Lowering provenance used by precise placement; -1 for already-placed markers.
        bool RuntimeManaged = false
    ) : IrInst;

    /// <summary>
    /// Perceus lifetime marker for splitting ownership of an ordinary heap value. The target is an
    /// identity-preserving alias of <paramref name="SourceTemp"/> until runtime reference counting is
    /// enabled; the optimizer erases the marker and remaps uses to the source.
    /// </summary>
    public sealed record RcDup(int Target, int SourceTemp, bool RuntimeManaged = false) : IrInst;

    /// <summary>
    /// Tests whether a runtime-managed value has exactly one owning reference. This operation is
    /// valid only for values whose allocation carries <see cref="HeapLayouts.RcHeader"/>.
    /// </summary>
    public sealed record RcIsUnique(int Target, int SourceTemp) : IrInst;

    /// <summary>
    /// Borrow instruction for compiler-inferred borrowing.
    /// Produces a non-owning reference to the owned value held in SourceTemp.
    /// The borrowed reference carries no drop responsibility — the owning scope
    /// still drops the original.
    /// In the current linear allocator this is a simple value copy (pointer pass-through).
    /// </summary>
    public sealed record Borrow(int Target, int SourceTemp) : IrInst;

    // Placeholder for a TCO back-edge arena block whose copy-out decision could not be made at
    // emission time because an argument's type was still an unresolved inference variable (e.g. an
    // accumulator only constrained by a deferred '+' or by the caller). Replaced in place — with the
    // real reset/copy-out block, or with nothing when the resolved types do not qualify — by
    // ResolveDeferredTcoResets at the end of lowering. The conservative temp/local use summaries
    // let coroutine state-machine liveness preserve everything the resolved block may read across
    // an await; the placeholder itself never reaches the backend.
    /// <summary>Placeholder for a TCO back-edge arena block whose copy-out decision could not be made at
    /// emission time (an operand type was still an unresolved inference variable). Replaced in place by
    /// <c>ResolveDeferredTcoResets</c> at the end of lowering; the placeholder never reaches the backend.</summary>
    /// <param name="Id">Correlates this placeholder with the deferred decision that resolves it.</param>
    /// <param name="UsedTemps">Conservative set of temps the resolved block may read, kept live across awaits.</param>
    /// <param name="ReadLocalSlots">Conservative set of local slots the resolved block may read.</param>
    public sealed record TcoResetPending(int Id, int[] UsedTemps, int[] ReadLocalSlots) : IrInst;

    /// <summary>
    /// Saves the current heap allocator state (cursor and end pointers) into two
    /// local slots. Emitted at ownership scope entry so that arena-based
    /// deallocation can restore the cursor at scope exit.
    /// </summary>
    /// <remarks>
    /// <see cref="CoroutineLoop"/> marks the save/restore/reclaim group emitted at an async
    /// tail-recursive loop's restart back-edge (inside a coroutine). The backend gates such a group:
    /// it is a no-op under the legacy task driver, and under the run-queue scheduler the restore and
    /// reclaim run only while the task's <c>LoopResetOk</c> header flag is set (cleared when a
    /// composite ancestor shares the arena, where a stale-watermark reset could free a sibling's
    /// live allocations).
    /// </remarks>
    public sealed record SaveArenaState(int CursorLocalSlot, int EndLocalSlot) : IrInst
    {
        /// <summary>True when this save is part of the save/restore/reclaim group at an async tail-recursive
        /// loop's restart back-edge, which the backend gates on the task's <c>LoopResetOk</c> header flag.</summary>
        public bool CoroutineLoop { get; init; }
    }

    /// <summary>
    /// Restores the heap allocator state (cursor and end pointers) from two local
    /// slots previously saved by <see cref="SaveArenaState"/>. Resets the bump
    /// pointer to the saved watermark, but does NOT free OS chunks — that is handled
    /// separately by <see cref="ReclaimArenaChunks"/>.
    ///
    /// <para>
    /// Before resetting, the current heap end pointer is saved to
    /// <see cref="PreRestoreEndSlot"/> so that a subsequent
    /// <see cref="ReclaimArenaChunks"/> can determine which chunks to free.
    /// </para>
    /// </summary>
    public sealed record RestoreArenaState(int CursorLocalSlot, int EndLocalSlot, int PreRestoreEndSlot) : IrInst
    {
        /// <summary>See <see cref="SaveArenaState.CoroutineLoop"/>.</summary>
        public bool CoroutineLoop { get; init; }
    }

    /// <summary>
    /// Frees OS chunks that were allocated between the saved watermark and the
    /// pre-restore heap state. Emitted AFTER <see cref="RestoreArenaState"/> and
    /// any <see cref="CopyOutArena"/> instructions, ensuring that copy-out reads
    /// complete before source chunks are unmapped.
    ///
    /// <para>
    /// Compares the saved end (<see cref="SavedEndSlot"/>) with the pre-restore end
    /// (<see cref="PreRestoreEndSlot"/>). If they differ, walks the chunk linked
    /// list from the pre-restore chunk back to the saved chunk, calling
    /// <c>munmap</c> (Linux) or <c>VirtualFree</c> (Windows) on each intermediate chunk.
    /// </para>
    /// </summary>
    public sealed record ReclaimArenaChunks(int SavedEndSlot, int PreRestoreEndSlot) : IrInst
    {
        /// <summary>See <see cref="SaveArenaState.CoroutineLoop"/>.</summary>
        public bool CoroutineLoop { get; init; }
    }

    /// <summary>
    /// Classifies every remaining copy-out so RC graph normalization cannot be confused with an
    /// arena lifetime fallback. The arena cases are deliberately limited to scoped scheduler or
    /// capability regions, TCO compaction inside those regions, and explicit independent cloning.
    /// </summary>
    public enum CopyOutPurpose
    {
        /// <summary>Ordinary RC graph normalization copy-out.</summary>
        RcNormalization,
        /// <summary>Arena copy-out at a scoped scheduler or capability region boundary.</summary>
        ArenaScopeBoundary,
        /// <summary>Arena copy-out crossing a call boundary out of a scoped region.</summary>
        ArenaCallBoundary,
        /// <summary>Arena copy-out for TCO compaction inside a scoped region.</summary>
        ArenaTcoCompaction,
        /// <summary>Explicit copy producing an independent clone of a value.</summary>
        IndependentClone,
    }

    /// <summary>
    /// Copies a heap object out of the arena to a fresh allocation, emitted AFTER
    /// <see cref="RestoreArenaState"/> but BEFORE <see cref="ReclaimArenaChunks"/>.
    /// The arena cursor has been reset to the scope-entry watermark W, but OS chunks
    /// have not yet been freed, so the source bytes at <paramref name="SrcTemp"/>
    /// are still physically readable. The copy is allocated starting from the reset
    /// cursor (at or below the source address), so a forward memcpy is always safe:
    /// dest ≤ src, no destructive overlap.
    ///
    /// <para>
    /// <b>String (<see cref="StaticSizeBytes"/> == -1):</b>
    /// The total size is read at runtime from the source object's length field
    /// (8 bytes at offset 0): <c>total = 8 + *src</c>. Allocates that many bytes
    /// and memcpy's the entire string (length word + inline bytes).
    /// </para>
    /// <para>
    /// <b>BigInt (<see cref="StaticSizeBytes"/> == <see cref="BigIntSize"/>):</b>
    /// The total size is read at runtime from the source object's header limb count
    /// (<c>total = 8 + (header &amp; 0xFFFFFFFF) * 8</c>). A BigInt is a self-contained
    /// <c>{ header, limb… }</c> buffer with no internal pointers, so a flat memcpy of the
    /// normalized prefix is a valid independent value — this is what lets a BigInt accumulator
    /// survive the TCO back-edge arena reset so the per-iteration BigInt garbage is reclaimed.
    /// </para>
    /// <para>
    /// <b>Fixed-size objects (<see cref="StaticSizeBytes"/> &gt; 0):</b>
    /// Allocates exactly <see cref="StaticSizeBytes"/> bytes and memcpy's them.
    /// Used for cons cells (16 bytes: head + tail) when the head is a copy type,
    /// ensuring the tail pointer to a pre-watermark cell is preserved.
    /// </para>
    /// </summary>
    /// <param name="DestTemp">Temp that receives the address of the fresh, arena-independent copy.</param>
    /// <param name="SrcTemp">Temp holding the source object still readable at the reset watermark.</param>
    /// <param name="StaticSizeBytes">Byte count for a fixed-size copy, or a sentinel selecting a
    /// runtime-sized mode: -1 for a string (size from the length word) or <see cref="BigIntSize"/>
    /// for a BigInt (size from the header limb count).</param>
    /// <param name="RuntimeManaged">True when the copied value is reference-counted, so the copy is
    /// registered with the runtime rather than treated as a plain arena clone.</param>
    /// <param name="Purpose">Classifies why the copy-out was emitted; see <see cref="CopyOutPurpose"/>.</param>
    /// <param name="DeferredElementType">When set, this copy-out was emitted provisionally for a
    /// tuple field whose element type was still an unresolved type variable (a string accumulator's
    /// var is only unified with Str by a later <c>+</c>). A post-lowering pass
    /// (<c>ResolveDeferredTupleMaterializations</c>) prunes this type once inference is complete: if
    /// it is <c>TStr</c> the copy-out stays; if it resolved to a scalar (Int/Float/Bool), the field
    /// never dangles and the copy-out is rewritten to a plain <see cref="Borrow"/> alias so no bytes
    /// are mis-copied as a string.</param>
    public sealed record CopyOutArena(
        int DestTemp,
        int SrcTemp,
        int StaticSizeBytes,
        bool RuntimeManaged,
        CopyOutPurpose Purpose,
        TypeRef? DeferredElementType = null
    ) : IrInst
    {
        /// <summary>Sentinel <see cref="StaticSizeBytes"/> selecting the BigInt copy mode (size from
        /// the header limb count). Distinct from -1 (string, size from the length word).</summary>
        public const int BigIntSize = -2;
    }

    /// <summary>
    /// Like <see cref="CopyOutArena"/> but the fresh copy is allocated in the persistent to-space
    /// (see <see cref="AllocAdtToSpace"/>) instead of the main arena. Used to materialize a heap-typed
    /// value (e.g. a String map key) that an in-place reuse specialization stores into the accumulator,
    /// so it survives the loop's per-iteration reset alongside the reused/to-space node.
    /// </summary>
    public sealed record CopyOutArenaToSpace(int DestTemp, int SrcTemp, int StaticSizeBytes) : IrInst;
    // In-place value-cell reuse: memcpy SizeBytes from SrcTemp into the (already-persistent, same-size) cell
    // at DestTemp. Used on the reuse/update path so a fresh fixed-size heap value (e.g. a Map tuple value)
    // overwrites the superseded value's blob cell instead of allocating a new one — keeps the blob bounded.
    /// <summary>In-place value-cell reuse: memcpys <paramref name="SizeBytes"/> from <paramref name="SrcTemp"/>
    /// into the already-persistent, same-size cell at <paramref name="DestTemp"/> on the reuse/update path,
    /// keeping the value blob bounded. See <see cref="CopyFixedIntoOrFresh"/> for the region-guarded form.</summary>
    /// <param name="DestTemp">Temp holding the destination cell pointer.</param>
    /// <param name="SrcTemp">Temp holding the source value.</param>
    /// <param name="SizeBytes">Number of bytes to copy.</param>
    public sealed record CopyFixedInto(int DestTemp, int SrcTemp, int SizeBytes) : IrInst;

    /// <summary>
    /// Variable-size in-place-or-fresh string/bytes value-cell reuse on the reuse/update path. The old
    /// value blob at <paramref name="OldBlobTemp"/> is the reused node's superseded value. It is
    /// overwritten in place ONLY when the new string at <paramref name="SrcTemp"/> both fits the old
    /// owned blob's capacity AND that blob provably lives in the persistent blob region (a runtime check
    /// against the current blob chunk); otherwise a fresh blob is materialized in the blob region. The
    /// region check keeps the reuse sound: a blob in the reclaimable main arena is never overwritten in
    /// place (which would dangle after the per-iteration arena reset). <paramref name="DestTemp"/>
    /// receives the resulting (in-place or fresh) blob pointer. Bounds blob growth to the largest value
    /// per cell — the variable-size analogue of <see cref="CopyFixedInto"/>.
    /// </summary>
    public sealed record CopyStringIntoOrFresh(int DestTemp, int OldBlobTemp, int SrcTemp) : IrInst;

    /// <summary>
    /// Fixed-size in-place-or-fresh value-cell reuse on the reuse/update path — the region-guarded
    /// form of <see cref="CopyFixedInto"/>. The old value cell at <paramref name="OldBlobTemp"/> is
    /// overwritten with <paramref name="SizeBytes"/> bytes from <paramref name="SrcTemp"/> ONLY when
    /// that cell provably lives in the persistent blob region (a runtime check against the current blob
    /// chunk); otherwise a fresh <paramref name="SizeBytes"/> cell is materialized in the blob region.
    /// The guard keeps the in-place write sound: a cell in the reclaimable main arena is never
    /// overwritten in place (which would dangle after the per-iteration arena reset).
    /// <paramref name="DestTemp"/> receives the resulting (in-place or fresh) cell pointer.
    /// </summary>
    public sealed record CopyFixedIntoOrFresh(int DestTemp, int OldBlobTemp, int SrcTemp, int SizeBytes) : IrInst;

    /// <summary>
    /// Describes how head values are handled during deep list copy-out.
    /// </summary>
    public enum ListHeadCopyKind
    {
        /// <summary>Head is an inline copy-type value (Int, Float, Bool). No head copy needed.</summary>
        Inline = 0,
        /// <summary>Head is a string pointer. Each string is dynamically copied (8 + length bytes).</summary>
        String = 1,
        /// <summary>Head is an inner list pointer (with copy-type elements). Each inner list is deep-copied.</summary>
        InnerList = 2,
    }

    /// <summary>
    /// Deep-copies an entire cons-cell chain out of the arena to fresh allocations.
    /// Each cons cell is 16 bytes: {head:i64, tail:i64}. The copy walks the tail
    /// pointers, allocating and copying each cell until a nil (0) tail is reached.
    /// <para>
    /// The <see cref="HeadCopy"/> parameter controls how head values are handled:
    /// <list type="bullet">
    ///   <item><b>Inline:</b> Head is a copy-type value stored directly in the cell's
    ///     head word. No additional copy needed.</item>
    ///   <item><b>String:</b> Head is a pointer to a string ({length, bytes}) that also
    ///     resides in arena memory. Each string is copied to a fresh allocation.</item>
    ///   <item><b>InnerList:</b> Head is a pointer to an inner cons-cell chain (with
    ///     copy-type elements). Each inner list is deep-copied recursively.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Emitted AFTER <see cref="RestoreArenaState"/> and BEFORE
    /// <see cref="ReclaimArenaChunks"/>, so old arena chunks are still readable.
    /// </para>
    /// </summary>
    public sealed record CopyOutList(
        int DestTemp,
        int SrcTemp,
        ListHeadCopyKind HeadCopy,
        bool RuntimeManaged,
        CopyOutPurpose Purpose
    ) : IrInst;

    /// <summary>
    /// Copies a closure ({code, env, packed env size, dropper}) and its
    /// environment out of the arena to a fresh allocation. Reads the env_size field
    /// from the source closure at offset 16, allocates env_size bytes for the env
    /// copy, then allocates the closure payload. Relinks the env pointer
    /// in the new closure to point to the new env copy.
    /// <para>
    /// If the env pointer is 0 (no captures), only the closure struct is
    /// copied (no env allocation needed).
    /// </para>
    /// <para>
    /// Emitted AFTER <see cref="RestoreArenaState"/> and BEFORE
    /// <see cref="ReclaimArenaChunks"/>, so old arena chunks are still readable.
    /// </para>
    /// </summary>
    public sealed record CopyOutClosure(
        int DestTemp,
        int SrcTemp,
        bool RuntimeManaged,
        CopyOutPurpose Purpose
    ) : IrInst;

    /// <summary>
    /// TCO-specific: copies a single cons cell (16 bytes) out of the arena and also
    /// copies the head value according to <see cref="HeadCopy"/>. Used for TCO
    /// accumulators of type <c>TList(TStr)</c> or <c>TList(TList(copy-type))</c>.
    /// <para>
    /// Only the top cons cell (created in the current TCO iteration) needs copying —
    /// the tail pointer already references cells from previous iterations that are
    /// safely below the arena watermark.
    /// </para>
    /// <para>
    /// <b>String head (<see cref="ListHeadCopyKind.String"/>):</b> Copies the string
    /// ({length, bytes}) to a fresh allocation, then copies the 16-byte cons cell
    /// and updates its head field to point to the new string.
    /// </para>
    /// <para>
    /// <b>InnerList head (<see cref="ListHeadCopyKind.InnerList"/>):</b> Deep-copies
    /// the inner cons-cell chain (with copy-type elements) to fresh allocations, then
    /// copies the 16-byte outer cons cell and updates its head to point to the new chain.
    /// </para>
    /// <para>
    /// Emitted AFTER <see cref="RestoreArenaState"/> and BEFORE
    /// <see cref="ReclaimArenaChunks"/>, so old arena chunks are still readable.
    /// </para>
    /// </summary>
    public sealed record CopyOutTcoListCell(
        int DestTemp,
        int SrcTemp,
        ListHeadCopyKind HeadCopy,
        CopyOutPurpose Purpose
    ) : IrInst;

    /// <summary>Converts an Ashes string to a NUL-terminated C string pointer for FFI, into <paramref name="Target"/>.</summary>
    /// <param name="Target">Temp receiving the C-string pointer.</param>
    /// <param name="StrTemp">Temp holding the source string.</param>
    public sealed record ToCString(int Target, int StrTemp) : IrInst;
    /// <summary>Calls an external (FFI) function, marshalling arguments and the result per the declared
    /// <see cref="FfiType"/> signature.</summary>
    /// <param name="Target">Temp receiving the (marshalled) return value.</param>
    /// <param name="SymbolName">Name of the external symbol to call.</param>
    /// <param name="LibraryName">Library the symbol resides in, or null for the default resolution.</param>
    /// <param name="ArgTemps">Temps holding the argument values, in order.</param>
    /// <param name="ParameterTypes">FFI types of the parameters, matching <paramref name="ArgTemps"/>.</param>
    /// <param name="ReturnType">FFI type of the return value.</param>
    public sealed record CallExternal(int Target, string SymbolName, string? LibraryName, IReadOnlyList<int> ArgTemps, IReadOnlyList<FfiType> ParameterTypes, FfiType ReturnType) : IrInst;

    /// <summary>
    /// Creates a Task value by allocating a task/state struct and storing
    /// the coroutine function pointer and captured environment.
    /// The task struct holds [state_index, coroutine_fn, result, awaited_task, captures...].
    /// StateStructSize includes the header, captures, and live variable slots.
    /// CaptureCount is the number of captured environment variables to copy.
    /// </summary>
    public sealed record CreateTask(int Target, int ClosureTemp, int StateStructSize, int CaptureCount) : IrInst
    {
        /// <summary>
        /// True for an async tail-recursive loop coroutine that emits a flagged arena reset at its
        /// restart back-edge; the backend stamps the task's <c>LoopResetOk</c> header flag so the
        /// scheduler can veto the reset when a composite ancestor shares the arena.
        /// </summary>
        public bool LoopResetEligible { get; init; }
    }

    /// <summary>
    /// Creates an already-completed Task value. Used by Ashes.Task.fromResult
    /// to wrap a value in a task without needing a coroutine function.
    /// The task struct has state_index = -1 (COMPLETED) and the result stored directly.
    /// </summary>
    public sealed record CreateCompletedTask(int Target, int ResultTemp) : IrInst;

    /// <summary>
    /// Awaits a Task value inside a coroutine. The state machine transform
    /// rewrites this into Suspend/Resume sequences at each await point.
    /// </summary>
    public sealed record AwaitTask(int Target, int TaskTemp) : IrInst;

    /// <summary>
    /// Synchronously runs a task to completion, returning the result value.
    /// Used by Ashes.Task.run to drive a coroutine outside an async context.
    /// </summary>
    public sealed record RunTask(int Target, int TaskTemp) : IrInst;

    /// <summary>
    /// Detaches a task for fire-and-forget cooperative execution (Ashes.Task.spawn).
    /// The task frame is copied into a fresh private arena chunk and appended to the runtime's
    /// detached-task list; the scheduler steps detached tasks (with their private arena installed)
    /// whenever a driver blocks waiting for a pending leaf, and frees the private arena when the
    /// task completes. The result value is dropped. Target receives Unit.
    /// </summary>
    public sealed record SpawnTask(int Target, int TaskTemp) : IrInst;

    /// <summary>
    /// Structured parallelism (Ashes.Task.Parallel.both). Spawns a worker thread to evaluate the
    /// <c>RightClosureTemp</c> thunk (applied to Unit) in its own per-thread arena, or — when
    /// the worker budget is exhausted — evaluates it inline. <c>DescTarget</c> receives a
    /// pointer to a heap-allocated task descriptor used by the matching <see cref="ParallelJoin"/>
    /// and <see cref="ParallelCleanup"/>. Only emitted at concrete result types (see
    /// LowerParallelBoth); polymorphic <c>both</c> lowers to a sequential pair instead.
    /// </summary>
    public sealed record ParallelFork(int DescTarget, int RightClosureTemp) : IrInst;

    /// <summary>
    /// Waits for the worker spawned by the matching <see cref="ParallelFork"/> to finish and
    /// yields its raw result pointer (in the worker's arena). The caller deep-copies that
    /// result into the parent arena before <see cref="ParallelCleanup"/> frees the worker arena.
    /// </summary>
    public sealed record ParallelJoin(int ResultTarget, int DescTemp) : IrInst;

    /// <summary>
    /// Releases the worker resources (stack, TCB, and arena chunks) for a finished
    /// <see cref="ParallelFork"/>; a no-op for the inline (un-spawned) case. Must run after the
    /// worker result has been deep-copied out.
    /// </summary>
    public sealed record ParallelCleanup(int DescTemp) : IrInst;

    /// <summary>
    /// Loads the current dynamically-scoped worker override (the runtime global set by
    /// <c>Ashes.Task.Parallel.withWorkers</c>); 0 means "unset — use the compiled max". Used by
    /// <c>withWorkers</c> lowering to save/restore the enclosing scope's value.
    /// </summary>
    public sealed record LoadParallelWorkerOverride(int Target) : IrInst;

    /// <summary>Stores a value into the worker-override global (0 clears it).</summary>
    public sealed record StoreParallelWorkerOverride(int Source) : IrInst;

    /// <summary>
    /// Work-conserving parallel reduce (queued lowering of Ashes.Task.Parallel.reduce). Snapshots the
    /// list elements into a shared queue region, spawns up to the worker-cap worker threads that
    /// pull element indexes from a shared atomic counter, record <c>f(element)</c> per index, and
    /// then merge the results pairwise through <c>combine</c> — adjacent index pairs per round,
    /// an odd trailing item promoting, until a single root remains. The merge shape depends only
    /// on the element count and pairs each left operand with the elements preceding its right
    /// operand, so the result is deterministic under reduce's associative-combine contract no
    /// matter which worker computes what. <c>DescTarget</c> receives the queue descriptor; the
    /// element count is readable at descriptor offset 8. When no worker slot can be claimed, the
    /// caller drains the whole queue — folds and merges — inline (a correct sequential fallback).
    /// Only emitted at concrete result types the caller can deep-copy out of a worker arena (see
    /// LowerParallelReduceQueued).
    /// </summary>
    public sealed record ParallelQueueStart(int DescTarget, int FClosureTemp, int CombineClosureTemp, int ListTemp) : IrInst;

    /// <summary>
    /// Waits until the merge tree's root result has been published and yields its raw pointer
    /// (in some worker's arena, possibly referencing several). The caller deep-copies it into its
    /// own arena before <see cref="ParallelQueueCleanup"/> frees the worker arenas. Must not be
    /// emitted for an empty element list (there is no root; the caller branches to the identity).
    /// </summary>
    public sealed record ParallelQueueAwait(int ResultTarget, int DescTemp) : IrInst;

    /// <summary>
    /// Waits for every spawned queue worker to fully exit, then frees the workers' stacks, TCBs,
    /// and arena chunks plus the queue region itself. Must run after every awaited result has been
    /// deep-copied out.
    /// </summary>
    public sealed record ParallelQueueCleanup(int DescTemp) : IrInst;

    /// <summary>
    /// State machine suspend point: saves live variables to the state struct,
    /// stores the awaited sub-task, sets the next state index, and returns SUSPENDED.
    /// Generated by the state machine transform (not emitted directly by lowering).
    /// </summary>
    public sealed record Suspend(int StateStructTemp, int NextState, int AwaitedTaskTemp,
        IReadOnlyList<(int SlotOffset, int SourceTemp)> SaveVars) : IrInst;

    /// <summary>
    /// State machine resume point: restores live variables from the state struct
    /// and loads the result from the awaited sub-task.
    /// Generated by the state machine transform (not emitted directly by lowering).
    /// </summary>
    public sealed record Resume(int StateStructTemp, int ResultTemp,
        IReadOnlyList<(int SlotOffset, int TargetTemp)> RestoreVars) : IrInst;

    /// <summary>
    /// Creates a sleep task that completes after the given number of milliseconds.
    /// Returns a Task(Str, Int) that suspends and resumes after the timeout.
    /// Used by Ashes.Task.sleep.
    /// </summary>
    public sealed record AsyncSleep(int Target, int MillisecondsTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP connect.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpConnectTask(int Target, int HostTemp, int PortTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP send.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpSendTask(int Target, int SocketTemp, int TextTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP receive.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpReceiveTask(int Target, int SocketTemp, int MaxBytesTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP close.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpCloseTask(int Target, int SocketTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP listen (bind + listen on a local port).
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpListenTask(int Target, int PortTemp) : IrInst;

    /// <summary>
    /// Creates a leaf task that forks (CountTemp - 1) child processes for the fork-based
    /// multi-reactor (serveParallel), so CountTemp processes total each run their own reactor.
    /// Returns this worker's index (0-based). Synchronous — never parks. Linux-only; a single
    /// process on other targets.
    /// </summary>
    public sealed record CreateForkWorkersTask(int Target, int PortTemp, int CountTemp) : IrInst;

    /// <summary>Sets the graceful-shutdown drain bound (ms) for this process; yields unit.</summary>
    public sealed record SetDrainTimeout(int Target, int MsTemp) : IrInst;

    /// <summary>Requests graceful whole-server shutdown (Stop.stop): rides the signal path on
    /// Linux (worker signals the parent, which forwards); sets the shutdown flag on Windows.
    /// No source temps; side-effectful. Yields unit.</summary>
    public sealed record RequestServerStop(int Target) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for TCP accept (accept one connection from a listener).
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTcpAcceptTask(int Target, int SocketTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for HTTP GET.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateHttpGetTask(int Target, int UrlTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for HTTP POST.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateHttpPostTask(int Target, int UrlTemp, int BodyTemp) : IrInst;

    /// <summary>
    /// Creates a staged networking task for TLS connect (TCP connect + TLS handshake).
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// </summary>
    public sealed record CreateTlsConnectTask(int Target, int HostTemp, int PortTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for a TLS handshake on top of an existing TCP socket.
    /// The task is completed by the runtime/task runner rather than a coroutine body.
    /// Internal-only: emitted from staged HTTPS/TLS connect lowering, not from a user-visible builtin.
    /// </summary>
    public sealed record CreateTlsHandshakeTask(int Target, int SocketTemp, int HostTemp) : IrInst;

    /// <summary>
    /// Creates a leaf task that runs the SERVER half of a TLS handshake over an accepted TCP
    /// socket. CertTemp/KeyTemp are PEM contents (Str): the certificate chain and private key.
    /// The server config is built once and cached process-wide; the handshake parks on
    /// WaitTlsWantRead/Write and completes with Ok(TlsSocket).
    /// </summary>
    public sealed record CreateTlsServerHandshakeTask(int Target, int SocketTemp, int CertTemp, int KeyTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for sending text over a TLS session.
    /// </summary>
    public sealed record CreateTlsSendTask(int Target, int SslTemp, int TextTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for receiving text over a TLS session.
    /// </summary>
    public sealed record CreateTlsReceiveTask(int Target, int SslTemp, int MaxBytesTemp) : IrInst;

    /// <summary>
    /// Creates a leaf networking task for closing a TLS session (close-notify flush + connection free).
    /// </summary>
    public sealed record CreateTlsCloseTask(int Target, int SslTemp) : IrInst;

    /// <summary>
    /// Runs all tasks in a list to completion and collects results into a list.
    /// Returns a completed Task(E, List(A)) containing all result values.
    /// Used by Ashes.Task.all.
    /// </summary>
    public sealed record AsyncAll(int Target, int TaskListTemp) : IrInst;

    /// <summary>
    /// Runs the first task in a list to completion and returns its result.
    /// Returns a completed Task(E, A) with the first task's result value.
    /// Used by Ashes.Task.race.
    /// </summary>
    public sealed record AsyncRace(int Target, int TaskListTemp) : IrInst;

    /// <summary>Aborts execution with the message string in <paramref name="Source"/> (an uncatchable panic).</summary>
    /// <param name="Source">Temp holding the panic message.</param>
    public sealed record PanicStr(int Source) : IrInst;

    /// <summary>
    /// Loads the current handler frame pointer for the capability with compile-time index
    /// <see cref="CapabilityIndex"/> from its module global (dynamically-scoped handler evidence).
    /// 0 means no handler is installed.
    /// </summary>
    public sealed record LoadCapabilityHandler(int Target, int CapabilityIndex) : IrInst;

    /// <summary>Stores a handler frame pointer into the capability's module global.</summary>
    public sealed record StoreCapabilityHandler(int CapabilityIndex, int Source) : IrInst;

    /// <summary>Defines a branch target named <paramref name="Name"/> in the instruction stream.</summary>
    /// <param name="Name">The label's name, referenced by <see cref="Jump"/>/<see cref="JumpIfFalse"/>.</param>
    public sealed record Label(string Name) : IrInst;
    /// <summary>Unconditionally branches to the <see cref="Label"/> named <paramref name="Target"/>.</summary>
    /// <param name="Target">Name of the label to jump to.</param>
    public sealed record Jump(string Target) : IrInst;
    /// <summary>Branches to <paramref name="Target"/> when the boolean in <paramref name="CondTemp"/> is false,
    /// otherwise falls through.</summary>
    /// <param name="CondTemp">Temp holding the branch condition.</param>
    /// <param name="Target">Name of the label to jump to when the condition is false.</param>
    public sealed record JumpIfFalse(int CondTemp, string Target) : IrInst;

    /// <summary>
    /// Multi-way dispatch on an ADT tag value (decision-tree pattern matching).
    /// Branches to the label paired with the case whose tag equals
    /// <see cref="TagTemp"/>, or to <see cref="DefaultLabel"/> when none match.
    /// Emitted by match lowering in place of a linear chain of tag comparisons when a
    /// match is over many single-ADT constructor arms; lowers to an LLVM <c>switch</c>
    /// (jump table or balanced binary search). A block terminator.
    /// </summary>
    public sealed record SwitchTag(int TagTemp, IReadOnlyList<(long Tag, string Label)> Cases, string DefaultLabel) : IrInst;

    /// <summary>Returns from the current function with the value in <paramref name="Source"/>. A block terminator.</summary>
    /// <param name="Source">Temp holding the value to return.</param>
    public sealed record Return(int Source) : IrInst;
}

/// <summary>An interned string constant: <paramref name="Value"/> addressed by <paramref name="Label"/> and
/// loaded via <see cref="IrInst.LoadConstStr"/>.</summary>
/// <param name="Label">Unique label naming this literal.</param>
/// <param name="Value">The literal's string contents.</param>
public sealed record IrStringLiteral(string Label, string Value);

/// <summary>Type of a value crossing the FFI boundary, describing how it is marshalled to and from C.</summary>
public abstract record FfiType
{
    /// <summary>A signed 64-bit integer.</summary>
    public sealed record Int : FfiType;
    /// <summary>An unsigned integer of width <paramref name="Bits"/>.</summary>
    /// <param name="Bits">Declared bit width, one of 8, 16, 32, or 64.</param>
    public sealed record UInt(int Bits) : FfiType;
    /// <summary>A 64-bit (double-precision) floating-point value.</summary>
    public sealed record Float : FfiType;
    /// <summary>A 32-bit (single-precision) floating-point value.</summary>
    public sealed record Float32 : FfiType;
    /// <summary>A boolean value.</summary>
    public sealed record Bool : FfiType;
    /// <summary>A string, marshalled as a NUL-terminated C string.</summary>
    public sealed record Str : FfiType;
    /// <summary>An opaque handle type identified by <paramref name="Name"/>, passed through unmodified.</summary>
    /// <param name="Name">The opaque type's name.</param>
    public sealed record Opaque(string Name) : FfiType;
    /// <summary>A pointer to <paramref name="Pointee"/>.</summary>
    /// <param name="Pointee">The pointed-to FFI type.</param>
    public sealed record Ptr(FfiType Pointee) : FfiType;
    /// <summary>No value (a <c>void</c> return).</summary>
    public sealed record Void : FfiType;
}

/// <summary>A declared external (FFI) function: the Ashes-visible <paramref name="Name"/> bound to the native
/// symbol <paramref name="SymbolName"/> with the given FFI signature. Invoked via <see cref="IrInst.CallExternal"/>.</summary>
/// <param name="Name">The Ashes-visible function name.</param>
/// <param name="SymbolName">The native symbol name to link against.</param>
/// <param name="ParameterTypes">FFI types of the parameters, in order.</param>
/// <param name="ReturnType">FFI type of the return value.</param>
/// <param name="LibraryName">Library the symbol resides in, or null for the default resolution.</param>
public sealed record IrExternalFunction(
    string Name,
    string SymbolName,
    IReadOnlyList<FfiType> ParameterTypes,
    FfiType ReturnType,
    string? LibraryName = null);

/// <summary>
/// Metadata for a coroutine function generated from an async block.
/// Describes the state machine layout computed by the state machine transform.
/// </summary>
public sealed record CoroutineInfo(
    int StateCount,         // number of states (N await points = N+1 states)
    int StateStructSize,    // total size of the state struct in bytes
    int CaptureCount        // number of captured environment variables
);

/// <summary>
/// Fixed header offsets in the task/state struct (each slot is 8 bytes).
/// </summary>
public static class TaskStructLayout
{
    /// <summary>Offset of the current state number (i64): -1 = COMPLETED, -2 = SLEEPING.</summary>
    public const int StateIndex = 0;       // current state number (i64): -1 = COMPLETED, -2 = SLEEPING
    /// <summary>Offset of the pointer to the coroutine function (i64).</summary>
    public const int CoroutineFn = 8;      // pointer to coroutine function (i64)
    /// <summary>Offset of the result value / awaited task result (i64).</summary>
    public const int ResultSlot = 16;      // result value / awaited task result (i64)
    /// <summary>Offset of the pointer to the sub-task being awaited (i64).</summary>
    public const int AwaitedTask = 24;     // pointer to sub-task being awaited (i64)
    /// <summary>Offset of the queue linked-list "next" pointer (i64).</summary>
    public const int NextTask = 32;        // queue linked list pointer (i64)
    /// <summary>Offset of the sleep duration in milliseconds (i64).</summary>
    public const int SleepDurationMs = 40; // sleep duration in milliseconds (i64)
    /// <summary>Offset of leaf-task argument slot 0 (i64).</summary>
    public const int IoArg0 = 48;          // leaf-task argument slot 0 (i64)
    /// <summary>Offset of leaf-task argument slot 1 (i64).</summary>
    public const int IoArg1 = 56;          // leaf-task argument slot 1 (i64)
    /// <summary>Offset of the pending wait descriptor kind (i64); see the <c>Wait*</c> constants.</summary>
    public const int WaitKind = 64;        // pending wait descriptor kind (i64)
    /// <summary>Offset of the pending wait handle / socket (i64).</summary>
    public const int WaitHandle = 72;      // pending wait handle / socket (i64)
    /// <summary>Offset of pending wait scratch slot 0 (i64).</summary>
    public const int WaitData0 = 80;       // pending wait scratch slot 0 (i64)
    /// <summary>Offset of pending wait scratch slot 1 (i64).</summary>
    public const int WaitData1 = 88;       // pending wait scratch slot 1 (i64)
    /// <summary>Offset of the total task-struct size including captures and live slots (i64).</summary>
    public const int FrameSizeBytes = 96;  // total task struct size incl. captures + live slots (i64)
    /// <summary>Offset of the detached task's private arena cursor; 0 unless spawned (i64).</summary>
    public const int ArenaCursor = 104;    // detached task's private arena cursor; 0 unless spawned (i64)
    /// <summary>Offset of the detached task's private arena end; 0 unless spawned (i64).</summary>
    public const int ArenaEnd = 112;       // detached task's private arena end; 0 unless spawned (i64)
    /// <summary>Offset of the run-queue "next ready task" link (i64).</summary>
    public const int ReadyNext = 120;      // run-queue "next ready task" link (i64); run-queue scheduler
    /// <summary>Offset of the task blocked on this task's completion, re-enqueued when it completes (i64).</summary>
    public const int Waiter = 128;         // task blocked on this task's completion, re-enqueued on it (i64)
    /// <summary>Offset of the nearest spawned-ancestor whose arena this task shares; 0 = global (i64).</summary>
    public const int ArenaOwner = 136;     // nearest spawned-ancestor whose arena this task shares; 0 = global (i64)
    /// <summary>Offset of the flag (1 = this async-loop coroutine may reset its arena at the restart back-edge) (i64).</summary>
    public const int LoopResetOk = 144;    // 1 = this async-loop coroutine may reset its arena at the restart back-edge (i64)
    /// <summary>Total header size in bytes; captures and live slots follow at this offset.</summary>
    public const int HeaderSize = 152;     // total header size in bytes
    // Captures follow at [HeaderSize + i*8]
    // Live variable slots follow captures

    /// <summary>State index value indicating the task has completed.</summary>
    public const long StateCompleted = -1;
    /// <summary>State index value indicating the task is sleeping (timer-based suspend).</summary>
    public const long StateSleeping = -2;
    /// <summary>State index value indicating a leaf TCP connect task.</summary>
    public const long StateTcpConnect = -10;
    /// <summary>State index value indicating a leaf TCP send task.</summary>
    public const long StateTcpSend = -11;
    /// <summary>State index value indicating a leaf TCP receive task.</summary>
    public const long StateTcpReceive = -12;
    /// <summary>State index value indicating a leaf TCP close task.</summary>
    public const long StateTcpClose = -13;
    /// <summary>State index value indicating a leaf TCP listen task.</summary>
    public const long StateTcpListen = -16;
    /// <summary>State index value indicating a leaf TCP accept task.</summary>
    public const long StateTcpAccept = -17;

    /// <summary>State index value indicating a leaf fork-workers task (multi-reactor process fork).</summary>
    public const long StateForkWorkers = -18;
    /// <summary>State index value indicating a leaf HTTP GET task.</summary>
    public const long StateHttpGet = -14;
    /// <summary>State index value indicating a leaf HTTP POST task.</summary>
    public const long StateHttpPost = -15;
    /// <summary>State index value indicating a staged TLS connect task.</summary>
    public const long StateTlsConnect = -19;
    /// <summary>State index value indicating a leaf TLS handshake task.</summary>
    public const long StateTlsHandshake = -20;
    /// <summary>State index value indicating a leaf TLS send task.</summary>
    public const long StateTlsSend = -21;
    /// <summary>State index value indicating a leaf TLS receive task.</summary>
    public const long StateTlsReceive = -22;
    /// <summary>State index value indicating a leaf TLS close task.</summary>
    public const long StateTlsClose = -23;
    /// <summary>State index value indicating a leaf server-side TLS handshake task.</summary>
    public const long StateTlsServerHandshake = -24;
    /// <summary>
    /// Run-queue composite task: <c>Ashes.Task.all</c>. Holds the child task list in <c>IoArg0</c>,
    /// a phase flag in <c>IoArg1</c> (0 = children not yet enqueued, 1 = enqueued), and a pending
    /// child counter in <c>WaitData0</c> (decremented by each child's completion; the composite is
    /// re-enqueued and collects results when it reaches 0).
    /// </summary>
    public const long StateAllComposite = -40;
    /// <summary>
    /// Run-queue composite task: <c>Ashes.Task.race</c>. Holds the child list in <c>IoArg0</c>, a
    /// phase flag in <c>IoArg1</c>, and a resolved flag in <c>WaitData0</c> (0 until the first child
    /// completes, whose result is delivered to the composite's <c>ResultSlot</c> and which re-enqueues
    /// the composite; later child completions are ignored).
    /// </summary>
    public const long StateRaceComposite = -41;

    /// <summary>No pending wait is registered for the task.</summary>
    public const long WaitNone = 0;
    /// <summary>The task is waiting for a socket to become readable.</summary>
    public const long WaitSocketRead = 1;
    /// <summary>The task is waiting for a socket to become writable.</summary>
    public const long WaitSocketWrite = 2;
    /// <summary>The task is waiting for a TLS read path to make progress.</summary>
    public const long WaitTlsWantRead = 3;
    /// <summary>The task is waiting for a TLS write path to make progress.</summary>
    public const long WaitTlsWantWrite = 4;
    /// <summary>The task is waiting for a sleep timer to elapse (cooperative sleep suspension).
    /// The remaining milliseconds live in <see cref="SleepDurationMs"/> of the sleeping leaf task
    /// (the task itself when it is a bare sleep leaf, or its <see cref="AwaitedTask"/> when a coroutine
    /// is suspended on one). The scheduler waits until the earliest such deadline instead of blocking
    /// on each sleep inline, so sibling tasks make progress while one sleeps.</summary>
    public const long WaitTimer = 5;
}

/// <summary>A lowered function: its label, its linear instruction stream, and the frame sizing and debug
/// metadata the backend needs to emit it.</summary>
/// <param name="Label">The function's unique label (also its LLVM symbol).</param>
/// <param name="Instructions">The function body as an ordered <see cref="IrInst"/> list.</param>
/// <param name="LocalCount">Number of local variable slots the frame reserves.</param>
/// <param name="TempCount">Number of numbered temps used by the body.</param>
/// <param name="HasEnvAndArgParams">True for lifted lambdas that take the implicit environment and argument
/// parameters.</param>
/// <param name="Coroutine">Non-null for async coroutine functions, describing their state-machine layout.</param>
/// <param name="LocalNames">Optional map from local slot to source name, for debug info.</param>
/// <param name="LocalTypes">Optional map from local slot to inferred type, for debug info.</param>
public sealed record IrFunction(
    string Label,
    List<IrInst> Instructions,
    int LocalCount,
    int TempCount,
    bool HasEnvAndArgParams, // true for lambdas (implicit env+arg params)
    CoroutineInfo? Coroutine = null, // non-null for async coroutine functions
    IReadOnlyDictionary<int, string>? LocalNames = null, // slot → source name (debug info)
    IReadOnlyDictionary<int, TypeRef>? LocalTypes = null // slot → inferred type (debug info)
);

/// <summary>The whole lowered program handed to the backend: the entry function, every other function,
/// interned string literals, external declarations, and feature flags telling codegen which runtime
/// facilities to link.</summary>
/// <param name="EntryFunction">The program's entry point function.</param>
/// <param name="Functions">All non-entry functions in the program.</param>
/// <param name="StringLiterals">Interned string constants referenced by <see cref="IrInst.LoadConstStr"/>.</param>
/// <param name="ExternalFunctions">Declared FFI functions the program calls.</param>
/// <param name="ExternalOpaqueTypes">Names of opaque FFI types the program references.</param>
/// <param name="UsesPrintInt">True when the program emits <see cref="IrInst.PrintInt"/>.</param>
/// <param name="UsesPrintStr">True when the program emits <see cref="IrInst.PrintStr"/>.</param>
/// <param name="UsesPrintBool">True when the program emits <see cref="IrInst.PrintBool"/>.</param>
/// <param name="UsesConcatStr">True when the program performs string concatenation.</param>
/// <param name="UsesClosures">True when the program constructs closures.</param>
/// <param name="UsesAsync">True when the program uses async/await coroutines.</param>
public sealed record IrProgram(
    IrFunction EntryFunction,
    List<IrFunction> Functions,
    List<IrStringLiteral> StringLiterals,
    List<IrExternalFunction> ExternalFunctions,
    IReadOnlySet<string> ExternalOpaqueTypes,
    bool UsesPrintInt,
    bool UsesPrintStr,
    bool UsesPrintBool,
    bool UsesConcatStr,
    bool UsesClosures,
    bool UsesAsync
)
{
    /// <summary>
    /// Number of declared capabilities. The backend materializes one module global per capability — the
    /// dynamically-scoped handler-evidence slot holding a pointer to the innermost installed
    /// handler frame for that capability (0 when none).
    /// </summary>
    public int CapabilityHandlerGlobals { get; init; }

    /// <summary>Convenience constructor for a program with no external functions or opaque types, defaulting
    /// those to empty collections.</summary>
    public IrProgram(
        IrFunction EntryFunction,
        List<IrFunction> Functions,
        List<IrStringLiteral> StringLiterals,
        bool UsesPrintInt,
        bool UsesPrintStr,
        bool UsesPrintBool,
        bool UsesConcatStr,
        bool UsesClosures,
        bool UsesAsync)
        : this(EntryFunction, Functions, StringLiterals, [], new HashSet<string>(StringComparer.Ordinal),
            UsesPrintInt, UsesPrintStr, UsesPrintBool, UsesConcatStr, UsesClosures, UsesAsync)
    {
    }
}
