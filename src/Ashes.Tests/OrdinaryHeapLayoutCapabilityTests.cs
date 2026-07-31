using System.Reflection;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class OrdinaryHeapLayoutCapabilityTests
{
    [Test]
    public void Record_layout_retains_copy_drop_offset_and_reuse_capabilities()
    {
        Lowering lowering = LowerProgram(
            """
            type Box =
                | value: Str

            let box = Box(value = "hello")
            Ashes.IO.print(box.value)
            """);
        TypeRef type = new TypeRef.TNamedType(lowering.TypeSymbols["Box"], []);

        OrdinaryHeapLayoutCapability capability = Describe(lowering, type);

        capability.OuterLayout.ShouldBe(LoweredTempLayoutKind.Adt);
        capability.StructuralCopy.ShouldBe(OrdinaryHeapStructuralCopyKind.Deep);
        capability.ArenaDeepCopySupported.ShouldBeTrue();
        capability.OwnedChildrenDroppable.ShouldBeTrue();
        capability.RuntimeRecordAdtSupported.ShouldBeTrue();
        capability.RuntimeOuterCellReuseSupported.ShouldBeTrue();
        capability.Rejections.ShouldBe(OrdinaryHeapLayoutRejection.None);
        OrdinaryHeapLayoutChild child = capability.Children.ShouldHaveSingleItem();
        child.ConstructorName.ShouldBe("Box");
        child.OffsetBytes.ShouldBe(HeapLayouts.WordSizeBytes);
        child.DropKind.ShouldBe(OrdinaryHeapChildDropKind.String);
    }

    [Test]
    public void Heterogeneous_adt_uses_constructor_specific_child_layouts()
    {
        Lowering lowering = LowerProgram(
            """
            type Choice =
                | Empty
                | Text(Str)

            let value = Text("hello")
            Ashes.IO.print("ok")
            """);
        TypeRef type = new TypeRef.TNamedType(lowering.TypeSymbols["Choice"], []);

        OrdinaryHeapLayoutCapability capability = Describe(lowering, type);

        capability.RuntimeCopyAdtSupported.ShouldBeFalse();
        capability.RuntimeRecordAdtSupported.ShouldBeFalse();
        capability.RuntimeOwnedChildAdtSupported.ShouldBeTrue();
        capability.RuntimeOuterCellReuseSupported.ShouldBeTrue();
        OrdinaryHeapLayoutChild child = capability.Children.ShouldHaveSingleItem();
        child.ConstructorName.ShouldBe("Text");
        child.Index.ShouldBe(0);
        child.OffsetBytes.ShouldBe(HeapLayouts.Adt.PayloadWordOffsetBytes(0));
        child.DropKind.ShouldBe(OrdinaryHeapChildDropKind.String);
    }

    [Test]
    public void Recursive_adt_analysis_is_cycle_guarded()
    {
        Lowering lowering = LowerProgram(
            """
            type Tree =
                | Leaf
                | Node(Tree, Tree)

            let tree = Node(Leaf, Leaf)
            Ashes.IO.print("ok")
            """);
        TypeRef type = new TypeRef.TNamedType(lowering.TypeSymbols["Tree"], []);

        OrdinaryHeapLayoutCapability capability = Describe(lowering, type);

        capability.StructuralCopy.ShouldBe(OrdinaryHeapStructuralCopyKind.None);
        capability.ArenaDeepCopySupported.ShouldBeFalse();
        capability.OwnedChildrenDroppable.ShouldBeTrue();
        capability.RuntimeOuterCellReuseSupported.ShouldBeTrue();
        capability.Children.Count.ShouldBe(2);
        capability.Children.All(child =>
            child.DropKind == OrdinaryHeapChildDropKind.Adt).ShouldBeTrue();
    }

    [Test]
    public void Tco_positional_layout_stays_separate_from_outer_cell_reuse()
    {
        Lowering lowering = LowerProgram(
            """
            type State =
                | State(List(Int))

            let state = State([1, 2, 3])
            Ashes.IO.print("ok")
            """);
        TypeRef type = new TypeRef.TNamedType(lowering.TypeSymbols["State"], []);

        OrdinaryHeapLayoutCapability capability = Describe(lowering, type);

        capability.RuntimeTcoOwnedChildAdtSupported.ShouldBeTrue();
        capability.RuntimeOuterCellReuseSupported.ShouldBeFalse();
        (capability.Rejections
            & OrdinaryHeapLayoutRejection.UnsupportedOuterCellReuse)
            .ShouldBe(OrdinaryHeapLayoutRejection.UnsupportedOuterCellReuse);
        capability.Children.ShouldHaveSingleItem().DropKind.ShouldBe(
            OrdinaryHeapChildDropKind.List);
    }

    [Test]
    public void Bytes_graph_is_tco_eligible_when_values_are_normalized_at_the_owning_boundary()
    {
        Lowering lowering = LowerProgram("Ashes.IO.print(\"ok\")");

        OrdinaryHeapLayoutCapability capability = Describe(
            lowering,
            new TypeRef.TBytes());

        capability.ContainsResourceOrBorrowedView.ShouldBeFalse();
        capability.RuntimeTcoListElementSupported.ShouldBeTrue();
        (capability.Rejections
            & OrdinaryHeapLayoutRejection.ResourceOrBorrowedViewContainment)
            .ShouldBe(OrdinaryHeapLayoutRejection.None);
    }

    [Test]
    public void Resource_and_unresolved_layouts_retain_stable_rejections()
    {
        Lowering lowering = LowerProgram("Ashes.IO.print(\"ok\")");

        OrdinaryHeapLayoutCapability resource = Describe(
            lowering,
            lowering.ResolveTypeName("Socket"));
        OrdinaryHeapLayoutCapability unresolved = Describe(
            lowering,
            new TypeRef.TVar(987654));

        (resource.Rejections
            & OrdinaryHeapLayoutRejection.ResourceOrBorrowedViewContainment)
            .ShouldBe(OrdinaryHeapLayoutRejection.ResourceOrBorrowedViewContainment);
        resource.RuntimeOuterCellReuseSupported.ShouldBeFalse();
        (unresolved.Rejections & OrdinaryHeapLayoutRejection.UnresolvedType)
            .ShouldBe(OrdinaryHeapLayoutRejection.UnresolvedType);
        (unresolved.Rejections
            & OrdinaryHeapLayoutRejection.UnsupportedChildDropLayout)
            .ShouldBe(OrdinaryHeapLayoutRejection.UnsupportedChildDropLayout);
    }

    private static OrdinaryHeapLayoutCapability Describe(
        Lowering lowering,
        TypeRef type)
    {
        MethodInfo method = typeof(Lowering).GetMethod(
            "GetOrdinaryHeapLayoutCapability",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(TypeRef)],
            modifiers: null)
            ?? throw new MissingMethodException(
                typeof(Lowering).FullName,
                "GetOrdinaryHeapLayoutCapability");
        return (OrdinaryHeapLayoutCapability)method.Invoke(lowering, [type])!;
    }

    private static Lowering LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        Program program = new Parser(source, diagnostics).ParseProgram();
        var lowering = new Lowering(diagnostics);
        lowering.Lower(program);
        diagnostics.Errors.ShouldBeEmpty();
        return lowering;
    }
}
