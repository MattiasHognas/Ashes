type Ordering =
    | Less
    | Equal
    | Greater
    | Unordered

trait Eq(a) =
    | equal : a -> a -> Bool
    | notEqual : a -> a -> Bool =
        given (left) ->
            given (right) -> !Eq.equal(left)(right)

trait Ord(a) requires {Eq(a)} =
    | compare : a -> a -> Ordering
    | less : a -> a -> Bool =
        given (left) ->
            given (right) ->
                match Ord.compare(left)(right) with
                    | Less -> true
                    | _ -> false
    | lessOrEqual : a -> a -> Bool =
        given (left) ->
            given (right) ->
                match Ord.compare(left)(right) with
                    | Less -> true
                    | Equal -> true
                    | _ -> false
    | greater : a -> a -> Bool =
        given (left) ->
            given (right) ->
                match Ord.compare(left)(right) with
                    | Greater -> true
                    | _ -> false
    | greaterOrEqual : a -> a -> Bool =
        given (left) ->
            given (right) ->
                match Ord.compare(left)(right) with
                    | Greater -> true
                    | Equal -> true
                    | _ -> false

trait Show(a) =
    | show : a -> Str

trait Hash(a) =
    | hash : a -> Int

trait Default(a) =
    | default : Unit -> a

trait Add(a) =
    | add : a -> a -> a

trait Subtract(a) =
    | subtract : a -> a -> a

trait Multiply(a) =
    | multiply : a -> a -> a

trait Divide(a) =
    | divide : a -> a -> a

trait Remainder(a) =
    | remainder : a -> a -> a

trait Negate(a) =
    | negate : a -> a

trait Not(a) =
    | not : a -> a

trait BitAnd(a) =
    | bitAnd : a -> a -> a

trait BitOr(a) =
    | bitOr : a -> a -> a

trait BitXor(a) =
    | bitXor : a -> a -> a

trait ShiftLeft(a) =
    | shiftLeft : a -> a -> a

trait ShiftRight(a) =
    | shiftRight : a -> a -> a

trait BitwiseNot(a) =
    | bitwiseNot : a -> a

implement Eq(Int) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Eq(Float) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Eq(BigInt) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Eq(u8) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Eq(u16) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Eq(u32) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Eq(u64) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Eq(Bool) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Eq(Str) =
    | equal =
        given (left) ->
            given (right) -> left == right

implement Ord(Int) =
    | compare =
        given (left) ->
            given (right) ->
                if left < right
                then Less
                else
                    if left > right
                    then Greater
                    else Equal

implement Ord(Float) =
    | compare =
        given (left) ->
            given (right) ->
                if left < right
                then Less
                else
                    if left > right
                    then Greater
                    else
                        if left == right
                        then Equal
                        else Unordered

implement Ord(BigInt) =
    | compare =
        given (left) ->
            given (right) ->
                if left < right
                then Less
                else
                    if left > right
                    then Greater
                    else Equal

implement Ord(u8) =
    | compare =
        given (left) ->
            given (right) ->
                if left < right
                then Less
                else
                    if left > right
                    then Greater
                    else Equal

implement Ord(u16) =
    | compare =
        given (left) ->
            given (right) ->
                if left < right
                then Less
                else
                    if left > right
                    then Greater
                    else Equal

implement Ord(u32) =
    | compare =
        given (left) ->
            given (right) ->
                if left < right
                then Less
                else
                    if left > right
                    then Greater
                    else Equal

implement Ord(u64) =
    | compare =
        given (left) ->
            given (right) ->
                if left < right
                then Less
                else
                    if left > right
                    then Greater
                    else Equal

implement Ord(Str) =
    | compare =
        given (left) ->
            given (right) ->
                if Ashes.Byte.compare(Ashes.Byte.fromText(left))(Ashes.Byte.fromText(right)) < 0
                then Less
                else
                    if Ashes.Byte.compare(Ashes.Byte.fromText(left))(Ashes.Byte.fromText(right)) > 0
                    then Greater
                    else Equal

implement Show(Int) =
    | show =
        given (value) -> Ashes.Text.fromInt(value)

implement Show(Float) =
    | show =
        given (value) -> Ashes.Text.fromFloat(value)

implement Show(BigInt) =
    | show =
        given (value) -> Ashes.Text.fromBigInt(value)

implement Show(u8) =
    | show =
        given (value) -> Ashes.Text.fromInt(Ashes.Number.UInt.toInt(value))

implement Show(u16) =
    | show =
        given (value) -> Ashes.Text.fromInt(Ashes.Number.UInt.toInt(value))

implement Show(u32) =
    | show =
        given (value) -> Ashes.Text.fromInt(Ashes.Number.UInt.toInt(value))

