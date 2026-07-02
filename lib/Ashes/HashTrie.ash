type Trie(K, V) =
    | TrieEmpty
    | TrieLeaf(Int, K, V, Trie)
    | TrieNode16(Int, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie, Trie)

let empty = TrieEmpty

let hashText text = Ashes.Bytes.hash(Ashes.Bytes.fromText(text))

let rec firstDiffShift a b shift = 
    if a >> shift & 15 == b >> shift & 15
    then firstDiffShift(a)(b)(shift + 4)
    else shift

let splitPair shift nibA a nibB b = 
    TrieNode16(shift)(if nibA == 0
    then a
    else 
        if nibB == 0
        then b
        else TrieEmpty)(if nibA == 1
    then a
    else 
        if nibB == 1
        then b
        else TrieEmpty)(if nibA == 2
    then a
    else 
        if nibB == 2
        then b
        else TrieEmpty)(if nibA == 3
    then a
    else 
        if nibB == 3
        then b
        else TrieEmpty)(if nibA == 4
    then a
    else 
        if nibB == 4
        then b
        else TrieEmpty)(if nibA == 5
    then a
    else 
        if nibB == 5
        then b
        else TrieEmpty)(if nibA == 6
    then a
    else 
        if nibB == 6
        then b
        else TrieEmpty)(if nibA == 7
    then a
    else 
        if nibB == 7
        then b
        else TrieEmpty)(if nibA == 8
    then a
    else 
        if nibB == 8
        then b
        else TrieEmpty)(if nibA == 9
    then a
    else 
        if nibB == 9
        then b
        else TrieEmpty)(if nibA == 10
    then a
    else 
        if nibB == 10
        then b
        else TrieEmpty)(if nibA == 11
    then a
    else 
        if nibB == 11
        then b
        else TrieEmpty)(if nibA == 12
    then a
    else 
        if nibB == 12
        then b
        else TrieEmpty)(if nibA == 13
    then a
    else 
        if nibB == 13
        then b
        else TrieEmpty)(if nibA == 14
    then a
    else 
        if nibB == 14
        then b
        else TrieEmpty)(if nibA == 15
    then a
    else 
        if nibB == 15
        then b
        else TrieEmpty)

let upsertHashed hash key missValue onHit = 
    (let rec go t = 
        match t with
            | TrieEmpty -> TrieLeaf(hash)(key)(missValue)(TrieEmpty)
            | TrieLeaf(h2, k2, v2, next) -> 
                if h2 == hash
                then 
                    if Ashes.Bytes.compare(Ashes.Bytes.fromText(k2))(Ashes.Bytes.fromText(key)) == 0
                    then TrieLeaf(h2)(k2)(onHit(v2))(next)
                    else TrieLeaf(h2)(k2)(v2)(go(next))
                else 
                    let ds = firstDiffShift(hash)(h2)(0)
                    in 
                        let keep = TrieLeaf(h2)(k2)(v2)(next)
                        in 
                            let fresh = TrieLeaf(hash)(key)(missValue)(TrieEmpty)
                            in splitPair(ds)(hash >> ds & 15)(fresh)(h2 >> ds & 15)(keep)
            | TrieNode16(s, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15) -> 
                let nib = hash >> s & 15
                in 
                    if nib < 8
                    then 
                        if nib < 4
                        then 
                            if nib < 2
                            then 
                                if nib == 0
                                then TrieNode16(s)(go(c0))(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(go(c1))(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib == 2
                                then TrieNode16(s)(c0)(c1)(go(c2))(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(go(c3))(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                        else 
                            if nib < 6
                            then 
                                if nib == 4
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(go(c4))(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(go(c5))(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib == 6
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(go(c6))(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(go(c7))(c8)(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                    else 
                        if nib < 12
                        then 
                            if nib < 10
                            then 
                                if nib == 8
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(go(c8))(c9)(c10)(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(go(c9))(c10)(c11)(c12)(c13)(c14)(c15)
                            else 
                                if nib == 10
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(go(c10))(c11)(c12)(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(go(c11))(c12)(c13)(c14)(c15)
                        else 
                            if nib < 14
                            then 
                                if nib == 12
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(go(c12))(c13)(c14)(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(go(c13))(c14)(c15)
                            else 
                                if nib == 14
                                then TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(go(c14))(c15)
                                else TrieNode16(s)(c0)(c1)(c2)(c3)(c4)(c5)(c6)(c7)(c8)(c9)(c10)(c11)(c12)(c13)(c14)(go(c15))
    in go)

let getHashed hash key = 
    (let rec go t = 
        match t with
            | TrieEmpty -> None
            | TrieLeaf(h2, k2, v2, next) -> 
                if h2 == hash
                then 
                    if Ashes.Bytes.compare(Ashes.Bytes.fromText(k2))(Ashes.Bytes.fromText(key)) == 0
                    then Some(v2)
                    else go(next)
                else None
            | TrieNode16(s, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15) -> 
                let nib = hash >> s & 15
                in 
                    if nib < 8
                    then 
                        if nib < 4
                        then 
                            if nib < 2
                            then 
                                if nib == 0
                                then go(c0)
                                else go(c1)
                            else 
                                if nib == 2
                                then go(c2)
                                else go(c3)
                        else 
                            if nib < 6
                            then 
                                if nib == 4
                                then go(c4)
                                else go(c5)
                            else 
                                if nib == 6
                                then go(c6)
                                else go(c7)
                    else 
                        if nib < 12
                        then 
                            if nib < 10
                            then 
                                if nib == 8
                                then go(c8)
                                else go(c9)
                            else 
                                if nib == 10
                                then go(c10)
                                else go(c11)
                        else 
                            if nib < 14
                            then 
                                if nib == 12
                                then go(c12)
                                else go(c13)
                            else 
                                if nib == 14
                                then go(c14)
                                else go(c15)
    in go)

let foldLeft folder state = 
    (let rec go acc t = 
        match t with
            | TrieEmpty -> acc
            | TrieLeaf(_hash, key, value, next) -> go(folder(acc)(key)(value))(next)
            | TrieNode16(_shift, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15) -> 
                let a0 = go(acc)(c0)
                in 
                    let a1 = go(a0)(c1)
                    in 
                        let a2 = go(a1)(c2)
                        in 
                            let a3 = go(a2)(c3)
                            in 
                                let a4 = go(a3)(c4)
                                in 
                                    let a5 = go(a4)(c5)
                                    in 
                                        let a6 = go(a5)(c6)
                                        in 
                                            let a7 = go(a6)(c7)
                                            in 
                                                let a8 = go(a7)(c8)
                                                in 
                                                    let a9 = go(a8)(c9)
                                                    in 
                                                        let a10 = go(a9)(c10)
                                                        in 
                                                            let a11 = go(a10)(c11)
                                                            in 
                                                                let a12 = go(a11)(c12)
                                                                in 
                                                                    let a13 = go(a12)(c13)
                                                                    in 
                                                                        let a14 = go(a13)(c14)
                                                                        in go(a14)(c15)
    in go(state))

let toList t = 
    (let prepend rest key value = (key, value) :: rest
    in foldLeft(prepend)([])(t))

let rec size t = 
    match t with
        | TrieEmpty -> 0
        | TrieLeaf(_hash, _key, _value, next) -> 1 + size(next)
        | TrieNode16(_shift, c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15) -> size(c0) + size(c1) + size(c2) + size(c3) + size(c4) + size(c5) + size(c6) + size(c7) + size(c8) + size(c9) + size(c10) + size(c11) + size(c12) + size(c13) + size(c14) + size(c15)
