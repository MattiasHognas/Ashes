import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreLowering
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrText
import AshesCompiler.Semantics.ProgramInference
export (
    value runCoreProgramLoweringTests,
)

let parsedProgram source =
    match parseProgram(source) with
        | ProgramParseResult { program = program, diagnostics = [] } -> program
        | ProgramParseResult { diagnostics = diagnostics } -> test.fail("program should parse cleanly: " + Ashes.Trait.Show.show(diagnostics))

let loweredProgramSource source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { program = Some(program), error = None } -> program
        | CoreLoweringResult { error = Some(error) } ->
            error
            |> Ashes.Trait.Show.show
            |> (given (text) -> test.fail("program lowering failed: " + text))
        | _ -> test.fail("program lowering produced no program")

let dumpSource source =
    formatIr(loweredProgramSource(source))(LoweredIr)(None)

let loweringErrorFor source =
    match source
    |> parsedProgram
    |> lowerCoreProgram with
        | CoreLoweringResult { error = Some(error) } -> error
        | CoreLoweringResult { error = None } -> test.fail("expected program lowering to fail, but it produced a program")

let expectPlainTopLevelLetsProduceIr unit =
    "let a = 1\nlet b = 2\na + b"
    |> loweredProgramSource
    |> (given (_) -> Unit)

let expectSelfRecursiveTopLevelLetLowers unit =
    "let recursive fact n = if n <= 1 then 1 else n * fact(n - 1)\nfact(5)"
    |> loweredProgramSource
    |> (given (_) -> Unit)

let expectMutualRecursionGroupLowers unit =
    "let recursive isEven n = if n == 0 then true else isOdd(n - 1)\nand isOdd n = if n == 0 then false else isEven(n - 1)\nisEven(10)"
    |> loweredProgramSource
    |> (given (_) -> Unit)

let expectMixedPlainAndRecursiveLettersLower unit =
    "let base = 10\nlet recursive countDown n = if n <= 0 then base else countDown(n - 1)\nlet doubled = base * 2\ncountDown(3) + doubled"
    |> loweredProgramSource
    |> (given (_) -> Unit)

let expectDuplicateTopLevelBindingIsRejected unit =
    match loweringErrorFor("let a = 1\nlet a = 2\na") with
        | DuplicateTopLevelBinding(name) -> test.assertEqual("a")(name)
        | other -> test.fail("expected DuplicateTopLevelBinding, got " + Ashes.Trait.Show.show(other))

let expectDuplicateInsideRecursiveGroupIsRejected unit =
    match loweringErrorFor("let recursive f n = f(n)\nand f n = f(n)\nf(0)") with
        | DuplicateTopLevelBinding(name) -> test.assertEqual("f")(name)
        | other -> test.fail("expected DuplicateTopLevelBinding, got " + Ashes.Trait.Show.show(other))

let expectDuplicateAcrossPlainAndRecursiveIsRejected unit =
    match loweringErrorFor("let a = 1\nlet recursive a n = a(n)\na(0)") with
        | DuplicateTopLevelBinding(name) -> test.assertEqual("a")(name)
        | other -> test.fail("expected DuplicateTopLevelBinding, got " + Ashes.Trait.Show.show(other))

// `a`'s value references `b`, declared LATER in the file — Model A's sequential top-level scoping
// makes this a forward reference (ASH014 in stage 0), not a plain undefined-variable error: `b` IS
// a real top-level binding, just not yet visible from `a`'s own position.
let expectForwardReferenceToLaterBindingIsRejected unit =
    match loweringErrorFor("let a = b\nlet b = 1\na") with
        | ForwardTopLevelReference(name) -> test.assertEqual("b")(name)
        | other -> test.fail("expected ForwardTopLevelReference, got " + Ashes.Trait.Show.show(other))

// A plain (non-recursive) `let a = a` — self-reference without `let recursive` — is ALSO a forward
// reference under Model A: `a` only becomes visible to what comes AFTER it, never to its own
// value. Self-recursion needs `let recursive`.
let expectSelfReferenceWithoutRecursiveIsRejected unit =
    match loweringErrorFor("let a = a\na") with
        | ForwardTopLevelReference(name) -> test.assertEqual("a")(name)
        | other -> test.fail("expected ForwardTopLevelReference, got " + Ashes.Trait.Show.show(other))

