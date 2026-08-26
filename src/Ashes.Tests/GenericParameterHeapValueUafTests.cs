using System.Diagnostics;
using Ashes.Backend.Backends;
using Ashes.Frontend;
using Ashes.Semantics;
using Shouldly;

namespace Ashes.Tests;

// A non-inlined generic function that stores a type-variable-typed parameter (a user
// `setTree key value tree`, or `HashMap.set`) into an outgoing constructor field used to keep a
// dangling arena pointer once enough unrelated allocation happened afterward: the callee is
// compiled once, generically, with no static layout to normalize an arena-placed argument on
// entry, so the caller must copy it into the persistent to-space/blob region itself before the
// call. Small programs look correct; a churn loop between the inserts and the reads is needed to
// actually observe the corruption, so these tests are slower than typical unit tests.
public sealed class GenericParameterHeapValueUafTests
{
    private const string TreeSetterChurnSource = """
            type Item =
                | text: Str
                | position: Int

            type Tree(v) =
                | Leaf
                | Node(Tree(v), Int, v, Tree(v))

            let recursive setTree key value tree =
                match tree with
                    | Leaf -> Node(Leaf)(key)(value)(Leaf)
                    | Node(left, nodeKey, nodeValue, right) ->
                        if key == nodeKey
                        then Node(left)(nodeKey)(value)(right)
                        else
                            if key <= nodeKey
                            then Node(setTree(key)(value)(left))(nodeKey)(nodeValue)(right)
                            else Node(left)(nodeKey)(nodeValue)(setTree(key)(value)(right))

            let recursive getTree key tree =
                match tree with
                    | Leaf -> None
                    | Node(left, nodeKey, nodeValue, right) ->
                        if key == nodeKey
                        then Some(nodeValue)
                        else
                            if key <= nodeKey
                            then getTree(key)(left)
                            else getTree(key)(right)

            let recursive build count acc =
                if count == 0
                then acc
                else build(count - 1)(Item(text = "item " + Ashes.Text.fromInt(count), position = count) :: acc)

            let recursive group items grouped =
                match items with
                    | [] -> grouped
                    | item :: rest ->
                        let key =
                            match item with
                                | Item { position = position } -> position
                        in
                            let text =
                                match item with
                                    | Item { text = text } -> text
                            in group(rest)(setTree(key)([text])(grouped))

            let recursive show keys grouped =
                match keys with
                    | [] -> Unit
                    | key :: rest ->
                        match getTree(key)(grouped) with
                            | Some(text :: _) ->
                                let _ = Ashes.IO.print(Ashes.Text.fromInt(key) + " -> " + text)
                                in show(rest)(grouped)
                            | _ ->
                                let _ = Ashes.IO.print(Ashes.Text.fromInt(key) + " missing")
                                in show(rest)(grouped)

            let recursive churn count acc =
                if count == 0
                then acc
                else churn(count - 1)(("churn " + Ashes.Text.fromInt(count)) :: acc)

            let recursive length items count =
                match items with
                    | [] -> count
                    | _ :: rest -> length(rest)(count + 1)

            let grouped = group(build(3)([]))(Leaf)

            let noise = churn(5000)([])

            let _ = Ashes.IO.print("noise " + Ashes.Text.fromInt(length(noise)(0)))

            show([1, 2, 3])(grouped)
            """;

    [Test]
    public async Task Generic_tree_setter_storing_a_list_value_survives_unrelated_allocation_churn()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string stdout = await CompileRunCaptureProgramAsync(TreeSetterChurnSource).ConfigureAwait(false);

