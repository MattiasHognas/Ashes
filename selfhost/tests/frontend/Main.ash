import TokenTests
import LexerCoreTests
import LexerLiteralTests
import LexerUnicodeTests
import LexerInvariantTests
import SyntaxTests
let tokenTestsChecked = TokenTests.run(Unit)

let lexerCoreTestsChecked = LexerCoreTests.run(Unit)

let lexerLiteralTestsChecked = LexerLiteralTests.run(Unit)

let lexerUnicodeTestsChecked = LexerUnicodeTests.run(Unit)

let lexerInvariantTestsChecked = LexerInvariantTests.run(Unit)

let syntaxTestsChecked = SyntaxTests.run(Unit)

Ashes.IO.print("all self-hosted frontend tests passed")