// A name that ISN'T any top-level binding at all (not even one declared later) is a genuine
// undefined variable, not a forward reference — proves the two error paths stay properly
// distinguished rather than ForwardTopLevelReference swallowing every lookup failure.
let expectGenuinelyUnknownNameStillRejectedAsUnknown unit =
    match loweringErrorFor("let a = totallyUnknownName\na") with
        | UnknownLoweringBinding(name) -> test.assertEqual("totallyUnknownName")(name)
        | other -> test.fail("expected UnknownLoweringBinding, got " + Ashes.Trait.Show.show(other))

let inferredProgramAndEnvironment source =
    match parsedProgram(source) with
        | program ->
            match inferProgram(program) with
                | ProgramInferenceResult { environment = environment, error = None } -> (program, environment)
                | ProgramInferenceResult { error = Some(error) } -> test.fail("program should infer cleanly: " + Ashes.Trait.Show.show(error))

// A one-method user trait, one concrete implementation, and a top-level binding whose OWN written
// `requires` clause is the only thing making it generic over the trait — proves
// lowerCoreProgramWithEnvironment threads a real inference TypeEnvironment (not a hand-built test
// fixture, the way every other TraitEvidence*Tests.ash file constructs one) into
// rewriteTraitConstrainedValue for the first time. No call site references `describe`: this proves
// the value-side rewrite alone (extra hidden dictionary parameter prepended, `Greet.greet`
// rewritten to read from it) independent of the call-site forwarding covered below.
let traitConstrainedProgramSource unit = "trait Greet(a) =\n    | greet : a -> Str\n\nimplement Greet(Int) =\n    | greet = given (n) -> \"hi\"\n\nlet describe : a -> Str requires {Greet(a)} = given (x) -> Greet.greet(x)\n\n42"

let expectTraitConstrainedBindingLowersWithEnvironment unit =
    match Unit
    |> traitConstrainedProgramSource
    |> inferredProgramAndEnvironment with
        | (program, environment) ->
            match lowerCoreProgramWithEnvironment(environment)(program) with
                | CoreLoweringResult { program = Some(_loweredProgram), error = None } -> Unit
                | CoreLoweringResult { error = Some(error) } -> test.fail("trait-constrained program lowering with environment failed: " + Ashes.Trait.Show.show(error))
                | _ -> test.fail("trait-constrained program lowering with environment produced no program")

// The same source through the environment-less entry point: `describe`'s body references
// `Greet.greet` as an ordinary qualified name with no dictionary parameter ever introduced, since
// rewriteTraitConstrainedValue never runs — proving lowerCoreProgramWithEnvironment's rewriting is
// actually doing real work, not merely passing through a program that would have lowered fine
// either way.
let expectTraitConstrainedBindingFailsWithoutEnvironment unit =
    match Unit
    |> traitConstrainedProgramSource
    |> inferredProgramAndEnvironment with
        | (program, _environment) ->
            match lowerCoreProgram(program) with
                | CoreLoweringResult { error = Some(_error) } -> Unit
                | CoreLoweringResult { error = None } -> test.fail("expected trait-constrained program lowering to fail without an environment, but it produced a program")

// `wrapper` is itself generic over the trait (`requires {Greet(a)}`) and calls `describe`, which
// carries the SAME requirement — the abstract-caller-to-abstract-callee case
// rewriteTraitConstrainedTopLevelValue's call-site forwarding exists for: `wrapper`'s own hidden
// dictionary parameter is the only evidence available inside its body (the type variable is not
// yet unified with anything concrete), so `describe(y)` must forward it rather than resolve it
// globally. Before this piece was wired, `describe`'s own rewrite (above) silently made this call
// ill-formed — the callee now expects the dictionary as its real first argument, but the call site
// never supplied one.
let forwardingProgramSource unit = "trait Greet(a) =\n    | greet : a -> Str\n\nimplement Greet(Int) =\n    | greet = given (n) -> \"hi\"\n\nlet describe : a -> Str requires {Greet(a)} = given (x) -> Greet.greet(x)\n\nlet wrapper : a -> Str requires {Greet(a)} = given (y) -> describe(y)\n\n42"

