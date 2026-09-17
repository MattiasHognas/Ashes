// Whole-program result reachability, stage 0's `MoveAnalysis` result-reach summaries: for every
// let-bound function of a program, nested ones included, which of its parameters its result may
// alias, whole or only through a destructured component, with a poison flag when the result is
// not provably confined to them. A registry walk records each function with the functions in
// scope of its body; a least fixpoint grows every summary from bottom until stable, a call
// substituting the callee's summary over its argument reaches.
//
// Invariants:
// - Reach is a may-analysis: a summary only grows, and any unmodelled shape poisons.
// - A stored summary names a parameter at presence one, bare when the result may keep the
//   parameter itself and as `name/*` when it keeps only components of it; the synthetic tokens
//   of one function's walk never leave it.
// - A function is keyed by its name and the source offset of its lambda value, the identity the
//   lowering recomputes from the binding it lowers.

import AshesCompiler.Frontend.Token.TextSpan
import AshesCompiler.Frontend.Syntax
import AshesCompiler.Semantics.CoreBuiltinLowering.freshRcBuiltinCall
import AshesCompiler.Semantics.OwnershipSummary
import Ashes.Collection.List.append
import Ashes.Collection.List.reverse
import Ashes.Collection.List.length
import Ashes.Collection.List.map
import Ashes.Collection.Map.MapTree
export (
    type ResultReachState(..),
    type ReachConstructor(..),
    type ReachNestedShape(..),
    type ReachFunction(..),
    type ReachRegistry(..),
    type ReachSummary(..),
    value reachBottom,
    value reachParam,
    value reachPoisoned,
    value reachJoin,
    value reachSum,
    value builtinReachConstructors,
    value reachConstructorsOf,
    value lambdaIdentityOf,
    value functionKeyOf,
    value buildReachRegistry,
    value computeReachSummaries,
    value programReachSummaries,
    value expressionReachSummaries,
    value lookupReachSummary,
    value reachSummaryFor,
    value reachSummaryNamed,
    value singleFunctionReach,
    value reachFactsOf,
)

type ResultReachState =
    | counts: List(ParameterReachEntry)
    | causes: List(ResultReachCause)
    | isPoisoned: Bool
    deriving {Eq, Show}

// Multiplicities cap at two: a parameter reachable through two simultaneous heap positions is
// internally shared, and a moved argument would be doubly aliased in the result.
let reachCap = 2

let reachBottom unit = ResultReachState(counts = [], causes = [], isPoisoned = false)

let reachOfToken (token: Str) = ResultReachState(counts = [ParameterReachEntry(parameterName = token, reachCount = 1)], causes = [], isPoisoned = false)

let reachParam (parameter: Str) = reachOfToken(parameter)

let reachPoisoned (cause: ResultReachCause) = ResultReachState(counts = [], causes = [cause], isPoisoned = true)

let withCounts (counts: List(ParameterReachEntry)) (causes: List(ResultReachCause)) =
    match causes with
        | [] -> ResultReachState(counts = counts, causes = [], isPoisoned = false)
        | _ -> ResultReachState(counts = counts, causes = causes, isPoisoned = true)

let recursive lookupCount (entries: List(ParameterReachEntry)) (key: Str) =
    match entries with
        | [] -> 0
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            if name == key
            then count
            else lookupCount(rest)(key)

