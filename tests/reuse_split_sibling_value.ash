// CO-23 regression: a reuse-spec arm that rebuilds the matched cell AND creates a fresh
// same-arity sibling (Ashes.HashTrie's natural-order leaf split). The fresh-value constructor
// consumes the matched cell's reuse token; without the liveness gate its in-place value
// materialization clobbered the old leaf's blob while the sibling rebuild still referenced it
// (cross-key stat corruption at scale). The gate must block in-place there (references to the
// superseded binding remain on the path) while keeping it for the hit-arm update (all
// references already evaluated), so hot upsert folds stay constant-memory.
// expect: bad=0 n=200
import Ashes.IO
import Ashes.HashTrie
import Ashes.Text
let recursive fill i acc = 
    if i >= 200
    then acc
    else 
        let key = "k" + Ashes.Text.fromInt(i)
        in 
            fill(i + 1)(Ashes.HashTrie.upsertHashed(Ashes.HashTrie.hashText(key))(key)((i, i))(given (old) -> old)(acc))

let recursive bump i acc = 
    if i >= 200
    then acc
    else 
        let key = "k" + Ashes.Text.fromInt(i)
        in 
            bump(i + 1)(Ashes.HashTrie.upsertHashed(Ashes.HashTrie.hashText(key))(key)((-1, -1))(given (old) -> 
                match old with
                    | (a, b) -> (a + 1000, b))(acc))

let t = bump(0)(fill(0)(Ashes.HashTrie.empty))

let recursive verify i bad = 
    if i >= 200
    then bad
    else 
        let key = "k" + Ashes.Text.fromInt(i)
        in 
            match Ashes.HashTrie.getHashed(Ashes.HashTrie.hashText(key))(key)(t) with
                | Some((a, b)) -> 
                    if a == i + 1000
                    then 
                        if b == i
                        then verify(i + 1)(bad)
                        else verify(i + 1)(bad + 1)
                    else verify(i + 1)(bad + 1)
                | None -> verify(i + 1)(bad + 100)

Ashes.IO.writeLine("bad=" + Ashes.Text.fromInt(verify(0)(0)) + " n=" + Ashes.Text.fromInt(Ashes.HashTrie.size(t)))