let expectCallSiteForwardingLowersWithEnvironment unit =
    match Unit
    |> forwardingProgramSource
    |> inferredProgramAndEnvironment with
        | (program, environment) ->
            match lowerCoreProgramWithEnvironment(environment)(program) with
                | CoreLoweringResult { program = Some(_loweredProgram), error = None } -> Unit
                | CoreLoweringResult { error = Some(error) } -> test.fail("call-site forwarding program lowering failed: " + Ashes.Trait.Show.show(error))
                | _ -> test.fail("call-site forwarding program lowering produced no program")

// `callsWithoutEvidence` carries no `requires` clause of its own and calls `describe` with a bare
// `Int` literal — the call-site walker's own-parameter guard (a forwarding candidate's argument
// must be one of the ENCLOSING binding's own written lambda parameters, never a literal or local)
// correctly declines to touch it: `5` isn't `callsWithoutEvidence`'s own parameter (it has none),
// so the reference is left exactly as it was before this piece existed. `describe` still expects
// its hidden dictionary as a real first argument, so the plain-looking `describe(5)` type-mismatches
// through CoreLowering's ordinary call-argument type check — proving the guard's conservatism holds
// (no bogus forwarding, no miscompile) rather than the walker inventing evidence that isn't there.
let missingEvidenceProgramSource unit = "trait Greet(a) =\n    | greet : a -> Str\n\nimplement Greet(Int) =\n    | greet = given (n) -> \"hi\"\n\nlet describe : a -> Str requires {Greet(a)} = given (x) -> Greet.greet(x)\n\nlet callsWithoutEvidence = describe(5)\n\n42"

let expectUnguardedConcreteCallStaysUnrewrittenAndTypeMismatches unit =
    match Unit
    |> missingEvidenceProgramSource
    |> inferredProgramAndEnvironment with
        | (program, environment) ->
            match lowerCoreProgramWithEnvironment(environment)(program) with
                | CoreLoweringResult { error = Some(CoreCallTypeMismatch(_unificationError)) } -> Unit
                | CoreLoweringResult { error = Some(error) } -> test.fail("expected CoreCallTypeMismatch, got " + Ashes.Trait.Show.show(error))
                | CoreLoweringResult { error = None } -> test.fail("expected the unguarded concrete call to fail lowering, but it produced a program")

// A well-typed program that reaches lowering with a genuinely ambiguous/mismatched active
// dictionary (two active parameters for the same trait at different type variables) proved hard to
// construct: this compiler's inference rejects any written `requires` clause it can't independently
// justify from the body (MissingWrittenTraitRequirement, UnjustifiedWrittenTraitRequirement — even
// `+` desugars through an implicit Add requirement), so a caller can't simply carry unrelated
// evidence for the walker to misuse. The MissingActiveTraitEvidence error path itself — including
// the exact-match vs. single-active-shape-fallback distinction the call-site walker's forwarding
// depends on — is already covered directly, with hand-built constraints, in
// TraitEvidenceForwardingTests.ash and TraitEvidenceCallRewritingTests.ash; this file's own
// integration coverage is expectCallSiteForwardingLowersWithEnvironment (the success path) and
// expectUnguardedConcreteCallStaysUnrewrittenAndTypeMismatches (the guard correctly declining).
// Both top-level lets and the trailing `a + b` are provably arena-safe (scalar Int throughout), so
// this now matches stage-0's own always-bracketed shape byte-for-byte: SaveArenaState before each
// value, RestoreArenaState + ReclaimArenaChunks after the rest of the program, innermost first.
let expectPlainTopLevelLetsProduceExpectedIr unit =
    "let a = 1\nlet b = 2\na + b"
    |> dumpSource
    |> test.assertEqual([
        "IR (lowered)",
        "============",
        "",
        "function _start_main  [ProgramEntry]",
        "  locals=8 temps=5",
        "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1",
        "    LoadConstInt          Target=0 Value=1",
        "    StoreLocal            Slot=2 Source=0",
        "    SaveArenaState        CursorLocalSlot=3 EndLocalSlot=4",
        "    LoadConstInt          Target=1 Value=2",
        "    StoreLocal            Slot=5 Source=1",
        "    LoadLocal             Target=2 Slot=2",
        "    LoadLocal             Target=3 Slot=5",
        "    AddInt                Target=4 Left=2 Right=3",
        "    RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=6",
        "    ReclaimArenaChunks    SavedEndSlot=4 PreRestoreEndSlot=6",
        "    RestoreArenaState     CursorLocalSlot=0 EndLocalSlot=1 PreRestoreEndSlot=7",
        "    ReclaimArenaChunks    SavedEndSlot=1 PreRestoreEndSlot=7",
        "    Return                Source=4",
        ""
    ])

