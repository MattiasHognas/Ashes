import Ashes.Text
export (
    type Style(..),
    value separator,
    value normalize,
    value join,
    value parent,
    value basename,
    value extension,
    value relativeTo,
)

type Style =
    | Unix
    | Windows

let reverseList values =
    (let recursive go remaining result =
        match remaining with
            | [] -> result
            | head :: tail -> go(tail)(head :: result)
    in go(values)([]))

let recursive appendList left right =
    match left with
        | [] -> right
        | head :: tail -> head :: appendList(tail)(right)

let separator style =
    match style with
        | Unix -> "/"
        | Windows -> "\\"

let recursive replaceSeparators style path =
    match Ashes.Text.unconsText(path) with
        | None -> ""
        | Some((head, tail)) ->
            let normalized =
                match style with
                    | Unix -> head
                    | Windows ->
                        if head == "\\"
                        then "/"
                        else head
            in normalized + replaceSeparators(style)(tail)

let recursive nonEmpty parts =
    match parts with
        | [] -> []
        | head :: tail ->
            if head == ""
            then nonEmpty(tail)
            else head :: nonEmpty(tail)

let recursive normalizeParts absolute parts stack =
    match parts with
        | [] -> reverseList(stack)
        | head :: tail ->
            if head == ""
            then normalizeParts(absolute)(tail)(stack)
            else
                if head == "."
                then normalizeParts(absolute)(tail)(stack)
                else
                    if head == ".."
                    then
                        match stack with
                            | [] ->
                                if absolute
                                then normalizeParts(absolute)(tail)([])
                                else normalizeParts(absolute)(tail)(".." :: [])
                            | top :: rest ->
                                if top == ".."
                                then normalizeParts(absolute)(tail)(".." :: stack)
                                else normalizeParts(absolute)(tail)(rest)
                    else normalizeParts(absolute)(tail)(head :: stack)

let hasDrive path =
    if Ashes.Text.length(path) >= 2
    then Ashes.Text.substring(path)(1)(1) == ":"
    else false

let rootAndParts style path =
    (let slashed = replaceSeparators(style)(path)
    in
        match style with
            | Unix ->
                let absolute = Ashes.Text.startsWith(slashed)("/")
                in ("", absolute, nonEmpty(Ashes.Text.split(slashed)("/")))
            | Windows ->
                if Ashes.Text.startsWith(slashed)("//")
                then
                    match nonEmpty(Ashes.Text.split(Ashes.Text.drop(slashed)(2))("/")) with
                        | server :: share :: rest -> ("//" + server + "/" + share, true, rest)
                        | _ -> ("", true, nonEmpty(Ashes.Text.split(Ashes.Text.drop(slashed)(2))("/")))
                else
                    if hasDrive(slashed)
                    then
                        let drive = Ashes.Internal.deepCopy(Ashes.Text.take(slashed)(2))
                        in
                            let remainder = Ashes.Internal.deepCopy(Ashes.Text.drop(slashed)(2))
                            in
                                let absolute = Ashes.Text.startsWith(remainder)("/")
                                in (drive, absolute, nonEmpty(Ashes.Text.split(Ashes.Internal.deepCopy(remainder))("/")))
                    else
                        let absolute = Ashes.Text.startsWith(slashed)("/")
                        in ("", absolute, nonEmpty(Ashes.Text.split(slashed)("/"))))

let render style prefix absolute parts =
    (let slash = separator(style)
    in
        let body = Ashes.Text.join(slash)(parts)
        in
            match style with
                | Unix ->
                    if absolute
                    then
                        if body == ""
                        then "/"
                        else "/" + body
                    else
                        if body == ""
                        then "."
                        else body
                | Windows ->
                    let renderedPrefix = Ashes.Text.join(slash)(Ashes.Text.split(prefix)("/"))
                    in
                        if prefix == ""
                        then
                            if absolute
                            then
                                if body == ""
                                then slash
                                else slash + body
                            else
                                if body == ""
                                then "."
                                else body
                        else
                            if absolute
                            then
                                if body == ""
                                then renderedPrefix + slash
                                else renderedPrefix + slash + body
                            else renderedPrefix + body)

let normalize style path =
    match rootAndParts(style)(path) with
        | (prefix, absolute, parts) -> render(style)(prefix)(absolute)(normalizeParts(absolute)(parts)([]))

let isRooted style path =
    match rootAndParts(style)(path) with
        | (prefix, absolute, _parts) ->
            if absolute
            then true
            else prefix != ""

let join style left right =
    if isRooted(style)(right)
    then normalize(style)(right)
    else
        if left == ""
        then normalize(style)(right)
        else
            if left == "."
            then normalize(style)(right)
            else normalize(style)(left + separator(style) + right)

let recursive removeLastReversed reversed =
    match reversed with
        | [] -> []
        | _last :: rest -> reverseList(rest)

let parent style path =
    match rootAndParts(style)(normalize(style)(path)) with
        | (prefix, absolute, parts) -> render(style)(prefix)(absolute)(removeLastReversed(reverseList(parts)))

let recursive lastPart parts current =
    match parts with
        | [] -> current
        | head :: tail -> lastPart(tail)(head)

let basename style path =
    match rootAndParts(style)(normalize(style)(path)) with
        | (_prefix, _absolute, parts) -> lastPart(parts)("")

let extension style path =
    (let name = basename(style)(path)
    in
        let recursive findLastDot remaining index lastDot =
            match Ashes.Text.unconsText(remaining) with
                | None -> lastDot
                | Some((head, tail)) ->
                    findLastDot(tail)(index + 1)(if head == "."
                    then index
                    else lastDot)
        in
            let dot = findLastDot(name)(0)(-1)
            in
                let length = Ashes.Text.length(name)
                in
                    if dot <= 0
                    then ""
                    else
                        if dot >= length - 1
                        then ""
                        else Ashes.Text.drop(name)(dot))

let samePart style left right =
    match style with
        | Unix -> left == right
        | Windows -> Ashes.Text.asciiLower(left) == Ashes.Text.asciiLower(right)

let sameRoot style leftPrefix leftAbsolute rightPrefix rightAbsolute =
    if leftAbsolute == rightAbsolute
    then samePart(style)(leftPrefix)(rightPrefix)
    else false

let recursive parentsFor parts =
    match parts with
        | [] -> []
        | _head :: tail -> ".." :: parentsFor(tail)

let recursive relativeParts style left right =
    match left with
        | [] -> right
        | leftHead :: leftTail ->
            match right with
                | [] -> parentsFor(left)
                | rightHead :: rightTail ->
                    if samePart(style)(leftHead)(rightHead)
                    then relativeParts(style)(leftTail)(rightTail)
                    else appendList(parentsFor(left))(right)

let relativeTo style base target =
    (let normalizedBase = normalize(style)(base)
    in
        let normalizedTarget = normalize(style)(target)
        in
            match rootAndParts(style)(normalizedBase) with
                | (basePrefix, baseAbsolute, baseParts) ->
                    match rootAndParts(style)(normalizedTarget) with
                        | (targetPrefix, targetAbsolute, targetParts) ->
                            if sameRoot(style)(basePrefix)(baseAbsolute)(targetPrefix)(targetAbsolute)
                            then render(style)("")(false)(relativeParts(style)(baseParts)(targetParts))
                            else normalizedTarget)
