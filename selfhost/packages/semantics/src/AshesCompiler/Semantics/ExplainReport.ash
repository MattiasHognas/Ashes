// Models what `--explain` asks for and the reportable view of a compilation built to answer it.
//
// Invariants:
// - A request deduplicates kinds; no kinds at all means no report is built or printed.
// - Report records carry structural enums and names, never formatted prose.
// - Building a report reads decisions and IR and changes neither.

import Ashes.Collection.List.append
import AshesCompiler.Semantics.DecisionSnapshot
import AshesCompiler.Semantics.HoverTypeInfo
import AshesCompiler.Semantics.Ir
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.OwnershipSummary
import AshesCompiler.Semantics.ReuseDecision
export (
    type ExplainKind(..),
    type ExplainRequest(..),
    type OwnershipParameterReport(..),
    type OwnershipFunctionReport(..),
    type RcFunctionReport(..),
    type ReuseFunctionReport(..),
    type RepresentationFunctionReport(..),
    type ConcurrencyFunctionReport(..),
    type CompilationExplainReport(..),
    value explainRequestNone,
    value explainRequestOf,
    value isExplainRequestEmpty,
    value explainRequestIncludes,
    value addExplainKind,
    value explainValidValues,
    value parseExplainValue,
    value rcReportTotal,
    value emptyExplainReport,
)

type ExplainKind =
    | ExplainOwnership
    | ExplainRc
    | ExplainReuse
    | ExplainTraits
    | ExplainAuthority
    | ExplainConcurrency
    | ExplainMemory
    deriving {Eq, Show}

// `functionFilter` restricts a report to matching functions; `None` reports everything.
type ExplainRequest =
    | kinds: List(ExplainKind)
    | functionFilter: Maybe(Str)
    deriving {Eq, Show}

type OwnershipParameterReport =
    | name: Str
    | ownership: ParameterOwnership
    | unique: Bool
    | moveSafe: Bool
    deriving {Eq, Show}

type OwnershipFunctionReport =
    | functionName: Str
    | origin: SourceFunctionOrigin
    | parameters: List(OwnershipParameterReport)
    | resultAliases: List(Str)
    | resultFresh: Bool
    | resultPoisoned: Bool
    | capturedValues: List(Str)
    deriving {Eq, Show}

type RcFunctionReport =
    | label: Str
    | origin: Maybe(IrFunctionOrigin)
    | dups: Int
    | drops: Int
    | uniquenessChecks: Int
    | allocations: Int
    | reusedAllocations: Int
    | reuseTokens: Int
    | copies: Int
    deriving {Eq, Show}

type ReuseFunctionReport =
    | functionName: Str
    | origin: IrFunctionOrigin
    | decision: ReuseDecisionKind
    | outcome: ReuseDecisionOutcome
    | reason: ReuseDecisionReason
    | candidate: Maybe(Str)
    | location: Maybe(IrSourceLocation)
    deriving {Eq, Show}

// `placements` counts values per category in first-seen order.
type RepresentationFunctionReport =
    | label: Str
    | origin: Maybe(IrFunctionOrigin)
    | placements: List((ValuePlacementCategory, Int))
    deriving {Eq, Show}

type ConcurrencyFunctionReport =
    | label: Str
    | origin: Maybe(IrFunctionOrigin)
    | scopes: Int
    | forks: Int
    | joins: Int
    | detachedSpawns: Int
    deriving {Eq, Show}

type CompilationExplainReport =
    | ownership: List(OwnershipFunctionReport)
    | rc: List(RcFunctionReport)
    | reuse: List(ReuseFunctionReport)
    | representation: List(RepresentationFunctionReport)
    | traitEvidence: TraitEvidenceAnnotations
    | externalResources: List(ExternalResourceOwnershipRecord)
    | authority: List(PublicAuthorityRecord)
    | externalAuthority: List(ExternalAuthorityRecord)
    | concurrency: List(ConcurrencyFunctionReport)
    deriving {Eq, Show}

let explainRequestNone = ExplainRequest(kinds = [], functionFilter = None)

let explainRequestOf (kinds: List(ExplainKind)) (functionFilter: Maybe(Str)) = ExplainRequest(kinds = kinds, functionFilter = functionFilter)

let isExplainRequestEmpty (request: ExplainRequest) =
    match request with
        | ExplainRequest { kinds = [] } -> true
        | _ -> false

let recursive containsKind (kinds: List(ExplainKind)) (kind: ExplainKind) =
    match kinds with
        | [] -> false
        | head :: rest ->
            if head == kind
            then true
            else containsKind(rest)(kind)

let explainRequestIncludes (kind: ExplainKind) (request: ExplainRequest) =
    match request with
        | ExplainRequest { kinds = kinds } -> containsKind(kinds)(kind)

// Repeats of a kind deduplicate, and a later selector replaces an earlier one so the last one
// written wins rather than two filters merging.
let addExplainKind (kind: ExplainKind) (selector: Maybe(Str)) (request: ExplainRequest) =
    match request with
        | ExplainRequest { kinds = kinds, functionFilter = existing } ->
            ExplainRequest(
                kinds = if containsKind(kinds)(kind)
                then kinds
                else append(kinds)([kind]),
                functionFilter = match selector with
                    | Some(_) -> selector
                    | None -> existing
            )

// The valid values, in report order, for help text and error messages.
let explainValidValues = ["ownership", "rc", "reuse", "traits", "authority", "concurrency", "memory"]

let explainKindNamed (name: Str) =
    match name with
        | "ownership" -> Some(ExplainOwnership)
        | "rc" -> Some(ExplainRc)
        | "reuse" -> Some(ExplainReuse)
        | "traits" -> Some(ExplainTraits)
        | "authority" -> Some(ExplainAuthority)
        | "concurrency" -> Some(ExplainConcurrency)
        | "memory" -> Some(ExplainMemory)
        | _ -> None

// An empty or blank selector after the colon means no filter.
let selectorOf (rest: Str) =
    if Ashes.Text.trim(rest) == ""
    then None
    else Some(rest)

let splitExplainValue (value: Str) =
    match Ashes.Text.indexOf(value)(":") with
        | -1 -> (value, None)
        | separator -> (Ashes.Text.substring(value)(0)(separator), selectorOf(Ashes.Text.substring(value)(separator + 1)(Ashes.Text.length(value) - separator - 1)))

// Parses one `--explain` value, in either the bare `ownership` or the filtered `ownership:Map.set`
// form, into its kind and optional selector; an unknown kind is an error naming it.
let parseExplainValue (value: Str) =
    match splitExplainValue(value) with
        | (kindText, filter) ->
            match explainKindNamed(Ashes.Text.asciiLower(Ashes.Text.trim(kindText))) with
                | Some(kind) -> Ok((kind, filter))
                | None -> Error("Unknown explain type '" + kindText + "'.")

let rcReportTotal (report: RcFunctionReport) =
    match report with
        | RcFunctionReport { dups = dups, drops = drops, uniquenessChecks = uniqueness, allocations = allocations, reusedAllocations = reused, reuseTokens = tokens, copies = copies } -> dups + drops + uniqueness + allocations + reused + tokens + copies

let emptyExplainReport =
    CompilationExplainReport(
        ownership = [],
        rc = [],
        reuse = [],
        representation = [],
        traitEvidence = emptyTraitEvidenceAnnotations,
        externalResources = [],
        authority = [],
        externalAuthority = [],
        concurrency = []
    )