let recursive containsLine (needle: Str) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest ->
            if line == needle
            then true
            else containsLine(needle)(rest)

// A let-bound lambda is lifted as that name's source function; a lambda nested inside it is a
// closure helper of the same name; a lambda bound nowhere is an anonymous closure helper.
let expectLambdaOriginsNameTheirBinding unit =
    "let makeAdder x =\n    given (y) -> x + y\n\nlet apply = (given (f) -> f(1))\n\napply(makeAdder(5))"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            lines
            |> containsLine("function lambda_0  [SourceFunction from makeAdder]")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("function lambda_1  [ClosureHelper from makeAdder]")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("function lambda_2  [SourceFunction from apply]")
            |> test.assertEqual(true)))

// A closure whose captures are all scalars gets stage 0's environment normalizer right after its
// own function: it copies each capture word from the source environment (slot 0) to the target
// environment (slot 1) and returns the no-dropper address. `x`'s type is settled only by the
// deferred `+` default, so the helper is decided at program finalization.
let expectScalarCaptureClosureGetsEnvironmentNormalizer unit =
    "let makeAdder x =\n    given (y) -> x + y\n\nlet add5 = makeAdder(5)\n\nadd5(10)"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            lines
            |> containsLine("function lambda_1$env_normalize  [ClosureEnvironmentNormalizer from makeAdder]")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("    LoadMemOffset         Target=2 BasePtr=0 OffsetBytes=0")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("    StoreMemOffset        BasePtr=1 OffsetBytes=0 Source=2")
            |> test.assertEqual(true)))

// A closure with no captures needs no normalizer, and one capturing a non-scalar gets none here.
let expectCaptureFreeClosureHasNoNormalizer unit =
    "let constant = (given (x) -> 1)\n\nconstant(2)"
    |> dumpSource
    |> (given (lines) ->
        lines
        |> containsLine("function lambda_0$env_normalize  [ClosureEnvironmentNormalizer from constant]")
        |> test.assertEqual(false))

// A general call keeps its chain's intermediates in an arena window of its own: saved before the
// callee is read, and reset after the last application when the result survives the reset. The
// inner `f(2)` window closes before the outer application consumes its result; the outer one
// closes after its own call. Builtin applications open no window.
let expectCallSpinesGetArenaWindows unit =
    "let f x = x + 1\n\nf(f(2))"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            lines
            |> containsLine("    SaveArenaState        CursorLocalSlot=3 EndLocalSlot=4")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("    SaveArenaState        CursorLocalSlot=5 EndLocalSlot=6")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("    RestoreArenaState     CursorLocalSlot=5 EndLocalSlot=6 PreRestoreEndSlot=7")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("    RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=8")
            |> test.assertEqual(true)))

// A call whose result is a closure cannot be copied out yet, so its window stays open.
let expectClosureResultKeepsCallWindowOpen unit =
    "let makeAdder x =\n    given (y) -> x + y\n\nlet add5 = makeAdder(5)\n\nadd5(10)"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            lines
            |> containsLine("    SaveArenaState        CursorLocalSlot=5 EndLocalSlot=6")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("    RestoreArenaState     CursorLocalSlot=5 EndLocalSlot=6 PreRestoreEndSlot=7")
            |> test.assertEqual(false))
        |> (given (_) ->
            lines
            |> containsLine("    RestoreArenaState     CursorLocalSlot=9 EndLocalSlot=10 PreRestoreEndSlot=11")
            |> test.assertEqual(true)))

// The else branch of an `if` is lowered expecting the then branch's type, and a call there has its
// result constrained to it before its arguments are lowered: the sibling call inside a recursive
// group member has a result type still unresolved on its own, yet its window resets under the
// then branch's Bool.
let expectElseBranchCallResetsUnderThenType unit =
    "let recursive isEven n =\n    if n == 0\n    then true\n    else isOdd(n - 1)\nand isOdd n =\n    if n == 0\n    then false\n    else isEven(n - 1)\n\nisEven(4)"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            lines
            |> containsLine("    CallClosure           Target=9 ClosureTemp=4 ArgTemp=8")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("    RestoreArenaState     CursorLocalSlot=3 EndLocalSlot=4 PreRestoreEndSlot=5")
            |> test.assertEqual(true)))