implement Show(u64) =
    | show =
        given (value) -> Ashes.Text.fromInt(Ashes.Number.UInt.toInt(value))

implement Show(Bool) =
    | show =
        given (value) ->
            if value
            then "true"
            else "false"

implement Show(Str) =
    | show =
        given (value) -> "\"" + value + "\""

implement Hash(Int) =
    | hash =
        given (value) -> value

implement Hash(Float) =
    | hash =
        given (value) ->
            if value == 0.0
            then 0
            else Ashes.Byte.hash(Ashes.Byte.fromText(Ashes.Text.fromFloat(value)))

implement Hash(BigInt) =
    | hash =
        given (value) -> Ashes.Byte.hash(Ashes.Byte.fromText(Ashes.Text.fromBigInt(value)))

implement Hash(u8) =
    | hash =
        given (value) -> Ashes.Number.UInt.toInt(value)

implement Hash(u16) =
    | hash =
        given (value) -> Ashes.Number.UInt.toInt(value)

implement Hash(u32) =
    | hash =
        given (value) -> Ashes.Number.UInt.toInt(value)

implement Hash(u64) =
    | hash =
        given (value) -> Ashes.Number.UInt.toInt(value)

implement Hash(Bool) =
    | hash =
        given (value) ->
            if value
            then 1
            else 0

implement Hash(Str) =
    | hash =
        given (value) -> Ashes.Byte.hash(Ashes.Byte.fromText(value))

implement Default(Int) =
    | default =
        given (_) -> 0

implement Default(Float) =
    | default =
        given (_) -> 0.0

implement Default(BigInt) =
    | default =
        given (_) -> 0N

implement Default(u8) =
    | default =
        given (_) -> 0u8

implement Default(u16) =
    | default =
        given (_) -> 0u16

implement Default(u32) =
    | default =
        given (_) -> 0u32

implement Default(u64) =
    | default =
        given (_) -> 0u64

implement Default(Bool) =
    | default =
        given (_) -> false

implement Default(Str) =
    | default =
        given (_) -> ""

implement Not(Bool) =
    | not =
        given (value) -> !value

implement Add(Int) =
    | add =
        given (left) ->
            given (right) -> left + right

implement Add(Float) =
    | add =
        given (left) ->
            given (right) -> left + right

implement Add(BigInt) =
    | add =
        given (left) ->
            given (right) -> left + right

implement Add(u8) =
    | add =
        given (left) ->
            given (right) -> left + right

implement Add(u16) =
    | add =
        given (left) ->
            given (right) -> left + right

implement Add(u32) =
    | add =
        given (left) ->
            given (right) -> left + right

implement Add(u64) =
    | add =
        given (left) ->
            given (right) -> left + right

implement Add(Str) =
    | add =
        given (left) ->
            given (right) -> left + right

implement Subtract(Int) =
    | subtract =
        given (left) ->
            given (right) -> left - right

implement Subtract(Float) =
    | subtract =
        given (left) ->
            given (right) -> left - right

implement Subtract(BigInt) =
    | subtract =
        given (left) ->
            given (right) -> left - right

implement Subtract(u8) =
    | subtract =
        given (left) ->
            given (right) -> left - right

implement Subtract(u16) =
    | subtract =
        given (left) ->
            given (right) -> left - right

implement Subtract(u32) =
    | subtract =
        given (left) ->
            given (right) -> left - right

implement Subtract(u64) =
    | subtract =
        given (left) ->
            given (right) -> left - right

implement Multiply(Int) =
    | multiply =
        given (left) ->
            given (right) -> left * right

implement Multiply(Float) =
    | multiply =
        given (left) ->
            given (right) -> left * right

implement Multiply(BigInt) =
    | multiply =
        given (left) ->
            given (right) -> left * right

implement Multiply(u8) =
    | multiply =
        given (left) ->
            given (right) -> left * right

implement Multiply(u16) =
    | multiply =
        given (left) ->
            given (right) -> left * right

implement Multiply(u32) =
    | multiply =
        given (left) ->
            given (right) -> left * right

implement Multiply(u64) =
    | multiply =
        given (left) ->
            given (right) -> left * right

implement Divide(Int) =
    | divide =
        given (left) ->
            given (right) -> left / right

implement Divide(Float) =
    | divide =
        given (left) ->
            given (right) -> left / right

implement Divide(BigInt) =
    | divide =
        given (left) ->
            given (right) -> left / right

implement Divide(u8) =
    | divide =
        given (left) ->
            given (right) -> left / right

implement Divide(u16) =
    | divide =
        given (left) ->
            given (right) -> left / right

implement Divide(u32) =
    | divide =
        given (left) ->
            given (right) -> left / right

