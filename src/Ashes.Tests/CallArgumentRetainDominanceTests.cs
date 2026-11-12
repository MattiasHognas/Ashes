using System.Reflection;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class CallArgumentRetainDominanceTests
{
    [Test]
    public void Completed_normalizer_branches_do_not_hide_a_later_retain()
    {
        Dominates([
            new IrInst.JumpIfFalse(0, "copy"),
            new IrInst.Jump("join"),
            new IrInst.Label("copy"),
            new IrInst.Label("join"),
            new IrInst.RcDup(2, 1, RuntimeManaged: true),
        ]).ShouldBeTrue();
    }

    [Test]
    public void Retain_inside_a_conditional_arm_does_not_dominate_completion()
    {
        Dominates([new IrInst.JumpIfFalse(0, "else"), new IrInst.RcDup(2, 1, RuntimeManaged: true)]).ShouldBeFalse();
        Dominates([
            new IrInst.JumpIfFalse(0, "else"), new IrInst.Jump("join"),
            new IrInst.Label("else"), new IrInst.RcDup(2, 1, RuntimeManaged: true),
        ]).ShouldBeFalse();
    }

    [Test]
    public void Every_switch_target_must_rejoin_before_the_retain()
    {
        IrInst.SwitchTag branch = new(0, [(0, "zero"), (1, "one")], "other");
        Dominates([branch, new IrInst.Label("zero"), new IrInst.Label("one")]).ShouldBeFalse();
        Dominates([branch, new IrInst.Label("zero"), new IrInst.Label("one"), new IrInst.Label("other")]).ShouldBeTrue();
    }

    [Test]
    public void An_escape_from_the_argument_region_is_not_a_completed_branch()
    {
        Dominates([new IrInst.Label("outside"), new IrInst.Jump("outside")], start: 1).ShouldBeFalse();
    }

    private static bool Dominates(IrInst[] instructions, int start = 0)
    {
        MethodInfo method = typeof(Lowering).GetMethod("CallArgumentRetainDominatesCompletion", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("CallArgumentRetainDominatesCompletion");
        return (bool)method.Invoke(null, [instructions, start])!;
    }
}