// A reference to a recursive-group sibling allocates the closure temp before the environment temp.
let expectSiblingClosureTempPrecedesEnvironmentTemp unit =
    "let recursive isEven n =\n    if n == 0\n    then true\n    else isOdd(n - 1)\nand isOdd n =\n    if n == 0\n    then false\n    else isEven(n - 1)\n\nisEven(4)"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            lines
            |> containsLine("    LoadLocal             Target=5 Slot=0")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("    MakeClosure           Target=4 FuncLabel=recgroup_1_isOdd EnvPtrTemp=5 EnvSizeBytes=0")
            |> test.assertEqual(true)))

let recursive countFileHandleCleanups (lines: List(Str)) =
    match lines with
        | [] -> 0
        | line :: rest ->
            if Ashes.Text.startsWith(line)("    CleanupResource       ") && Ashes.Text.contains(line)("TypeName=FileHandle")
            then 1 + countFileHandleCleanups(rest)
            else countFileHandleCleanups(rest)

let openedHandleProgram (armBody: Str) = "match Ashes.IO.File.open(\"input.txt\") with\n    | Error(_) -> Ashes.IO.print(\"error\")\n    | Ok(fh) ->\n" + armBody

let readLineArm = "        match Ashes.IO.File.readLine(fh) with\n            | None -> Ashes.IO.print(\"none\")\n            | Some(line) -> Ashes.IO.print(line)"

// Reading a handle after an explicit close is use-after-close, with stage 0's message.
let expectFileReadAfterCloseIsRejected unit =
    match "        let _ = Ashes.IO.File.close(fh)\n        in\n" + readLineArm
    |> openedHandleProgram
    |> loweringErrorFor with
        | ResourceUseAfterClose(message) -> test.assertEqual("Resource 'fh' has already been closed. Using a resource after it has been closed is not allowed.")(message)
        | other -> test.fail("expected ResourceUseAfterClose, got " + Ashes.Trait.Show.show(other))

// Storing a handle into an aggregate moves it; reading it afterwards is use-after-move.
let expectStoredResourceIsMoved unit =
    match "        let wrapped = Some(fh)\n        in\n" + readLineArm
    |> openedHandleProgram
    |> loweringErrorFor with
        | ResourceUseAfterMove(message) -> test.assertEqual("Resource 'fh' has been moved and can no longer be used here. Passing a resource to a function or storing it in a data structure transfers ownership.")(message)
        | other -> test.fail("expected ResourceUseAfterMove, got " + Ashes.Trait.Show.show(other))

// A helper that stores the handle consumes it: the call moves the argument.
let expectConsumingHelperMovesResource unit =
    match loweringErrorFor("let stash =\n    given (h) -> Some(h)\nin\n" + openedHandleProgram("        let _ = stash(fh)\n        in\n" + readLineArm)) with
        | ResourceUseAfterMove(_message) -> Unit
        | other -> test.fail("expected ResourceUseAfterMove, got " + Ashes.Trait.Show.show(other))

// A helper that closes the handle consumes it as well.
let expectClosingHelperMovesResource unit =
    match loweringErrorFor("let closeIt =\n    given (h) -> Ashes.IO.File.close(h)\nin\n" + openedHandleProgram("        let done = closeIt(fh)\n        in\n" + readLineArm)) with
        | ResourceUseAfterMove(_message) -> Unit
        | other -> test.fail("expected ResourceUseAfterMove, got " + Ashes.Trait.Show.show(other))

// A helper that only reads the handle borrows it: the caller keeps using it and its arm closes it
// exactly once at scope exit.
let expectReadingHelperBorrowsResource unit =
    "let peek =\n    given (h) -> Ashes.IO.File.readChunk(h)(2)\nin\n" + openedHandleProgram("        let head = peek(fh)\n        in\n" + readLineArm)
    |> dumpSource
    |> countFileHandleCleanups
    |> test.assertEqual(1)

