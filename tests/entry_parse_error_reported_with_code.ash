// A parse error in the entry file is reported as a diagnostic with its code, not as the
// structural form of the stitching error.
// fmt-skip: the binding is deliberately missing its name
// expect-compile-error: ASH003 Expected Ident but found Equals.
let = 1
1
