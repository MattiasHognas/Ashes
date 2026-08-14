import Ashes.Test as test
import AshesCompiler.Frontend.Parser
import AshesCompiler.Frontend.Syntax
import ParserProgramTests
let checkRichDeclarations unit =
    (let source = "external type Handle resource destructor closeHandle\nexternal read(borrow *UInt8, consume FfiBuffer(UInt8), out Int) -> FfiStr(nullable owned freeText) needs {Clock} = \"read_native\"\n0"
    in
        match parseProgram(source) with
            | ProgramParseResult { program = ProgramSyntax { items = opaque :: function :: [], body = Some(_body) }, diagnostics = diagnostics } ->
                let diagnosticsChecked = test.assertEqual([])(diagnostics)
                in
                    let opaqueChecked =
                        match ParserProgramTests.unspanTopLevel(opaque) with
                            | TopLevelExternal(ExternalOpaqueType("Handle", Some("closeHandle"))) -> Unit
                            | _ -> test.fail("expected external resource type")
                    in
                        match ParserProgramTests.unspanTopLevel(function) with
                            | TopLevelExternal(ExternalFunction("read", ParsedPointer(ParsedNamed("UInt8")) :: ParsedBuffer(ParsedNamed("UInt8")) :: ParsedOut(ParsedNamed("Int")) :: [], ParsedNativeString(true, FfiStringOwned, Some("freeText")), Some("read_native"), ExternalOwnershipBorrow :: ExternalOwnershipConsume :: ExternalOwnershipUnspecified :: [], Some(NeedsRowSyntax { capabilities = CapabilityRefSyntax { name = "Clock", args = [] } :: [], tailVariable = None }))) -> Unit
                            | _ -> test.fail("expected external function metadata")
            | _ -> test.fail("expected external declarations"))

let checkPlainDeclarations unit =
    match parseProgram("external type Handle\nexternal noop() -> Unit\n0") with
        | ProgramParseResult { program = ProgramSyntax { items = opaque :: function :: [], body = Some(_body) }, diagnostics = diagnostics } ->
            let diagnosticsChecked = test.assertEqual([])(diagnostics)
            in
                match (ParserProgramTests.unspanTopLevel(opaque), ParserProgramTests.unspanTopLevel(function)) with
                    | (TopLevelExternal(ExternalOpaqueType("Handle", None)), TopLevelExternal(ExternalFunction("noop", [], ParsedNamed("Unit"), None, [], None))) -> Unit
                    | _ -> test.fail("expected plain external declarations")
        | _ -> test.fail("expected plain external program")

let expectDiagnostics source =
    match parseProgram(source) with
        | ProgramParseResult { program = _program, diagnostics = _diagnostic :: _tail } -> Unit
        | _ -> test.fail("expected external parser diagnostic")

let run unit =
    (let richChecked = checkRichDeclarations(Unit)
    in
        let plainChecked = checkPlainDeclarations(Unit)
        in
            let resourceChecked = expectDiagnostics("external type Handle resource wrong closeHandle")
            in expectDiagnostics("external read() -> FfiStr(owned)"))