// A handle closed explicitly or moved into an aggregate gets no scope-exit cleanup.
let expectClosedOrMovedResourceIsNotCleanedUp unit =
    Unit
    |> (given (_) ->
        "        let _ = Ashes.IO.File.close(fh)\n        in Ashes.IO.print(\"closed\")"
        |> openedHandleProgram
        |> dumpSource
        |> countFileHandleCleanups
        |> test.assertEqual(0))
    |> (given (_) ->
        "        let wrapped = Some(fh)\n        in Ashes.IO.print(\"moved\")"
        |> openedHandleProgram
        |> dumpSource
        |> countFileHandleCleanups
        |> test.assertEqual(0))

// A top-level wrapper that only hands the handle to a reading helper: the whole-program fixpoint
// proves the hand-off borrows where the wrapper's own summary saw a consuming call, so the caller
// keeps reading the handle and its arm still closes it exactly once.
let expectProvenInspectingHandOffBorrowsResource unit =
    "let peek h = Ashes.IO.File.readChunk(h)(2)\nlet peekTwice h = (let a = peek(h) in peek(h))\n" + openedHandleProgram("        let head = peekTwice(fh)\n        in\n" + readLineArm)
    |> dumpSource
    |> countFileHandleCleanups
    |> test.assertEqual(1)

let recursive anyLineContains (fragment: Str) (lines: List(Str)) =
    match lines with
        | [] -> false
        | line :: rest -> Ashes.Text.contains(line)(fragment) || anyLineContains(fragment)(rest)

// A dead top-level constructor binding whose payload reaches past its cell names the type's
// structural dropper on its release; the dropper walks the list payload and is registered as a
// function of its own.
let expectDeadConstructorBindingNamesStructuralDropper unit =
    "type Box =\n    | Wrap(List(Int))\n\nlet dead = Wrap([1, 2])\n\nAshes.IO.print(\"done\")"
    |> dumpSource
    |> (given (lines) ->
        Unit
        |> (given (_) ->
            lines
            |> anyLineContains("TypeName=Wrap RuntimeManaged=true StructuralDropperLabel=__rcdrop_structural_0")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> containsLine("function __rcdrop_structural_0  [StructuralOwnerDropper]")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> anyLineContains("TypeName=Box RuntimeManaged=true")
            |> test.assertEqual(true))
        |> (given (_) ->
            lines
            |> anyLineContains("TypeName=List RuntimeManaged=true")
            |> test.assertEqual(true)))

let expectAnonymousLambdaIsAnAnonymousClosureHelper unit =
    "(given (x) -> x + 1)(41)"
    |> dumpSource
    |> (given (lines) ->
        lines
        |> containsLine("function lambda_0  [ClosureHelper]")
        |> test.assertEqual(true))

let constructorLetProgramSource unit = "type Option(a) =\n    | NoVal\n    | HasVal(a)\n\nlet value = HasVal(42)\n\nmatch value with\n    | HasVal(n) -> n + 1\n    | NoVal -> 0"

