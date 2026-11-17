// expect: false,true

type Rank =
    | Rank(Int)
    deriving {Eq}

implement Ord(Rank) =
    | compare =
        given (left) ->
            given (right) -> Less
    | lessOrEqual =
        given (left) ->
            given (right) -> false

type Ranked(a) =
    | Ranked(a)
    deriving {Eq}

implement Ord(Ranked(a)) requires {Ord(a)} =
    | compare =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Ranked(x), Ranked(y)) ->
                        if x < y
                        then Less
                        else
                            if x > y
                            then Greater
                            else Equal

type Holder(a) =
    | Holder(a)

implement Eq(Holder(a)) requires {Ord(a)} =
    | equal =
        given (left) ->
            given (right) ->
                match (left, right) with
                    | (Holder(x), Holder(y)) -> x <= y

Ashes.IO.print(Ashes.Trait.Show.show(Holder(Rank(1)) == Holder(Rank(2))) + "," + Ashes.Trait.Show.show(Holder(Ranked("a")) == Holder(Ranked("z"))))