implement Divide(u64) =
    | divide =
        given (left) ->
            given (right) -> left / right

implement Remainder(Int) =
    | remainder =
        given (left) ->
            given (right) -> left % right

implement Remainder(BigInt) =
    | remainder =
        given (left) ->
            given (right) -> left % right

implement Remainder(u8) =
    | remainder =
        given (left) ->
            given (right) -> left % right

implement Remainder(u16) =
    | remainder =
        given (left) ->
            given (right) -> left % right

implement Remainder(u32) =
    | remainder =
        given (left) ->
            given (right) -> left % right

implement Remainder(u64) =
    | remainder =
        given (left) ->
            given (right) -> left % right

implement Negate(Int) =
    | negate =
        given (value) -> -value

implement Negate(Float) =
    | negate =
        given (value) -> -value

implement Negate(BigInt) =
    | negate =
        given (value) -> -value

implement Negate(u8) =
    | negate =
        given (value) -> -value

implement Negate(u16) =
    | negate =
        given (value) -> -value

implement Negate(u32) =
    | negate =
        given (value) -> -value

implement Negate(u64) =
    | negate =
        given (value) -> -value

implement BitAnd(Int) =
    | bitAnd =
        given (left) ->
            given (right) -> left & right

implement BitOr(Int) =
    | bitOr =
        given (left) ->
            given (right) -> left ^ right ^ left & right

implement BitXor(Int) =
    | bitXor =
        given (left) ->
            given (right) -> left ^ right

implement ShiftLeft(Int) =
    | shiftLeft =
        given (left) ->
            given (right) -> left << right

implement ShiftRight(Int) =
    | shiftRight =
        given (left) ->
            given (right) -> left >> right

implement BitwiseNot(Int) =
    | bitwiseNot =
        given (value) -> ~value

implement BitAnd(u8) =
    | bitAnd =
        given (left) ->
            given (right) -> left & right

implement BitAnd(u16) =
    | bitAnd =
        given (left) ->
            given (right) -> left & right

implement BitAnd(u32) =
    | bitAnd =
        given (left) ->
            given (right) -> left & right

implement BitAnd(u64) =
    | bitAnd =
        given (left) ->
            given (right) -> left & right

implement BitOr(u8) =
    | bitOr =
        given (left) ->
            given (right) -> left ^ right ^ left & right

implement BitOr(u16) =
    | bitOr =
        given (left) ->
            given (right) -> left ^ right ^ left & right

implement BitOr(u32) =
    | bitOr =
        given (left) ->
            given (right) -> left ^ right ^ left & right

implement BitOr(u64) =
    | bitOr =
        given (left) ->
            given (right) -> left ^ right ^ left & right

implement BitXor(u8) =
    | bitXor =
        given (left) ->
            given (right) -> left ^ right

implement BitXor(u16) =
    | bitXor =
        given (left) ->
            given (right) -> left ^ right

implement BitXor(u32) =
    | bitXor =
        given (left) ->
            given (right) -> left ^ right

implement BitXor(u64) =
    | bitXor =
        given (left) ->
            given (right) -> left ^ right

implement ShiftLeft(u8) =
    | shiftLeft =
        given (left) ->
            given (right) -> left << right

implement ShiftLeft(u16) =
    | shiftLeft =
        given (left) ->
            given (right) -> left << right

implement ShiftLeft(u32) =
    | shiftLeft =
        given (left) ->
            given (right) -> left << right

implement ShiftLeft(u64) =
    | shiftLeft =
        given (left) ->
            given (right) -> left << right

implement ShiftRight(u8) =
    | shiftRight =
        given (left) ->
            given (right) -> left >> right

implement ShiftRight(u16) =
    | shiftRight =
        given (left) ->
            given (right) -> left >> right

implement ShiftRight(u32) =
    | shiftRight =
        given (left) ->
            given (right) -> left >> right

implement ShiftRight(u64) =
    | shiftRight =
        given (left) ->
            given (right) -> left >> right

implement BitwiseNot(u8) =
    | bitwiseNot =
        given (value) -> ~value

implement BitwiseNot(u16) =
    | bitwiseNot =
        given (value) -> ~value

implement BitwiseNot(u32) =
    | bitwiseNot =
        given (value) -> ~value

implement BitwiseNot(u64) =
    | bitwiseNot =
        given (value) -> ~value

implement Eq(List(a)) requires {Eq(a)} =
    | equal =
        let recursive equalLists : List(a) -> List(a) -> Bool requires {Eq(a)} =
            given (left) ->
                given (right) ->
                    match (left, right) with
                        | ([], []) -> true
                        | (x :: restX, y :: restY) ->
                            if x == y
                            then equalLists(restX)(restY)
                            else false
                        | _ -> false
        in equalLists