let recursive replaceCount (key: Str) (value: Int) (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> [ParameterReachEntry(parameterName = key, reachCount = value)]
        | (ParameterReachEntry { parameterName = name } as entry) :: rest ->
            if name == key
            then ParameterReachEntry(parameterName = key, reachCount = value) :: rest
            else entry :: replaceCount(key)(value)(rest)

let recursive holdsCount (entries: List(ParameterReachEntry)) (key: Str) (value: Int) =
    match entries with
        | [] -> false
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            if name == key
            then count == value
            else holdsCount(rest)(key)(value)

// Counts saturate at the cap, so most writes restate the value an entry already holds; those
// return the table itself instead of rebuilding the spine ahead of the key.
let setCount (key: Str) (value: Int) (entries: List(ParameterReachEntry)) =
    if holdsCount(entries)(key)(value)
    then entries
    else replaceCount(key)(value)(entries)

let recursive containsCause (causes: List(ResultReachCause)) (target: ResultReachCause) =
    match causes with
        | [] -> false
        | cause :: rest -> cause == target || containsCause(rest)(target)

let addCause (cause: ResultReachCause) (causes: List(ResultReachCause)) =
    if containsCause(causes)(cause)
    then causes
    else append(causes)([cause])

let recursive mergeCauses (left: List(ResultReachCause)) (right: List(ResultReachCause)) =
    match right with
        | [] -> left
        | cause :: rest ->
            mergeCauses(addCause(cause)(left))(rest)

let capped (value: Int) =
    if value >= reachCap
    then reachCap
    else value

let recursive lookupCountOr (missing: Int) (entries: List(ParameterReachEntry)) (key: Str) =
    match entries with
        | [] -> missing
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            if name == key
            then count
            else lookupCountOr(missing)(rest)(key)

let recursive namesRepeat (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> false
        | ParameterReachEntry { parameterName = name } :: rest -> lookupCountOr(-1)(rest)(name) >= 0 || namesRepeat(rest)

let recursive sumCountsEntryByEntry (left: List(ParameterReachEntry)) (right: List(ParameterReachEntry)) =
    match right with
        | [] -> left
        | ParameterReachEntry { parameterName = key, reachCount = count } :: rest ->
            sumCountsEntryByEntry(setCount(key)(capped(lookupCount(left)(key) + count))(left))(rest)

let recursive summedOntoLeft (left: List(ParameterReachEntry)) (right: List(ParameterReachEntry)) (added: List(ParameterReachEntry)) =
    match left with
        | [] -> added
        | (ParameterReachEntry { parameterName = name, reachCount = count } as entry) :: rest ->
            let other = lookupCountOr(-1)(right)(name)
            in
                if other < 0
                then entry :: summedOntoLeft(rest)(right)(added)
                else ParameterReachEntry(parameterName = name, reachCount = capped(count + other)) :: summedOntoLeft(rest)(right)(added)

let recursive summedBesideLeft (right: List(ParameterReachEntry)) (left: List(ParameterReachEntry)) =
    match right with
        | [] -> []
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            if lookupCountOr(-1)(left)(name) >= 0
            then summedBesideLeft(rest)(left)
            else ParameterReachEntry(parameterName = name, reachCount = capped(count)) :: summedBesideLeft(rest)(left)

// The left table with the right one added into it: shared names in place, the right's own names
// after them in its order. Built in one pass that only reads both tables. Writing the right's
// entries into the left one at a time hands the table to a helper and takes it back once per entry,
// and a helper that returns a list it was passed must return an owned one, which for a parameter
// whose ownership it cannot see is a copy of the whole table. A right table naming something twice
// keeps the entry-by-entry order, which is what defines the result then.
let sumCounts (left: List(ParameterReachEntry)) (right: List(ParameterReachEntry)) =
    if namesRepeat(right)
    then sumCountsEntryByEntry(left)(right)
    else
        left
        |> summedBesideLeft(right)
        |> summedOntoLeft(left)(right)

let recursive anyCapped (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> false
        | ParameterReachEntry { reachCount = count } :: rest -> count >= reachCap || anyCapped(rest)

let recursive hasDescendantOf (prefix: Str) (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> false
        | ParameterReachEntry { parameterName = name } :: rest -> Ashes.Text.length(name) > Ashes.Text.length(prefix) && Ashes.Text.startsWith(name)(prefix) || hasDescendantOf(prefix)(rest)

// Whether the entries hold a token and a proper path descendant of it (`map` beside `map/1`): the
// result embeds a value and one of its own sub-cells, the internal sharing an entry copy unshares.
let recursive hasPathAncestorPair (entries: List(ParameterReachEntry)) (all: List(ParameterReachEntry)) =
    match entries with
        | [] -> false
        | ParameterReachEntry { parameterName = name } :: rest -> hasDescendantOf(name + "/")(all) || hasPathAncestorPair(rest)(all)

// Simultaneously live positions (a constructor's fields, an aggregate's elements): multiplicities
// add, and a parameter reached twice, or beside one of its own sub-cells, poisons.
let reachSum (left: ResultReachState) (right: ResultReachState) =
    match (left, right) with
        | (ResultReachState { counts = leftCounts, causes = leftCauses }, ResultReachState { counts = rightCounts, causes = rightCauses }) ->
            let counts = sumCounts(leftCounts)(rightCounts)
            in
                let causes = mergeCauses(leftCauses)(rightCauses)
                in
                    if anyCapped(counts) || hasPathAncestorPair(counts)(counts)
                    then
                        causes
                        |> addCause(InternalSharing)
                        |> withCounts(counts)
                    else withCounts(counts)(causes)

let recursive maxCountsEntryByEntry (left: List(ParameterReachEntry)) (right: List(ParameterReachEntry)) =
    match right with
        | [] -> left
        | ParameterReachEntry { parameterName = key, reachCount = count } :: rest ->
            let current = lookupCount(left)(key)
            in
                if current > count
                then maxCountsEntryByEntry(left)(rest)
                else
                    maxCountsEntryByEntry(setCount(key)(count)(left))(rest)

let recursive maximizedOntoLeft (left: List(ParameterReachEntry)) (right: List(ParameterReachEntry)) (added: List(ParameterReachEntry)) =
    match left with
        | [] -> added
        | (ParameterReachEntry { parameterName = name, reachCount = count } as entry) :: rest ->
            let other = lookupCountOr(-1)(right)(name)
            in
                if other < 0 || count > other
                then entry :: maximizedOntoLeft(rest)(right)(added)
                else ParameterReachEntry(parameterName = name, reachCount = other) :: maximizedOntoLeft(rest)(right)(added)

let recursive absentFromLeft (right: List(ParameterReachEntry)) (left: List(ParameterReachEntry)) =
    match right with
        | [] -> []
        | (ParameterReachEntry { parameterName = name } as entry) :: rest ->
            if lookupCountOr(-1)(left)(name) >= 0
            then absentFromLeft(rest)(left)
            else entry :: absentFromLeft(rest)(left)

// The branch-join counterpart of `sumCounts`, one reading pass for the same reason.
let maxCounts (left: List(ParameterReachEntry)) (right: List(ParameterReachEntry)) =
    if namesRepeat(right)
    then maxCountsEntryByEntry(left)(right)
    else
        left
        |> absentFromLeft(right)
        |> maximizedOntoLeft(left)(right)

// A branch join (at most one arm executes) and the fixpoint join: multiplicities take the maximum.
let reachJoin (left: ResultReachState) (right: ResultReachState) =
    match (left, right) with
        | (ResultReachState { counts = leftCounts, causes = leftCauses }, ResultReachState { counts = rightCounts, causes = rightCauses }) ->
            rightCauses
            |> mergeCauses(leftCauses)
            |> withCounts(maxCounts(leftCounts)(rightCounts))

let scaleEntry (factor: Int) (entry: ParameterReachEntry) =
    match entry with
        | ParameterReachEntry { parameterName = name, reachCount = count } -> ParameterReachEntry(parameterName = name, reachCount = capped(count * factor))

// A callee embedding a parameter `factor` times multiplies the reach of the argument bound to it.
let reachScale (state: ResultReachState) (factor: Int) =
    if factor <= 0
    then reachBottom(Unit)
    else
        match state with
            | ResultReachState { counts = counts, causes = causes } ->
                let scaled =
                    map(scaleEntry(factor))(counts)
                in
                    if anyCapped(scaled)
                    then
                        causes
                        |> addCause(InternalSharing)
                        |> withCounts(scaled)
                    else withCounts(scaled)(causes)

let extendEntry (suffix: Str) (entry: ParameterReachEntry) =
    match entry with
        | ParameterReachEntry { parameterName = name, reachCount = count } -> ParameterReachEntry(parameterName = name + suffix, reachCount = count)

let extendEntries (suffix: Str) (state: ResultReachState) =
    match state with
        | ResultReachState { counts = counts, causes = causes } ->
            withCounts(map(extendEntry(suffix))(counts))(causes)

// Destructuring field `index` of a value yields a distinct sub-cell of every cell the value may
// alias, so every token becomes `token/index`.
let extendPaths (index: Int) (state: ResultReachState) = extendEntries("/" + Ashes.Text.fromInt(index))(state)

let extendPathsNamed (field: Str) (state: ResultReachState) = extendEntries("/" + field)(state)

// An unspecified sub-cell position: a callee keeping only destructured parts of its parameter
// hands the caller a value containing parts of the argument, never the argument itself.
let extendPathsComponent (state: ResultReachState) = extendEntries("/*")(state)

let rootOf (name: Str) =
    (let slash = Ashes.Text.indexOf(name)("/")
    in
        if slash < 0
        then name
        else Ashes.Text.substring(name)(0)(slash))

let isWholeName (name: Str) = Ashes.Text.indexOf(name)("/") < 0

let isSyntheticRoot (root: Str) = root == "" || Ashes.Text.startsWith(root)("#")

let wholeEntry (root: Str) = ParameterReachEntry(parameterName = root, reachCount = 1)

let componentEntry (root: Str) = ParameterReachEntry(parameterName = root + "/*", reachCount = 1)

// Records a root at presence one; a whole reach replaces a component-only one of the same root.
let recursive recordRoot (root: Str) (whole: Bool) (entries: List(ParameterReachEntry)) =
    match entries with
        | [] ->
            if whole
            then [wholeEntry(root)]
            else [componentEntry(root)]
        | (ParameterReachEntry { parameterName = name } as entry) :: rest ->
            if rootOf(name) == root
            then
                if whole
                then wholeEntry(root) :: rest
                else entry :: rest
            else entry :: recordRoot(root)(whole)(rest)

let recursive stripEntries (entries: List(ParameterReachEntry)) (stripped: List(ParameterReachEntry)) =
    match entries with
        | [] -> stripped
        | ParameterReachEntry { parameterName = name } :: rest ->
            let root = rootOf(name)
            in
                if isSyntheticRoot(root)
                then stripEntries(rest)(stripped)
                else
                    stripped
                    |> recordRoot(root)(isWholeName(name))
                    |> stripEntries(rest)

// Collapses a working reach to the stored summary: each path token reduces to its root, the
// synthetic tokens drop, and each parameter is recorded once, whole when any path reached it
// without a field segment.
let stripSyntheticTokens (state: ResultReachState) =
    match state with
        | ResultReachState { counts = counts, causes = causes } ->
            withCounts(stripEntries(counts)([]))(causes)

let recursive countsSubsumed (entries: List(ParameterReachEntry)) (within: List(ParameterReachEntry)) =
    match entries with
        | [] -> true
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest -> lookupCount(within)(name) == count && countsSubsumed(rest)(within)

let recursive causesSubsumed (causes: List(ResultReachCause)) (within: List(ResultReachCause)) =
    match causes with
        | [] -> true
        | cause :: rest -> containsCause(within)(cause) && causesSubsumed(rest)(within)

let reachEquals (left: ResultReachState) (right: ResultReachState) =
    match (left, right) with
        | (ResultReachState { counts = leftCounts, causes = leftCauses }, ResultReachState { counts = rightCounts, causes = rightCauses }) -> countsSubsumed(leftCounts)(rightCounts) && countsSubsumed(rightCounts)(leftCounts) && causesSubsumed(leftCauses)(rightCauses) && causesSubsumed(rightCauses)(leftCauses)

// A data constructor as the reach walk sees it: its arity, its field names, which fields hold
// copy-typed scalars inline (they alias no heap cell), and whether it is the only nullary
// constructor of its type (a tag cell that is safe to share).
type ReachConstructor =
    | name: Str
    | arity: Int
    | fieldNames: List(Str)
    | copyFields: List(Bool)
    | soleNullary: Bool
    deriving {Eq, Show}

let recursive lookupConstructor (name: Str) (constructors: List(ReachConstructor)) =
    match constructors with
        | [] -> None
        | (ReachConstructor { name = candidate } as constructor) :: rest ->
            if candidate == name
            then Some(constructor)
            else lookupConstructor(name)(rest)

let recursive lookupTypeErasure (name: Str) (erasures: List((Str, TypeExpr))) =
    match erasures with
        | [] -> None
        | (candidate, target) :: rest ->
            if candidate == name
            then Some(target)
            else lookupTypeErasure(name)(rest)

// Stage 0's `CanArenaReset` over declared syntax: the scalar types, through zero-cost wrappers
// and aliases.
let recursive isCopyTypeExpr (erasures: List((Str, TypeExpr))) (depth: Int) (typeExpr: TypeExpr) =
    if depth > 8
    then false
    else
        match typeExpr with
            | TypeAt(_span, inner) -> isCopyTypeExpr(erasures)(depth)(inner)
            | TypeNamed(name) ->
                if name == "Int" || name == "UInt" || name == "Float" || name == "Rune" || name == "Bool"
                then true
                else
                    match lookupTypeErasure(name)(erasures) with
                        | Some(target) -> isCopyTypeExpr(erasures)(depth + 1)(target)
                        | None -> false
            | _ -> false

let recursive nullaryCount (constructors: List(TypeConstructor)) =
    match constructors with
        | [] -> 0
        | TypeConstructor { parameters = [] } :: rest -> 1 + nullaryCount(rest)
        | _ :: rest -> nullaryCount(rest)

let constructorEntry (erasures: List((Str, TypeExpr))) (soleNullaryInType: Bool) (constructor: TypeConstructor) =
    match constructor with
        | TypeConstructor { name = name, parameters = parameters, fieldNames = fieldNames } ->
            ReachConstructor(
                name = name,
                arity = length(parameters),
                fieldNames = fieldNames,
                copyFields = map(isCopyTypeExpr(erasures)(0))(parameters),
                soleNullary = length(parameters) == 0 && soleNullaryInType
            )

let recursive typeErasuresOf (items: List(TopLevelItem)) (erasures: List((Str, TypeExpr))) =
    match items with
        | [] -> erasures
        | TopLevelAt(_span, inner) :: rest -> typeErasuresOf(inner :: rest)(erasures)
        | TopLevelZeroCostType(ZeroCostTypeDecl { name = name, constructor = TypeConstructor { parameters = inner :: [] } }) :: rest -> typeErasuresOf(rest)((name, inner) :: erasures)
        | TopLevelTypeAlias(TypeAliasDecl { name = name, target = target }) :: rest -> typeErasuresOf(rest)((name, target) :: erasures)
        | _ :: rest -> typeErasuresOf(rest)(erasures)

let recursive declaredReachConstructors (erasures: List((Str, TypeExpr))) (items: List(TopLevelItem)) (constructors: List(ReachConstructor)) =
    match items with
        | [] -> constructors
        | TopLevelAt(_span, inner) :: rest -> declaredReachConstructors(erasures)(inner :: rest)(constructors)
        | TopLevelType(TypeDecl { constructors = declared }) :: rest ->
            declared
            |> map(constructorEntry(erasures)(nullaryCount(declared) == 1))
            |> append(constructors)
            |> declaredReachConstructors(erasures)(rest)
        | TopLevelZeroCostType(ZeroCostTypeDecl { constructor = constructor }) :: rest ->
            [constructorEntry(erasures)(true)(constructor)]
            |> append(constructors)
            |> declaredReachConstructors(erasures)(rest)
        | _ :: rest -> declaredReachConstructors(erasures)(rest)(constructors)

let heapConstructor (name: Str) = ReachConstructor(name = name, arity = 1, fieldNames = [], copyFields = [false], soleNullary = false)

let soleNullaryConstructor (name: Str) = ReachConstructor(name = name, arity = 0, fieldNames = [], copyFields = [], soleNullary = true)

// The constructors every program has without declaring them.
let builtinReachConstructors = [soleNullaryConstructor("Unit"), soleNullaryConstructor("None"), heapConstructor("Some"), heapConstructor("Ok"), heapConstructor("Error")]

let reachConstructorsOf (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items } ->
            declaredReachConstructors(typeErasuresOf(items)([]))(items)(builtinReachConstructors)

// The Map.set shape: a chain of outer parameters whose innermost body returns a single-parameter
// `let recursive` bare, so the whole application is analyzable with the accumulator as a
// trailing parameter and the inner self-call resolved against the enclosing function's own
// summary.
type ReachNestedShape =
    | recursiveName: Str
    | recursiveKey: Str
    | outer: List(Str)
    | accumulator: Str
    deriving {Eq, Show}

// A registered function: its parameters and innermost body (the nested shape's outer parameters
// plus accumulator over the inner body), and the functions in scope of that body by name.
// The names in scope at a point of the program, each mapped to the key of the function it names.
type alias ReachScope = MapTree(Str, Str)

// The summaries so far, keyed by function key.
type alias ReachTable = MapTree(Str, ResultReachState)

type ReachFunction =
    | key: Str
    | name: Str
    | enclosing: Maybe(Str)
    | identity: Int
    | parameters: List(Str)
    | body: Expr
    | nested: Maybe(ReachNestedShape)
    | scope: ReachScope

// `byKey` and `byIdentity` index `functions` by their key and by `name@identity`, so a call site
// and a binding find their function without a walk over every function of the program.
type ReachRegistry =
    | functions: List(ReachFunction)
    | constructors: List(ReachConstructor)
    | valueNames: MapTree(Str, Bool)
    | byKey: MapTree(Str, ReachFunction)
    | byIdentity: MapTree(Str, ReachFunction)

type ReachSummary =
    | function: ReachFunction
    | reach: ResultReachState

let recursive stripSpans (expr: Expr) =
    match expr with
        | ExprAt(_span, inner) -> stripSpans(inner)
        | other -> other

// The source offset identifying a function's lambda value: the first located node through its
// parameter chain, `-1` for an unlocated value.
let recursive lambdaIdentityOf (value: Expr) =
    match value with
        | ExprAt(TextSpan { start = start }, _inner) -> start
        | ExprLambda(_parameter, body, _annotation) -> lambdaIdentityOf(body)
        | _ -> -1

let recursive lambdaChainOf (value: Expr) (parameters: List(Str)) =
    match value with
        | ExprAt(_span, inner) -> lambdaChainOf(inner)(parameters)
        | ExprLambda(parameter, body, _annotation) -> lambdaChainOf(body)(parameter :: parameters)
        | body -> (reverse(parameters), body)

let isLambda (expr: Expr) =
    match stripSpans(expr) with
        | ExprLambda(_parameter, _body, _annotation) -> true
        | _ -> false

let recursive nestedShapeOf (value: Expr) (outer: List(Str)) =
    match value with
        | ExprAt(_span, inner) -> nestedShapeOf(inner)(outer)
        | ExprLambda(parameter, body, _annotation) -> nestedShapeOf(body)(parameter :: outer)
        | ExprLetRecursive(name, recursiveValue, letBody, _parameters, _annotation, _requirements) ->
            match (outer, stripSpans(recursiveValue), stripSpans(letBody)) with
                | (_first :: _rest, ExprLambda(accumulator, innerBody, _innerAnnotation), ExprVar(reference)) ->
                    if reference == name && isLambda(innerBody) == false
                    then Some((reverse(outer), accumulator, name, recursiveValue, innerBody))
                    else None
                | _ -> None
        | _ -> None

let functionKeyOf (enclosing: Maybe(Str)) (name: Str) (identity: Int) =
    match enclosing with
        | Some(parent) -> parent + "." + name + "@" + Ashes.Text.fromInt(identity)
        | None -> name + "@" + Ashes.Text.fromInt(identity)

// The names in scope, each mapped to the key of the function it names. A name a parameter or
// pattern shadows maps to the empty key, which no function has, so a shadowed entry reads as
// absent without the map needing a removal.
let emptyReachScope = Ashes.Collection.Map.empty

let lookupScope (name: Str) (scope: ReachScope) =
    match Ashes.Collection.Map.getStr(name)(scope) with
        | Some(key) ->
            if key == ""
            then None
            else Some(key)
        | None -> None

let removeScopeName (name: Str) (scope: ReachScope) = Ashes.Collection.Map.setStr(name)("")(scope)

let recursive removeScopeNames (names: List(Str)) (scope: ReachScope) =
    match names with
        | [] -> scope
        | name :: rest ->
            scope
            |> removeScopeName(name)
            |> removeScopeNames(rest)

let setScopeName (name: Str) (key: Str) (scope: ReachScope) = Ashes.Collection.Map.setStr(name)(key)(scope)

// The let-bound value names, as a map so a program's thousands of bindings register and resolve
// in logarithmic time; a name bound more than once is marked ambiguous and no longer a value.
let containsName (name: Str) (names: MapTree(Str, Bool)) =
    match Ashes.Collection.Map.getStr(name)(names) with
        | Some(present) -> present
        | None -> false

let addName (name: Str) (names: MapTree(Str, Bool)) = Ashes.Collection.Map.setStr(name)(true)(names)

let withoutName (name: Str) (names: MapTree(Str, Bool)) = Ashes.Collection.Map.setStr(name)(false)(names)

// The registration in progress: the functions found so far (latest first), the let-bound names
// bound exactly once, and the names bound more than once.
type ReachRegistration =
    | functions: List(ReachFunction)
    | valueNames: MapTree(Str, Bool)
    | ambiguous: MapTree(Str, Bool)

let emptyRegistration = ReachRegistration(functions = [], valueNames = Ashes.Collection.Map.empty, ambiguous = Ashes.Collection.Map.empty)

let recordValueName (name: Str) (registration: ReachRegistration) =
    match registration with
        | ReachRegistration { functions = functions, valueNames = valueNames, ambiguous = ambiguous } ->
            if containsName(name)(ambiguous)
            then registration
            else
                if containsName(name)(valueNames)
                then ReachRegistration(functions = functions, valueNames = withoutName(name)(valueNames), ambiguous = addName(name)(ambiguous))
                else ReachRegistration(functions = functions, valueNames = addName(name)(valueNames), ambiguous = ambiguous)

let addFunction (function: ReachFunction) (registration: ReachRegistration) = registration with functions = function :: registration.functions

let recursive patternBinders (pattern: Pattern) (binders: List(Str)) =
    match pattern with
        | PatternAt(_span, inner) -> patternBinders(inner)(binders)
        | PatternVar(name) -> name :: binders
        | PatternCons(head, tail) ->
            binders
            |> patternBinders(head)
            |> patternBinders(tail)
        | PatternTuple(elements) -> patternListBinders(elements)(binders)
        | PatternConstructor(_constructor, fields) -> patternListBinders(fields)(binders)
        | PatternRecord(_record, fields) -> patternFieldBinders(fields)(binders)
        | PatternAs(inner, name) -> name :: patternBinders(inner)(binders)
        | PatternOr(first :: _rest) -> patternBinders(first)(binders)
        | _ -> binders
and patternListBinders (patterns: List(Pattern)) (binders: List(Str)) =
    match patterns with
        | [] -> binders
        | pattern :: rest ->
            binders
            |> patternBinders(pattern)
            |> patternListBinders(rest)
and patternFieldBinders (fields: List((Str, Pattern))) (binders: List(Str)) =
    match fields with
        | [] -> binders
        | (_field, pattern) :: rest ->
            binders
            |> patternBinders(pattern)
            |> patternFieldBinders(rest)

let recursive patternsBinders (patterns: List(Pattern)) (binders: List(Str)) =
    match patterns with
        | [] -> binders
        | pattern :: rest ->
            binders
            |> patternBinders(pattern)
            |> patternsBinders(rest)

// The function a binding declares, when its value is a lambda: the nested shape's outer
// parameters and accumulator over the inner body with the recursive name in scope, otherwise the
// parameter chain over the innermost body with the parameters out of scope.
let registeredFunctionOf (key: Str) (name: Str) (enclosing: Maybe(Str)) (identity: Int) (value: Expr) (parameters: List(Str)) (innerBody: Expr) (scope: ReachScope) =
    match nestedShapeOf(value)([]) with
        | Some((outer, accumulator, recursiveName, recursiveValue, nestedBody)) ->
            let recursiveKey =
                recursiveValue
                |> lambdaIdentityOf
                |> functionKeyOf(Some(key))(recursiveName)
            in
                ReachFunction(
                    key = key,
                    name = name,
                    enclosing = enclosing,
                    identity = identity,
                    parameters = append(outer)([accumulator]),
                    body = nestedBody,
                    nested = Some(ReachNestedShape(recursiveName = recursiveName, recursiveKey = recursiveKey, outer = outer, accumulator = accumulator)),
                    scope = scope
                    |> removeScopeNames(outer)
                    |> setScopeName(recursiveName)(recursiveKey)
                    |> removeScopeNames([accumulator])
                )
        | None ->
            ReachFunction(
                key = key,
                name = name,
                enclosing = enclosing,
                identity = identity,
                parameters = parameters,
                body = innerBody,
                nested = None,
                scope = removeScopeNames(parameters)(scope)
            )

let recursive registerExpr (expr: Expr) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    match expr with
        | ExprAt(_span, inner) -> registerExpr(inner)(enclosing)(scope)(registration)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) -> registerBinding(false)(name)(value)(Some(body))(enclosing)(scope)(registration)
        | ExprLetResult(name, value, body) -> registerBinding(false)(name)(value)(Some(body))(enclosing)(scope)(registration)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) -> registerBinding(true)(name)(value)(Some(body))(enclosing)(scope)(registration)
        | ExprLambda(parameter, body, _annotation) ->
            registerExpr(body)(enclosing)(removeScopeNames([parameter])(scope))(registration)
        | ExprIf(condition, thenBranch, elseBranch) ->
            registration
            |> registerExpr(condition)(enclosing)(scope)
            |> registerExpr(thenBranch)(enclosing)(scope)
            |> registerExpr(elseBranch)(enclosing)(scope)
        | ExprCall(function, argument, _isSugar, _layout) -> registerPair(function)(argument)(enclosing)(scope)(registration)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            registration
            |> registerExpr(scrutinee)(enclosing)(scope)
            |> registerArms(arms)(enclosing)(scope)
        | ExprTuple(elements) -> registerList(elements)(enclosing)(scope)(registration)
        | ExprList(elements, _isMultiline) -> registerList(elements)(enclosing)(scope)(registration)
        | ExprCons(head, tail) -> registerPair(head)(tail)(enclosing)(scope)(registration)
        | ExprRecord(_name, fields, _isMultiline) -> registerFields(fields)(enclosing)(scope)(registration)
        | ExprRecordUpdate(target, fields) ->
            registration
            |> registerExpr(target)(enclosing)(scope)
            |> registerFields(fields)(enclosing)(scope)
        | ExprAwait(inner) -> registerExpr(inner)(enclosing)(scope)(registration)
        | ExprPerform(inner) -> registerExpr(inner)(enclosing)(scope)(registration)
        | ExprHandle(body, arms) ->
            registration
            |> registerExpr(body)(enclosing)(scope)
            |> registerHandlerArms(arms)(enclosing)(scope)
        | ExprAdd(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprSubtract(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprMultiply(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprDivide(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprModulo(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprBitwiseAnd(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprBitwiseOr(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprBitwiseXor(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprShiftLeft(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprShiftRight(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprBitwiseNot(operand) -> registerExpr(operand)(enclosing)(scope)(registration)
        | ExprLogicalNot(operand) -> registerExpr(operand)(enclosing)(scope)(registration)
        | ExprLogicalAnd(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprLogicalOr(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprEqual(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprNotEqual(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprLessThan(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprLessOrEqual(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprGreaterThan(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprGreaterOrEqual(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprResultPipe(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | ExprResultMapErrorPipe(left, right) -> registerPair(left)(right)(enclosing)(scope)(registration)
        | _ -> registration
and registerPair (left: Expr) (right: Expr) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    registration
    |> registerExpr(left)(enclosing)(scope)
    |> registerExpr(right)(enclosing)(scope)
and registerList (elements: List(Expr)) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    match elements with
        | [] -> registration
        | element :: rest ->
            registration
            |> registerExpr(element)(enclosing)(scope)
            |> registerList(rest)(enclosing)(scope)
and registerFields (fields: List((Str, Expr))) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    match fields with
        | [] -> registration
        | (_field, value) :: rest ->
            registration
            |> registerExpr(value)(enclosing)(scope)
            |> registerFields(rest)(enclosing)(scope)
and registerArms (arms: List((Pattern, Expr, Maybe(Expr)))) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    match arms with
        | [] -> registration
        | (pattern, body, guard) :: rest ->
            let armScope =
                removeScopeNames(patternBinders(pattern)([]))(scope)
            in
                registration
                |> registerGuard(guard)(enclosing)(armScope)
                |> registerExpr(body)(enclosing)(armScope)
                |> registerArms(rest)(enclosing)(scope)
and registerGuard (guard: Maybe(Expr)) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    match guard with
        | Some(expr) -> registerExpr(expr)(enclosing)(scope)(registration)
        | None -> registration
and registerHandlerArms (arms: List((Maybe(Str), Str, List(Pattern), Expr))) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    match arms with
        | [] -> registration
        | (_capability, _operation, parameters, body) :: rest ->
            registration
            |> registerExpr(body)(enclosing)(removeScopeNames(patternsBinders(parameters)([]))(scope))
            |> registerHandlerArms(rest)(enclosing)(scope)
and registerBody (body: Maybe(Expr)) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    match body with
        | Some(expr) -> registerExpr(expr)(enclosing)(scope)(registration)
        | None -> registration
// A binding registers its function when its value is a lambda, walks the value for the functions
// nested in it (a recursive binding visible to its own value), and continues into the body with
// the name bound to the function or, for a plain value, shadowing any function of that name.
and registerBinding (isRecursive: Bool) (name: Str) (value: Expr) (body: Maybe(Expr)) (enclosing: Maybe(Str)) (scope: ReachScope) (registration: ReachRegistration) =
    match lambdaChainOf(value)([]) with
        | ([], _innerBody) ->
            registration
            |> recordValueName(name)
            |> registerExpr(value)(enclosing)(scope)
            |> registerBody(body)(enclosing)(removeScopeName(name)(scope))
        | (parameters, innerBody) ->
            let identity = lambdaIdentityOf(value)
            in
                let key = functionKeyOf(enclosing)(name)(identity)
                in
                    let recursiveScope =
                        if isRecursive
                        then setScopeName(name)(key)(scope)
                        else scope
                    in
                        registration
                        |> recordValueName(name)
                        |> addFunction(registeredFunctionOf(key)(name)(enclosing)(identity)(value)(parameters)(innerBody)(recursiveScope))
                        |> registerExpr(value)(Some(key))(recursiveScope)
                        |> registerBody(body)(enclosing)(setScopeName(name)(key)(scope))

// The scope after a top-level binding: the name bound to its function, or shadowing one.
let scopeAfterBinding (name: Str) (value: Expr) (scope: ReachScope) =
    match lambdaChainOf(value)([]) with
        | ([], _innerBody) -> removeScopeName(name)(scope)
        | _ ->
            setScopeName(name)(value
            |> lambdaIdentityOf
            |> functionKeyOf(None)(name))(scope)

let recursive groupScopeOf (bindings: List(LetBindingSyntax)) (scope: ReachScope) =
    match bindings with
        | [] -> scope
        | LetBindingSyntax { name = name, value = value } :: rest ->
            scope
            |> scopeAfterBinding(name)(value)
            |> groupScopeOf(rest)

let recursive registerGroup (bindings: List(LetBindingSyntax)) (groupScope: ReachScope) (registration: ReachRegistration) =
    match bindings with
        | [] -> registration
        | LetBindingSyntax { name = name, value = value } :: rest ->
            registration
            |> registerBinding(true)(name)(value)(None)(None)(groupScope)
            |> registerGroup(rest)(groupScope)

// Registers the top-level declarations in order under sequential scoping, yielding the scope
// the trailing expression sees.
let recursive registerItems (items: List(TopLevelItem)) (scope: ReachScope) (registration: ReachRegistration) =
    match items with
        | [] -> (scope, registration)
        | TopLevelAt(_span, inner) :: rest -> registerItems(inner :: rest)(scope)(registration)
        | TopLevelLet(LetBindingSyntax { name = name, value = value }, isRecursive) :: rest ->
            registration
            |> registerBinding(isRecursive)(name)(value)(None)(None)(scope)
            |> registerItems(rest)(scopeAfterBinding(name)(value)(scope))
        | TopLevelRecursiveGroup(bindings) :: rest ->
            let groupScope = groupScopeOf(bindings)(scope)
            in
                registration
                |> registerGroup(bindings)(groupScope)
                |> registerItems(rest)(groupScope)
        | _ :: rest -> registerItems(rest)(scope)(registration)

let recursive indexFunctions (functions: List(ReachFunction)) (byKey: MapTree(Str, ReachFunction)) (byIdentity: MapTree(Str, ReachFunction)) =
    match functions with
        | [] -> (byKey, byIdentity)
        | (ReachFunction { key = key, name = name, identity = identity } as function) :: rest ->
            byIdentity
            |> Ashes.Collection.Map.setStr(name + "@" + Ashes.Text.fromInt(identity))(function)
            |> indexFunctions(rest)(Ashes.Collection.Map.setStr(key)(function)(byKey))

// A registry with its two function indexes built once over its functions.
let indexedRegistry (functions: List(ReachFunction)) (constructors: List(ReachConstructor)) (valueNames: MapTree(Str, Bool)) =
    match indexFunctions(functions)(Ashes.Collection.Map.empty)(Ashes.Collection.Map.empty) with
        | (byKey, byIdentity) -> ReachRegistry(functions = functions, constructors = constructors, valueNames = valueNames, byKey = byKey, byIdentity = byIdentity)

let buildReachRegistry (program: ProgramSyntax) =
    match program with
        | ProgramSyntax { items = items, body = body } ->
            match registerItems(items)(emptyReachScope)(emptyRegistration) with
                | (scope, registered) ->
                    match registerBody(body)(None)(scope)(registered) with
                        | ReachRegistration { functions = functions, valueNames = valueNames } ->
                            indexedRegistry(reverse(functions))(reachConstructorsOf(program))(valueNames)

let recursive lookupFunction (key: Str) (functions: List(ReachFunction)) =
    match functions with
        | [] -> None
        | (ReachFunction { key = candidate } as function) :: rest ->
            if candidate == key
            then Some(function)
            else lookupFunction(key)(rest)

let recursive functionByIdentity (name: Str) (identity: Int) (functions: List(ReachFunction)) =
    match functions with
        | [] -> None
        | (ReachFunction { name = candidate, identity = candidateIdentity } as function) :: rest ->
            if candidate == name && candidateIdentity == identity
            then Some(function)
            else functionByIdentity(name)(identity)(rest)

// The summaries so far, keyed by function key; a function not yet swept reads as bottom.
let lookupTable (key: Str) (table: ReachTable) =
    match Ashes.Collection.Map.getStr(key)(table) with
        | Some(reach) -> reach
        | None -> reachBottom(Unit)

let setTable (key: Str) (reach: ResultReachState) (table: ReachTable) = Ashes.Collection.Map.setStr(key)(reach)(table)

// One function's walk: the registry and the summaries so far, the function whose body is walked
// (its nested shape resolves the inner self-call), and the nesting of over-applications inlined.
type ReachContext =
    | registry: ReachRegistry
    | table: ReachTable
    | current: ReachFunction
    | overDepth: Int

let maxOverApplicationDepth = 4

// A reach with the next synthetic token: every let and pattern binding takes a fresh identity
// token so a binding used twice sums to a shared reach.
type ReachStep =
    | reach: ResultReachState
    | token: Int

let stepOf (reach: ResultReachState) (token: Int) = ReachStep(reach = reach, token = token)

let tokenReach (token: Int) = reachOfToken("#" + Ashes.Text.fromInt(token))

let recursive lookupEnv (name: Str) (env: List((Str, ResultReachState))) =
    match env with
        | [] -> None
        | (candidate, reach) :: rest ->
            if candidate == name
            then Some(reach)
            else lookupEnv(name)(rest)

let constructorsOf (context: ReachContext) =
    match context with
        | ReachContext { registry = ReachRegistry { constructors = constructors } } -> constructors

let functionsOf (context: ReachContext) =
    match context with
        | ReachContext { registry = ReachRegistry { functions = functions } } -> functions

let lookupFunctionIn (context: ReachContext) (key: Str) =
    match context with
        | ReachContext { registry = ReachRegistry { byKey = byKey } } -> Ashes.Collection.Map.getStr(key)(byKey)

let functionByIdentityIn (context: ReachContext) (name: Str) (identity: Int) =
    match context with
        | ReachContext { registry = ReachRegistry { byIdentity = byIdentity } } -> Ashes.Collection.Map.getStr(name + "@" + Ashes.Text.fromInt(identity))(byIdentity)

let isCapitalized (name: Str) =
    Ashes.Text.length(name) > 0 && Ashes.Text.contains("ABCDEFGHIJKLMNOPQRSTUVWXYZ")(Ashes.Text.substring(name)(0)(1))

// A bare pattern name that is a data constructor matches a nullary tag and binds nothing.
let isConstructorName (context: ReachContext) (name: Str) =
    match context
    |> constructorsOf
    |> lookupConstructor(name) with
        | Some(_constructor) -> true
        | None -> isCapitalized(name)

// A bare name: a bound name's reach; a nullary constructor reaches nothing when it is the sole
// nullary of its type and poisons otherwise; a free name is a top-level or enclosing binding, or
// unknown, and poisons either way.
let variableReach (context: ReachContext) (env: List((Str, ResultReachState))) (name: Str) =
    match lookupEnv(name)(env) with
        | Some(reach) -> reach
        | None ->
            match context
            |> constructorsOf
            |> lookupConstructor(name) with
                | Some(ReachConstructor { arity = 0, soleNullary = true }) -> reachBottom(Unit)
                | Some(_constructor) -> reachPoisoned(UnmodelledReach)
                | None ->
                    match context with
                        | ReachContext { registry = ReachRegistry { valueNames = valueNames } } ->
                            if containsName(name)(valueNames)
                            then reachPoisoned(GlobalOrTopLevelReach)
                            else reachPoisoned(ConservativeUnknownReach)

let recursive extendSegments (segments: List(Str)) (reach: ResultReachState) =
    match segments with
        | [] -> reach
        | segment :: rest ->
            reach
            |> extendPathsNamed(segment)
            |> extendSegments(rest)

// A dotted name whose first segment is bound reads a field of that binding through the named
// sub-cell per segment; any other dotted name is a module member.
let qualifiedReach (env: List((Str, ResultReachState))) (qualifier: Str) (name: Str) =
    match Ashes.Text.split(qualifier)(".") with
        | first :: rest ->
            match lookupEnv(first)(env) with
                | Some(bound) ->
                    bound
                    |> extendSegments(rest)
                    |> extendPathsNamed(name)
                | None -> reachPoisoned(GlobalOrTopLevelReach)
        | [] -> reachPoisoned(GlobalOrTopLevelReach)

// The scope inside a binding's body: the name resolves to its registered function, or shadows one.
let bindingScopeOf (registry: ReachRegistry) (scope: ReachScope) (name: Str) (value: Expr) =
    match lambdaChainOf(value)([]) with
        | ([], _innerBody) -> removeScopeName(name)(scope)
        | _ ->
            match Ashes.Collection.Map.getStr(name + "@" + Ashes.Text.fromInt(lambdaIdentityOf(value)))(registry.byIdentity) with
                | Some(ReachFunction { key = key }) -> setScopeName(name)(key)(scope)
                | None -> removeScopeName(name)(scope)

let bindingScope (context: ReachContext) (scope: ReachScope) (name: Str) (value: Expr) = bindingScopeOf(context.registry)(scope)(name)(value)

let recursive callSpineOf (expr: Expr) (arguments: List(Expr)) =
    match expr with
        | ExprAt(_span, inner) -> callSpineOf(inner)(arguments)
        | ExprCall(function, argument, _isSugar, _layout) -> callSpineOf(function)(argument :: arguments)
        | root -> (root, arguments)

let recursive argumentAt (index: Int) (arguments: List(Expr)) =
    match arguments with
        | [] -> None
        | argument :: rest ->
            if index == 0
            then Some(argument)
            else argumentAt(index - 1)(rest)

let recursive indexOfName (name: Str) (names: List(Str)) (index: Int) =
    match names with
        | [] -> -1
        | candidate :: rest ->
            if candidate == name
            then index
            else indexOfName(name)(rest)(index + 1)

let recursive copyFieldAt (index: Int) (copyFields: List(Bool)) =
    match copyFields with
        | [] -> false
        | copy :: rest ->
            if index == 0
            then copy
            else copyFieldAt(index - 1)(rest)

let recursive markerList (index: Int) (count: Int) =
    if index >= count
    then []
    else "@" + Ashes.Text.fromInt(index) :: markerList(index + 1)(count)

let recursive markerEnv (parameters: List(Str)) (index: Int) =
    match parameters with
        | [] -> []
        | parameter :: rest -> (parameter, reachOfToken("@" + Ashes.Text.fromInt(index))) :: markerEnv(rest)(index + 1)

let selfRecursiveCall (context: ReachContext) (scope: ReachScope) (name: Str) (arguments: List(Expr)) =
    match context with
        | ReachContext { current = ReachFunction { nested = Some(ReachNestedShape { recursiveKey = recursiveKey }) } } -> lookupScope(name)(scope) == Some(recursiveKey) && length(arguments) == 1
        | _ -> false

let deeper (context: ReachContext) = context with overDepth = context.overDepth + 1

let recursive reachOf (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (expr: Expr) =
    match expr with
        | ExprAt(_span, inner) -> reachOf(context)(env)(scope)(token)(inner)
        | ExprInt(_value) ->
            stepOf(reachBottom(Unit))(token)
        | ExprBigInt(_value) ->
            stepOf(reachBottom(Unit))(token)
        | ExprUInt(_value, _width, _text) ->
            stepOf(reachBottom(Unit))(token)
        | ExprFloat(_value, _text) ->
            stepOf(reachBottom(Unit))(token)
        | ExprString(_value) ->
            stepOf(reachBottom(Unit))(token)
        | ExprRune(_value) ->
            stepOf(reachBottom(Unit))(token)
        | ExprBool(_value) ->
            stepOf(reachBottom(Unit))(token)
        | ExprAdd(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprSubtract(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprMultiply(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprDivide(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprModulo(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprBitwiseAnd(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprBitwiseOr(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprBitwiseXor(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprShiftLeft(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprShiftRight(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprBitwiseNot(_operand) ->
            stepOf(reachBottom(Unit))(token)
        | ExprLogicalNot(_operand) ->
            stepOf(reachBottom(Unit))(token)
        | ExprLogicalAnd(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprLogicalOr(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprGreaterThan(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprLessThan(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprGreaterOrEqual(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprLessOrEqual(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprEqual(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprNotEqual(_left, _right) ->
            stepOf(reachBottom(Unit))(token)
        | ExprVar(name) ->
            stepOf(variableReach(context)(env)(name))(token)
        | ExprQualifiedVar(qualifier, name) ->
            stepOf(qualifiedReach(env)(qualifier)(name))(token)
        | ExprIf(_condition, thenBranch, elseBranch) -> joinReach(context)(env)(scope)(token)(thenBranch)(elseBranch)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) -> letReach(context)(env)(scope)(token)(name)(value)(body)
        | ExprLetResult(name, value, body) -> letReach(context)(env)(scope)(token)(name)(value)(body)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            reachOf(context)((name, reachPoisoned(UnmodelledReach)) :: env)(bindingScope(context)(scope)(name)(value))(token)(body)
        | ExprCall(_function, _argument, _isSugar, _layout) -> callReach(context)(env)(scope)(token)(expr)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            match reachOf(context)(env)(scope)(token)(scrutinee) with
                | ReachStep { reach = scrutineeReach, token = afterScrutinee } -> armsReach(context)(env)(scope)(afterScrutinee)(scrutineeReach)(arms)(None)
        | ExprCons(head, tail) -> sumPair(context)(env)(scope)(token)(head)(tail)
        | ExprList(elements, _isMultiline) ->
            Unit
            |> reachBottom
            |> sumList(context)(env)(scope)(token)(elements)
        | ExprTuple(elements) ->
            Unit
            |> reachBottom
            |> sumList(context)(env)(scope)(token)(elements)
        | ExprRecord(typeName, fields, _isMultiline) ->
            Unit
            |> reachBottom
            |> sumFields(context)(env)(scope)(token)(context
            |> constructorsOf
            |> lookupConstructor(typeName))(fields)
        | ExprRecordUpdate(target, fields) ->
            match reachOf(context)(env)(scope)(token)(target) with
                | ReachStep { reach = targetReach, token = afterTarget } ->
                    targetReach
                    |> extendPathsComponent
                    |> sumFields(context)(env)(scope)(afterTarget)(None)(fields)
        | _ ->
            stepOf(reachPoisoned(UnmodelledReach))(token)
and joinReach (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (left: Expr) (right: Expr) =
    match reachOf(context)(env)(scope)(token)(left) with
        | ReachStep { reach = leftReach, token = afterLeft } ->
            match reachOf(context)(env)(scope)(afterLeft)(right) with
                | ReachStep { reach = rightReach, token = afterRight } ->
                    stepOf(reachJoin(leftReach)(rightReach))(afterRight)
and sumPair (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (left: Expr) (right: Expr) =
    match reachOf(context)(env)(scope)(token)(left) with
        | ReachStep { reach = leftReach, token = afterLeft } ->
            match reachOf(context)(env)(scope)(afterLeft)(right) with
                | ReachStep { reach = rightReach, token = afterRight } ->
                    stepOf(reachSum(leftReach)(rightReach))(afterRight)
and sumList (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (elements: List(Expr)) (acc: ResultReachState) =
    match elements with
        | [] -> stepOf(acc)(token)
        | element :: rest ->
            match reachOf(context)(env)(scope)(token)(element) with
                | ReachStep { reach = elementReach, token = next } ->
                    elementReach
                    |> reachSum(acc)
                    |> sumList(context)(env)(scope)(next)(rest)
// A record's fields sum, except the copy-typed ones its constructor declares inline.
and sumFields (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (constructor: Maybe(ReachConstructor)) (fields: List((Str, Expr))) (acc: ResultReachState) =
    match fields with
        | [] -> stepOf(acc)(token)
        | (field, value) :: rest ->
            if isCopyField(constructor)(field)
            then sumFields(context)(env)(scope)(token)(constructor)(rest)(acc)
            else
                match reachOf(context)(env)(scope)(token)(value) with
                    | ReachStep { reach = valueReach, token = next } ->
                        valueReach
                        |> reachSum(acc)
                        |> sumFields(context)(env)(scope)(next)(constructor)(rest)
and isCopyField (constructor: Maybe(ReachConstructor)) (field: Str) =
    match constructor with
        | Some(ReachConstructor { fieldNames = fieldNames, copyFields = copyFields }) ->
            copyFieldAt(indexOfName(field)(fieldNames)(0))(copyFields)
        | None -> false
// A binding's value reach plus a fresh identity token, so the name used twice reads as shared.
and letReach (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (name: Str) (value: Expr) (body: Expr) =
    match reachOf(context)(env)(scope)(token)(value) with
        | ReachStep { reach = valueReach, token = afterValue } ->
            reachOf(context)((name, afterValue
            |> tokenReach
            |> reachSum(valueReach)) :: env)(bindingScope(context)(scope)(name)(value))(afterValue + 1)(body)
// The arms of a match join; each arm's pattern binders take the scrutinee's reach by component.
and armsReach (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (scrutineeReach: ResultReachState) (arms: List((Pattern, Expr, Maybe(Expr)))) (acc: Maybe(ResultReachState)) =
    match arms with
        | [] ->
            match acc with
                | Some(reach) -> stepOf(reach)(token)
                | None ->
                    stepOf(reachPoisoned(ConservativeUnknownReach))(token)
        | (pattern, body, _guard) :: rest ->
            match bindPatternReach(context)(pattern)(scrutineeReach)(env)(token) with
                | (armEnv, afterPattern) ->
                    match reachOf(context)(armEnv)(removeScopeNames(patternBinders(pattern)([]))(scope))(afterPattern)(body) with
                        | ReachStep { reach = armReach, token = afterArm } ->
                            armReach
                            |> joinArm(acc)
                            |> Some
                            |> armsReach(context)(env)(scope)(afterArm)(scrutineeReach)(rest)
and joinArm (acc: Maybe(ResultReachState)) (armReach: ResultReachState) =
    match acc with
        | Some(reach) -> reachJoin(reach)(armReach)
        | None -> armReach
// Every binder of a pattern bound to its disjoint sub-cell of the scrutinee's reach, each with a
// fresh identity token; a nullary-constructor name binds nothing.
and bindPatternReach (context: ReachContext) (pattern: Pattern) (parent: ResultReachState) (env: List((Str, ResultReachState))) (token: Int) =
    match pattern with
        | PatternAt(_span, inner) -> bindPatternReach(context)(inner)(parent)(env)(token)
        | PatternVar(name) ->
            if isConstructorName(context)(name)
            then (env, token)
            else
                ((name, token
                |> tokenReach
                |> reachSum(parent)) :: env, token + 1)
        | PatternConstructor(_constructor, patterns) -> bindPatternList(context)(patterns)(0)(parent)(env)(token)
        | PatternTuple(elements) -> bindPatternList(context)(elements)(0)(parent)(env)(token)
        | PatternCons(head, tail) ->
            match bindPatternReach(context)(head)(extendPaths(0)(parent))(env)(token) with
                | (headEnv, afterHead) ->
                    bindPatternReach(context)(tail)(extendPaths(1)(parent))(headEnv)(afterHead)
        | PatternRecord(typeName, fields) ->
            match context
            |> constructorsOf
            |> lookupConstructor(typeName) with
                | Some(ReachConstructor { fieldNames = fieldNames }) -> bindRecordPatterns(context)(fieldNames)(fields)(parent)(env)(token)
                | None -> (env, token)
        | PatternAs(inner, name) ->
            bindPatternReach(context)(inner)(parent)((name, token
            |> tokenReach
            |> reachSum(parent)) :: env)(token + 1)
        | PatternOr(first :: _rest) -> bindPatternReach(context)(first)(parent)(env)(token)
        | _ -> (env, token)
and bindPatternList (context: ReachContext) (patterns: List(Pattern)) (index: Int) (parent: ResultReachState) (env: List((Str, ResultReachState))) (token: Int) =
    match patterns with
        | [] -> (env, token)
        | pattern :: rest ->
            match bindPatternReach(context)(pattern)(extendPaths(index)(parent))(env)(token) with
                | (nextEnv, nextToken) -> bindPatternList(context)(rest)(index + 1)(parent)(nextEnv)(nextToken)
and bindRecordPatterns (context: ReachContext) (fieldNames: List(Str)) (fields: List((Str, Pattern))) (parent: ResultReachState) (env: List((Str, ResultReachState))) (token: Int) =
    match fields with
        | [] -> (env, token)
        | (field, pattern) :: rest ->
            match bindPatternReach(context)(pattern)(extendPaths(indexOfName(field)(fieldNames)(0))(parent))(env)(token) with
                | (nextEnv, nextToken) -> bindRecordPatterns(context)(fieldNames)(rest)(parent)(nextEnv)(nextToken)
// A call: the enclosing function's own inner self-call, a saturated constructor over its heap
// fields, or a function in scope, over-applied past its parameters or applied exactly; anything
// else is not modelled and poisons.
and callReach (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (expr: Expr) =
    match callSpineOf(expr)([]) with
        | (ExprVar(name), arguments) ->
            if selfRecursiveCall(context)(scope)(name)(arguments)
            then selfRecursiveReach(context)(env)(scope)(token)(arguments)
            else
                match context
                |> constructorsOf
                |> lookupConstructor(name) with
                    | Some(constructor) -> constructorReach(context)(env)(scope)(token)(constructor)(arguments)
                    | None ->
                        match lookupScope(name)(scope) with
                            | Some(key) ->
                                match lookupFunctionIn(context)(key) with
                                    | Some(function) ->
                                        if length(arguments) > length(function.parameters)
                                        then overAppliedReach(context)(env)(scope)(token)(function)(arguments)
                                        else registeredReach(context)(env)(scope)(token)(function)(arguments)
                                    | None ->
                                        stepOf(reachPoisoned(UnmodelledReach))(token)
                            | None ->
                                stepOf(reachPoisoned(UnmodelledReach))(token)
        | (ExprQualifiedVar(moduleName, memberName), arguments) ->
            if arguments
            |> length
            |> freshRcBuiltinCall(moduleName)(memberName)
            then
                stepOf(reachBottom(Unit))(token)
            else
                stepOf(reachPoisoned(UnmodelledReach))(token)
        | _ ->
            stepOf(reachPoisoned(UnmodelledReach))(token)
and constructorReach (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (constructor: ReachConstructor) (arguments: List(Expr)) =
    match constructor with
        | ReachConstructor { arity = arity, copyFields = copyFields } ->
            if length(arguments) != arity
            then
                stepOf(reachPoisoned(UnmodelledReach))(token)
            else
                Unit
                |> reachBottom
                |> sumConstructorFields(context)(env)(scope)(token)(copyFields)(arguments)
and sumConstructorFields (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (copyFields: List(Bool)) (arguments: List(Expr)) (acc: ResultReachState) =
    match arguments with
        | [] -> stepOf(acc)(token)
        | argument :: rest ->
            let copy =
                match copyFields with
                    | first :: _more -> first
                    | [] -> false
            in
                let remaining =
                    match copyFields with
                        | _first :: more -> more
                        | [] -> []
                in
                    if copy
                    then sumConstructorFields(context)(env)(scope)(token)(remaining)(rest)(acc)
                    else
                        match reachOf(context)(env)(scope)(token)(argument) with
                            | ReachStep { reach = argumentReach, token = next } ->
                                argumentReach
                                |> reachSum(acc)
                                |> sumConstructorFields(context)(env)(scope)(next)(remaining)(rest)
// A saturated call to a registered function substitutes, for every parameter its summary reaches,
// the argument's reach (through an unspecified component when the callee keeps only parts of the
// parameter), scaled by the multiplicity, under the callee's causes; an under-application poisons.
and registeredReach (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (function: ReachFunction) (arguments: List(Expr)) =
    match function with
        | ReachFunction { key = key, parameters = parameters } ->
            if length(arguments) != length(parameters)
            then
                stepOf(reachPoisoned(UnmodelledReach))(token)
            else
                match lookupTable(key)(context.table) with
                    | ResultReachState { counts = counts, causes = causes } ->
                        causes
                        |> withCounts([])
                        |> substituteEntries(context)(env)(scope)(token)(parameters)(arguments)(counts)
and substituteEntries (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (parameters: List(Str)) (arguments: List(Expr)) (entries: List(ParameterReachEntry)) (acc: ResultReachState) =
    match entries with
        | [] -> stepOf(acc)(token)
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            match argumentAt(indexOfName(rootOf(name))(parameters)(0))(arguments) with
                | None ->
                    stepOf(reachPoisoned(ConservativeUnknownReach))(token)
                | Some(argument) ->
                    match reachOf(context)(env)(scope)(token)(argument) with
                        | ReachStep { reach = argumentReach, token = next } ->
                            count
                            |> reachScale(placedReach(name)(argumentReach))
                            |> reachSum(acc)
                            |> substituteEntries(context)(env)(scope)(next)(parameters)(arguments)(rest)
and placedReach (name: Str) (argumentReach: ResultReachState) =
    if isWholeName(name)
    then argumentReach
    else extendPathsComponent(argumentReach)
// The inner self-call of the nested shape: the enclosing function applied to the same outer
// parameters with the accumulator set to the argument, against its own growing summary.
and selfRecursiveReach (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (arguments: List(Expr)) =
    match context with
        | ReachContext { current = ReachFunction { key = key, nested = Some(ReachNestedShape { accumulator = accumulator }) }, table = table } ->
            match lookupTable(key)(table) with
                | ResultReachState { counts = counts, causes = causes } ->
                    causes
                    |> withCounts([])
                    |> substituteSelfEntries(context)(env)(scope)(token)(accumulator)(arguments)(counts)
        | _ ->
            stepOf(reachPoisoned(ConservativeUnknownReach))(token)
and substituteSelfEntries (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (accumulator: Str) (arguments: List(Expr)) (entries: List(ParameterReachEntry)) (acc: ResultReachState) =
    match entries with
        | [] -> stepOf(acc)(token)
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            if rootOf(name) == accumulator
            then
                match argumentAt(0)(arguments) with
                    | None ->
                        stepOf(reachPoisoned(ConservativeUnknownReach))(token)
                    | Some(argument) ->
                        match reachOf(context)(env)(scope)(token)(argument) with
                            | ReachStep { reach = argumentReach, token = next } ->
                                count
                                |> reachScale(placedReach(name)(argumentReach))
                                |> reachSum(acc)
                                |> substituteSelfEntries(context)(env)(scope)(next)(accumulator)(arguments)(rest)
            else
                match lookupEnv(rootOf(name))(env) with
                    | Some(outerReach) ->
                        count
                        |> reachScale(placedReach(name)(outerReach))
                        |> reachSum(acc)
                        |> substituteSelfEntries(context)(env)(scope)(token)(accumulator)(arguments)(rest)
                    | None ->
                        stepOf(reachPoisoned(ConservativeUnknownReach))(token)
// An over-application inlines the callee's body one level, binding each surplus argument to the
// parameter of the lambda the body returns; the symbolic reach over argument-position markers is
// then substituted with the arguments' reaches.
and overAppliedReach (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (function: ReachFunction) (arguments: List(Expr)) =
    if context.overDepth >= maxOverApplicationDepth
    then
        stepOf(reachPoisoned(ConservativeUnknownReach))(token)
    else
        match function with
            | ReachFunction { parameters = parameters, body = body, scope = functionScope } ->
                let markers =
                    arguments
                    |> length
                    |> markerList(length(parameters))
                in
                    match overApplyReach(deeper(context))(body)(markers)(0)(markerEnv(parameters)(0))(functionScope)(token) with
                        | ReachStep { reach = symbolic, token = afterInline } ->
                            match stripSyntheticTokens(symbolic) with
                                | ResultReachState { counts = counts, causes = causes } ->
                                    causes
                                    |> withCounts([])
                                    |> substituteMarkers(context)(env)(scope)(afterInline)(arguments
                                    |> length
                                    |> markerList(0))(arguments)(counts)
and substituteMarkers (context: ReachContext) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (markers: List(Str)) (arguments: List(Expr)) (entries: List(ParameterReachEntry)) (acc: ResultReachState) =
    match entries with
        | [] -> stepOf(acc)(token)
        | ParameterReachEntry { parameterName = name, reachCount = count } :: rest ->
            match argumentAt(indexOfName(rootOf(name))(markers)(0))(arguments) with
                | None ->
                    stepOf(reachPoisoned(ConservativeUnknownReach))(token)
                | Some(argument) ->
                    match reachOf(context)(env)(scope)(token)(argument) with
                        | ReachStep { reach = argumentReach, token = next } ->
                            count
                            |> reachScale(argumentReach)
                            |> reachSum(acc)
                            |> substituteMarkers(context)(env)(scope)(next)(markers)(arguments)(rest)
and overApplyReach (context: ReachContext) (body: Expr) (markers: List(Str)) (index: Int) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) =
    match argumentMarker(index)(markers) with
        | None -> reachOf(context)(env)(scope)(token)(body)
        | Some(marker) ->
            match body with
                | ExprAt(_span, inner) -> overApplyReach(context)(inner)(markers)(index)(env)(scope)(token)
                | ExprLambda(parameter, lambdaBody, _annotation) ->
                    overApplyReach(context)(lambdaBody)(markers)(index + 1)((parameter, reachOfToken(marker)) :: env)(removeScopeNames([parameter])(scope))(token)
                | ExprIf(_condition, thenBranch, elseBranch) ->
                    match overApplyReach(context)(thenBranch)(markers)(index)(env)(scope)(token) with
                        | ReachStep { reach = thenReach, token = afterThen } ->
                            match overApplyReach(context)(elseBranch)(markers)(index)(env)(scope)(afterThen) with
                                | ReachStep { reach = elseReach, token = afterElse } ->
                                    stepOf(reachJoin(thenReach)(elseReach))(afterElse)
                | ExprMatch(scrutinee, arms, _defaultArm) ->
                    match reachOf(context)(env)(scope)(token)(scrutinee) with
                        | ReachStep { reach = scrutineeReach, token = afterScrutinee } -> overApplyArms(context)(markers)(index)(env)(scope)(afterScrutinee)(scrutineeReach)(arms)(None)
                | ExprLet(name, value, letBody, _parameters, _annotation, _requirements) -> overApplyLet(context)(markers)(index)(env)(scope)(token)(name)(value)(letBody)
                | ExprLetResult(name, value, letBody) -> overApplyLet(context)(markers)(index)(env)(scope)(token)(name)(value)(letBody)
                | ExprLetRecursive(name, value, letBody, _parameters, _annotation, _requirements) ->
                    overApplyReach(context)(letBody)(markers)(index)((name, reachPoisoned(UnmodelledReach)) :: env)(bindingScope(context)(scope)(name)(value))(token)
                | _ ->
                    stepOf(reachPoisoned(UnmodelledReach))(token)
and argumentMarker (index: Int) (markers: List(Str)) =
    match markers with
        | [] -> None
        | marker :: rest ->
            if index == 0
            then Some(marker)
            else argumentMarker(index - 1)(rest)
and overApplyLet (context: ReachContext) (markers: List(Str)) (index: Int) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (name: Str) (value: Expr) (letBody: Expr) =
    match reachOf(context)(env)(scope)(token)(value) with
        | ReachStep { reach = valueReach, token = afterValue } ->
            overApplyReach(context)(letBody)(markers)(index)((name, afterValue
            |> tokenReach
            |> reachSum(valueReach)) :: env)(bindingScope(context)(scope)(name)(value))(afterValue + 1)
and overApplyArms (context: ReachContext) (markers: List(Str)) (index: Int) (env: List((Str, ResultReachState))) (scope: ReachScope) (token: Int) (scrutineeReach: ResultReachState) (arms: List((Pattern, Expr, Maybe(Expr)))) (acc: Maybe(ResultReachState)) =
    match arms with
        | [] ->
            match acc with
                | Some(reach) -> stepOf(reach)(token)
                | None ->
                    stepOf(reachPoisoned(ConservativeUnknownReach))(token)
        | (pattern, body, _guard) :: rest ->
            match bindPatternReach(context)(pattern)(scrutineeReach)(env)(token) with
                | (armEnv, afterPattern) ->
                    match overApplyReach(context)(body)(markers)(index)(armEnv)(removeScopeNames(patternBinders(pattern)([]))(scope))(afterPattern) with
                        | ReachStep { reach = armReach, token = afterArm } ->
                            armReach
                            |> joinArm(acc)
                            |> Some
                            |> overApplyArms(context)(markers)(index)(env)(scope)(afterArm)(scrutineeReach)(rest)

let recursive parameterEnv (parameters: List(Str)) =
    match parameters with
        | [] -> []
        | parameter :: rest -> (parameter, reachParam(parameter)) :: parameterEnv(rest)

// One function's stored summary under the summaries so far.
let functionReach (registry: ReachRegistry) (table: ReachTable) (function: ReachFunction) =
    match function with
        | ReachFunction { parameters = parameters, body = body, scope = scope } ->
            match reachOf(ReachContext(registry = registry, table = table, current = function, overDepth = 0))(parameterEnv(parameters))(scope)(0)(body) with
                | ReachStep { reach = reach } -> stripSyntheticTokens(reach)

// The keys a body can consult the summary of: every name it resolves through its scope to a
// registered function, whether called or referred to, with nested bindings, lambda parameters,
// and pattern binders extending or shadowing the scope the way the walk does. A superset of
// what the walk consults, so a summary's change re-summarizes at least the functions stage 0's
// dependents queue would.
let recursive dependencyKeys (registry: ReachRegistry) (scope: ReachScope) (expr: Expr) (keys: MapTree(Str, Bool)) =
    match expr with
        | ExprAt(_span, inner) -> dependencyKeys(registry)(scope)(inner)(keys)
        | ExprInt(_value) -> keys
        | ExprBigInt(_value) -> keys
        | ExprUInt(_value, _width, _text) -> keys
        | ExprFloat(_value, _text) -> keys
        | ExprString(_value) -> keys
        | ExprRune(_value) -> keys
        | ExprBool(_value) -> keys
        | ExprVar(name) ->
            match lookupScope(name)(scope) with
                | Some(key) -> Ashes.Collection.Map.setStr(key)(true)(keys)
                | None -> keys
        | ExprQualifiedVar(_moduleName, _memberName) -> keys
        | ExprAdd(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprSubtract(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprMultiply(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprDivide(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprModulo(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprBitwiseAnd(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprBitwiseOr(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprBitwiseXor(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprShiftLeft(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprShiftRight(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprBitwiseNot(operand) -> dependencyKeys(registry)(scope)(operand)(keys)
        | ExprLogicalNot(operand) -> dependencyKeys(registry)(scope)(operand)(keys)
        | ExprLogicalAnd(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprLogicalOr(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprGreaterThan(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprLessThan(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprGreaterOrEqual(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprLessOrEqual(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprEqual(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprNotEqual(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprResultPipe(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprResultMapErrorPipe(left, right) -> pairDependencyKeys(registry)(scope)(left)(right)(keys)
        | ExprLet(name, value, body, _parameters, _annotation, _requirements) ->
            keys
            |> dependencyKeys(registry)(scope)(value)
            |> dependencyKeys(registry)(bindingScopeOf(registry)(scope)(name)(value))(body)
        | ExprLetResult(name, value, body) ->
            keys
            |> dependencyKeys(registry)(scope)(value)
            |> dependencyKeys(registry)(bindingScopeOf(registry)(scope)(name)(value))(body)
        | ExprLetRecursive(name, value, body, _parameters, _annotation, _requirements) ->
            let recursiveScope = bindingScopeOf(registry)(scope)(name)(value)
            in
                keys
                |> dependencyKeys(registry)(recursiveScope)(value)
                |> dependencyKeys(registry)(recursiveScope)(body)
        | ExprIf(condition, thenBranch, elseBranch) ->
            keys
            |> dependencyKeys(registry)(scope)(condition)
            |> pairDependencyKeys(registry)(scope)(thenBranch)(elseBranch)
        | ExprLambda(parameter, body, _annotation) ->
            dependencyKeys(registry)(removeScopeNames([parameter])(scope))(body)(keys)
        | ExprCall(function, argument, _isSugar, _layout) -> pairDependencyKeys(registry)(scope)(function)(argument)(keys)
        | ExprTuple(elements) -> listDependencyKeys(registry)(scope)(elements)(keys)
        | ExprList(elements, _isMultiline) -> listDependencyKeys(registry)(scope)(elements)(keys)
        | ExprCons(head, tail) -> pairDependencyKeys(registry)(scope)(head)(tail)(keys)
        | ExprMatch(scrutinee, arms, _defaultArm) ->
            keys
            |> dependencyKeys(registry)(scope)(scrutinee)
            |> armDependencyKeys(registry)(scope)(arms)
        | ExprAwait(inner) -> dependencyKeys(registry)(scope)(inner)(keys)
        | ExprRecord(_typeName, fields, _isMultiline) -> fieldDependencyKeys(registry)(scope)(fields)(keys)
        | ExprRecordUpdate(record, fields) ->
            keys
            |> dependencyKeys(registry)(scope)(record)
            |> fieldDependencyKeys(registry)(scope)(fields)
        | ExprPerform(inner) -> dependencyKeys(registry)(scope)(inner)(keys)
        | ExprHandle(inner, arms) ->
            keys
            |> dependencyKeys(registry)(scope)(inner)
            |> handleArmDependencyKeys(registry)(scope)(arms)
and pairDependencyKeys (registry: ReachRegistry) (scope: ReachScope) (left: Expr) (right: Expr) (keys: MapTree(Str, Bool)) =
    keys
    |> dependencyKeys(registry)(scope)(left)
    |> dependencyKeys(registry)(scope)(right)
and listDependencyKeys (registry: ReachRegistry) (scope: ReachScope) (expressions: List(Expr)) (keys: MapTree(Str, Bool)) =
    match expressions with
        | [] -> keys
        | expression :: rest ->
            keys
            |> dependencyKeys(registry)(scope)(expression)
            |> listDependencyKeys(registry)(scope)(rest)
and fieldDependencyKeys (registry: ReachRegistry) (scope: ReachScope) (fields: List((Str, Expr))) (keys: MapTree(Str, Bool)) =
    match fields with
        | [] -> keys
        | (_field, expression) :: rest ->
            keys
            |> dependencyKeys(registry)(scope)(expression)
            |> fieldDependencyKeys(registry)(scope)(rest)
and armDependencyKeys (registry: ReachRegistry) (scope: ReachScope) (arms: List((Pattern, Expr, Maybe(Expr)))) (keys: MapTree(Str, Bool)) =
    match arms with
        | [] -> keys
        | (pattern, body, guard) :: rest ->
            let armScope =
                removeScopeNames(patternBinders(pattern)([]))(scope)
            in
                let withGuard =
                    match guard with
                        | Some(guardExpr) -> dependencyKeys(registry)(armScope)(guardExpr)(keys)
                        | None -> keys
                in
                    withGuard
                    |> dependencyKeys(registry)(armScope)(body)
                    |> armDependencyKeys(registry)(scope)(rest)
and handleArmDependencyKeys (registry: ReachRegistry) (scope: ReachScope) (arms: List((Maybe(Str), Str, List(Pattern), Expr))) (keys: MapTree(Str, Bool)) =
    match arms with
        | [] -> keys
        | (resumeName, _operation, patterns, body) :: rest ->
            let boundNames =
                match resumeName with
                    | Some(name) -> name :: patternsBinders(patterns)([])
                    | None -> patternsBinders(patterns)([])
            in
                keys
                |> dependencyKeys(registry)(removeScopeNames(boundNames)(scope))(body)
                |> handleArmDependencyKeys(registry)(scope)(rest)

// The functions to re-summarize when a function's summary changes: every function whose body
// depends on it, stage 0's result-reach dependents.
let recursive addDependents (function: Str) (dependencies: List((Str, Bool))) (dependents: MapTree(Str, List(Str))) =
    match dependencies with
        | [] -> dependents
        | (dependency, _present) :: rest ->
            dependents
            |> Ashes.Collection.Map.upsertStr(dependency)([function])(given (existing: List(Str)) -> function :: existing)
            |> addDependents(function)(rest)

let recursive dependentsOf (registry: ReachRegistry) (functions: List(ReachFunction)) (dependents: MapTree(Str, List(Str))) =
    match functions with
        | [] -> dependents
        | ReachFunction { key = key, body = body, scope = scope } :: rest ->
            dependents
            |> addDependents(key)(Ashes.Collection.Map.empty
            |> dependencyKeys(registry)(scope)(body)
            |> Ashes.Collection.Map.toList)
            |> dependentsOf(registry)(rest)

let recursive enqueueDependents (pending: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) =
    match pending with
        | [] -> (back, queued)
        | key :: rest ->
            match Ashes.Collection.Map.getStr(key)(queued) with
                | Some(true) -> enqueueDependents(rest)(back)(queued)
                | _ ->
                    queued
                    |> Ashes.Collection.Map.setStr(key)(true)
                    |> enqueueDependents(rest)(key :: back)

// Stage 0's `ComputeResultReach` worklist: every function starts queued at bottom; a function
// taken off the queue is re-summarized under the summaries so far, and a summary that grew
// re-queues the functions that depend on it. The queue is two lists, taken from the front and
// added to the back, so the order is stage 0's first-in first-out. The fuel bounds the total
// work at the sweeps-times-functions the sweep-until-stable form spent at most.
let recursive solveReach (registry: ReachRegistry) (dependents: MapTree(Str, List(Str))) (table: ReachTable) (front: List(Str)) (back: List(Str)) (queued: MapTree(Str, Bool)) (fuel: Int) =
    match front with
        | [] ->
            match back with
                | [] -> table
                | _ ->
                    solveReach(registry)(dependents)(table)(reverse(back))([])(queued)(fuel)
        | key :: rest ->
            if fuel <= 0
            then table
            else
                match Ashes.Collection.Map.getStr(key)(registry.byKey) with
                    | None ->
                        solveReach(registry)(dependents)(table)(rest)(back)(Ashes.Collection.Map.setStr(key)(false)(queued))(fuel - 1)
                    | Some(function) ->
                        let dequeued = Ashes.Collection.Map.setStr(key)(false)(queued)
                        in
                            let previous = lookupTable(key)(table)
                            in
                                let merged =
                                    function
                                    |> functionReach(registry)(table)
                                    |> reachJoin(previous)
                                in
                                    if reachEquals(previous)(merged)
                                    then solveReach(registry)(dependents)(table)(rest)(back)(dequeued)(fuel - 1)
                                    else
                                        let waiting =
                                            match Ashes.Collection.Map.getStr(key)(dependents) with
                                                | Some(callers) -> callers
                                                | None -> []
                                        in
                                            match enqueueDependents(waiting)(back)(dequeued) with
                                                | (nextBack, nextQueued) ->
                                                    solveReach(registry)(dependents)(setTable(key)(merged)(table))(rest)(nextBack)(nextQueued)(fuel - 1)

let recursive functionKeys (functions: List(ReachFunction)) (keys: List(Str)) =
    match functions with
        | [] -> reverse(keys)
        | ReachFunction { key = key } :: rest -> functionKeys(rest)(key :: keys)

let recursive allQueued (keys: List(Str)) (queued: MapTree(Str, Bool)) =
    match keys with
        | [] -> queued
        | key :: rest ->
            queued
            |> Ashes.Collection.Map.setStr(key)(true)
            |> allQueued(rest)

let sweepUntilStable (registry: ReachRegistry) (table: ReachTable) (fuel: Int) =
    (let keys = functionKeys(registry.functions)([])
    in
        solveReach(registry)(dependentsOf(registry)(registry.functions)(Ashes.Collection.Map.empty))(table)(keys)([])(allQueued(keys)(Ashes.Collection.Map.empty))(fuel * length(registry.functions)))

let recursive initialTableInto (functions: List(ReachFunction)) (table: ReachTable) =
    match functions with
        | [] -> table
        | ReachFunction { key = key } :: rest ->
            table
            |> setTable(key)(reachBottom(Unit))
            |> initialTableInto(rest)

let initialTable (functions: List(ReachFunction)) = initialTableInto(functions)(Ashes.Collection.Map.empty)

let summaryOf (table: ReachTable) (function: ReachFunction) = ReachSummary(function = function, reach = lookupTable(function.key)(table))

// The least fixpoint over every registered function: each starts at bottom, and every sweep
// recomputes each body under the summaries so far, joining the growth in, until a sweep changes
// nothing.
let computeReachSummaries (registry: ReachRegistry) =
    map(256
    |> sweepUntilStable(registry)(initialTable(registry.functions))
    |> summaryOf)(registry.functions)

let programReachSummaries (program: ProgramSyntax) =
    program
    |> buildReachRegistry
    |> computeReachSummaries

// The summaries of the functions a bare expression binds, for a lowering without a program.
let expressionReachSummaries (expr: Expr) =
    match registerExpr(expr)(None)(emptyReachScope)(emptyRegistration) with
        | ReachRegistration { functions = functions, valueNames = valueNames } ->
            valueNames
            |> indexedRegistry(reverse(functions))(builtinReachConstructors)
            |> computeReachSummaries

let recursive lookupReachSummary (key: Str) (summaries: List(ReachSummary)) =
    match summaries with
        | [] -> None
        | (ReachSummary { function = ReachFunction { key = candidate } } as summary) :: rest ->
            if candidate == key
            then Some(summary)
            else lookupReachSummary(key)(rest)

// The summary of the function a binding of `name` declared with the given lambda identity.
let recursive reachSummaryFor (name: Str) (identity: Int) (summaries: List(ReachSummary)) =
    match summaries with
        | [] -> None
        | (ReachSummary { function = ReachFunction { name = candidate, identity = candidateIdentity } } as summary) :: rest ->
            if candidate == name && candidateIdentity == identity
            then Some(summary)
            else reachSummaryFor(name)(identity)(rest)

// The first summary of a function named `name`, preferring a top-level one.
let recursive reachSummaryNamed (name: Str) (summaries: List(ReachSummary)) =
    match topLevelSummaryNamed(name)(summaries) with
        | Some(summary) -> Some(summary)
        | None -> anySummaryNamed(name)(summaries)
and topLevelSummaryNamed (name: Str) (summaries: List(ReachSummary)) =
    match summaries with
        | [] -> None
        | (ReachSummary { function = ReachFunction { name = candidate, enclosing = None } } as summary) :: rest ->
            if candidate == name
            then Some(summary)
            else topLevelSummaryNamed(name)(rest)
        | _ :: rest -> topLevelSummaryNamed(name)(rest)
and anySummaryNamed (name: Str) (summaries: List(ReachSummary)) =
    match summaries with
        | [] -> None
        | (ReachSummary { function = ReachFunction { name = candidate } } as summary) :: rest ->
            if candidate == name
            then Some(summary)
            else anySummaryNamed(name)(rest)

// A function's summary on its own: no other function in scope, only the builtin constructors.
let singleFunctionReach (parameters: List(Str)) (body: Expr) =
    (let function =
        ReachFunction(
            key = "@",
            name = "",
            enclosing = None,
            identity = -1,
            parameters = parameters,
            body = body,
            nested = None,
            scope = emptyReachScope
        )
    in
        Ashes.Collection.Map.empty
        |> indexedRegistry([function])(builtinReachConstructors)
        |> computeReachSummaries
        |> (given (summaries) ->
            match summaries with
                | ReachSummary { reach = reach } :: _rest -> reach
                | [] -> reachBottom(Unit)))

let recursive wholeNames (entries: List(ParameterReachEntry)) =
    match entries with
        | [] -> []
        | ParameterReachEntry { parameterName = name } :: rest ->
            if isWholeName(name)
            then name :: wholeNames(rest)
            else wholeNames(rest)

// The stored summary as the ownership summary's reach facts.
let reachFactsOf (reach: ResultReachState) =
    match reach with
        | ResultReachState { counts = counts, causes = causes, isPoisoned = poisoned } ->
            FunctionResultReachFacts(
                parameterReach = counts,
                causes = causes,
                isPoisoned = poisoned,
                wholeParameterReach = wholeNames(counts)
            )