        stdout.ShouldBe("""
            noise 5000
            1 -> item 1
            2 -> item 2
            3 -> item 3

            """.ReplaceLineEndings("\n"));
    }

    [Test]
    public async Task Generic_tree_setter_storing_a_bare_str_value_survives_unrelated_allocation_churn()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string stdout = await CompileRunCaptureProgramAsync(TreeSetterStrChurnSource).ConfigureAwait(false);

        stdout.ShouldBe("""
            noise 5000
            1 -> item 1
            2 -> item 2
            3 -> item 3

            """.ReplaceLineEndings("\n"));
    }

    private const string TreeSetterStrChurnSource = """
            type Item =
                | text: Str
                | position: Int

            type Tree(v) =
                | Leaf
                | Node(Tree(v), Int, v, Tree(v))

            let recursive setTree key value tree =
                match tree with
                    | Leaf -> Node(Leaf)(key)(value)(Leaf)
                    | Node(left, nodeKey, nodeValue, right) ->
                        if key == nodeKey
                        then Node(left)(nodeKey)(value)(right)
                        else
                            if key <= nodeKey
                            then Node(setTree(key)(value)(left))(nodeKey)(nodeValue)(right)
                            else Node(left)(nodeKey)(nodeValue)(setTree(key)(value)(right))

            let recursive getTree key tree =
                match tree with
                    | Leaf -> None
                    | Node(left, nodeKey, nodeValue, right) ->
                        if key == nodeKey
                        then Some(nodeValue)
                        else
                            if key <= nodeKey
                            then getTree(key)(left)
                            else getTree(key)(right)

            let recursive build count acc =
                if count == 0
                then acc
                else build(count - 1)(Item(text = "item " + Ashes.Text.fromInt(count), position = count) :: acc)

            let recursive group items grouped =
                match items with
                    | [] -> grouped
                    | item :: rest ->
                        let key =
                            match item with
                                | Item { position = position } -> position
                        in
                            let text =
                                match item with
                                    | Item { text = text } -> text
                            in group(rest)(setTree(key)(text)(grouped))

            let recursive show keys grouped =
                match keys with
                    | [] -> Unit
                    | key :: rest ->
                        match getTree(key)(grouped) with
                            | Some(text) ->
                                let _ = Ashes.IO.print(Ashes.Text.fromInt(key) + " -> " + text)
                                in show(rest)(grouped)
                            | _ ->
                                let _ = Ashes.IO.print(Ashes.Text.fromInt(key) + " missing")
                                in show(rest)(grouped)

            let recursive length items count =
                match items with
                    | [] -> count
                    | _ :: rest -> length(rest)(count + 1)

            let recursive churn count acc =
                if count == 0
                then acc
                else churn(count - 1)(("churn " + Ashes.Text.fromInt(count)) :: acc)

            let grouped = group(build(3)([]))(Leaf)

            let noise = churn(5000)([])

            let _ = Ashes.IO.print("noise " + Ashes.Text.fromInt(length(noise)(0)))

            show([1, 2, 3])(grouped)
            """;

    // setTree's own body never retains `value`: its parameter type is generic, so the function is
    // compiled once with no static layout to normalize an arena-placed argument before storing it.
    // The fix instead protects the argument at each call site (see the CopyOutArenaToSpace
    // assertions below), so this stays a bare SetAdtField by design — documented here so the
    // absence of a retain inside setTree itself is never mistaken for a regression.
    [Test]
    public void Generic_tree_setter_stores_its_value_parameter_with_no_retain_in_its_own_body()
    {
        Diagnostics diagnostics = new();
        var program = new Parser(MinimalSetTreeSource, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("setTree.ash", MinimalSetTreeSource);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();

        IReadOnlyList<string> setTreeIr = IrTextFormatter.Format(
            IrOptimizer.Optimize(ir), IrDumpStage.Final, "setTree");
        string dump = string.Join('\n', setTreeIr);

        dump.ShouldContain("SetAdtField", Case.Insensitive);
    }

    // The protective copy lives at each call site, not inside setTree: a caller passing a Str to
    // setTree's generic `value` parameter copies it into the persistent to-space/blob region first,
    // since setTree's own body has no static layout to do that itself.
    [Test]
    public void Call_site_copies_a_str_argument_to_setTrees_generic_parameter_into_tospace()
    {
        Diagnostics diagnostics = new();
        var program = new Parser(MinimalSetTreeStrSource, diagnostics).ParseProgram();
        diagnostics.ThrowIfAny();
        Lowering lowering = new(diagnostics);
        lowering.SetSourceContext("setTreeStr.ash", MinimalSetTreeStrSource);
        IrProgram ir = lowering.Lower(program);
        diagnostics.ThrowIfAny();

        IReadOnlyList<string> entryIr = IrTextFormatter.Format(
            IrOptimizer.Optimize(ir), IrDumpStage.Final, filter: null);
        string dump = string.Join('\n', entryIr);

        dump.ShouldContain("CopyOutArenaToSpace", Case.Insensitive);
    }

    private const string MinimalSetTreeStrSource = """
        type Tree(v) =
            | Leaf
            | Node(Tree(v), Int, v, Tree(v))

        let recursive setTree key value tree =
            match tree with
                | Leaf -> Node(Leaf)(key)(value)(Leaf)
                | Node(left, nodeKey, nodeValue, right) ->
                    if key == nodeKey
                    then Node(left)(nodeKey)(value)(right)
                    else
                        if key <= nodeKey
                        then Node(setTree(key)(value)(left))(nodeKey)(nodeValue)(right)
                        else Node(left)(nodeKey)(nodeValue)(setTree(key)(value)(right))

        let ignored = setTree(1)("x")(Leaf)

        Ashes.IO.print("ok")
        """;

    private const string MinimalSetTreeSource = """
        type Tree(v) =
            | Leaf
            | Node(Tree(v), Int, v, Tree(v))

        let recursive setTree key value tree =
            match tree with
                | Leaf -> Node(Leaf)(key)(value)(Leaf)
                | Node(left, nodeKey, nodeValue, right) ->
                    if key == nodeKey
                    then Node(left)(nodeKey)(value)(right)
                    else
                        if key <= nodeKey
                        then Node(setTree(key)(value)(left))(nodeKey)(nodeValue)(right)
                        else Node(left)(nodeKey)(nodeValue)(setTree(key)(value)(right))

        let ignored = setTree(1)([1])(Leaf)

        Ashes.IO.print("ok")
        """;

    private static async Task<string> CompileRunCaptureProgramAsync(string source)
    {
        var diag = new Diagnostics();
        var program = new Parser(source, diag).ParseProgram();
        diag.ThrowIfAny();

        var ir = new Lowering(diag).Lower(program);
        diag.ThrowIfAny();

        var elfBytes = new LinuxX64LlvmBackend().Compile(ir);

        var tmpDir = Path.Combine(Path.GetTempPath(), "ashes-tests");
        Directory.CreateDirectory(tmpDir);
        var exePath = Path.Combine(tmpDir, $"uaf_{Guid.NewGuid():N}");
        TestProcessHelper.WriteExecutable(exePath, elfBytes);

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = await TestProcessHelper.StartProcessAsync(psi).ConfigureAwait(false);
        var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        proc.ExitCode.ShouldBe(0, $"stderr: {stderr}");
        return stdout;
    }
}