implement Ord(List(a)) requires {Ord(a)} =
    | compare =
        let recursive compareLists : List(a) -> List(a) -> Ordering requires {Ord(a)} =
            given (left) ->
                given (right) ->
                    match (left, right) with
                        | ([], []) -> Equal
                        | ([], _ :: _) -> Less
                        | (_ :: _, []) -> Greater
                        | (x :: restX, y :: restY) ->
                            if x < y
                            then Less
                            else
                                if y < x
                                then Greater
                                else compareLists(restX)(restY)
        in compareLists

implement Show(List(a)) requires {Show(a)} =
    | show =
        given (items) ->
            match items with
                | [] -> "[]"
                | value :: [] -> "[" + Show.show(value) + "]"
                | value :: tail ->
                    let renderedTail = Show.show(tail)
                    in "[" + Show.show(value) + ", " + Ashes.Byte.subText(Ashes.Byte.fromText(renderedTail))(1)(Ashes.Text.byteLength(renderedTail) - 1)

implement Hash(List(a)) requires {Hash(a)} =
    | hash =
        given (items) ->
            match items with
                | [] -> 2166136261
                | value :: tail -> Hash.hash(value) * 16777619 + Hash.hash(tail)

implement Default(List(a)) =
    | default =
        given (_) -> []

implement Eq(Maybe(a)) requires {Eq(a)} =
    | equal =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (None, None) -> true
                    | (Some(x), Some(y)) -> Eq.equal(x)(y)
                    | _ -> false

implement Ord(Maybe(a)) requires {Ord(a)} =
    | compare =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (None, None) -> Equal
                    | (None, Some(_)) -> Less
                    | (Some(_), None) -> Greater
                    | (Some(x), Some(y)) -> Ord.compare(x)(y)

implement Show(Maybe(a)) requires {Show(a)} =
    | show =
        given (value) ->
            match value with
                | None -> "None"
                | Some(inner) -> "Some(" + Show.show(inner) + ")"

implement Hash(Maybe(a)) requires {Hash(a)} =
    | hash =
        given (value) ->
            match value with
                | None -> 0
                | Some(inner) -> Hash.hash(inner) * 16777619 + 1

implement Default(Maybe(a)) =
    | default =
        given (_) -> None

implement Eq(Result(e, a)) requires {Eq(a), Eq(e)} =
    | equal =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Error(x), Error(y)) -> Eq.equal(x)(y)
                    | (Ok(x), Ok(y)) -> Eq.equal(x)(y)
                    | _ -> false

implement Ord(Result(e, a)) requires {Ord(a), Ord(e)} =
    | compare =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Error(x), Error(y)) -> Ord.compare(x)(y)
                    | (Error(_), Ok(_)) -> Less
                    | (Ok(_), Error(_)) -> Greater
                    | (Ok(x), Ok(y)) -> Ord.compare(x)(y)

implement Show(Result(e, a)) requires {Show(a), Show(e)} =
    | show =
        given (value) ->
            match value with
                | Error(inner) -> "Error(" + Show.show(inner) + ")"
                | Ok(inner) -> "Ok(" + Show.show(inner) + ")"

implement Hash(Result(e, a)) requires {Hash(a), Hash(e)} =
    | hash =
        given (value) ->
            match value with
                | Error(inner) -> Hash.hash(inner) * 16777619
                | Ok(inner) -> Hash.hash(inner) * 16777619 + 1

implement Eq((a, b)) requires {Eq(a), Eq(b)} =
    | equal =
        given (left) ->
            given (right) ->
                match left with
                    | (leftA, leftB) ->
                        match right with
                            | (rightA, rightB) ->
                                if Eq.equal(leftA)(rightA)
                                then Eq.equal(leftB)(rightB)
                                else false

implement Ord((a, b)) requires {Ord(a), Ord(b)} =
    | compare =
        given (left) ->
            given (right) ->
                match left with
                    | (leftA, leftB) ->
                        match right with
                            | (rightA, rightB) ->
                                match Ord.compare(leftA)(rightA) with
                                    | Equal -> Ord.compare(leftB)(rightB)
                                    | ordering -> ordering

implement Show((a, b)) requires {Show(a), Show(b)} =
    | show =
        given (value) ->
            match value with
                | (first, second) -> "(" + Show.show(first) + ", " + Show.show(second) + ")"

implement Hash((a, b)) requires {Hash(a), Hash(b)} =
    | hash =
        given (value) ->
            match value with
                | (first, second) -> (Hash.hash(first) * 16777619 + Hash.hash(second)) * 16777619 + 2

implement Default((a, b)) requires {Default(a), Default(b)} =
    | default =
        given (_) -> (Default.default(Unit), Default.default(Unit))
