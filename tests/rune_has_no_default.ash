// expect-compile-error: No implementation supplies 'Ashes.Trait.Default(Rune)'
import Ashes.Trait
let rune : Rune = Default.default(Unit)
in rune