// A `let` bound to a saturated constructor of scalar arguments, consumed only by a `match` whose
// constructor patterns bind those scalar fields, is arena-confined: the whole program is
// bracketed like a scalar chain, and the cell itself is an arena `AllocAdt` (no RC request).
// This is the `pattern_match` parity fixture's source. The owned read of `value` borrows, and
// because the top-level scope owns that binding the program result is spilled to a slot across
// the closing restore. The binding's `RcDrop` marker is placed at its last use on every path:
// after the field read in the matching arm, after the tag read in the second arm, and on the
// edge from the second arm's null test into the arm cleanup, which the tag test also reaches
// after its own drop, so that edge gets its own drop block rather than the cleanup's entry.
let expectConstructorLetMatchedByConstructorPatternsIsBracketed unit =
    Unit
    |> constructorLetProgramSource
    |> dumpSource
    |> test.assertEqual([
        "IR (lowered)",
        "============",
        "",
        "function _start_main  [ProgramEntry]",
        "  locals=15 temps=23",
        "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1",
        "    LoadConstInt          Target=0 Value=42",
        "    AllocAdt              Target=1 Tag=1 FieldCount=1",
        "    SetAdtField           Ptr=1 FieldIndex=0 Source=0",
        "    StoreLocal            Slot=2 Source=1",
        "    LoadLocal             Target=2 Slot=2",
        "    Borrow                Target=3 SourceTemp=2",
        "    SaveArenaState        CursorLocalSlot=4 EndLocalSlot=5",
        "    LoadConstInt          Target=4 Value=0",
        "    CmpIntNe              Target=5 Left=3 Right=4",
        "    JumpIfFalse           CondTemp=5 Target=match_arm_cleanup_3",
        "    GetAdtTag             Target=6 Ptr=3",
        "    LoadConstInt          Target=8 Value=1",
        "    CmpIntEq              Target=7 Left=6 Right=8",
        "    JumpIfFalse           CondTemp=7 Target=match_arm_cleanup_3",
        "    GetAdtField           Target=9 Ptr=3 FieldIndex=0",
        "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
        "    StoreLocal            Slot=6 Source=9",
        "    LoadLocal             Target=10 Slot=6",
        "    LoadConstInt          Target=11 Value=1",
        "    AddInt                Target=12 Left=10 Right=11",
        "    StoreLocal            Slot=3 Source=12",
        "    RestoreArenaState     CursorLocalSlot=4 EndLocalSlot=5 PreRestoreEndSlot=7",
        "    ReclaimArenaChunks    SavedEndSlot=5 PreRestoreEndSlot=7",
        "    Jump                  Target=match_end_0",
        "  match_arm_cleanup_3:",
        "    RestoreArenaState     CursorLocalSlot=4 EndLocalSlot=5 PreRestoreEndSlot=8",
        "    ReclaimArenaChunks    SavedEndSlot=5 PreRestoreEndSlot=8",
        "    Jump                  Target=match_next_2",
        "  match_next_2:",
        "    SaveArenaState        CursorLocalSlot=9 EndLocalSlot=10",
        "    LoadConstInt          Target=13 Value=0",
        "    CmpIntNe              Target=14 Left=3 Right=13",
        "    JumpIfFalse           CondTemp=14 Target=_start_main_rc_edge_2_4",
        "    GetAdtTag             Target=15 Ptr=3",
        "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
        "    LoadConstInt          Target=17 Value=0",
        "    CmpIntEq              Target=16 Left=15 Right=17",
        "    JumpIfFalse           CondTemp=16 Target=match_arm_cleanup_4",
        "    LoadConstInt          Target=18 Value=0",
        "    StoreLocal            Slot=3 Source=18",
        "    RestoreArenaState     CursorLocalSlot=9 EndLocalSlot=10 PreRestoreEndSlot=11",
        "    ReclaimArenaChunks    SavedEndSlot=10 PreRestoreEndSlot=11",
        "    Jump                  Target=match_end_0",
        "  match_arm_cleanup_4:",
        "    RestoreArenaState     CursorLocalSlot=9 EndLocalSlot=10 PreRestoreEndSlot=12",
        "    ReclaimArenaChunks    SavedEndSlot=10 PreRestoreEndSlot=12",
        "    Jump                  Target=match_none_1",
        "  match_none_1:",
        "    LoadConstInt          Target=19 Value=0",
        "    StoreLocal            Slot=3 Source=19",
        "  match_end_0:",
        "    LoadLocal             Target=20 Slot=3",
        "    StoreLocal            Slot=13 Source=20",
        "    RestoreArenaState     CursorLocalSlot=0 EndLocalSlot=1 PreRestoreEndSlot=14",
        "    ReclaimArenaChunks    SavedEndSlot=1 PreRestoreEndSlot=14",
        "    LoadLocal             Target=22 Slot=13",
        "    Return                Source=22",
        "  _start_main_rc_edge_2_4:",
        "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
        "    Jump                  Target=match_arm_cleanup_4",
        ""
    ])

// The same cell escaping as the program's result is bracketed like every other `let`, but its
// window is never reset: the result would not survive it. The returned alias is the binding's
// last use, and a use whose "after" is unreachable places the drop before the return. Stage 0
// emits exactly this shape.
let expectEscapingConstructorLetIsBracketedButNeverReset unit =
    "type Option(a) =\n    | NoVal\n    | HasVal(a)\n\nlet value = HasVal(42)\n\nvalue"
    |> dumpSource
    |> test.assertEqual([
        "IR (lowered)",
        "============",
        "",
        "function _start_main  [ProgramEntry]",
        "  locals=5 temps=6",
        "    SaveArenaState        CursorLocalSlot=0 EndLocalSlot=1",
        "    LoadConstInt          Target=0 Value=42",
        "    AllocAdt              Target=1 Tag=1 FieldCount=1",
        "    SetAdtField           Ptr=1 FieldIndex=0 Source=0",
        "    StoreLocal            Slot=2 Source=1",
        "    LoadLocal             Target=2 Slot=2",
        "    Borrow                Target=3 SourceTemp=2",
        "    StoreLocal            Slot=3 Source=3",
        "    LoadLocal             Target=5 Slot=3",
        "    RcDrop                SourceTemp=1 TypeName=Option OwnerSlot=2",
        "    Return                Source=5",
        ""
    ])

