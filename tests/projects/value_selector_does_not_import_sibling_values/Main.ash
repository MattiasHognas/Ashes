// expect-compile-error: Module 'Library' does not export 'sibling'.
import Library.chosen as pick
let selected = pick

Library.sibling
