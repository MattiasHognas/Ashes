using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

public sealed class LinuxBackendCoverageTests
{
    [Test]
    public void Linux_backend_accepts_unoptimized_erased_rc_markers()
    {
        var instructions = new List<IrInst>
        {
            new IrInst.LoadConstInt(0, 42),
            new IrInst.RcDup(1, 0),
            new IrInst.RcDrop(1, "String"),
            new IrInst.Return(1),
        };
        var function = new IrFunction("entry", instructions, 0, 2, false);
        var program = new IrProgram(function, [], [], false, false, false, false, false, false);

        var bytes = new LinuxX64LlvmBackend().Compile(program);

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public async Task Linux_backend_runs_runtime_managed_adt_dup_and_drop()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var instructions = new List<IrInst>
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.LoadConstInt(1, 42),
            new IrInst.SetAdtField(0, 0, 1),
            new IrInst.RcDup(2, 0, RuntimeManaged: true),
            new IrInst.RcDrop(2, "Box", RuntimeManaged: true),
            new IrInst.GetAdtField(3, 0, 0),
            new IrInst.PrintInt(3),
            new IrInst.RcDrop(0, "Box", RuntimeManaged: true),
            new IrInst.LoadConstInt(4, 0),
            new IrInst.Return(4),
        };
        var function = new IrFunction("entry", instructions, 0, 5, false);
        var program = new IrProgram(function, [], [], true, false, false, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_reports_runtime_rc_uniqueness_transitions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var instructions = new List<IrInst>
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.RcIsUnique(1, 0),
            new IrInst.PrintBool(1),
            new IrInst.RcDup(2, 0, RuntimeManaged: true),
            new IrInst.RcIsUnique(3, 0),
            new IrInst.PrintBool(3),
            new IrInst.RcDrop(2, "UnitBox", RuntimeManaged: true),
            new IrInst.RcIsUnique(4, 0),
            new IrInst.PrintBool(4),
            new IrInst.RcDrop(0, "UnitBox", RuntimeManaged: true),
            new IrInst.LoadConstInt(5, 0),
            new IrInst.Return(5),
        };
        var function = new IrFunction("entry", instructions, 0, 6, false);
        var program = new IrProgram(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\nfalse\ntrue\n");
    }

