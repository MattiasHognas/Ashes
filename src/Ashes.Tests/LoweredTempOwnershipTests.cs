using System.Reflection;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LoweredTempOwnershipTests
{
    [Test]
    public void Runtime_managed_result_instructions_require_the_registration_contract()
    {
        Type[] candidates = typeof(IrInst)
            .GetNestedTypes(BindingFlags.Public)
            .Where(type => type.IsAssignableTo(typeof(IrInst)))
            .Where(type => type.GetProperty("RuntimeManaged")?.PropertyType == typeof(bool))
            .Where(type => type.GetProperty("Target")?.PropertyType == typeof(int)
                || type.GetProperty("DestTemp")?.PropertyType == typeof(int))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();

        Type[] missingContracts = candidates
            .Where(type => !type.IsAssignableTo(typeof(IrInst.IRuntimeManagedTargetResult))
                && !type.IsAssignableTo(typeof(IrInst.IRuntimeManagedDestinationResult)))
            .ToArray();

        missingContracts.ShouldBe([typeof(IrInst.DropReuse)]);
    }

    [Test]
    public void Emission_forward_propagates_runtime_ownership_without_an_instruction_scan()
    {
        var lowering = new Lowering(new Diagnostics());

        Emit(lowering, new IrInst.AllocAdt(1, 0, 0, RuntimeManaged: true));
        Emit(lowering, new IrInst.Borrow(2, 1));
        Emit(lowering, new IrInst.RcDup(3, 1, RuntimeManaged: true));
        Emit(lowering, new IrInst.BytesSubView(4, 1, 10, 11));
        Emit(lowering, new IrInst.Alloc(5, 16, RuntimeManaged: false));
        Emit(lowering, new IrInst.CallKnown(6, "known", 20, 21));
        Emit(lowering, new IrInst.CallClosure(7, 22, 23));

        IReadOnlyDictionary<int, LoweredTempOwnershipFact> facts = Facts(lowering);
        facts[1].Representation.ShouldBe(LoweredTempRepresentation.RuntimeRc);
        facts[1].Layout.ShouldBe(LoweredTempLayoutKind.Adt);
        facts[1].Ownership.ShouldBe(LoweredTempOwnershipKind.NewlyProduced);
        facts[1].OwnerTemp.ShouldBe(1);
        facts[2].Representation.ShouldBe(LoweredTempRepresentation.RuntimeRc);
        facts[2].Ownership.ShouldBe(LoweredTempOwnershipKind.Borrowed);
        facts[2].OwnerTemp.ShouldBe(1);
        facts[3].Representation.ShouldBe(LoweredTempRepresentation.RuntimeRc);
        facts[3].Ownership.ShouldBe(LoweredTempOwnershipKind.Transferred);
        facts[3].OwnerTemp.ShouldBe(3);
        facts[4].Representation.ShouldBe(LoweredTempRepresentation.BorrowedView);
        facts[4].Layout.ShouldBe(LoweredTempLayoutKind.Bytes);
        facts[4].DropKind.ShouldBe(LoweredTempDropKind.BorrowedViewNoDrop);
        facts[4].OwnerTemp.ShouldBe(1);
        facts[5].Representation.ShouldBe(LoweredTempRepresentation.ArenaRegion);
        facts[5].DropKind.ShouldBe(LoweredTempDropKind.NoRuntimeDrop);
        facts[6].Representation.ShouldBe(LoweredTempRepresentation.Unknown);
        facts[6].Reason.ShouldBe(LoweredTempOwnershipReason.KnownCallResult);
        facts[7].Representation.ShouldBe(LoweredTempRepresentation.Unknown);
        facts[7].Reason.ShouldBe(LoweredTempOwnershipReason.UnknownCallResult);
    }

    [Test]
    public void Lowered_value_carries_the_canonical_fact_snapshot()
    {
        var lowering = new Lowering(new Diagnostics());
        Emit(lowering, new IrInst.BytesEmpty(1, RuntimeManaged: true));

        MethodInfo createValue = typeof(Lowering).GetMethod(
            "CreateLoweredValue",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(Lowering).FullName,
                "CreateLoweredValue");
        var value = (LoweredValue)createValue.Invoke(
            lowering,
            [1, new TypeRef.TBytes()])!;

        value.Temp.ShouldBe(1);
        value.Type.ShouldBeOfType<TypeRef.TBytes>();
        value.Ownership.ShouldBeSameAs(Facts(lowering)[1]);
        value.Ownership.Representation.ShouldBe(
            LoweredTempRepresentation.RuntimeRc);
        value.Ownership.Layout.ShouldBe(LoweredTempLayoutKind.Bytes);
    }

    [Test]
    public void Runtime_representation_requires_a_consumer_ownership_proof()
    {
        LoweredValueRequest representationOnly =
            LoweredValueRequest.None with
            {
                RuntimeRepresentation =
                    LoweredValueRuntimeRepresentation.String,
            };
        LoweredValueRequest owned = LoweredValueRequest.OwnedRuntime(
            LoweredValueRuntimeRepresentation.String);

        representationOnly.EmitsRuntime(
            LoweredValueRuntimeRepresentation.String).ShouldBeFalse();
        owned.EmitsRuntime(
            LoweredValueRuntimeRepresentation.String).ShouldBeTrue();
    }

    [Test]
    public void Lowering_has_no_ambient_runtime_allocation_request_fields()
    {
        string[] requestFields = typeof(Lowering)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.Name)
            .Where(name => name.StartsWith(
                    "_runtimeRc",
                    StringComparison.Ordinal)
                && name.EndsWith(
                    "AllocationRequested",
                    StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        requestFields.ShouldBeEmpty();
    }

    private static void Emit(Lowering lowering, IrInst instruction)
    {
        MethodInfo method = typeof(Lowering).GetMethod(
            "Emit",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(Lowering).FullName, "Emit");
        method.Invoke(lowering, [instruction]);
    }

    private static IReadOnlyDictionary<int, LoweredTempOwnershipFact> Facts(
        Lowering lowering)
    {
        FieldInfo field = typeof(Lowering).GetField(
            "_tempOwnershipFacts",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(
                typeof(Lowering).FullName,
                "_tempOwnershipFacts");
        return (IReadOnlyDictionary<int, LoweredTempOwnershipFact>)field.GetValue(lowering)!;
    }
}
