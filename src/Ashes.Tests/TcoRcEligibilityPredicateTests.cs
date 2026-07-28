using System.Reflection;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// Pins the truth table of IsRcEligibleScalarTupleOrAdtType, the scalar/tuple/ADT type-shape test
// shared by IsIndependentlyRcEligibleTcoParam, LowerCallTcoPromoteResolvedRuntimeParam's resolved-
// argument-type check, and TcoBackEdgeRuntimeManagedArgCanReset. Before this predicate was extracted,
// each of those three sites carried its own copy of the same OR chain (one copy with an extra,
// entirely redundant disjunct); this suite exercises every TypeRef shape category the shared
// predicate itself decides on, independent of any single call site's own list/closure handling
// (which stays separate at each site and is not covered here).
public sealed class TcoRcEligibilityPredicateTests
{
    private static bool InvokeIsRcEligible(Lowering lowering, TypeRef type)
    {
        MethodInfo method = typeof(Lowering)
            .GetMethod("IsRcEligibleScalarTupleOrAdtType", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Method 'IsRcEligibleScalarTupleOrAdtType' not found via reflection.");
        return (bool)method.Invoke(lowering, [type])!;
    }

    [Test]
    public void Plain_scalar_types_are_never_eligible()
    {
        var lowering = new Lowering(new Diagnostics());

        InvokeIsRcEligible(lowering, new TypeRef.TInt()).ShouldBeFalse();
        InvokeIsRcEligible(lowering, new TypeRef.TBool()).ShouldBeFalse();
        InvokeIsRcEligible(lowering, new TypeRef.TFloat()).ShouldBeFalse();
    }

    [Test]
    public void Str_and_BigInt_are_always_eligible()
    {
        var lowering = new Lowering(new Diagnostics());

        InvokeIsRcEligible(lowering, new TypeRef.TStr()).ShouldBeTrue();
        InvokeIsRcEligible(lowering, new TypeRef.TBigInt()).ShouldBeTrue();
    }

    [Test]
    public void Tuple_of_copy_safe_elements_is_eligible()
    {
        var lowering = new Lowering(new Diagnostics());
        var tuple = new TypeRef.TTuple([new TypeRef.TInt(), new TypeRef.TInt()]);

        InvokeIsRcEligible(lowering, tuple).ShouldBeTrue();
    }

    [Test]
    public void Tuple_containing_a_function_element_is_not_eligible()
    {
        var lowering = new Lowering(new Diagnostics());
        var funElement = new TypeRef.TFun(new TypeRef.TInt(), new TypeRef.TInt());
        var tuple = new TypeRef.TTuple([new TypeRef.TInt(), funElement]);

        InvokeIsRcEligible(lowering, tuple).ShouldBeFalse();
    }

    // A synthetic zero-constructor type symbol never reaches the constructor-field walk any of the
    // three underlying ADT predicates perform (each bails out on the constructor-count check before
    // resolving a single field), so it is safe to hand-build here without lowering a real program.
    [Test]
    public void Zero_constructor_named_type_is_not_eligible()
    {
        var lowering = new Lowering(new Diagnostics());
        var symbol = new TypeSymbol("Empty", [], [], new TypeDecl("Empty", [], []));
        var namedType = new TypeRef.TNamedType(symbol, []);

        InvokeIsRcEligible(lowering, namedType).ShouldBeFalse();
    }

    private static Lowering LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        var lowering = new Lowering(diagnostics);
        lowering.Lower(program);
        diagnostics.Errors.ShouldBeEmpty();
        return lowering;
    }

    [Test]
    public void Single_constructor_record_of_copy_safe_fields_is_eligible_via_shallow_copy_out()
    {
        const string source =
            """
            type Point =
                | x: Int
                | y: Int

            let p = Point(x = 1, y = 2)

            Ashes.IO.print(Ashes.Text.fromInt(p.x))
            """;

        var lowering = LowerProgram(source);
        var namedType = new TypeRef.TNamedType(lowering.TypeSymbols["Point"], []);

        InvokeIsRcEligible(lowering, namedType).ShouldBeTrue();
    }

    [Test]
    public void Single_constructor_record_with_a_heap_field_is_eligible_via_the_runtime_managed_dropper()
    {
        const string source =
            """
            type BoxedName =
                | label: Str

            let b = BoxedName(label = "hi")

            Ashes.IO.print(b.label)
            """;

        var lowering = LowerProgram(source);
        var namedType = new TypeRef.TNamedType(lowering.TypeSymbols["BoxedName"], []);

        InvokeIsRcEligible(lowering, namedType).ShouldBeTrue();
    }

    [Test]
    public void Multi_constructor_adt_with_only_copy_safe_fields_is_not_eligible()
    {
        // Every field across every constructor is arena-resettable on its own, so there is nothing a
        // runtime-managed representation would need to protect -- the arena-only representation is
        // already sufficient and neither CanCopyOutAdt (differing arities) nor CanRuntimeManageAdt
        // (more than one constructor) nor CanRuntimeManageTcoAdt's owned-child variants (no field ever
        // fails CanArenaReset, so none counts as an "owned child") license the promotion.
        const string source =
            """
            type Color =
                | Red
                | Green
                | Blue(Int)

            let c = Blue(3)

            Ashes.IO.print(Ashes.Text.fromInt(1))
            """;

        var lowering = LowerProgram(source);
        var namedType = new TypeRef.TNamedType(lowering.TypeSymbols["Color"], []);

        InvokeIsRcEligible(lowering, namedType).ShouldBeFalse();
    }
}