    [Test]
    public async Task Linux_backend_runtime_drop_reuse_reuses_unique_cell()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 1, RuntimeManaged: true),
            new IrInst.LoadConstInt(1, 42),
            new IrInst.SetAdtField(0, 0, 1),
            new IrInst.DropReuse(2, 0, 1, RuntimeManaged: true),
            new IrInst.AllocReusing(3, 1, 1, 2, RuntimeManaged: true),
            new IrInst.LoadConstInt(4, 43),
            new IrInst.SetAdtField(3, 0, 4),
            new IrInst.GetAdtField(5, 3, 0),
            new IrInst.PrintInt(5),
            new IrInst.RcIsUnique(6, 3),
            new IrInst.PrintBool(6),
            new IrInst.RcDrop(3, "Box", RuntimeManaged: true),
            new IrInst.LoadConstInt(7, 0),
            new IrInst.Return(7),
        };
        IrFunction function = new("entry", instructions, 0, 8, false);
        IrProgram program = new(function, [], [], true, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("43\ntrue\n");
    }

    [Test]
    public async Task Linux_backend_runtime_drop_reuse_falls_back_when_cell_is_shared()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.DropReuse(2, 0, 0, RuntimeManaged: true),
            new IrInst.AllocReusing(3, 1, 0, 2, RuntimeManaged: true),
            new IrInst.CmpIntNe(4, 1, 3),
            new IrInst.PrintBool(4),
            new IrInst.RcIsUnique(5, 1),
            new IrInst.PrintBool(5),
            new IrInst.RcIsUnique(6, 3),
            new IrInst.PrintBool(6),
            new IrInst.RcDrop(1, "Box", RuntimeManaged: true),
            new IrInst.RcDrop(3, "Box", RuntimeManaged: true),
            new IrInst.LoadConstInt(7, 0),
            new IrInst.Return(7),
        };
        IrFunction function = new("entry", instructions, 0, 8, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\ntrue\ntrue\n");
    }

    [Test]
    public async Task Linux_backend_runtime_reuse_child_transfer_dups_for_null_token()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.AllocAdt(1, 0, 1, RuntimeManaged: true),
            new IrInst.SetAdtField(1, 0, 0),
            new IrInst.RcDup(2, 1, RuntimeManaged: true),
            new IrInst.GetAdtField(3, 1, 0),
            new IrInst.DropReuse(4, 1, 1, RuntimeManaged: true),
            new IrInst.LoadConstInt(5, 0),
            new IrInst.CmpIntNe(6, 4, 5),
            new IrInst.JumpIfFalse(6, "transfer_dup"),
            new IrInst.StoreLocal(0, 3),
            new IrInst.Jump("transfer_continue"),
            new IrInst.Label("transfer_dup"),
            new IrInst.RcDup(7, 3, RuntimeManaged: true),
            new IrInst.StoreLocal(0, 7),
            new IrInst.Label("transfer_continue"),
            new IrInst.LoadLocal(8, 0),
            new IrInst.AllocReusing(9, 1, 1, 4, RuntimeManaged: true),
            new IrInst.SetAdtField(9, 0, 8),
            new IrInst.RcIsUnique(10, 3),
            new IrInst.PrintBool(10),
            new IrInst.RcDrop(3, "Child", RuntimeManaged: true),
            new IrInst.RcDrop(2, "Parent", RuntimeManaged: true),
            new IrInst.RcIsUnique(11, 8),
            new IrInst.PrintBool(11),
            new IrInst.RcDrop(8, "Child", RuntimeManaged: true),
            new IrInst.RcDrop(9, "Parent", RuntimeManaged: true),
            new IrInst.LoadConstInt(12, 0),
            new IrInst.Return(12),
        };
        IrFunction function = new("entry", instructions, 1, 13, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("false\ntrue\n");
    }

    [Test]
    public async Task Linux_backend_lowers_copy_adt_rebuild_to_runtime_reuse()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            let choice = Left(42)
            match choice with
                | Left(value) -> Right(value + 1)
                | Right(value) -> Left(value - 1)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Linux_backend_releases_incompatible_runtime_reuse_token()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Choice =
                | Empty
                | One(Int)

            let choice = One(1)
            match choice with
                | Empty -> Empty
                | One(_) -> Empty
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Linux_backend_reuses_recursive_adt_after_releasing_old_children()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Leaf)(42)(Leaf)
            match tree with
                | Leaf -> Leaf
                | Node(_, value, _) -> Node(Leaf)(value + 1)(Leaf)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Linux_backend_reuses_recursive_adt_with_transferred_child()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Leaf)(42)(Leaf)
            match tree with
                | Leaf -> Leaf
                | Node(left, value, _) -> Node(left)(value + 1)(Leaf)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe(string.Empty);
    }

    [Test]
    public async Task Linux_backend_reuses_nested_record_with_transferred_child()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let rebuilt =
                let node = Node(child = Leaf(value = 40), bonus = 2) in
                match node with
                    | Node(child, bonus) -> Node(child = child, bonus = bonus + 1)
            match rebuilt with
                | Node(Leaf(value), bonus) -> Ashes.IO.print(value + bonus)
            """);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("43\n");
    }

    [Test]
    public async Task Linux_backend_reuses_pointer_variant_with_record_child()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Choice =
                | Empty
                | Full(Leaf, Int)

            let rebuilt =
                let choice = Full(Leaf(value = 40))(2) in
                match choice with
                    | Empty -> Empty
                    | Full(child, bonus) -> Full(child)(bonus + 1)
            match rebuilt with
                | Empty -> Ashes.IO.print(0)
                | Full(Leaf(value), bonus) -> Ashes.IO.print(value + bonus)
            """);
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.DropReuse { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("43\n");
    }

    [Test]
    public async Task Linux_backend_shares_existing_runtime_record_child_with_parent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let leaf = Leaf(value = 40)
            let node = Node(child = leaf, bonus = 2)
            Ashes.IO.print(node.bonus + leaf.value)
            """);
        program.EntryFunction.Instructions.Count(instruction =>
            instruction is IrInst.RcDup { RuntimeManaged: true }).ShouldBe(1);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_string_concat()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let text = \"ab\" + \"cd\" in Ashes.IO.print(text)");
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        program.EntryFunction.Instructions.Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("abcd\n");
    }

    [Test]
    public async Task Linux_backend_transfers_directly_escaping_runtime_string_without_copy_out()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcEscapingStringProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("4\n");
    }

    [Test]
    public async Task Linux_backend_transfers_direct_known_function_runtime_string_result_without_copy_out()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcKnownFunctionStringProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        program.EntryFunction.Instructions.Any(instruction => instruction is IrInst.CallClosure).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        program.Functions.Any(function =>
            function.Instructions.Any(instruction =>
                instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true })
            && function.Instructions.All(instruction => instruction is not IrInst.CopyOutArena)).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("4\n");
    }

    [Test]
    public async Task Linux_backend_transfers_direct_known_function_runtime_bytes_and_bigint_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcKnownFunctionBytesAndBigIntProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        program.Functions.Any(function =>
            function.Instructions.Any(instruction =>
                instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true })
            && function.Instructions.Any(instruction =>
                instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true })
            && function.Instructions.All(instruction => instruction is not IrInst.CopyOutArena)).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("133\n");
    }

    [Test]
    public async Task Linux_backend_transfers_directly_escaping_runtime_bytes_without_copy_out()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcEscapingBytesProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("19\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_bytes_append()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.append(Ashes.Byte.fromText(\"ab\"))(Ashes.Byte.fromText(\"cd\")) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("4\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_append_byte()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromText(\"ab\"))(33u8) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("3\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_bytes_from_list()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.fromList([7u8, 8u8, 9u8]) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("3\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_byte_singleton()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.singleton(7u8) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_empty_bytes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let measure unit = let bytes = Ashes.Byte.empty(Unit) in Ashes.Byte.length(bytes)\nAshes.IO.print(measure(0))");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesEmpty { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("0\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_fixed_width_bytes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcFixedWidthBytesProgram(iterations: 1));
        AllInstructions(program).Any(instruction => instruction is IrInst.BytesU16Le { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.BytesU32Le { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("14\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_byte_subtext()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let emit unit = let text = Ashes.Byte.subText(Ashes.Byte.fromText(\"abcdef\"))(1)(3) in Ashes.IO.print(text)\nemit(0)");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram nestedArenaProducer = LowerProgram(BuildRuntimeRcOwnedHeapClosureScratchProgram());
        AllInstructions(nestedArenaProducer).Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(nestedArenaProducer).Any(instruction =>
            instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("bcd\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_text_from_int()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let emit unit = let text = Ashes.Text.fromInt(-42) in Ashes.IO.print(text)\nemit(0)");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let text = Ashes.Text.fromInt(-42) in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(escaping).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("-42\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_matched_text_parse_int_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcTextParseIntProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextParseInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let parsed = Ashes.Text.parseInt(\"123\") in parsed");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextParseInt { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("123\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_matched_text_parse_float_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcTextParseFloatProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextParseFloat { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let parsed = Ashes.Text.parseFloat(\"1.5\") in parsed");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextParseFloat { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_matched_bigint_to_int_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntToIntProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntToInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let converted = Ashes.Number.BigInt.toInt(123N) in converted");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntToInt { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("123\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_compared_bigint_parse_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntParseResultProgram(iterations: 1));
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.BigIntFromString { RuntimeManaged: true }).ShouldBe(2);
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Result", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let parsed = Ashes.Text.parseBigInt(\"123\") in parsed");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntFromString { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_escaping_text_uncons_results()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcTextUnconsProgram(iterations: 1));
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.TextUncons { RuntimeManaged: true }).ShouldBe(2);
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Maybe", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();

        IrProgram escaping = LowerProgram("let split = Ashes.Text.uncons(\"abc\") in split");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextUncons { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("6\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_text_to_hex()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram("let emit unit = let text = Ashes.Text.toHex(48879) in Ashes.IO.print(text)\nemit(0)");
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextToHex { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let text = Ashes.Text.toHex(48879) in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextToHex { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("0xbeef\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_ascii_case_text()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcAsciiCaseTextProgram(iterations: 1));
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.TextAsciiCase { RuntimeManaged: true }).ShouldBe(2);
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(2);

        IrProgram escaping = LowerProgram("let text = Ashes.Text.asciiUpper(\"hello\") in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextAsciiCase { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("HELLO\nhello\n1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_consumed_float_text()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcFloatTextProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextFromFloat { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextFormatFloat { RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let text = Ashes.Text.fromFloat(12.25) in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.TextFromFloat { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("12.25\n12.250\n1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_compared_bigint_from_int()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntFromIntProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let escaped = (let value = Ashes.Number.BigInt.fromInt(42) in value) in Ashes.Number.BigInt.compare(escaped)(escaped)");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(escaping).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("0\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_compared_bigint_arithmetic()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntArithmeticProgram(iterations: 1));
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.BigIntBinary { RuntimeManaged: true }).ShouldBe(5);
        AllInstructions(program).Count(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeGreaterThanOrEqualTo(5);

        IrProgram escaping = LowerProgram("let value = Ashes.Number.BigInt.add(40N)(2N) in value");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntBinary { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("0\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_measured_bigint_text()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcBigIntTextProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntToString { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let text = Ashes.Text.fromBigInt(42N) in text");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.BigIntToString { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("30\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_called_copy_capture_closures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcCopyClosureProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Function", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram escaping = LowerProgram("let n = 1 in let f = if n > 0 then given (x) -> x + n else given (x) -> x + n in f");
        AllInstructions(escaping).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("2\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_called_owned_heap_capture_closures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcOwnedHeapClosureProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeFalse();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true }).ShouldBeTrue();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("8\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_called_owned_bytes_capture_closures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcOwnedBytesClosureProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram nestedProducer = LowerProgram(BuildRejectedRcOwnedBytesClosureScratchProgram());
        AllInstructions(nestedProducer).Any(instruction =>
            instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runtime_manages_immediately_called_owned_bigint_capture_closures()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildRuntimeRcOwnedBigIntClosureProgram(iterations: 1));
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true }).ShouldBeTrue();

        IrProgram arithmeticProducer = LowerProgram(BuildRuntimeRcOwnedBigIntClosureScratchProgram());
        AllInstructions(arithmeticProducer).Any(instruction =>
            instruction is IrInst.BigIntBinary { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(arithmeticProducer).Any(instruction =>
            instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("1\n");
    }

    [Test]
    public async Task Linux_backend_runs_optimized_runtime_rc_ownership_transfer()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.RcDrop(0, "UnitBox", RuntimeManaged: true),
            new IrInst.RcIsUnique(2, 1),
            new IrInst.PrintBool(2),
            new IrInst.RcDrop(1, "UnitBox", RuntimeManaged: true),
            new IrInst.LoadConstInt(3, 0),
            new IrInst.Return(3),
        };
        IrFunction function = new("entry", instructions, 0, 4, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);
        optimized.EntryFunction.Instructions.Any(inst => inst is IrInst.RcDup).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(optimized).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\n");
    }

    [Test]
    public async Task Linux_backend_runs_optimized_branch_sunk_runtime_rc_dup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<IrInst> instructions = new()
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.LoadConstBool(2, true),
            new IrInst.RcDup(1, 0, RuntimeManaged: true),
            new IrInst.JumpIfFalse(2, "else"),
            new IrInst.RcIsUnique(3, 1),
            new IrInst.PrintBool(3),
            new IrInst.RcDrop(1, "UnitBox", RuntimeManaged: true),
            new IrInst.Jump("end"),
            new IrInst.Label("else"),
            new IrInst.RcDrop(1, "UnitBox", RuntimeManaged: true),
            new IrInst.Label("end"),
            new IrInst.RcDrop(0, "UnitBox", RuntimeManaged: true),
            new IrInst.Return(2),
        };
        IrFunction function = new("entry", instructions, 0, 4, false);
        IrProgram program = new(function, [], [], false, false, true, false, false, false);

        IrProgram optimized = IrOptimizer.Optimize(program);
        optimized.EntryFunction.Instructions.Count(inst => inst is IrInst.RcDrop { SourceTemp: 1 }).ShouldBe(1);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(optimized).ConfigureAwait(false);

        result.Stdout.ShouldBe("false\n");
    }

    [Test]
    public async Task Linux_backend_keeps_runtime_rc_child_while_parent_is_shared()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var instructions = new List<IrInst>
        {
            new IrInst.AllocAdt(0, 0, 0, RuntimeManaged: true),
            new IrInst.AllocAdt(1, 0, 1, RuntimeManaged: true),
            new IrInst.SetAdtField(1, 0, 0),
            new IrInst.RcDup(2, 1, RuntimeManaged: true),
            new IrInst.RcIsUnique(3, 1),
            new IrInst.JumpIfFalse(3, "first_parent_shared"),
            new IrInst.RcDrop(0, "Leaf", RuntimeManaged: true),
            new IrInst.Label("first_parent_shared"),
            new IrInst.RcDrop(1, "Node", RuntimeManaged: true),
            new IrInst.RcIsUnique(4, 0),
            new IrInst.PrintBool(4),
            new IrInst.RcIsUnique(5, 2),
            new IrInst.JumpIfFalse(5, "second_parent_shared"),
            new IrInst.RcDrop(0, "Leaf", RuntimeManaged: true),
            new IrInst.Label("second_parent_shared"),
            new IrInst.RcDrop(2, "Node", RuntimeManaged: true),
            new IrInst.LoadConstInt(6, 0),
            new IrInst.Return(6),
        };
        var function = new IrFunction("entry", instructions, 0, 7, false);
        var program = new IrProgram(function, [], [], false, false, true, false, false, false);

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("true\n");
    }

    [Test]
    public void Linux_backend_compile_should_emit_elf_header_for_int_program()
    {
        var bytes = CompileForLinux("Ashes.IO.print(40 + 2)");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_support_compiler_features_used_by_ashes_programs()
    {
        var bytes = CompileForLinux("let z = 20 in let f = given (x) -> if x <= z then x + z else x + 1 in Ashes.IO.print(f(22))");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_not_emit_a_constant_image_for_simple_programs()
    {
        var first = CompileForLinux("Ashes.IO.print(40 + 2)");
        var second = CompileForLinux("Ashes.IO.print(40 + 3)");

        first.ShouldNotBe(second);
    }

    [Test]
    public void Linux_backend_parallel_worker_stack_size_tunable_is_honored()
    {
        var ir = LowerExpression("match Ashes.Task.Parallel.both(given (u) -> 3 + 4)(given (u) -> 5 + 6) with | (a, b) -> Ashes.IO.print(a + b)");

        byte[] unset = new LinuxX64LlvmBackend().Compile(ir);
        byte[] explicitDefault = new LinuxX64LlvmBackend().Compile(ir,
            new BackendCompileOptions(BackendOptimizationLevel.O2, ParallelWorkerStackBytes: 1L * 1024 * 1024));
        byte[] custom = new LinuxX64LlvmBackend().Compile(ir,
            new BackendCompileOptions(BackendOptimizationLevel.O2, ParallelWorkerStackBytes: 8L * 1024 * 1024));

        // Default is unchanged: leaving the tunable unset matches an explicit 1 MiB worker stack.
        explicitDefault.ShouldBe(unset);
        // The tunable is honored: an 8 MiB worker stack changes the emitted image.
        custom.ShouldNotBe(unset);
    }

    [Test]
    public void Linux_backend_compile_should_support_string_concat_programs()
    {
        var bytes = CompileForLinux("Ashes.IO.print(\"hello \" + \"world\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_support_large_rdata_programs()
    {
        var bytes = CompileForLinux($"Ashes.IO.print(\"{new string('a', 20000)}\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_compile_should_support_program_args_programs()
    {
        var bytes = CompileForLinux("match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")");

        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_float_arithmetic_and_comparisons()
    {
        AssertLinuxLlvmCompiles(LowerExpression("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_heap_backed_tuple_and_list_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b) | _ -> Ashes.IO.print(0)"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_adt_field_programs()
    {
        AssertLinuxLlvmCompiles(LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> Ashes.IO.print(a + b)
            """));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_string_compare_and_concat_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("if (\"he\" + \"llo\") == \"hello\" then 1 else 0"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_program_args_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("match Ashes.IO.args with | a :: b :: [] -> 1 | _ -> 0"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_read_line_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""match Ashes.IO.readLine(Unit) with | None -> 0 | Some(text) -> 1"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_file_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""match Ashes.IO.File.exists("present.txt") with | Ok(found) -> if found then 1 else 0 | Error(_) -> 0"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_network_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""match await Ashes.Net.Http.get("http://127.0.0.1:8080/") with | Ok(text) -> text | Error(msg) -> msg"""));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_print_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("Ashes.IO.write(\"hi\")"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_closure_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("let z = 20 in let f = given (x) -> x + z in f(22)"));
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_nested_heap_backed_closure_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("""let mk = given (x) -> given (y) -> let ignored = [x, y] in x + y in let f = mk(20) in f(22)"""));
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_first_order_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("let z = 20 in let f = given (x) -> x + z in Ashes.IO.print(f(22))").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_nested_heap_backed_closure_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("""let mk = given (x) -> given (y) -> let ignored = [x, y] in x + y in let f = mk(20) in Ashes.IO.print(f(22))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_preserve_nested_string_results_across_scope_cleanup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """
            let prefix = "outer" in
            let text =
                match 1 with
                    | 1 ->
                        let suffix = "inner" in
                        prefix + suffix
                    | _ -> "bad"
            in Ashes.IO.print(text)
            """).ConfigureAwait(false);
        result.Stdout.ShouldBe("outerinner\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_program_args_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            "match Ashes.IO.args with | a :: b :: [] -> Ashes.IO.print(a + \":\" + b) | _ -> Ashes.IO.print(\"bad\")",
            ["first", "second"]).ConfigureAwait(false);
        result.Stdout.ShouldBe("first:second\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_read_line_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.IO.readLine(Unit) with | None -> Ashes.IO.print("none") | Some(text) -> Ashes.IO.print(text)""",
            stdin: "hello\n").ConfigureAwait(false);
        result.Stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_return_none_at_read_line_eof()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.IO.readLine(Unit) with | None -> Ashes.IO.print("none") | Some(text) -> Ashes.IO.print(text)""",
            stdin: "").ConfigureAwait(false);
        result.Stdout.ShouldBe("none\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_file_read_text_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "hello.txt"), "hello").ConfigureAwait(false);

            var result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.readText("hello.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("hello\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_missing_file_read_errors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            var result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.readText("missing.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("Ashes.IO.File.readText() failed\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_invalid_utf8_file_read_errors()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tmpDir, "invalid_utf8.bin"), [0xFF, 0xFE, 0xFD]).ConfigureAwait(false);

            var result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.readText("invalid_utf8.bin") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("Ashes.IO.File.readText() encountered invalid UTF-8\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_file_write_text_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            var result = await CompileRunWithLinuxLlvmAsync(
                """match Ashes.IO.File.writeText("out.txt")("hello") with | Error(msg) -> Ashes.IO.print(msg) | Ok(_) -> match Ashes.IO.File.readText("out.txt") with | Ok(text) -> Ashes.IO.print(text) | Error(msg) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("hello\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_uncons_unicode_scalars()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.uncons("é!") with | None -> Ashes.IO.print("empty") | Some((head, tail)) -> Ashes.IO.print(head + "|" + tail)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("é|!\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_uncons_long_json_like_strings()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """
            let sample = "{ \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] }"
            in match Ashes.Text.uncons(sample) with
            | None -> Ashes.IO.print("none")
            | Some((head, tail)) ->
                if head == "{"
                then if tail == " \"name\" : \"Ashes\", \"active\" : true, \"count\" : 42, \"ratio\" : 1.5, \"items\" : [ null, false, { \"nested\" : \"ok\" } ] }"
                then Ashes.IO.print("ok")
                else Ashes.IO.print("bad")
                else Ashes.IO.print("bad")
            """).ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_parse_integers()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.parseInt("-42") with | Ok(value) -> Ashes.IO.print(value) | Error(msg) -> Ashes.IO.print(msg)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("-42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_parse_floats_with_exponents()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """match Ashes.Text.parseFloat("1e3") with | Ok(value) -> if value == 1000.0 then Ashes.IO.print("ok") else Ashes.IO.print("bad") | Error(msg) -> Ashes.IO.print(msg)""").ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_format_ints_floats_and_hex()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """Ashes.IO.print(Ashes.Text.fromInt(-42) + "|" + Ashes.Text.fromFloat(0.0 - 12.25) + "|" + Ashes.Text.toHex(48879))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("-42|-12.25|0xbeef\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_format_floats_with_fixed_precision()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(
            """Ashes.IO.print(Ashes.Text.formatFloat(3.141592653589793)(9) + "|" + Ashes.Text.formatFloat(0.0 - 12.25)(3) + "|" + Ashes.Text.formatFloat(2.5)(0))""").ConfigureAwait(false);
        result.Stdout.ShouldBe("3.141592654|-12.250|3\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_file_exists_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var tmpDir = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tmpDir, "present.txt"), "x").ConfigureAwait(false);

            var result = await CompileRunWithLinuxLlvmAsync(
                """match (Ashes.IO.File.exists("present.txt"), Ashes.IO.File.exists("missing.txt")) with | (Ok(a), Ok(b)) -> Ashes.IO.print((if a then "true" else "false") + ":" + (if b then "true" else "false")) | (Error(msg), _) -> Ashes.IO.print(msg) | (_, Error(msg)) -> Ashes.IO.print(msg)""",
                workingDirectory: tmpDir).ConfigureAwait(false);
            result.Stdout.ShouldBe("true:false\n");
        }
        finally
        {
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_tcp_connect_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.close(sock) with | Ok(_) -> "ok" | Error(msg) -> msg)""",
            async _ => await Task.Delay(100).ConfigureAwait(false)).ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_resolve_localhost_tcp_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("localhost")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.close(sock) with | Ok(_) -> "ok" | Error(msg) -> msg)""",
            async _ => await Task.Delay(100).ConfigureAwait(false)).ConfigureAwait(false);
        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_tcp_send_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> Ashes.IO.print(msg) | Ok(sock) -> match await Ashes.Net.Tcp.send(sock)("hello") with | Ok(n) -> Ashes.IO.print(n) | Error(msg) -> Ashes.IO.print(msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    (await ReadTextAsync(stream, 64).ConfigureAwait(false)).ShouldBe("hello");
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("5\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_tcp_receive_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Tcp.connect("__HOST__")(__PORT__) with | Error(msg) -> msg | Ok(sock) -> match await Ashes.Net.Tcp.receive(sock)(64) with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var payload = Encoding.UTF8.GetBytes("hello");
                    await stream.WriteAsync(payload).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("hello\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_http_get_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("http://__HOST__:__PORT__/hello") with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    request.ShouldContain("GET /hello HTTP/1.1");
                    request.ShouldContain("Host: 127.0.0.1");
                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from http");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("hello from http\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_http_post_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.post("http://__HOST__:__PORT__/echo")("hello") with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    request.ShouldContain("POST /echo HTTP/1.1");
                    request.ShouldContain("Content-Length: 5");
                    request.ShouldContain("\r\n\r\nhello");
                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nposted");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("posted\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_https_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET / HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello from https");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost").ConfigureAwait(false);
        result.Stdout.ShouldBe("hello from https\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_https_trust_failures_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nshould-not-succeed");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            trustServerCertificate: false,
            allowServerHandshakeFailure: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("Ashes TLS handshake failed: invalid peer certificate: UnknownIssuer\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_https_hostname_mismatches_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("https://__HOST__:__PORT__/") with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nshould-not-succeed");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "127.0.0.1",
            certificateHost: "localhost",
            allowServerHandshakeFailure: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("Ashes TLS handshake failed: invalid peer certificate: NotValidForName\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_return_first_completed_https_race_task_against_loopback_tls_fixture()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Task.race([Ashes.Net.Http.get("https://__HOST__:__PORT__/a"), Ashes.Net.Http.get("https://__HOST__:__PORT__/b")]) with | Ok(text) -> text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("Host: localhost");
                // Both endpoints respond with the same body ("ok") so the test result is
                // deterministic regardless of which Async.race task technically completes first
                // (avoids timing flakiness on loaded CI runners).
                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nok");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost",
            expectedClientCount: 2,
            tolerateClientDisconnect: true).ConfigureAwait(false);

        result.Stdout.ShouldBe("ok\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_treat_https_close_notify_eof_as_end_of_body()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmTlsLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("https://__HOST__:__PORT__/empty") with | Ok(text) -> if text == "" then "empty" else "bad:" + text | Error(msg) -> msg)""",
            async stream =>
            {
                var request = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                request.ShouldContain("GET /empty HTTP/1.1");
                request.ShouldContain("Host: localhost");

                var response = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            },
            host: "localhost").ConfigureAwait(false);

        result.Stdout.ShouldBe("empty\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_report_http_non_success_statuses()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmLoopbackAsync(
            """Ashes.IO.print(match await Ashes.Net.Http.get("http://__HOST__:__PORT__/missing") with | Ok(text) -> text | Error(msg) -> msg)""",
            async client =>
            {
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    _ = await ReadTextAsync(stream, 4096).ConfigureAwait(false);
                    var response = Encoding.UTF8.GetBytes("HTTP/1.1 404 Not Found\r\nConnection: close\r\n\r\nmissing");
                    await stream.WriteAsync(response).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        result.Stdout.ShouldBe("HTTP 404\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_float_arithmetic_and_comparisons()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("if (1.5 + 2.5) == 4.0 then Ashes.IO.print(42) else Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_heap_backed_tuple_and_list_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("match ([1, 2], (3, 4)) with | (x :: _, (a, b)) -> Ashes.IO.print(x + a + b) | _ -> Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("8\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_string_compare_and_concat_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("if (\"he\" + \"llo\") == \"hello\" then Ashes.IO.print(42) else Ashes.IO.print(0)").ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_print_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("Ashes.IO.write(\"hi\")").ConfigureAwait(false);
        result.Stdout.ShouldBe("hi");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_adt_field_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Pair = | Pair(A, B)
            let value = Pair(40, 2)
            in match value with
            | Pair(a, b) -> Ashes.IO.print(a + b)
            """)).ConfigureAwait(false);
        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_runtime_rc_copy_adt_match()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            let choice = Left(42)
            match choice with
                | Left(value) -> Ashes.IO.print(value)
                | Right(value) -> Ashes.IO.print(value + 1)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_release_runtime_rc_copy_adts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Choice =
                | Left(Int)
                | Right(Int)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let choice = Left(1) in
                    match choice with
                        | Left(value) -> loop(n - 1)(total + value)
                        | Right(value) -> loop(n - 1)(total + value + 1)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("20000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_allocator_reuses_mixed_size_blocks()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Small =
                | Small(Int)

            type Large =
                | Large(Int, Int, Int)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let small = Small(1) in
                    match small with
                        | Small(a) ->
                            let large = Large(1)(1)(1) in
                            match large with
                                | Large(b, c, d) -> loop(n - 1)(total + a + b + c + d)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("80000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_release_fresh_recursive_runtime_rc_adts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let tree = Node(Node(Leaf)(20)(Leaf))(42)(Node(Leaf)(22)(Leaf))
            match tree with
                | Leaf -> Ashes.IO.print(0)
                | Node(_, value, _) -> Ashes.IO.print(value)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_share_recursive_runtime_rc_adt_children()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let child = Node(Leaf)(20)(Leaf)
            let tree = Node(child)(42)(Leaf)
            match tree with
                | Leaf -> Ashes.IO.print(0)
                | Node(_, value, _) ->
                    match child with
                        | Leaf -> Ashes.IO.print(value)
                        | Node(_, childValue, _) -> Ashes.IO.print(value + childValue)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("62\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_share_recursive_runtime_rc_adt_children()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let child = Node(Leaf)(20)(Leaf) in
                    let tree = Node(child)(42)(Leaf) in
                    match tree with
                        | Leaf -> loop(n - 1)(total)
                        | Node(_, value, _) ->
                            match child with
                                | Leaf -> loop(n - 1)(total + value)
                                | Node(_, childValue, _) -> loop(n - 1)(total + value + childValue)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("1240000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_hot_loop_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> list = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcListMemoryProgram,
            outputPerIteration: 41).ConfigureAwait(false);
        List<MemoryExecutionResult> escapingList = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcEscapingListMemoryProgram,
            outputPerIteration: 41).ConfigureAwait(false);
        List<MemoryExecutionResult> ownedElementList = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcOwnedElementListMemoryProgram,
            outputPerIteration: 3).ConfigureAwait(false);
        List<MemoryExecutionResult> tuple = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcTupleMemoryProgram,
            outputPerIteration: 129).ConfigureAwait(false);
        List<MemoryExecutionResult> adt = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcAdtMemoryProgram,
            outputPerIteration: 196).ConfigureAwait(false);
        List<MemoryExecutionResult> recordOwnedChild = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcRecordOwnedChildMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);
        List<MemoryExecutionResult> variantOwnedChild = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcVariantOwnedChildMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);
        List<MemoryExecutionResult> genericOwnedChild = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcGenericOwnedChildMemoryProgram,
            outputPerIteration: 84).ConfigureAwait(false);
        List<MemoryExecutionResult> higherOrderResult = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcHigherOrderResultMemoryProgram,
            outputPerIteration: 8).ConfigureAwait(false);
        List<MemoryExecutionResult> reuse = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcAdtReuseMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> nestedRecordReuse = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcNestedRecordReuseMemoryProgram,
            outputPerIteration: 42).ConfigureAwait(false);
        List<MemoryExecutionResult> pointerVariantReuse = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcPointerVariantReuseMemoryProgram,
            outputPerIteration: 42).ConfigureAwait(false);
        List<MemoryExecutionResult> sharedRecordChild = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcSharedRecordChildMemoryProgram,
            outputPerIteration: 42).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC list", list);
        AssertMemoryPlateaus("runtime-RC escaping list", escapingList);
        AssertMemoryPlateaus("runtime-RC owned-element list", ownedElementList);
        AssertMemoryPlateaus("runtime-RC tuple", tuple);
        AssertMemoryPlateaus("runtime-RC ADT", adt);
        AssertMemoryPlateaus("runtime-RC record owned child", recordOwnedChild);
        AssertMemoryPlateaus("runtime-RC variant owned child", variantOwnedChild);
        AssertMemoryPlateaus("runtime-RC generic owned child", genericOwnedChild);
        AssertMemoryPlateaus("runtime-RC higher-order result", higherOrderResult);
        AssertMemoryPlateaus("runtime-RC ADT reuse", reuse);
        AssertMemoryPlateaus("runtime-RC nested-record reuse", nestedRecordReuse);
        AssertMemoryPlateaus("runtime-RC pointer-variant reuse", pointerVariantReuse);
        AssertMemoryPlateaus("runtime-RC shared record child", sharedRecordChild);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_higher_order_list_result_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> copySamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcHigherOrderListResultMemoryProgram,
            outputPerIteration: 40).ConfigureAwait(false);
        List<MemoryExecutionResult> stringSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcHigherOrderStringListResultMemoryProgram,
            outputPerIteration: 5).ConfigureAwait(false);
        List<MemoryExecutionResult> nestedSamples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcHigherOrderNestedListResultMemoryProgram,
            outputPerIteration: 40).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC higher-order copy-list result", copySamples);
        AssertMemoryPlateaus("runtime-RC higher-order String-list result", stringSamples);
        AssertMemoryPlateaus("runtime-RC higher-order nested-list result", nestedSamples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_closure_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildRuntimeRcEscapingClosureMemoryProgram(1));
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Function", RuntimeManaged: true }).ShouldBeTrue();

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcEscapingClosureMemoryProgram,
            outputPerIteration: 7).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC escaping closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_list_capture_closure_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildRuntimeRcEscapingListCaptureClosureMemoryProgram(1));
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcEscapingListCaptureClosureMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC escaping List capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_aggregate_capture_closure_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildRuntimeRcEscapingAggregateCaptureClosureMemoryProgram(1));
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Tuple", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "Box", RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(probe).Any(instruction =>
            instruction is IrInst.RcDrop { TypeName: "List", RuntimeManaged: true }).ShouldBeTrue();

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildRuntimeRcEscapingAggregateCaptureClosureMemoryProgram,
            outputPerIteration: 2).ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC escaping aggregate capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_string_concat_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcStringConcatMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC string concat", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_string_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcEscapingStringMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC directly escaping string", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_known_function_string_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcKnownFunctionStringMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC known-function String result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_known_function_bytes_and_bigint_results_memory_should_plateau()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcKnownFunctionBytesAndBigIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC known-function Bytes and BigInt results", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_bytes_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcEscapingBytesMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC directly escaping Bytes", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bytes_append_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBytesAppendMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC bytes append", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_append_byte_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcAppendByteMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC append byte", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bytes_from_list_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBytesFromListMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC bytes from list", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_byte_singleton_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcByteSingletonMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC byte singleton", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_empty_bytes_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcEmptyBytesMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC empty bytes", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_fixed_width_bytes_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcFixedWidthBytesMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC fixed-width bytes", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_byte_subtext_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcByteSubTextMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC byte subText", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_from_int_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextFromIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.fromInt", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_to_hex_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextToHexMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.toHex", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_ascii_case_text_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcAsciiCaseTextMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC ASCII case text", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_float_text_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcFloatTextMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC float text", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_from_int_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntFromIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt.fromInt", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_escaping_bigint_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcEscapingBigIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC directly escaping BigInt", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_parse_int_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextParseIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.parseInt Result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_parse_float_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextParseFloatMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.parseFloat Result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_to_int_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntToIntMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt.toInt Result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_parse_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntParseResultMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt parse Result", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_text_uncons_result_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcTextUnconsMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC Text.uncons Maybe", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_arithmetic_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntArithmeticMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt arithmetic", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_bigint_text_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcBigIntTextMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC BigInt text", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_copy_capture_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcCopyClosureMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC copy-capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_owned_heap_capture_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcOwnedHeapClosureMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC owned-heap-capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_owned_bytes_capture_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcOwnedBytesClosureMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC owned-Bytes-capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_owned_bigint_capture_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRuntimeRcOwnedBigIntClosureMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("runtime-RC owned-BigInt-capture closure", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_region_managed_task_frames_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureRegionManagedTaskFrameMemoryGrowthAsync()
            .ConfigureAwait(false);

        AssertMemoryPlateaus("region-managed task frames", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_runtime_rc_list_performance_stays_within_arena_baseline_budget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const int iterations = 500_000;
        IrProgram lowered = LowerProgram(BuildRuntimeRcListMemoryProgram(iterations));
        IrProgram runtimeRc = IrOptimizer.Optimize(lowered);
        IrProgram arenaBaseline = IrOptimizer.Optimize(ConvertRuntimeRcToArenaBaseline(lowered));

        AllInstructions(runtimeRc).Any(inst => inst is IrInst.Alloc { RuntimeManaged: true }).ShouldBeTrue();
        AllInstructions(arenaBaseline).Any(inst => inst is IrInst.Alloc { RuntimeManaged: false }).ShouldBeTrue();
        AllInstructions(arenaBaseline).Any(inst => inst is IrInst.RestoreArenaState).ShouldBeTrue();
        AllInstructions(arenaBaseline).Any(inst => inst is IrInst.RcDrop { RuntimeManaged: true }
            or IrInst.RcDup { RuntimeManaged: true }
            or IrInst.RcIsUnique).ShouldBeFalse();

        string expectedOutput = $"{iterations * 41L}\n";
        double runtimeRcMedianMs = await CompileAndMeasureMedianCpuTimeAsync(runtimeRc, expectedOutput).ConfigureAwait(false);
        double arenaMedianMs = await CompileAndMeasureMedianCpuTimeAsync(arenaBaseline, expectedOutput).ConfigureAwait(false);
        double allowedRuntimeRcMedianMs = Math.Max(arenaMedianMs * 8.0, arenaMedianMs + 100.0);

        runtimeRcMedianMs.ShouldBeLessThanOrEqualTo(allowedRuntimeRcMedianMs,
            $"runtime RC median CPU time was {runtimeRcMedianMs:F1} ms versus arena baseline " +
            $"{arenaMedianMs:F1} ms (relative budget {allowedRuntimeRcMedianMs:F1} ms)");
    }

    [Test]
    public async Task Linux_backend_llvm_legacy_arena_list_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaListMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("legacy arena list", samples);
    }

    [Test]
    public async Task Linux_backend_llvm_legacy_arena_string_and_record_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> strings = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaStringMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> records = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaRecordMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> growingStrings = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaGrowingStringMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("legacy arena string", strings);
        AssertMemoryPlateaus("legacy arena pointer record", records);
        AssertMemoryPlateaus("legacy arena growing string accumulator", growingStrings);
    }

    [Test]
    public async Task Linux_backend_llvm_legacy_arena_bytes_and_bigint_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> bytes = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaBytesMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> bigints = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaBigIntMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("legacy arena bytes", bytes);
        AssertMemoryPlateaus("legacy arena bigint", bigints);
    }

    [Test]
    public async Task Linux_backend_llvm_legacy_arena_closure_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram probe = LowerProgram(BuildLegacyArenaClosureMemoryProgram(2_000));
        probe.Functions.SelectMany(function => function.Instructions)
            .Any(inst => inst is IrInst.MakeClosure).ShouldBeTrue(
                "memory workload must exercise heap-backed closures");

        List<MemoryExecutionResult> closures = await MeasureMemoryGrowthAsync(
            BuildLegacyArenaClosureMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("legacy arena closure", closures);
    }

    [Test]
    public async Task Linux_backend_llvm_persistent_map_reuse_memory_should_plateau_as_updates_scale()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram mapProbe = LowerProgramWithImports(BuildPersistentMapStringUpdateMemoryProgram(1));
        AllInstructions(mapProbe).Any(instruction => instruction is IrInst.AllocAdtToSpace).ShouldBeTrue();
        AllInstructions(mapProbe).Any(instruction => instruction is IrInst.CopyStringIntoOrFresh).ShouldBeTrue();

        IrProgram hashMapProbe = LowerProgramWithImports(BuildPersistentHashMapUpdateMemoryProgram(1));
        AllInstructions(hashMapProbe).Any(instruction => instruction is IrInst.AllocAdtToSpace).ShouldBeTrue();

        List<MemoryExecutionResult> mapSamples = await MeasureImportedMemoryGrowthAsync(
            BuildPersistentMapStringUpdateMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);
        List<MemoryExecutionResult> hashMapSamples = await MeasureImportedMemoryGrowthAsync(
            BuildPersistentHashMapUpdateMemoryProgram,
            outputPerIteration: 1).ConfigureAwait(false);

        AssertMemoryPlateaus("persistent Map String-value update", mapSamples);
        AssertMemoryPlateaus("persistent HashMap fixed-key update", hashMapSamples);
    }

    [Test]
    public async Task Linux_backend_llvm_one_brc_memory_stays_bounded_as_rows_scale()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string source = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "challenges", "1brc", "brc.ash"));
        IrProgram ir = LowerProgramWithImports(source);
        AllInstructions(ir).Any(instruction => instruction is IrInst.ParallelQueueStart).ShouldBeTrue();
        AllInstructions(ir).Any(instruction => instruction is IrInst.AllocAdtToSpace).ShouldBeTrue();

        byte[] elfBytes = new LinuxX64LlvmBackend().Compile(
            ir,
            BackendCompileOptions.Default with { ParallelWorkerCap = 4 });
        string tmpDir = CreateTempDirectory();
        string exePath = Path.Combine(tmpDir, $"one_brc_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            int[] rowCounts = [75_000, 150_000, 300_000];
            List<MemoryExecutionResult> samples = new(rowCounts.Length);
            foreach (int rows in rowCounts)
            {
                string inputPath = Path.Combine(tmpDir, $"measurements_{rows}.txt");
                File.WriteAllText(inputPath, BuildOneBrcMeasurements(rows));
                MemoryExecutionResult sample = await RunLinuxExecutablePeakRssAsync(exePath, [inputPath])
                    .ConfigureAwait(false);
                sample.Stdout.ShouldContain("Alpha=1.0/1.0/1.0");
                sample.Stdout.ShouldContain("Beta=-2.0/-2.0/-2.0");
                sample.Stdout.ShouldContain("Gamma=3.5/3.5/3.5");
                samples.Add(sample);
            }

            AssertMemoryPlateaus("1BRC bounded-row profile", samples, maxRssKb: 128_000, growthBudgetKb: 8_192);
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    [Test]
    public async Task Linux_backend_llvm_parallel_worker_memory_should_plateau_as_work_scales()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        List<MemoryExecutionResult> samples = await MeasureMemoryGrowthAsync(
            BuildParallelWorkerMemoryProgram,
            outputPerIteration: 3).ConfigureAwait(false);

        AssertMemoryPlateaus("parallel worker shared list", samples);
    }

    [Test]
    public async Task Linux_backend_parallel_shared_values_stay_outside_non_atomic_runtime_rc()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        IrProgram program = LowerProgram(BuildParallelWorkerMemoryProgram(iterations: 1));
        AllInstructions(program).Any(instruction => instruction is IrInst.ParallelFork).ShouldBeTrue();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.Alloc { RuntimeManaged: true }).ShouldBeFalse();
        AllInstructions(program).Any(instruction =>
            instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeFalse();

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(program).ConfigureAwait(false);

        result.Stdout.ShouldBe("3\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_release_recursive_runtime_rc_adts()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let tree = Node(Node(Leaf)(20)(Leaf))(42)(Node(Leaf)(22)(Leaf)) in
                    match tree with
                        | Leaf -> loop(n - 1)(total)
                        | Node(_, value, _) -> loop(n - 1)(total + value)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("840000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_release_fresh_runtime_rc_copy_lists()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let values = [42, 2, 1]
            match values with
                | [] -> Ashes.IO.print(0)
                | head :: _ -> Ashes.IO.print(head)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_release_runtime_rc_copy_lists()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let values = [1, 2, 3] in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + head)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("20000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_share_runtime_rc_copy_list_tails()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let tail = [40, 2]
            let values = 1 :: tail
            match values with
                | [] -> Ashes.IO.print(0)
                | head :: _ ->
                    match tail with
                        | [] -> Ashes.IO.print(0)
                        | tailHead :: _ -> Ashes.IO.print(head + tailHead)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("41\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_share_runtime_rc_copy_list_tails()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let tail = (let fresh = [40, 2] in fresh) in
                    let values = 1 :: tail in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match tail with
                                | [] -> loop(n - 1)(total)
                                | tailHead :: _ -> loop(n - 1)(total + head + tailHead)

            Ashes.IO.print(loop(20000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("820000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_local_runtime_rc_record()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Point =
                | x: Int
                | y: Int

            let point = Point(x = 40, y = 2)
            Ashes.IO.print(point.x + point.y)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("42\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_repeatedly_release_local_runtime_rc_records()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Point =
                | x: Int
                | y: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let point = Point(x = 40, y = 2)
                    in loop(n - 1)(total + point.x + point.y)

            Ashes.IO.print(loop(2000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("84000\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_release_fresh_nested_runtime_rc_records()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let node = Node(child = Leaf(value = 40), bonus = 2)
            Ashes.IO.print(node.bonus)
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("2\n");
    }

    [Test]
    public async Task Linux_backend_llvm_should_release_nested_runtime_rc_records_at_tco_back_edges()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ExecutionResult result = await CompileRunWithLinuxLlvmAsync(LowerProgram("""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let node = Node(child = Leaf(value = 40), bonus = 2)
                    in loop(n - 1)(total + node.bonus)

            Ashes.IO.print(loop(2000)(0))
            """)).ConfigureAwait(false);

        result.Stdout.ShouldBe("4000\n");
    }

    [Test]
    public void Linux_backend_llvm_support_check_should_accept_panic_programs()
    {
        AssertLinuxLlvmCompiles(LowerExpression("Ashes.IO.panic(\"boom\")"));
    }

    [Test]
    public async Task Linux_backend_llvm_should_run_panic_programs()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var result = await CompileRunWithLinuxLlvmAsync("Ashes.IO.panic(\"boom\")", expectedExitCode: 1).ConfigureAwait(false);
        result.Stdout.ShouldBe("boom\n");
    }

    private static byte[] CompileForLinux(string source)
    {
        var ir = LowerExpression(source);
        return CompileForLinux(ir);
    }

    private static byte[] CompileForLinux(IrProgram ir)
    {
        return new LinuxX64LlvmBackend().Compile(ir);
    }

    private static void AssertLinuxLlvmCompiles(IrProgram ir)
    {
        var bytes = CompileForLinux(ir);
        bytes.Length.ShouldBeGreaterThan(256);
        bytes[0].ShouldBe((byte)0x7F);
        bytes[1].ShouldBe((byte)'E');
        bytes[2].ShouldBe((byte)'L');
        bytes[3].ShouldBe((byte)'F');
    }

    private static IrProgram LowerExpression(string source)
    {
        var diagnostics = new Diagnostics();
        var ast = new Parser(source, diagnostics).ParseExpression();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(ast);
        diagnostics.ThrowIfAny();
        return ir;
    }

    [Test]
    public async Task Linux_backend_llvm_should_serve_tls_echo_via_serve_tls()
    {
        // Server-side TLS: the Ashes program terminates TLS (Ashes.Net.Tls.Server.serveTls with a
        // self-signed cert), the C# test is an SslStream CLIENT that trusts the test cert via a
        // validation callback. Exercises the TLS server config build (certified key from PEMs),
        // the server half of the handshake (parking on WaitTlsWantRead/Write), and the shared TLS
        // send/receive/close paths on an accepted connection.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = $$"""
            import Ashes.IO
            import Ashes.Net.Tls
            import Ashes.Net.Tls.Server
            import Ashes.Task
            let onClient tls =
                async(match await Ashes.Net.Tls.receive(tls)(4096) with
                    | Error(e) -> Error(e)
                    | Ok(msg) ->
                        match await Ashes.Net.Tls.send(tls)("echo: " + msg) with
                            | Error(e2) -> Error(e2)
                            | Ok(_n) -> await Ashes.Net.Tls.close(tls))
            in match Ashes.Task.run(Ashes.Net.Tls.Server.serveTls({{port}})("cert.pem")("key.pem")(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_tls_srv_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            WriteSelfSignedServerPems(tmpDir);
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            foreach (var payload in new[] { "tls-one", "tls-two" })
            {
                var reply = await TlsConnectSendReceiveWithRetryAsync(port, payload).ConfigureAwait(false);
                reply.ShouldBe("echo: " + payload);
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    /// <summary>
    /// Writes a self-signed ECDSA P-256 localhost certificate + PKCS#8 key as cert.pem / key.pem.
    /// ECDSA keeps handshakes cheap under qemu emulation (see TlsLoopbackTestHost).
    /// </summary>
    private static void WriteSelfSignedServerPems(string directory)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(directory, "cert.pem"), certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(directory, "key.pem"), key.ExportPkcs8PrivateKeyPem());
    }

    private static async Task<string> TlsConnectSendReceiveWithRetryAsync(int port, string payload)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await using (tls.ConfigureAwait(false))
                {
                    await tls.AuthenticateAsClientAsync("localhost").WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var outBytes = Encoding.UTF8.GetBytes(payload);
                    await tls.WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    int read = await tls.ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    return Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_serve_http_over_the_tcp_server()
    {
        // HTTP layer coverage: Ashes.Net.Http.Server.serve parses the request line, routes on the path,
        // and writes an HTTP/1.1 response. The test drives it with raw HTTP GETs over loopback.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = HttpRoutingServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_http_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            await AssertHttpRoutingResponsesAsync(port).ConfigureAwait(false);
            await AssertHttpBufferingAndPipeliningAsync(port).ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string HttpRoutingServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        let route req =
            async(match Ashes.Net.Http.Server.path(req) with
                | "/health" -> Ashes.Net.Http.Server.text(200)("ok")
                | "/echo" -> Ashes.Net.Http.Server.text(200)("body=" + Ashes.Net.Http.Server.body(req))
                | "/ua" ->
                    match Ashes.Net.Http.Server.header(req)("user-agent") with
                        | Some(ua) -> Ashes.Net.Http.Server.text(200)(ua)
                        | None -> Ashes.Net.Http.Server.text(200)("no-ua")
                | "/data" -> Ashes.Net.Http.Server.json(200)("{\"ok\":true}")
                | _p -> Ashes.Net.Http.Server.text(404)("not found"))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(route)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    private static async Task AssertHttpRoutingResponsesAsync(int port)
    {
        var health = await HttpGetRawWithRetryAsync(port, "/health").ConfigureAwait(false);
        health.ShouldContain("HTTP/1.1 200 OK");
        health.ShouldContain("Content-Length: 2");
        health.ShouldEndWith("ok");

        var missing = await HttpGetRawWithRetryAsync(port, "/nope").ConfigureAwait(false);
        missing.ShouldContain("HTTP/1.1 404 Not Found");
        missing.ShouldEndWith("not found");

        // Request body is available to the handler.
        var echoed = await HttpRequestRawWithRetryAsync(port,
            "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: 9\r\nConnection: close\r\n\r\nhi-there!").ConfigureAwait(false);
        echoed.ShouldEndWith("body=hi-there!");

        // Request headers are read case-insensitively (handler asks "user-agent"; client sends "User-Agent").
        var ua = await HttpRequestRawWithRetryAsync(port,
            "GET /ua HTTP/1.1\r\nHost: localhost\r\nUser-Agent: probe/2.0\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
        ua.ShouldEndWith("probe/2.0");

        // json() sets an application/json Content-Type.
        var data = await HttpGetRawWithRetryAsync(port, "/data").ConfigureAwait(false);
        data.ShouldContain("Content-Type: application/json");
        data.ShouldEndWith("{\"ok\":true}");
    }

    private static async Task AssertHttpBufferingAndPipeliningAsync(int port)
    {
        // A body larger than one read is buffered across receives (cross-read buffering).
        var bigBody = new string('A', 100_000);
        var bigEcho = await HttpRequestRawWithRetryAsync(port,
            $"POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: {bigBody.Length}\r\nConnection: close\r\n\r\n{bigBody}").ConfigureAwait(false);
        bigEcho.ShouldEndWith("body=" + bigBody);

        // Keep-alive: two requests on a single TCP connection, second response still correct.
        var (first, second) = await HttpTwoRequestsOneConnectionAsync(port,
            "GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n",
            "GET /data HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
        first.ShouldContain("HTTP/1.1 200 OK");
        first.ShouldContain("Connection: keep-alive");
        first.ShouldEndWith("ok");
        second.ShouldContain("Content-Type: application/json");
        second.ShouldEndWith("{\"ok\":true}");

        // Pipelining across a split body: the tail of a POST body arrives in the same read as
        // the NEXT request. The incremental body path must hand the handler exactly
        // Content-Length bytes and carry the overshoot over as the next request's buffer.
        var combined = await HttpTwoSegmentsOneConnectionAsync(port,
            "POST /echo HTTP/1.1\r\nHost: localhost\r\nContent-Length: 10\r\n\r\nhello-",
            "tail" + "GET /data HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
        combined.ShouldContain("body=hello-tail");
        combined.ShouldContain("{\"ok\":true}");
    }

    [Test]
    public async Task Linux_backend_llvm_http_keep_alive_memory_should_plateau_as_requests_scale()
    {
        // Async-loop arena reset: the HTTP connection loop reclaims its per-request allocations
        // (buffered reads, parse scaffolding, the rendered response) at the loop back-edge. Serve a
        // ~16 KB body over ONE keep-alive connection many times and assert the server's resident
        // memory stays flat — without the reset every request leaks its garbage into the connection's
        // arena (~50 MB across this run).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = HttpKeepAliveMemoryServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_httpka_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var warm = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
            warm.ShouldContain("HTTP/1.1 200 OK");

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");

            // Sample after progressively larger request counts. The late phase starts only after
            // connection/parser/response scaffolding has settled, so retained per-request garbage
            // shows up as a proportional slope rather than being hidden in startup noise.
            await HttpKeepAliveBurstAsync(stream, request, 50).ConfigureAwait(false);
            long rssAt50Kb = ReadVmRssKb(proc.Id);
            await HttpKeepAliveBurstAsync(stream, request, 450).ConfigureAwait(false);
            long rssAt500Kb = ReadVmRssKb(proc.Id);
            await HttpKeepAliveBurstAsync(stream, request, 2500).ConfigureAwait(false);
            long rssAt3000Kb = ReadVmRssKb(proc.Id);

            proc.HasExited.ShouldBeFalse();
            rssAt50Kb.ShouldBeGreaterThan(0);
            rssAt500Kb.ShouldBeGreaterThan(0);
            rssAt3000Kb.ShouldBeGreaterThan(0);
            long totalGrowthKb = rssAt3000Kb - rssAt50Kb;
            long lateGrowthKb = rssAt3000Kb - rssAt500Kb;
            totalGrowthKb.ShouldBeLessThan(24_000,
                $"server RSS grew {totalGrowthKb} KB from 50 to 3000 requests " +
                $"({rssAt50Kb}, {rssAt500Kb}, {rssAt3000Kb} KB checkpoints)");
            lateGrowthKb.ShouldBeLessThan(16_000,
                $"server RSS grew {lateGrowthKb} KB from 500 to 3000 requests " +
                $"({rssAt50Kb}, {rssAt500Kb}, {rssAt3000Kb} KB checkpoints)");
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string HttpKeepAliveMemoryServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        import Ashes.Text
        let recursive repeat s n =
            if n == 0
            then s
            else repeat(s + s)(n - 1)
        let big = repeat("x")(14)
        let route req =
            async(Ashes.Net.Http.Server.text(200)(big))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serveParallel({{port}})(1)(route)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    // Sends `count` identical keep-alive requests on one connection, fully reading each response
    // (headers + the fixed 16384-byte body) before sending the next.
    private static async Task HttpKeepAliveBurstAsync(NetworkStream stream, byte[] request, int count)
    {
        var buffer = new byte[65536];
        for (int i = 0; i < count; i++)
        {
            await stream.WriteAsync(request).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
            int total = 0;
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                read.ShouldBeGreaterThan(0, "server closed a keep-alive connection mid-response");
                total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                int headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0 && total >= headerEnd + 4 + 16384)
                {
                    break;
                }
            }
        }
    }

    // Reads the process's resident set size (VmRSS, in KB) from /proc.
    private static long ReadVmRssKb(int pid)
    {
        foreach (var line in File.ReadLines($"/proc/{pid}/status"))
        {
            if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                return long.Parse(line[6..].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    // Sends two requests on one persistent connection, returning both responses (keep-alive).
    private static async Task<(string First, string Second)> HttpTwoRequestsOneConnectionAsync(int port, string firstRequest, string secondRequest)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(firstRequest)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var firstBuffer = new byte[4096];
                    int firstRead = await stream.ReadAsync(firstBuffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(secondRequest)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var second = await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    return (Encoding.UTF8.GetString(firstBuffer, 0, firstRead).Trim(), second.Trim());
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static Task<string> HttpGetRawWithRetryAsync(int port, string path)
        => HttpRequestRawWithRetryAsync(port, $"GET {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

    private static async Task<string> HttpRequestRawWithRetryAsync(int port, string request)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(request)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> HttpTwoSegmentsOneConnectionAsync(int port, string firstSegment, string secondSegment)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(firstSegment)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    // Let the server consume the first segment and park mid-body before the rest arrives.
                    await Task.Delay(150).ConfigureAwait(false);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(secondSegment)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    return (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_shut_down_gracefully_on_sigterm()
    {
        // Graceful shutdown: SIGTERM interrupts the parked accept, serve stops and returns Ok(()), so
        // the program prints its clean-stop message and exits 0 (rather than being terminated).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = SigtermShutdownServerSource(port);

        // Single reactor keeps the signal/exit deterministic (no worker/pdeathsig race in the test).
        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_shutdown_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Wait until it accepts, then send SIGTERM and assert a clean stop.
            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            await SendSigtermAsync(proc.Id).ConfigureAwait(false);

            await AssertExitsCleanlyAsync(proc, "stopped-clean").ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string SigtermShutdownServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(m) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-clean")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    [Test]
    public async Task Linux_backend_llvm_stop_capability_should_stop_the_server()
    {
        // Programmatic stop: a handler performs Stop.stop(Unit) after replying; the server stops
        // accepting, drains, and returns Ok(()) so the program prints its clean-stop message and
        // exits 0 — the capability's requirement threads out of the handler through serve (the
        // recursive-helper row fix), and Stop.stop rides the signal/drain path.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = StopCapabilityServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_stop_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // One client connects and sends; the handler replies, then requests stop.
            var reply = await ConnectSendReceiveWithRetryAsync(port, "now").ConfigureAwait(false);
            reply.ShouldBe("bye: now");

            await AssertExitsCleanlyAsync(proc, "stopped-by-request").ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string StopCapabilityServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    match await Ashes.Net.Tcp.send(client)("bye: " + msg) with
                        | Error(e2) -> Error(e2)
                        | Ok(_n) ->
                            let _s = Stop.stop(Unit)
                            in await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-by-request")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    [Test]
    public async Task Linux_backend_llvm_shutdown_should_drain_inflight_handlers()
    {
        // Drain-with-timeout: SIGTERM while a handler is mid-request stops accepting but lets the
        // in-flight handler finish (the client still gets its reply), then the server exits Ok(()).
        // Also covers the second-signal force: a SIGTERM during a never-ending drain exits at once.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = DrainShutdownServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_drain_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Open a connection and send; the handler sleeps 700 ms before replying.
            using var client = new TcpClient();
            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("drain-me")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
            // Give the server a moment to accept and spawn the handler, then SIGTERM mid-sleep.
            await Task.Delay(200).ConfigureAwait(false);
            await SendSigtermAsync(proc.Id).ConfigureAwait(false);

            // The in-flight handler must still complete and the reply must arrive.
            var buf = new byte[256];
            int n = await stream.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Encoding.UTF8.GetString(buf, 0, n).ShouldBe("done: drain-me");

            // And the server must then exit cleanly, well before the 10 s drain bound.
            await AssertExitsCleanlyAsync(proc, "stopped-clean").ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string DrainShutdownServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    match await Ashes.Task.sleep(700) with
                        | Error(e2) -> Error(e2)
                        | Ok(_t) ->
                            match await Ashes.Net.Tcp.send(client)("done: " + msg) with
                                | Error(e3) -> Error(e3)
                                | Ok(_n) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-clean")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    [Test]
    public async Task Linux_backend_llvm_shutdown_should_forward_to_workers_and_reap_them()
    {
        // Multi-reactor graceful shutdown: SIGTERM to the PARENT forwards to the forked workers and
        // the parent reaps them before exiting, so no worker is cut mid-drain by the death signal
        // and no orphan keeps the port open after the parent reports a clean stop.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = WorkerForwardShutdownServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_mwdrain_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Wait for a worker to accept, then SIGTERM the parent only.
            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            await SendSigtermAsync(proc.Id).ConfigureAwait(false);

            await AssertExitsCleanlyAsync(proc, "stopped-clean").ConfigureAwait(false);

            // All workers must be gone with the parent: the port must refuse new connections.
            await AssertPortRefusesConnectionsAsync(port).ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string WorkerForwardShutdownServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(m) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serveParallel({{port}})(3)(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-clean")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    private static async Task AssertPortRefusesConnectionsAsync(int port)
    {
        await Task.Delay(200).ConfigureAwait(false);
        var refused = false;
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            refused = true;
        }
        refused.ShouldBeTrue("workers should have drained and exited with the parent");
    }

    [Test]
    public async Task Linux_backend_llvm_shutdown_second_signal_should_force_exit()
    {
        // A second SIGTERM during the drain forces an immediate clean exit even though a handler
        // is still running (here: a handler that sleeps far past any reasonable test bound).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = ForceExitShutdownServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_force_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            using var client = new TcpClient();
            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("hang")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);

            // First signal starts the drain (60 s handler keeps it alive), second forces exit(0).
            await SendSigtermAsync(proc.Id).ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            await SendSigtermAsync(proc.Id).ConfigureAwait(false);

            var exited = await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);
            exited.ShouldBeTrue();
            proc.ExitCode.ShouldBe(0);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ForceExitShutdownServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    match await Ashes.Task.sleep(60000) with
                        | Error(e2) -> Error(e2)
                        | Ok(_t) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped-clean")
            | Error(e) -> Ashes.IO.print("err: " + e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_parse_query_and_reject_oversized()
    {
        // Query-string parsing + percent-decoding (path stripped of the query, %XX/+ decoded) and the
        // request size limit (a declared Content-Length over the cap returns 413 on the header).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = QueryParsingServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_query_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    var reply = await HttpRequestRawWithRetryAsync(port, "GET /users?name=Ada%20Lovelace HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                    reply.ShouldEndWith("p=/users n=Ada Lovelace");
                    break;
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            // A declared Content-Length above the 8 MiB cap is rejected with 413 on the header.
            var tooBig = await HttpRequestRawWithRetryAsync(port, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 99999999\r\nConnection: close\r\n\r\nabc").ConfigureAwait(false);
            tooBig.ShouldContain("413 Payload Too Large");
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string QueryParsingServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        let onReq req =
            async(match Ashes.Net.Http.Server.queryParam(req)("name") with
                | Some(v) -> Ashes.Net.Http.Server.text(200)("p=" + Ashes.Net.Http.Server.path(req) + " n=" + v)
                | None -> Ashes.Net.Http.Server.text(200)("p=" + Ashes.Net.Http.Server.path(req) + " none"))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(onReq)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_stream_a_chunked_response()
    {
        // Response streaming: the handler returns Ashes.Net.Http.Server.streamed with a pull `step`
        // producer (a function-typed field of the StreamStep ADT). The server frames the body with
        // Transfer-Encoding: chunked, one chunk per pulled StreamChunk, terminated by StreamDone.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = ChunkedResponseServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_stream_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var raw = await HttpGetRawWithRetryAsync(port, "/stream").ConfigureAwait(false);
            raw.ShouldContain("Transfer-Encoding: chunked");
            raw.ShouldNotContain("Content-Length");
            // Three 6-byte chunks then the terminating zero-length chunk.
            raw.ShouldContain("6\r\npart0-\r\n");
            raw.ShouldContain("6\r\npart1-\r\n");
            raw.ShouldContain("6\r\npart2-\r\n");
            // The last chunk is immediately followed by the zero-length terminating chunk
            // (the helper trims the final CRLFs).
            raw.ShouldEndWith("part2-\r\n0");
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ChunkedResponseServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Text
        import Ashes.Net.Http.Server
        import Ashes.Task
        let step acc =
            async(match Ashes.Text.parseInt(acc) with
                | Error(_e) -> StreamDone
                | Ok(i) ->
                    if i >= 3
                    then StreamDone
                    else StreamChunk("part" + Ashes.Text.fromInt(i) + "-")(Ashes.Text.fromInt(i + 1)))
        let route _req =
            async(Ashes.Net.Http.Server.streamed(200)("Content-Type: text/plain\r\n")("0")(step))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(route)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_decode_a_chunked_request_body()
    {
        // Transfer-Encoding: chunked request body — decoded and echoed. Also split across two writes so
        // the second read parks, exercising cross-read chunk buffering.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = ChunkedRequestServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_chunked_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        var head = Encoding.ASCII.GetBytes("POST /e HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n4\r\nWiki\r\n");
                        await stream.WriteAsync(head).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        await Task.Delay(200).ConfigureAwait(false);
                        var tail = Encoding.ASCII.GetBytes("5\r\npedia\r\n0\r\n\r\n");
                        await stream.WriteAsync(tail).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var reply = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                        reply.ShouldEndWith("body=Wikipedia");
                        break;
                    }
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ChunkedRequestServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        let onReq req = async(Ashes.Net.Http.Server.text(200)("body=" + Ashes.Net.Http.Server.body(req)))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(onReq)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_decode_chunked_frames_across_many_writes()
    {
        // Incremental chunked decoding: many chunk frames arriving in many separate writes, with
        // writes deliberately split MID-frame (inside a size line and inside chunk data), keep-alive
        // (not Connection: close), and the NEXT pipelined request arriving in the same write as the
        // terminating 0-frame. The decoder must carry only the undecoded tail between reads, hand
        // the handler the exact body, and serve the pipelined request from the remainder.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = ChunkedFramesServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source), BackendCompileOptions.Default with { ParallelWorkerCap = 1 });
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_chunkinc_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            await AssertChunkedFramesDecodedAcrossManyWritesAsync(port).ConfigureAwait(false);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ChunkedFramesServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Text
        import Ashes.Net.Http.Server
        import Ashes.Task
        let onReq req =
            async(match Ashes.Net.Http.Server.path(req) with
                | "/len" -> Ashes.Net.Http.Server.text(200)("len=" + Ashes.Text.fromInt(Ashes.Text.byteLength(Ashes.Net.Http.Server.body(req))))
                | _p -> Ashes.Net.Http.Server.text(200)("body=" + Ashes.Net.Http.Server.body(req)))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serve({{port}})(onReq)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    private static async Task AssertChunkedFramesDecodedAcrossManyWritesAsync(int port)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    // 30 frames of 100 bytes; the full wire text is cut into 37-byte writes so
                    // frame boundaries never align with write boundaries.
                    var piece = new string('x', 100);
                    var wire = new StringBuilder("POST /body HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n");
                    for (int i = 0; i < 30; i++)
                    {
                        wire.Append("64\r\n").Append(piece).Append("\r\n");
                    }
                    wire.Append("0\r\n\r\n");
                    // Pipelined second request in the same final write as the terminator.
                    wire.Append("GET /len HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    var wireBytes = Encoding.ASCII.GetBytes(wire.ToString());
                    for (int off = 0; off < wireBytes.Length; off += 37)
                    {
                        int n = Math.Min(37, wireBytes.Length - off);
                        await stream.WriteAsync(wireBytes.AsMemory(off, n)).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        await Task.Delay(2).ConfigureAwait(false);
                    }
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var replies = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                    replies.ShouldContain("body=" + string.Concat(Enumerable.Repeat(piece, 30)));
                    replies.ShouldEndWith("len=0");
                    break;
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    [Test]
    public async Task Linux_backend_llvm_should_serve_http_concurrently_across_workers()
    {
        // Concurrent HTTP under the run-queue scheduler: many simultaneous requests against a
        // multi-reactor server, every one must get a 200 and the server must stay up. Regression
        // guard for the async-loop lowering — with the server loops compiled as nested blocking
        // scheduler runs (instead of one suspending coroutine each), this crashed the reactor.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = ConcurrentHttpServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_httpc_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Readiness.
            var warm = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
            warm.ShouldContain("HTTP/1.1 200 OK");

            // Fire many concurrent requests at once; every one must get a 200 and the server must stay up.
            const int total = 120;
            var tasks = new List<Task<bool>>(total);
            for (int i = 0; i < total; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    var r = await HttpGetRawWithRetryAsync(port, "/").ConfigureAwait(false);
                    return r.Contains("HTTP/1.1 200 OK", StringComparison.Ordinal);
                }));
            }
            bool[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
            int ok = 0;
            foreach (bool r in results)
            {
                if (r)
                {
                    ok++;
                }
            }

            ok.ShouldBe(total);
            proc.HasExited.ShouldBeFalse();
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ConcurrentHttpServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Http.Server
        import Ashes.Task
        let route req =
            async(Ashes.Net.Http.Server.text(200)("ok"))
        in match Ashes.Task.run(Ashes.Net.Http.Server.serveParallel({{port}})(3)(route)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_serve_parallel_should_serve_across_workers()
    {
        // serveParallel forks an explicit number of independent reactor processes that each bind the
        // port with SO_REUSEPORT; the kernel load-balances connections across them. Assert it serves
        // correctly with several workers (the functional contract; worker fan-out is a perf property).
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = ServeParallelEchoServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_par_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            int served = 0;
            while (served < 6 && DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        var payload = Encoding.UTF8.GetBytes($"m{served}");
                        await stream.WriteAsync(payload).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var reply = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                        reply.ShouldBe($"echo:m{served}");
                        served++;
                    }
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }

            served.ShouldBe(6);
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ServeParallelEchoServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        let onConn client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    match await Ashes.Net.Tcp.send(client)("echo:" + msg) with
                        | Error(e2) -> Error(e2)
                        | Ok(_n) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serveParallel({{port}})(3)(onConn)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_read_across_a_parking_receive()
    {
        // Regression: a spawned handler that accumulates across a receive which PARKS on epoll used to
        // overflow the stack (ashes_detached_wait_meta counted the mid-step task as runnable, forcing a
        // non-blocking spin that leaked per-wait stack scratch). The client sends the request in two
        // writes with a gap so the second receive parks; the handler must buffer and reply.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = ParkingReceiveServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_park_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
            while (true)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var stream = client.GetStream();
                    await using (stream.ConfigureAwait(false))
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("hello")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        await Task.Delay(250).ConfigureAwait(false);
                        await stream.WriteAsync(Encoding.UTF8.GetBytes("-world")).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var reply = (await reader.ReadToEndAsync().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false)).Trim();
                        reply.ShouldBe("got:hello-world");
                        break;
                    }
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static string ParkingReceiveServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        import Ashes.Text
        let onClient client =
            async(let recursive loop buffered =
                if Ashes.Text.length(buffered) >= 11
                then
                    match await Ashes.Net.Tcp.send(client)("got:" + buffered) with
                        | Error(e) -> Error(e)
                        | Ok(_n) -> await Ashes.Net.Tcp.close(client)
                else
                    match await Ashes.Net.Tcp.receive(client)(65536) with
                        | Error(e2) -> Error(e2)
                        | Ok(chunk) -> loop(buffered + chunk)
            in loop(""))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_serve_connections_concurrently()
    {
        // serve() spawns each handler (Ashes.Task.spawn), so a slow handler must not serialize
        // other connections. Concurrency is asserted from the handlers' own monotonic sleep
        // windows rather than client wall-clock time: all four connections are opened and their
        // payloads sent up front, each handler reports [sleepStart, sleepEnd] around a 500 ms
        // sleep, and the windows must pairwise overlap. Serialized handlers can never produce
        // overlapping windows, while load on the test box merely delays everything uniformly —
        // an absolute wall-clock bound here was flaky under a parallel suite run.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = ConcurrentConnectionsServerSource(port);

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_conc_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            // Wait for the listener, then open the four measured connections.
            _ = await ConnectSendReceiveWithRetryAsync(port, "warmup").ConfigureAwait(false);

            var clients = new List<TcpClient>();
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    clients.Add(await ConnectAndSendAsync(port, $"conc-{i}").ConfigureAwait(false));
                }

                var replies = await Task.WhenAll(clients.Select(ReadWholeReplyAsync)).ConfigureAwait(false);
                var windows = replies.Select(ParseConcurrencyReply).ToList();

                windows.Max(w => w.SleepStart).ShouldBeLessThan(windows.Min(w => w.SleepEnd),
                    "handler sleep windows should overlap; serialized handlers can never overlap");
            }
            finally
            {
                foreach (var client in clients)
                {
                    client.Dispose();
                }
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    private static async Task<TcpClient> ConnectAndSendAsync(int port, string payload)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
        var outBytes = Encoding.UTF8.GetBytes(payload);
        await client.GetStream().WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
        return client;
    }

    private static async Task<string> ReadWholeReplyAsync(TcpClient client)
    {
        var buffer = new byte[4096];
        int read = await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.AcceptTimeout).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static (long SleepStart, long SleepEnd) ParseConcurrencyReply(string reply, int index)
    {
        var parts = reply.Split(';');
        parts.Length.ShouldBe(3, $"reply '{reply}' should be 'echo: <payload>;<sleepStart>;<sleepEnd>'");
        parts[0].ShouldBe($"echo: conc-{index}");
        return (long.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    private static string ConcurrentConnectionsServerSource(int port) => $$"""
        import Ashes.IO
        import Ashes.Net.Tcp
        import Ashes.Net.Tcp.Server
        import Ashes.Task
        import Ashes.IO.Console
        import Ashes.Text
        let onClient client =
            async(match await Ashes.Net.Tcp.receive(client)(4096) with
                | Error(e) -> Error(e)
                | Ok(msg) ->
                    let sleepStart = Ashes.IO.Console.monotonicMillis()
                    in
                        match await Ashes.Task.sleep(500) with
                            | Error(e2) -> Error(e2)
                            | Ok(_t) ->
                                let sleepEnd = Ashes.IO.Console.monotonicMillis()
                                in
                                    match await Ashes.Net.Tcp.send(client)("echo: " + msg + ";" + Ashes.Text.fromInt(sleepStart) + ";" + Ashes.Text.fromInt(sleepEnd)) with
                                        | Error(e3) -> Error(e3)
                                        | Ok(_n) -> await Ashes.Net.Tcp.close(client))
        in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
            | Ok(_u) -> Ashes.IO.print("stopped")
            | Error(e) -> Ashes.IO.print(e)
        """;

    [Test]
    public async Task Linux_backend_llvm_should_run_a_tcp_echo_server_via_serve()
    {
        // Server-side coverage on native linux-x64: the Ashes program is the LISTENER
        // (Ashes.Net.Tcp.Server.serve), the C# test is the CLIENT connecting in. Exercises the
        // socket/bind/listen/accept4 syscalls and the accept-park on WaitSocketRead.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int port = GetFreeLoopbackPort();
        var source = $$"""
            import Ashes.IO
            import Ashes.Net.Tcp
            import Ashes.Net.Tcp.Server
            import Ashes.Task
            let onClient client =
                async(match await Ashes.Net.Tcp.receive(client)(4096) with
                    | Error(e) -> Error(e)
                    | Ok(msg) ->
                        match await Ashes.Net.Tcp.send(client)("echo: " + msg) with
                            | Error(e2) -> Error(e2)
                            | Ok(_n) -> await Ashes.Net.Tcp.close(client))
            in match Ashes.Task.run(Ashes.Net.Tcp.Server.serve({{port}})(onClient)) with
                | Ok(_u) -> Ashes.IO.print("stopped")
                | Error(e) -> Ashes.IO.print(e)
            """;

        var elfBytes = new LinuxX64LlvmBackend().Compile(LowerProgramWithImports(source));
        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_srv_{Guid.NewGuid():N}");
        Process? proc = null;
        try
        {
            proc = await StartServerProcessAsync(exePath, tmpDir, elfBytes).ConfigureAwait(false);

            foreach (var payload in new[] { "linux-one", "linux-two", "linux-three" })
            {
                var reply = await ConnectSendReceiveWithRetryAsync(port, payload).ConfigureAwait(false);
                reply.ShouldBe("echo: " + payload);
            }
        }
        finally
        {
            CleanUpServerProcess(proc, exePath, tmpDir);
        }
    }

    // Writes the compiled ELF image to exePath and starts it with redirected stdio.
    private static async Task<Process> StartServerProcessAsync(string exePath, string tmpDir, byte[] elfBytes)
    {
        TestProcessHelper.WriteExecutable(exePath, elfBytes);
        return await TestProcessHelper.StartProcessAsync(new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = tmpDir,
        }).ConfigureAwait(false);
    }

    private static void CleanUpServerProcess(Process? proc, string exePath, string tmpDir)
    {
        if (proc is not null)
        {
            TryKillProcess(proc);
        }
        DeleteFileIfExists(exePath);
        DeleteDirectoryIfExists(tmpDir);
    }

    private static async Task SendSigtermAsync(int pid)
    {
        using (var kill = Process.Start(new ProcessStartInfo("kill", $"-TERM {pid}") { UseShellExecute = false })!)
        {
            await kill.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    // Waits for the server process to exit on its own, then asserts a clean stop message.
    private static async Task AssertExitsCleanlyAsync(Process proc, string expectedOutput)
    {
        var exited = await Task.Run(() => proc.WaitForExit(5000)).ConfigureAwait(false);
        exited.ShouldBeTrue();
        proc.ExitCode.ShouldBe(0);
        (await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).ShouldContain(expectedOutput);
    }

    private static IrProgram LowerProgramWithImports(string source)
    {
        var parsed = ProjectSupport.ParseImportHeader(source, "<memory>");
        var layout = ProjectSupport.BuildStandaloneCompilationLayout(parsed.SourceWithoutImports, parsed.ImportNames);
        var importedStdModules = parsed.ImportNames.Where(ProjectSupport.IsStdModule).ToHashSet(StringComparer.Ordinal);

        var diagnostics = new Diagnostics();
        var program = new Parser(layout.Source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics, importedStdModules, parsed.ImportAliases.Count == 0 ? null : parsed.ImportAliases).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static int GetFreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task<string> ConnectSendReceiveWithRetryAsync(int port, string payload)
    {
        var deadline = DateTime.UtcNow + SocketTestConstants.AcceptTimeout;
        while (true)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                var stream = client.GetStream();
                await using (stream.ConfigureAwait(false))
                {
                    var outBytes = Encoding.UTF8.GetBytes(payload);
                    await stream.WriteAsync(outBytes).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    var buffer = new byte[4096];
                    int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(SocketTestConstants.SocketTimeout).ConfigureAwait(false);
                    return Encoding.UTF8.GetString(buffer, 0, read);
                }
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static IrProgram LowerProgram(string source)
    {
        var diagnostics = new Diagnostics();
        var program = new Parser(source, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();

        var ir = new Lowering(diagnostics).Lower(program);
        diagnostics.ThrowIfAny();
        return ir;
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmAsync(
        string source,
        IReadOnlyList<string>? args = null,
        string? stdin = null,
        string? workingDirectory = null,
        int expectedExitCode = 0,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var ir = LowerExpression(source);
        return await CompileRunWithLinuxLlvmAsync(ir, args, stdin, workingDirectory, expectedExitCode, environmentVariables).ConfigureAwait(false);
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmAsync(
        IrProgram ir,
        IReadOnlyList<string>? args = null,
        string? stdin = null,
        string? workingDirectory = null,
        int expectedExitCode = 0,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var elfBytes = new LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = CreateTempDirectory();
        var exePath = Path.Combine(tmpDir, $"llvm_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);

            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            if (workingDirectory is not null)
            {
                psi.WorkingDirectory = workingDirectory;
            }
            if (environmentVariables is not null)
            {
                foreach (var entry in environmentVariables)
                {
                    psi.Environment[entry.Key] = entry.Value;
                }
            }
            if (args is not null)
            {
                foreach (var arg in args)
                {
                    psi.ArgumentList.Add(arg);
                }
            }

            using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
            if (stdin is not null)
            {
                await proc.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                proc.StandardInput.Close();
            }
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            proc.ExitCode.ShouldBe(expectedExitCode, $"stderr: {stderr}");
            return new ExecutionResult(stdout, stderr, proc.ExitCode);
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<List<MemoryExecutionResult>> MeasureMemoryGrowthAsync(
        Func<int, string> sourceFactory,
        int outputPerIteration)
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(sourceFactory(iterations));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * outputPerIteration}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureImportedMemoryGrowthAsync(
        Func<int, string> sourceFactory,
        int outputPerIteration)
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgramWithImports(sourceFactory(iterations));
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * outputPerIteration}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcStringConcatMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcStringConcatMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 5) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcEscapingStringMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcEscapingStringProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 4L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcKnownFunctionStringMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcKnownFunctionStringProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.ConcatStr { RuntimeManaged: true }).ShouldBeTrue();
            ir.EntryFunction.Instructions.Any(instruction => instruction is IrInst.CallClosure).ShouldBeTrue();
            ir.Functions.Any(function =>
                function.Instructions.Any(instruction =>
                    instruction is IrInst.RcDrop { TypeName: "String", RuntimeManaged: true })
                && function.Instructions.All(instruction => instruction is not IrInst.CopyOutArena)).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 4L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcKnownFunctionBytesAndBigIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcKnownFunctionBytesAndBigIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
            ir.Functions.Any(function =>
                function.Instructions.Any(instruction =>
                    instruction is IrInst.RcDrop { TypeName: "Bytes", RuntimeManaged: true })
                && function.Instructions.Any(instruction =>
                    instruction is IrInst.RcDrop { TypeName: "BigInt", RuntimeManaged: true })
                && function.Instructions.All(instruction => instruction is not IrInst.CopyOutArena)).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 133L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcEscapingBytesMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcEscapingBytesProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 19L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBytesAppendMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBytesAppendMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesAppend { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 4}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcAppendByteMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcAppendByteMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesAppendByte { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 3}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBytesFromListMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBytesFromListMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesFromList { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 3}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcByteSingletonMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcByteSingletonMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesSingleton { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcEmptyBytesMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcEmptyBytesMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesEmpty { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcFixedWidthBytesMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcFixedWidthBytesProgram(iterations));
            AllInstructions(ir).Any(instruction => instruction is IrInst.BytesU16Le { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.BytesU32Le { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.BytesU64Le { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 14}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcByteSubTextMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcByteSubTextMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BytesSubText { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 4) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextFromIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextFromIntMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextFromInt { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 4) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextToHexMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextToHexMemoryProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextToHex { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 7) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcAsciiCaseTextMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcAsciiCaseTextProgram(iterations));
            AllInstructions(ir).Count(instruction =>
                instruction is IrInst.TextAsciiCase { RuntimeManaged: true }).ShouldBe(2);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 12) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcFloatTextMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcFloatTextProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextFromFloat { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextFormatFloat { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.Length.ShouldBe((iterations * 13) + iterations.ToString(CultureInfo.InvariantCulture).Length + 1);
            sample.Stdout.ShouldEndWith($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntFromIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntFromIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe("0\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcEscapingBigIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcEscapingBigIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntFromInt { RuntimeManaged: true }).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.CopyOutArena).ShouldBeFalse();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextParseIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextParseIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextParseInt { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 123L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextParseFloatMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextParseFloatProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.TextParseFloat { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntToIntMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntToIntProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntToInt { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 123L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntParseResultMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntParseResultProgram(iterations));
            AllInstructions(ir).Count(instruction =>
                instruction is IrInst.BigIntFromString { RuntimeManaged: true }).ShouldBe(2);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcTextUnconsMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcTextUnconsProgram(iterations));
            AllInstructions(ir).Count(instruction =>
                instruction is IrInst.TextUncons { RuntimeManaged: true }).ShouldBe(2);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 6L}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntArithmeticMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntArithmeticProgram(iterations));
            AllInstructions(ir).Count(instruction =>
                instruction is IrInst.BigIntBinary { RuntimeManaged: true }).ShouldBe(5);
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe("0\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcBigIntTextMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcBigIntTextProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.BigIntToString { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations * 30}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcCopyClosureMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcCopyClosureProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            long expected = ((long)iterations * (iterations + 1) / 2) + iterations;
            sample.Stdout.ShouldBe($"{expected}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcOwnedHeapClosureMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcOwnedHeapClosureProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            long expected = iterations * 8L;
            sample.Stdout.ShouldBe($"{expected}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcOwnedBytesClosureMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcOwnedBytesClosureProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRuntimeRcOwnedBigIntClosureMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRuntimeRcOwnedBigIntClosureProgram(iterations));
            AllInstructions(ir).Any(instruction =>
                instruction is IrInst.MakeClosure { RuntimeManaged: true }).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            sample.Stdout.ShouldBe($"{iterations}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static async Task<List<MemoryExecutionResult>> MeasureRegionManagedTaskFrameMemoryGrowthAsync()
    {
        int[] iterationCounts = [2_000, 10_000, 50_000];
        List<MemoryExecutionResult> samples = new(iterationCounts.Length);
        foreach (int iterations in iterationCounts)
        {
            IrProgram ir = LowerProgram(BuildRegionManagedTaskFrameProgram(iterations));
            AllInstructions(ir).Any(instruction => instruction is IrInst.CreateTask).ShouldBeTrue();
            AllInstructions(ir).Any(instruction => instruction is IrInst.RestoreArenaState).ShouldBeTrue();
            MemoryExecutionResult sample = await CompileRunWithLinuxLlvmPeakRssAsync(ir).ConfigureAwait(false);
            long expected = (long)iterations * (iterations + 1) / 2;
            sample.Stdout.ShouldBe($"{expected}\n");
            samples.Add(sample);
        }

        return samples;
    }

    private static void AssertMemoryPlateaus(
        string workload,
        IReadOnlyList<MemoryExecutionResult> samples,
        long maxRssKb = 64_000,
        long growthBudgetKb = 8_192)
    {
        samples.Count.ShouldBe(3);
        string sampleSummary = string.Join(", ", samples.Select(sample => $"{sample.MaxRssKb} KB"));
        foreach (MemoryExecutionResult sample in samples)
        {
            sample.MaxRssKb.ShouldBeGreaterThan(0);
            sample.MaxRssKb.ShouldBeLessThan(maxRssKb,
                $"{workload} peaked at {sample.MaxRssKb} KB; samples: {sampleSummary}");
        }

        long totalGrowthKb = Math.Max(0, samples[2].MaxRssKb - samples[0].MaxRssKb);
        long lateGrowthKb = Math.Max(0, samples[2].MaxRssKb - samples[1].MaxRssKb);
        totalGrowthKb.ShouldBeLessThan(growthBudgetKb,
            $"{workload} RSS grew {totalGrowthKb} KB from first to last sample; samples: {sampleSummary}");
        lateGrowthKb.ShouldBeLessThan(growthBudgetKb,
            $"{workload} RSS grew {lateGrowthKb} KB from middle to last sample; samples: {sampleSummary}");
    }

    private static string BuildRuntimeRcListMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let tail = [40, 2] in
                    let values = 1 :: tail in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match tail with
                                | [] -> loop(n - 1)(total)
                                | tailHead :: _ -> loop(n - 1)(total + head + tailHead)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingListMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let tail = (let fresh = [40, 2] in fresh) in
                    let values = 1 :: tail in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match tail with
                                | [] -> loop(n - 1)(total)
                                | tailHead :: _ -> loop(n - 1)(total + head + tailHead)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTupleMemoryProgram(int iterations)
        => $$"""
            type Pair =
                | Pair(Int, Int)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let pair =
                        let fresh = ((40, 2), Ashes.Text.fromInt(40), Ashes.Byte.u16Le(258u16), Ashes.Number.BigInt.fromInt(42), [40, 2], Pair(40)(2)) in fresh
                    in
                    match pair with
                        | ((left, right), text, bytes, big, values, Pair(pairLeft, pairRight)) ->
                            match values with
                                | [] -> loop(n - 1)(total)
                                | head :: _ ->
                                    loop(n - 1)(total + left + right + Ashes.Text.byteLength(text) + Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1 + head + pairLeft + pairRight)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcOwnedElementListMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let tail = [Ashes.Text.fromInt(2)] in
                    let escaped = Ashes.Text.fromInt(40) :: tail in
                    match escaped with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match tail with
                                | [] -> loop(n - 1)(total)
                                | tailHead :: _ -> loop(n - 1)(total + Ashes.Text.byteLength(head) + Ashes.Text.byteLength(tailHead))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildParallelWorkerMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let shared = 20 :: 22 :: [] in
                    match Ashes.Task.Parallel.both(given (_u) -> 1 :: shared)(given (_u) -> 2 :: shared) with
                        | (left, right) ->
                            match left with
                                | [] -> loop(n - 1)(total)
                                | leftHead :: _ ->
                                    match right with
                                        | [] -> loop(n - 1)(total)
                                        | rightHead :: _ -> loop(n - 1)(total + leftHead + rightHead)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcAdtMemoryProgram(int iterations)
        => $$"""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            type Pair =
                | Pair(Int, Int)

            type Box(a) =
                | Box(a)

            type TextBox =
                | TextBox(Str)

            type Payload =
                | Payload(Bytes, BigInt, List(Int))

            type Wrapped =
                | Wrapped((Int, Int))

            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let pair = Pair(40)(2) in pair
                    in match escaped with
                        | Pair(left, right) ->
                            let textBox =
                                let fresh = TextBox(Ashes.Text.fromInt(40)) in fresh
                            in match textBox with
                                | TextBox(text) ->
                                    let payload =
                                        let fresh = Payload(Ashes.Byte.u16Le(258u16))(Ashes.Number.BigInt.fromInt(42))([40, 2]) in fresh
                                    in match payload with
                                        | Payload(bytes, big, values) ->
                                            match values with
                                                | [] -> loop(n - 1)(total)
                                                | head :: _ ->
                                                    let wrapped =
                                                        let fresh = Wrapped((40, 2)) in fresh
                                                    in match wrapped with
                                                        | Wrapped((wrappedLeft, wrappedRight)) ->
                                                            let boxed =
                                                                let fresh = Box(5) in fresh
                                                            in match boxed with
                                                                | Box(boxedValue) ->
                                                                    let tree =
                                                                        let fresh = Node(Node(Leaf)(20)(Leaf))(42)(Leaf) in fresh
                                                                    in
                                                                    match tree with
                                                                        | Leaf -> loop(n - 1)(total + left + right + Ashes.Text.byteLength(text) + Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1 + head + wrappedLeft + wrappedRight + boxedValue)
                                                                        | Node(child, value, _) ->
                                                                            match child with
                                                                                | Leaf -> loop(n - 1)(total + left + right + Ashes.Text.byteLength(text) + Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1 + head + wrappedLeft + wrappedRight + boxedValue + value)
                                                                                | Node(_, childValue, _) -> loop(n - 1)(total + left + right + Ashes.Text.byteLength(text) + Ashes.Byte.length(bytes) + Ashes.Number.BigInt.compare(big)(big) + 1 + head + wrappedLeft + wrappedRight + boxedValue + value + childValue)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcAdtReuseMemoryProgram(int iterations)
        => $$"""
            type Tree =
                | Leaf
                | Node(Tree, Int, Tree)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let tree = Node(Node(Leaf)(2)(Leaf))(1)(Leaf) in
                    match tree with
                        | Leaf ->
                            let rebuilt = Leaf in
                            loop(n - 1)(total)
                        | Node(left, value, _) ->
                            let rebuilt = Node(left)(value + 1)(Leaf) in
                            loop(n - 1)(total + value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcRecordOwnedChildMemoryProgram(int iterations)
        => $$"""
            type TextBox =
                | value: Str

            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let box = TextBox(value = Ashes.Text.fromInt(42)) in box
                    in loop(n - 1)(total + Ashes.Text.byteLength(escaped.value))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcVariantOwnedChildMemoryProgram(int iterations)
        => $$"""
            type Choice =
                | Empty
                | Text(Str)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let choice = Text(Ashes.Text.fromInt(42)) in choice
                    in match escaped with
                        | Empty -> loop(n - 1)(total)
                        | Text(value) -> loop(n - 1)(total + Ashes.Text.byteLength(value))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcGenericOwnedChildMemoryProgram(int iterations)
        => $$"""
            type Box(a) =
                | Box(a)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let textBox =
                        let box = Box(Ashes.Text.fromInt(42)) in box
                    in match textBox with
                        | Box(text) ->
                            let listBox =
                                let box = Box([40, 2]) in box
                            in match listBox with
                                | Box(values) ->
                                    match values with
                                        | [] -> loop(n - 1)(total)
                                        | head :: _ ->
                                            let tupleBox =
                                                let box = Box((40, 2)) in box
                                            in match tupleBox with
                                                | Box((left, right)) -> loop(n - 1)(total + Ashes.Text.byteLength(text) + head + left + right)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcHigherOrderResultMemoryProgram(int iterations)
        => $$"""
            let apply : (Int -> Str) -> Str = given f -> f(0)
            let make unit = let text = "ab" + "cd" in text
            let literal unit = "wxyz"

            let recursive loop n total =
                if n <= 0 then total
                else
                    let first = apply(make) in
                    let second = apply(literal) in
                    loop(n - 1)(total + Ashes.Text.byteLength(first) + Ashes.Text.byteLength(second))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcHigherOrderListResultMemoryProgram(int iterations)
        => $$"""
            let apply : (Int -> List(Int)) -> List(Int) = given f -> f(0)
            let source = [40, 2]
            let borrow unit = source

            let recursive loop n total =
                if n <= 0 then total
                else
                    let result = apply(borrow) in
                    match result with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + head)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcHigherOrderStringListResultMemoryProgram(int iterations)
        => $$"""
            let apply : (Int -> List(Str)) -> List(Str) = given f -> f(0)
            let source = ["forty", "two"]
            let borrow unit = source

            let recursive loop n total =
                if n <= 0 then total
                else
                    let result = apply(borrow) in
                    match result with
                        | [] -> loop(n - 1)(total)
                        | head :: _ -> loop(n - 1)(total + Ashes.Text.byteLength(head))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcHigherOrderNestedListResultMemoryProgram(int iterations)
        => $$"""
            let apply : (Int -> List(List(Int))) -> List(List(Int)) = given f -> f(0)
            let source = [[40, 2]]
            let borrow unit = source

            let recursive loop n total =
                if n <= 0 then total
                else
                    let result = apply(borrow) in
                    match result with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match head with
                                | [] -> loop(n - 1)(total)
                                | value :: _ -> loop(n - 1)(total + value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingClosureMemoryProgram(int iterations)
        => $$"""
            let make n =
                let text = "value-" + "x"
                in given (ignored) -> Ashes.Text.byteLength(text)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let measure = make(n)
                    in loop(n - 1)(total + measure(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingListCaptureClosureMemoryProgram(int iterations)
        => $$"""
            let make n =
                let values = [n, 2]
                in given (ignored) -> match values with
                    | [] -> 0
                    | _ :: _ -> 2

            let recursive loop n total =
                if n <= 0 then total
                else
                    let measure = make(n)
                    in loop(n - 1)(total + measure(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingAggregateCaptureClosureMemoryProgram(int iterations)
        => $$"""
            type Box =
                | Box(List(Int))

            let make n =
                let graph = (Box([n, 2]), n)
                in given (ignored) -> match graph with
                    | (Box(_), _) -> 2

            let recursive loop n total =
                if n <= 0 then total
                else
                    let measure = make(n)
                    in loop(n - 1)(total + measure(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcNestedRecordReuseMemoryProgram(int iterations)
        => $$"""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let node =
                        let fresh = Node(child = Leaf(value = 40), bonus = 2) in fresh
                    in
                    match node with
                        | Node(child, bonus) ->
                            let rebuilt = Node(child = child, bonus = bonus + 1) in
                            loop(n - 1)(total + bonus + 40)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcPointerVariantReuseMemoryProgram(int iterations)
        => $$"""
            type Leaf =
                | value: Int

            type Choice =
                | Empty
                | Full(Leaf, Int)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let choice = Full(Leaf(value = 40))(2) in
                    match choice with
                        | Empty ->
                            let rebuilt = Empty in
                            loop(n - 1)(total)
                        | Full(child, bonus) ->
                            let rebuilt = Full(child)(bonus + 1) in
                            loop(n - 1)(total + bonus + 40)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcSharedRecordChildMemoryProgram(int iterations)
        => $$"""
            type Leaf =
                | value: Int

            type Node =
                | child: Leaf
                | bonus: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let leaf = Leaf(value = 40) in
                    let node = Node(child = leaf, bonus = 2) in
                    loop(n - 1)(total + node.bonus + leaf.value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcStringConcatMemoryProgram(int iterations)
        => $$"""
            let emit unit =
                let text = "ab" + "cd" in
                Ashes.IO.print(text)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignored = emit(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcEscapingStringProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let text = "ab" + "cd" in text
                    in let length = Ashes.Text.byteLength(escaped)
                    in loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcKnownFunctionStringProgram(int iterations)
        => $$"""
            let make : Str -> Str -> Str = given (left) -> given (right) ->
                let ignored = Ashes.Text.byteLength(left) in
                let text = "ab" + "cd" in text

            let alias = make

            let recursive loop n total =
                if n <= 0 then total
                else
                    let value = alias("left")("right") in
                    let length = Ashes.Text.byteLength(value) in
                    loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcKnownFunctionBytesAndBigIntProgram(int iterations)
        => $$"""
            type Pair =
                | Pair(Int, Int)

            type Point =
                | x: Int
                | y: Int

            let makeBytes unit =
                let bytes = Ashes.Byte.u64Le(72623859790382856u64) in bytes

            let makeBigInt number =
                let big = Ashes.Number.BigInt.fromInt(number) in big

            let makeList unit =
                let values = [40, 2] in values

            let makePair unit =
                let pair = Pair(40)(2) in pair

            let makePoint unit =
                let point = Point(x = 40, y = 2) in point

            let recursive loop n total =
                if n <= 0 then total
                else
                    let bytes = makeBytes(0) in
                    let byteCount = Ashes.Byte.length(bytes) in
                    let big = makeBigInt(n) in
                    let comparison = Ashes.Number.BigInt.compare(big)(big) in
                    let values = makeList(0) in
                    let pair = makePair(0) in
                    let point = makePoint(0) in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            match pair with
                                | Pair(left, right) ->
                                    loop(n - 1)(total + byteCount + comparison + 1 + head + left + right + point.x + point.y)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingBytesProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let one =
                        let escaped =
                            let bytes = Ashes.Byte.singleton(7u8) in bytes
                        in Ashes.Byte.length(escaped)
                    in let eight =
                        let escaped =
                            let bytes = Ashes.Byte.u64Le(72623859790382856u64) in bytes
                        in Ashes.Byte.length(escaped)
                    in let four =
                        let escaped =
                            let bytes = Ashes.Byte.append(Ashes.Byte.fromText("ab"))(Ashes.Byte.fromText("cd")) in bytes
                        in Ashes.Byte.length(escaped)
                    in let three =
                        let escaped =
                            let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromText("ab"))(33u8) in bytes
                        in Ashes.Byte.length(escaped)
                    in let threeFromList =
                        let escaped =
                            let bytes = Ashes.Byte.fromList([7u8, 8u8, 9u8]) in bytes
                        in Ashes.Byte.length(escaped)
                    in loop(n - 1)(total + one + eight + four + three + threeFromList)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBytesAppendMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.append(Ashes.Byte.fromText("ab"))(Ashes.Byte.fromText("cd")) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcAppendByteMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.appendByte(Ashes.Byte.fromText("ab"))(33u8) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBytesFromListMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.fromList([7u8, 8u8, 9u8]) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcByteSingletonMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.singleton(7u8) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEmptyBytesMemoryProgram(int iterations)
        => $$"""
            let measure unit =
                let bytes = Ashes.Byte.empty(Unit) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else
                    let size = measure(0) in
                    loop(n - 1)(total + size + 1)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcFixedWidthBytesProgram(int iterations)
        => $$"""
            let measure16 unit =
                let bytes = Ashes.Byte.u16Le(258u16) in
                Ashes.Byte.length(bytes)

            let measure32 unit =
                let bytes = Ashes.Byte.u32Le(16909060u32) in
                Ashes.Byte.length(bytes)

            let measure64 unit =
                let bytes = Ashes.Byte.u64Le(72623859790382856u64) in
                Ashes.Byte.length(bytes)

            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + measure16(0) + measure32(0) + measure64(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcByteSubTextMemoryProgram(int iterations)
        => $$"""
            let emit unit =
                let escaped =
                    let text = Ashes.Byte.subText(Ashes.Byte.fromText("abcdef"))(1)(3) in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignored = emit(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcTextFromIntMemoryProgram(int iterations)
        => $$"""
            let emit unit =
                let escaped =
                    let text = Ashes.Text.fromInt(-42) in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignored = emit(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcTextToHexMemoryProgram(int iterations)
        => $$"""
            let emit unit =
                let escaped =
                    let text = Ashes.Text.toHex(48879) in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignored = emit(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcAsciiCaseTextProgram(int iterations)
        => $$"""
            let emitUpper unit =
                let escaped =
                    let text = Ashes.Text.asciiUpper("hello") in text
                in Ashes.IO.print(escaped)

            let emitLower unit =
                let escaped =
                    let text = Ashes.Text.asciiLower("HELLO") in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignoredUpper = emitUpper(0) in
                    let ignoredLower = emitLower(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcFloatTextProgram(int iterations)
        => $$"""
            let emitFloat unit =
                let escaped =
                    let text = Ashes.Text.fromFloat(12.25) in text
                in Ashes.IO.print(escaped)

            let emitFixed unit =
                let escaped =
                    let text = Ashes.Text.formatFloat(12.25)(3) in text
                in Ashes.IO.print(escaped)

            let recursive loop n =
                if n <= 0 then 0
                else
                    let ignoredFloat = emitFloat(0) in
                    let ignoredFixed = emitFixed(0) in
                    loop(n - 1)

            let ignored = loop({{iterations}})
            Ashes.IO.print({{iterations}})
            """;

    private static string BuildRuntimeRcBigIntFromIntProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let comparison =
                        let value = Ashes.Number.BigInt.fromInt(n) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in loop(n - 1)(total + comparison)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcEscapingBigIntProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let escaped =
                        let value = Ashes.Number.BigInt.fromInt(n) in value
                    in let comparison = Ashes.Number.BigInt.compare(escaped)(escaped)
                    in loop(n - 1)(total + comparison + 1)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBigIntArithmeticProgram(int iterations)
        => $$"""
            let left = 123456789012345678901234567890N
            let right = 987654321N

            let recursive loop n total =
                if n <= 0 then total
                else
                    let addComparison =
                        let escaped =
                            let value = Ashes.Number.BigInt.add(left)(right) in value
                        in Ashes.Number.BigInt.compare(escaped)(escaped)
                    in let subComparison =
                        let value = Ashes.Number.BigInt.sub(left)(right) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in let mulComparison =
                        let value = Ashes.Number.BigInt.mul(left)(right) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in let divComparison =
                        let value = Ashes.Number.BigInt.div(left)(right) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in let modComparison =
                        let value = Ashes.Number.BigInt.mod(left)(right) in
                        Ashes.Number.BigInt.compare(value)(value)
                    in loop(n - 1)(total + addComparison + subComparison + mulComparison + divComparison + modComparison)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBigIntTextProgram(int iterations)
        => $$"""
            let value = 123456789012345678901234567890N

            let recursive loop n total =
                if n <= 0 then total
                else
                    let length =
                        let escaped =
                            let text = Ashes.Text.fromBigInt(value) in text
                        in Ashes.Text.byteLength(escaped)
                    in loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTextParseIntProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let value =
                        let escaped =
                            let parsed = Ashes.Text.parseInt("123") in parsed
                        in match escaped with
                            | Ok(number) -> number
                            | Error(_message) -> 0
                    in loop(n - 1)(total + value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTextParseFloatProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let value =
                        let escaped =
                            let parsed = Ashes.Text.parseFloat("1.5") in parsed
                        in match escaped with
                            | Ok(number) -> if number == 1.5 then 1 else 0
                            | Error(_message) -> 0
                    in loop(n - 1)(total + value)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBigIntToIntProgram(int iterations)
        => $$"""
            let value = 123N

            let recursive loop n total =
                if n <= 0 then total
                else
                    let converted =
                        let escaped =
                            let result = Ashes.Number.BigInt.toInt(value) in result
                        in match escaped with
                            | Ok(number) -> number
                            | Error(_message) -> 0
                    in loop(n - 1)(total + converted)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcBigIntParseResultProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let valid =
                        let escaped =
                            let result = Ashes.Text.parseBigInt("123") in result
                        in match escaped with
                            | Ok(value) -> Ashes.Number.BigInt.compare(value)(value)
                            | Error(_message) -> 1
                    in let invalid =
                        let escaped =
                            let result = Ashes.Text.parseBigInt("bad") in result
                        in match escaped with
                            | Ok(value) -> Ashes.Number.BigInt.compare(value)(value)
                            | Error(_message) -> 1
                    in loop(n - 1)(total + valid + invalid)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcTextUnconsProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let nonEmpty =
                        let split = (let escaped = Ashes.Text.uncons("abcdef") in escaped) in
                        match split with
                            | None -> 0
                            | Some((head, tail)) -> Ashes.Text.byteLength(head) + Ashes.Text.byteLength(tail)
                    in let empty =
                        let split = (let escaped = Ashes.Text.uncons("") in escaped) in
                        match split with
                            | None -> 0
                            | Some((head, tail)) -> Ashes.Text.byteLength(head) + Ashes.Text.byteLength(tail)
                    in loop(n - 1)(total + nonEmpty + empty)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcCopyClosureProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let result =
                        let f =
                            if n > 0
                            then given (x) -> x + n
                            else given (x) -> x + n
                        in f(1)
                    in loop(n - 1)(total + result)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcOwnedHeapClosureProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let length =
                        let text = "value-" + Ashes.Text.fromInt(42) in
                        let f =
                            if n > 0
                            then given (unit) -> Ashes.Text.byteLength(text)
                            else given (unit) -> Ashes.Text.byteLength(text)
                        in f(0)
                    in loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcOwnedHeapClosureScratchProgram()
        => """
            let n = 1 in
            let text = "value-" + Ashes.Text.fromInt(n) in
            let f =
                if n > 0
                then given (unit) -> Ashes.Text.byteLength(text)
                else given (unit) -> Ashes.Text.byteLength(text)
            in f(0)
            """;

    private static string BuildRuntimeRcOwnedBytesClosureProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let length =
                        let bytes = Ashes.Byte.singleton(7u8) in
                        let f =
                            if n > 0
                            then given (unit) -> Ashes.Byte.length(bytes)
                            else given (unit) -> Ashes.Byte.length(bytes)
                        in f(0)
                    in loop(n - 1)(total + length)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRejectedRcOwnedBytesClosureScratchProgram()
        => """
            let bytes = Ashes.Byte.append(Ashes.Byte.singleton(1u8))(Ashes.Byte.singleton(2u8)) in
            let f =
                if Ashes.Byte.length(bytes) > 0
                then given (unit) -> Ashes.Byte.length(bytes)
                else given (unit) -> Ashes.Byte.length(bytes)
            in f(0)
            """;

    private static string BuildRuntimeRcOwnedBigIntClosureProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let comparison =
                        let value = Ashes.Number.BigInt.fromInt(n) in
                        let f =
                            if n > 0
                            then given (unit) -> Ashes.Number.BigInt.compare(value)(value)
                            else given (unit) -> Ashes.Number.BigInt.compare(value)(value)
                        in f(0)
                    in loop(n - 1)(total + comparison + 1)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildRuntimeRcOwnedBigIntClosureScratchProgram()
        => """
            let value = Ashes.Number.BigInt.add(1N)(2N) in
            let f =
                if Ashes.Number.BigInt.compare(value)(value) == 0
                then given (unit) -> Ashes.Number.BigInt.compare(value)(value)
                else given (unit) -> Ashes.Number.BigInt.compare(value)(value)
            in f(0)
            """;

    private static string BuildRegionManagedTaskFrameProgram(int iterations)
        => $$"""
            let runOne n =
                match Ashes.Task.run(async(match await async n with
                    | Ok(value) -> value
                    | Error(_) -> 0)) with
                    | Ok(value) -> value
                    | Error(_) -> 0

            let recursive loop n total =
                if n <= 0 then total
                else loop(n - 1)(total + runOne(n))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaListMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let values = ["a", "b", "c"] in
                    match values with
                        | [] -> loop(n - 1)(total)
                        | head :: _ ->
                            if head == "a"
                            then loop(n - 1)(total + 1)
                            else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaStringMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let text = "value-" + Ashes.Text.fromInt(n) in
                    if Ashes.Text.byteLength(text) > 0
                    then loop(n - 1)(total + 1)
                    else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaRecordMemoryProgram(int iterations)
        => $$"""
            type Box =
                | text: String
                | value: Int

            let recursive loop n total =
                if n <= 0 then total
                else
                    let box = Box(text = "value-" + Ashes.Text.fromInt(n), value = 1) in
                    if Ashes.Text.byteLength(box.text) > 0
                    then loop(n - 1)(total + box.value)
                    else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaGrowingStringMemoryProgram(int iterations)
        => $$"""
            let recursive loop n text =
                if n <= 0 then Ashes.Text.byteLength(text)
                else loop(n - 1)(text + "x")

            Ashes.IO.print(loop({{iterations}})(""))
            """;

    private static string BuildLegacyArenaBytesMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let left = Ashes.Byte.fromText("value-") in
                    let right = Ashes.Byte.fromText(Ashes.Text.fromInt(n)) in
                    let bytes = Ashes.Byte.appendByte(Ashes.Byte.append(left)(right))(33u8) in
                    if Ashes.Byte.length(bytes) > 0
                    then loop(n - 1)(total + 1)
                    else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaBigIntMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let value = 123456789012345678901234567890N * Ashes.Number.BigInt.fromInt(n) in
                    if value > 0N
                    then loop(n - 1)(total + 1)
                    else loop(n - 1)(total)

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildLegacyArenaClosureMemoryProgram(int iterations)
        => $$"""
            let recursive loop n total =
                if n <= 0 then total
                else
                    let text = "value-" + Ashes.Text.fromInt(n) in
                    let f =
                        if n > 0
                        then given (x) -> if Ashes.Text.byteLength(text) > 0 then x + 1 else x
                        else given (x) -> x
                    in loop(n - 1)(total + f(0))

            Ashes.IO.print(loop({{iterations}})(0))
            """;

    private static string BuildPersistentMapStringUpdateMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.Map

            let compareInt left right =
                if left == right then 0
                else if left < right then -1
                else 1

            let recursive loop i limit map =
                if i > limit then map
                else loop(i + 1)(limit)(Ashes.Collection.Map.set(compareInt)(0)(Ashes.Text.fromInt(i))(map))

            let seeded = Ashes.Collection.Map.set(compareInt)(0)("seed")(Ashes.Collection.Map.empty)
            let final = loop(1)({{iterations}})(seeded)
            in match Ashes.Collection.Map.get(compareInt)(0)(final) with
                | None -> Ashes.IO.print(0)
                | Some(value) -> Ashes.IO.print(value)
            """;

    private static string BuildPersistentHashMapUpdateMemoryProgram(int iterations)
        => $$"""
            import Ashes.Collection.HashMap

            let recursive loop i limit map =
                if i > limit then map
                else loop(i + 1)(limit)(Ashes.Collection.HashMap.set("fixed")(i)(map))

            let final = loop(1)({{iterations}})(Ashes.Collection.HashMap.empty)
            in match Ashes.Collection.HashMap.get("fixed")(final) with
                | None -> Ashes.IO.print(0)
                | Some(value) -> Ashes.IO.print(value)
            """;

    private static string BuildOneBrcMeasurements(int rows)
    {
        StringBuilder source = new(rows * 11);
        for (int row = 0; row < rows; row += 3)
        {
            source.Append("Alpha;1.0\nBeta;-2.0\nGamma;3.5\n");
        }

        return source.ToString();
    }

    private static string GetRepositoryRoot([CallerFilePath] string callerFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerFile)!, "..", ".."));

    private static async Task<MemoryExecutionResult> CompileRunWithLinuxLlvmPeakRssAsync(IrProgram ir)
    {
        byte[] elfBytes = new LinuxX64LlvmBackend().Compile(ir);
        string tmpDir = CreateTempDirectory();
        string exePath = Path.Combine(tmpDir, $"llvm_rss_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            return await RunLinuxExecutablePeakRssAsync(exePath).ConfigureAwait(false);
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<MemoryExecutionResult> RunLinuxExecutablePeakRssAsync(
        string exePath,
        IReadOnlyList<string>? arguments = null)
    {
        ProcessStartInfo startInfo = new(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (arguments is not null)
        {
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        LinuxMeasuredExecution result = await RunLinuxMeasuredProcessAsync(startInfo).ConfigureAwait(false);
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
        result.MaxRssKb.ShouldBeGreaterThan(0, "Linux resource wrapper did not report peak RSS");
        return new MemoryExecutionResult(result.Stdout, result.MaxRssKb);
    }

    private static async Task<LinuxMeasuredExecution> RunLinuxMeasuredProcessAsync(ProcessStartInfo startInfo)
    {
        const string usageMarker = "__ASHES_LINUX_USAGE__=";
        const string measureScript = """
            import errno, resource, subprocess, sys, time
            for attempt in range(3):
                try:
                    completed = subprocess.run(sys.argv[1:])
                    break
                except OSError as error:
                    if error.errno != errno.ETXTBSY or attempt == 2:
                        raise
                    time.sleep(0.025)
            usage = resource.getrusage(resource.RUSAGE_CHILDREN)
            print(f"__ASHES_LINUX_USAGE__={usage.ru_maxrss}|{usage.ru_utime + usage.ru_stime}", file=sys.stderr)
            sys.exit(completed.returncode)
            """;
        ProcessStartInfo measuredStartInfo = new("python3")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        measuredStartInfo.ArgumentList.Add("-c");
        measuredStartInfo.ArgumentList.Add(measureScript);
        measuredStartInfo.ArgumentList.Add(startInfo.FileName);
        foreach (string argument in startInfo.ArgumentList)
        {
            measuredStartInfo.ArgumentList.Add(argument);
        }

        using Process process = await TestProcessHelper.StartProcessAsync(measuredStartInfo).ConfigureAwait(false);
        string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        foreach (string line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(usageMarker, StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = line[usageMarker.Length..].Split('|');
            fields.Length.ShouldBe(2);
            long maxRssKb = long.Parse(fields[0], CultureInfo.InvariantCulture);
            double cpuMilliseconds = double.Parse(fields[1], CultureInfo.InvariantCulture) * 1_000.0;
            return new LinuxMeasuredExecution(stdout, stderr, process.ExitCode, maxRssKb, cpuMilliseconds);
        }

        throw new InvalidOperationException($"Python resource wrapper did not report child usage. stderr: {stderr}");
    }

    private static IrProgram ConvertRuntimeRcToArenaBaseline(IrProgram program)
    {
        return program with
        {
            EntryFunction = ConvertFunction(program.EntryFunction),
            Functions = program.Functions.Select(ConvertFunction).ToList()
        };

        static IrFunction ConvertFunction(IrFunction function)
        {
            List<IrInst> instructions = function.Instructions.Select(ConvertInstruction).ToList();
            return function with { Instructions = instructions };
        }

        static IrInst ConvertInstruction(IrInst instruction)
            => instruction switch
            {
                IrInst.Alloc allocation when allocation.RuntimeManaged => allocation with { RuntimeManaged = false },
                IrInst.AllocAdt allocation when allocation.RuntimeManaged => allocation with { RuntimeManaged = false },
                IrInst.RcDup duplicate when duplicate.RuntimeManaged => duplicate with { RuntimeManaged = false },
                IrInst.RcDrop drop when drop.RuntimeManaged => drop with { RuntimeManaged = false },
                IrInst.RcIsUnique unique => new IrInst.LoadConstBool(unique.Target, true) { Location = unique.Location },
                _ => instruction
            };
    }

    private static IEnumerable<IrInst> AllInstructions(IrProgram program)
        => program.Functions.Prepend(program.EntryFunction).SelectMany(function => function.Instructions);

    private static async Task<double> CompileAndMeasureMedianCpuTimeAsync(IrProgram program, string expectedOutput)
    {
        byte[] elfBytes = new LinuxX64LlvmBackend().Compile(program);
        string tmpDir = CreateTempDirectory();
        string exePath = Path.Combine(tmpDir, $"llvm_perf_{Guid.NewGuid():N}");
        try
        {
            TestProcessHelper.WriteExecutable(exePath, elfBytes);
            _ = await RunAndMeasureCpuTimeAsync(exePath, expectedOutput).ConfigureAwait(false);

            const int sampleCount = 3;
            double[] samples = new double[sampleCount];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = await RunAndMeasureCpuTimeAsync(exePath, expectedOutput).ConfigureAwait(false);
            }

            Array.Sort(samples);
            return samples[samples.Length / 2];
        }
        finally
        {
            DeleteFileIfExists(exePath);
            DeleteDirectoryIfExists(tmpDir);
        }
    }

    private static async Task<double> RunAndMeasureCpuTimeAsync(string exePath, string expectedOutput)
    {
        ProcessStartInfo startInfo = new(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        LinuxMeasuredExecution result = await RunLinuxMeasuredProcessAsync(startInfo).ConfigureAwait(false);
        result.ExitCode.ShouldBe(0, $"stderr: {result.Stderr}");
        result.Stdout.ShouldBe(expectedOutput);
        return result.CpuMilliseconds;
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmLoopbackAsync(string sourceTemplate, Func<TcpClient, Task> handleClientAsync, string host = "127.0.0.1")
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var serverTask = RunLoopbackServerAsync(listener, handleClientAsync);
        var result = await CompileRunWithLinuxLlvmAsync(source).ConfigureAwait(false);
        var serverException = await serverTask.ConfigureAwait(false);
        serverException.ShouldBeNull(serverException?.ToString());
        return result;
    }

    private static async Task<ExecutionResult> CompileRunWithLinuxLlvmTlsLoopbackAsync(
        string sourceTemplate,
        Func<SslStream, Task> handleClientAsync,
        string host = "localhost",
        string? certificateHost = null,
        bool trustServerCertificate = true,
        int expectedClientCount = 1,
        bool allowServerHandshakeFailure = false,
        bool tolerateClientDisconnect = false)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var tlsHost = await TlsLoopbackTestHost.CreateAsync(certificateHost ?? host).ConfigureAwait(false);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var source = sourceTemplate.Replace("__HOST__", host, StringComparison.Ordinal).Replace("__PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var serverTask = TlsLoopbackTestHost.RunServerAsync(listener, expectedClientCount, tlsHost.ServerCertificate, handleClientAsync, tolerateClientDisconnect);
        IReadOnlyDictionary<string, string>? environmentVariables = trustServerCertificate
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SSL_CERT_FILE"] = tlsHost.TrustCertificatePath
            }
            : null;
        var result = await CompileRunWithLinuxLlvmAsync(source, environmentVariables: environmentVariables).ConfigureAwait(false);
        var serverException = await serverTask.ConfigureAwait(false);
        if (allowServerHandshakeFailure && serverException is not null)
        {
            // The client rejects the certificate mid-handshake: Mbed TLS sends a fatal TLS alert,
            // which the .NET peer surfaces as an AuthenticationException (an abrupt close would
            // surface as an IOException instead).
            (serverException is AuthenticationException or IOException)
                .ShouldBeTrue(serverException.ToString());
        }
        else
        {
            serverException.ShouldBeNull(serverException?.ToString());
        }
        return result;
    }

    private static async Task<Exception?> RunLoopbackServerAsync(TcpListener listener, Func<TcpClient, Task> handleClientAsync)
    {
        try
        {
            using var acceptCts = new CancellationTokenSource(SocketTestConstants.AcceptTimeout);
            using var client = await listener.AcceptTcpClientAsync(acceptCts.Token).ConfigureAwait(false);
            client.ReceiveTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
            client.SendTimeout = (int)SocketTestConstants.SocketTimeout.TotalMilliseconds;
            await handleClientAsync(client).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> ReadTextAsync(Stream stream, int maxBytes)
    {
        var buffer = new byte[maxBytes];
        var total = 0;
        byte[] headerTerminator = "\r\n\r\n"u8.ToArray();

        while (total < buffer.Length)
        {
            try
            {
                using var readCts = new CancellationTokenSource(SocketTestConstants.ReadChunkTimeout);
                var count = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), readCts.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                total += count;
                if (total >= headerTerminator.Length && buffer.AsSpan(0, total).IndexOf(headerTerminator) >= 0)
                {
                    break;
                }

                if (stream is NetworkStream networkStream && !networkStream.DataAvailable)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (total > 0)
            {
                break;
            }
            catch (IOException) when (total > 0)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    private static string CreateTempDirectory()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        return tmpDir;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        const int maxAttempts = 5;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private readonly record struct ExecutionResult(string Stdout, string Stderr, int ExitCode);
    private readonly record struct LinuxMeasuredExecution(
        string Stdout,
        string Stderr,
        int ExitCode,
        long MaxRssKb,
        double CpuMilliseconds);
    private readonly record struct MemoryExecutionResult(string Stdout, long MaxRssKb);
}
