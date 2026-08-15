import TokenTests
import LexerCoreTests
import LexerLiteralTests
import LexerUnicodeTests
import LexerInvariantTests
import SyntaxTests
import ParserExpressionTests
import ParserOperatorTests
import ParserPrimaryTests
import ParserInvariantTests
import ParserControlTests
import ParserPatternTests
import ParserTypeTests
import ParserAnnotationTests
import ParserProgramTests
import ParserRecursiveGroupTests
import ParserProgramBoundaryTests
import ParserExternalDeclarationTests
import ParserCapabilityTraitTests
import ParserFrontendParityTests
import ImportHeaderTests
import ImportResolutionTests
let tokenTestsChecked = TokenTests.run(Unit)

let lexerCoreTestsChecked = LexerCoreTests.run(Unit)

let lexerLiteralTestsChecked = LexerLiteralTests.run(Unit)

let lexerUnicodeTestsChecked = LexerUnicodeTests.run(Unit)

let lexerInvariantTestsChecked = LexerInvariantTests.run(Unit)

let syntaxTestsChecked = SyntaxTests.run(Unit)

let parserExpressionTestsChecked = ParserExpressionTests.run(Unit)

let parserOperatorTestsChecked = ParserOperatorTests.run(Unit)

let parserPrimaryTestsChecked = ParserPrimaryTests.run(Unit)

let parserInvariantTestsChecked = ParserInvariantTests.run(Unit)

let parserControlTestsChecked = ParserControlTests.run(Unit)

let parserPatternTestsChecked = ParserPatternTests.run(Unit)

let parserTypeTestsChecked = ParserTypeTests.run(Unit)

let parserAnnotationTestsChecked = ParserAnnotationTests.run(Unit)

let parserProgramTestsChecked = ParserProgramTests.run(Unit)

let parserRecursiveGroupTestsChecked = ParserRecursiveGroupTests.run(Unit)

let parserProgramBoundaryTestsChecked = ParserProgramBoundaryTests.run(Unit)

let parserExternalDeclarationTestsChecked = ParserExternalDeclarationTests.run(Unit)

let parserCapabilityTraitTestsChecked = ParserCapabilityTraitTests.run(Unit)

let parserFrontendParityTestsChecked = ParserFrontendParityTests.run(Unit)

let importHeaderTestsChecked = ImportHeaderTests.run(Unit)

let importResolutionTestsChecked = ImportResolutionTests.run(Unit)

Ashes.IO.print("all self-hosted frontend tests passed")
