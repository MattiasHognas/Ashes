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

Ashes.IO.print("all self-hosted frontend tests passed")