// A field read through a receiver whose type is still a variable at the access (a parameter read
// before any call constrains it) resolves by the field's name when exactly one record type
// declares it, stage 0's `ResolveRecordReceiverByFieldName`; two records sharing the field leave
// the receiver unresolved.
let expectFieldAccessOnUnresolvedReceiverResolvesByUniqueField unit =
    "type Case =\n    | tag: Int\n    | label: Str\n\nlet taken c = c.tag + 1\n\ntaken(Case(tag = 1, label = \"a\"))"
    |> dumpSource
    |> anyLineContains("GetAdtField           Target=1 Ptr=0 FieldIndex=0 Tagless=true")
    |> test.assertEqual(true)

let expectAmbiguousFieldAccessStaysUnresolved unit =
    match loweringErrorFor("type Case =\n    | tag: Int\n\ntype Other =\n    | tag: Int\n\nlet taken c = c.tag + 1\n\ntaken(Case(tag = 1))") with
        | CoreRecordUpdateRequiresRecord(_receiverType) -> Unit
        | other -> test.fail("expected CoreRecordUpdateRequiresRecord, got " + Ashes.Trait.Show.show(other))

// An operand that fails to lower reports its own error, not a type mismatch between the
// operator's placeholder operand types.
let expectFailedOperandReportsItsOwnError unit =
    match loweringErrorFor("let a = missing == 1\na") with
        | UnknownLoweringBinding(name) -> test.assertEqual("missing")(name)
        | other -> test.fail("expected UnknownLoweringBinding, got " + Ashes.Trait.Show.show(other))

let runCoreProgramLoweringTests unit =
    unit
    |> expectFieldAccessOnUnresolvedReceiverResolvesByUniqueField
    |> expectAmbiguousFieldAccessStaysUnresolved
    |> expectFailedOperandReportsItsOwnError
    |> expectPlainTopLevelLetsProduceIr
    |> expectPlainTopLevelLetsProduceExpectedIr
    |> expectConstructorLetMatchedByConstructorPatternsIsBracketed
    |> expectEscapingConstructorLetIsBracketedButNeverReset
    |> expectSelfRecursiveTopLevelLetLowers
    |> expectMutualRecursionGroupLowers
    |> expectMixedPlainAndRecursiveLettersLower
    |> expectDuplicateTopLevelBindingIsRejected
    |> expectDuplicateInsideRecursiveGroupIsRejected
    |> expectDuplicateAcrossPlainAndRecursiveIsRejected
    |> expectForwardReferenceToLaterBindingIsRejected
    |> expectSelfReferenceWithoutRecursiveIsRejected
    |> expectGenuinelyUnknownNameStillRejectedAsUnknown
    |> expectTraitConstrainedBindingLowersWithEnvironment
    |> expectTraitConstrainedBindingFailsWithoutEnvironment
    |> expectCallSiteForwardingLowersWithEnvironment
    |> expectUnguardedConcreteCallStaysUnrewrittenAndTypeMismatches
    |> expectLambdaOriginsNameTheirBinding
    |> expectScalarCaptureClosureGetsEnvironmentNormalizer
    |> expectCaptureFreeClosureHasNoNormalizer
    |> expectCallSpinesGetArenaWindows
    |> expectClosureResultKeepsCallWindowOpen
    |> expectElseBranchCallResetsUnderThenType
    |> expectSiblingClosureTempPrecedesEnvironmentTemp
    |> expectFileReadAfterCloseIsRejected
    |> expectStoredResourceIsMoved
    |> expectConsumingHelperMovesResource
    |> expectClosingHelperMovesResource
    |> expectReadingHelperBorrowsResource
    |> expectClosedOrMovedResourceIsNotCleanedUp
    |> expectAnonymousLambdaIsAnAnonymousClosureHelper
    |> (given (_) -> Ashes.IO.print("all self-hosted core program lowering tests passed"))
