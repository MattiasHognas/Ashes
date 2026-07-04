// A hash map keyed by Str, value type V. Internally an AVL tree ordered by the
// composite key (FNV-1a hash of the key, then the key itself). Tree navigation is
// dominated by cheap 64-bit integer hash comparisons; only on a hash collision does
// it fall back to comparing the key strings. This removes the caller-supplied
// ordering that Ashes.Map requires and the per-node string-compare cost.
// Self-contained: uses only the Ashes.Bytes/Ashes.Text intrinsics.
//
// It does NOT change the memory model: like Ashes.Map it is a persistent structure
// that allocates O(log K) nodes per update.
//
// NOTE: ADT field types in Ashes must be simple type names, so the node stores the
// hash, key and value as separate fields rather than a bucket of tuples.

type HashMapTree(V) =
    | HEmpty
    | HNode(Int, HashMapTree, Int, Str, V, HashMapTree)

let empty = HEmpty

let hashKey key = Ashes.Bytes.hash(Ashes.Bytes.fromText(key))

let strCompare a b = 
    (let ab = Ashes.Bytes.fromText(a)
    in 
        let bb = Ashes.Bytes.fromText(b)
        in 
            let alen = Ashes.Bytes.length(ab)
            in 
                let blen = Ashes.Bytes.length(bb)
                in 
                    let recursive go i = 
                        if i >= alen
                        then 
                            if i >= blen
                            then 0
                            else -1
                        else 
                            if i >= blen
                            then 1
                            else 
                                let x = Ashes.Bytes.get(ab)(i)
                                in 
                                    let y = Ashes.Bytes.get(bb)(i)
                                    in 
                                        if x == y
                                        then go(i + 1)
                                        else 
                                            if x < y
                                            then -1
                                            else 1
                    in go(0))

let compareComposite targetHash targetKey nodeHash nodeKey = 
    if targetHash == nodeHash
    then strCompare(targetKey)(nodeKey)
    else 
        if targetHash <= nodeHash - 1
        then -1
        else 1

let hHeight tree = 
    match tree with
        | HEmpty -> 0
        | HNode(h, _l, _hk, _k, _v, _r) -> h

let hMax a b = 
    if a >= b
    then a
    else b

let hMake left hash key value right = HNode(hMax(hHeight(left))(hHeight(right)) + 1)(left)(hash)(key)(value)(right)

let hRotateLeft tree = 
    match tree with
        | HNode(_h, left, hash, key, value, HNode(_rh, rl, rhash, rkey, rvalue, rr)) -> hMake(hMake(left)(hash)(key)(value)(rl))(rhash)(rkey)(rvalue)(rr)
        | _ -> tree

let hRotateRight tree = 
    match tree with
        | HNode(_h, HNode(_lh, ll, lhash, lkey, lvalue, lr), hash, key, value, right) -> hMake(ll)(lhash)(lkey)(lvalue)(hMake(lr)(hash)(key)(value)(right))
        | _ -> tree

let hBalance tree = 
    match tree with
        | HEmpty -> HEmpty
        | HNode(_h, left, hash, key, value, right) -> 
            let normalized = hMake(left)(hash)(key)(value)(right)
            in 
                if hHeight(left) >= hHeight(right) + 2
                then 
                    match left with
                        | HEmpty -> normalized
                        | HNode(_lh, ll, _lhash, _lkey, _lvalue, lr) -> 
                            if hHeight(ll) >= hHeight(lr)
                            then hRotateRight(normalized)
                            else hRotateRight(hMake(hRotateLeft(left))(hash)(key)(value)(right))
                else 
                    if hHeight(right) >= hHeight(left) + 2
                    then 
                        match right with
                            | HEmpty -> normalized
                            | HNode(_rh, rl, _rhash, _rkey, _rvalue, rr) -> 
                                if hHeight(rr) >= hHeight(rl)
                                then hRotateLeft(normalized)
                                else hRotateLeft(hMake(left)(hash)(key)(value)(hRotateRight(right)))
                    else normalized

let get searchKey map = 
    (let target = hashKey(searchKey)
    in 
        let recursive go tree = 
            match tree with
                | HEmpty -> None
                | HNode(_h, left, nodeHash, nodeKey, nodeValue, right) -> 
                    let ordering = compareComposite(target)(searchKey)(nodeHash)(nodeKey)
                    in 
                        if ordering == 0
                        then Some(nodeValue)
                        else 
                            if ordering <= -1
                            then go(left)
                            else go(right)
        in go(map))

let contains searchKey map = 
    match get(searchKey)(map) with
        | None -> false
        | Some(_) -> true

let set newKey newValue map = 
    (let target = hashKey(newKey)
    in 
        let recursive go tree = 
            match tree with
                | HEmpty -> hMake(HEmpty)(target)(newKey)(newValue)(HEmpty)
                | HNode(_h, left, nodeHash, nodeKey, nodeValue, right) -> 
                    let ordering = compareComposite(target)(newKey)(nodeHash)(nodeKey)
                    in 
                        if ordering == 0
                        then hMake(left)(nodeHash)(nodeKey)(newValue)(right)
                        else 
                            if ordering <= -1
                            then hBalance(hMake(go(left))(nodeHash)(nodeKey)(nodeValue)(right))
                            else hBalance(hMake(left)(nodeHash)(nodeKey)(nodeValue)(go(right)))
        in go(map))

let insert = set

let recursive size map = 
    match map with
        | HEmpty -> 0
        | HNode(_h, left, _hk, _k, _v, right) -> 1 + size(left) + size(right)

let foldLeft folder state map = 
    (let recursive go acc tree = 
        match tree with
            | HEmpty -> acc
            | HNode(_h, left, _hk, key, value, right) -> 
                let afterLeft = go(acc)(left)
                in 
                    let afterNode = folder(afterLeft)(key)(value)
                    in go(afterNode)(right)
    in go(state)(map))
