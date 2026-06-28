// expect:
// exit: 0
// fmt-skip: exercises a sugared (ML-param) flat top-level binding whose value, after the
// `fun`s are stripped, leads directly with `let..in` at EOF — the case that tripped ASH003
// ("Expected In but found EOF") because the paramless `let..in` pyramid-head EOF carve-out
// also caught it. `ashes fmt` canonicalizes the RHS into a parenthesized `(let..in)`, the
// escape form that was never broken, so it would no longer cover this unit's fix.
let id x = let g = x in g

let f x = let rec go n = match n with | 0 -> x | _ -> go(n - 1) in go(id(x))
