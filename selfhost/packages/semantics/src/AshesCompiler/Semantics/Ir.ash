// Models the complete lowered program handed from semantics to backend phases.
//
// Invariants:
// - Entry and lifted functions own independent temp/local index spaces.
// - Local debug metadata is ordered by slot so serialization remains deterministic.
// - Function origins are reporting metadata and do not affect instruction semantics.
// - Trait evidence and external ABI metadata remain structural and inference-object free.

import AshesCompiler.Semantics.ExternalAbi
import AshesCompiler.Semantics.IrInstructions
import AshesCompiler.Semantics.IrOrigins
import AshesCompiler.Semantics.Types
export (
    type CoroutineInfo(..),
    type IrStringLiteral(..),
    type TraitDictionaryAbiAnnotation(..),
    type TraitResolutionAnnotation(..),
    type TraitEvidenceAnnotations(..),
    type IrFunction(..),
    type IrProgram(..),
    value emptyTraitEvidenceAnnotations,
)

type CoroutineInfo =
    | stateCount: Int
    | stateStructSize: Int
    | captureCount: Int
    deriving {Eq, Show}

type IrStringLiteral =
    | label: Str
    | value: Str
    deriving {Eq, Show}

type TraitDictionaryAbiAnnotation =
    | functionName: Str
    | functionSource: Str
    | functionOffset: Int
    | parameterIndex: Int
    | traitName: Str
    | methods: List(Str)
    | supertraits: List(Str)
    deriving {Eq, Show}

type TraitResolutionAnnotation =
    | requirement: Str
    | implementationModule: Str
    | implementationSource: Str
    | implementationOffset: Int
    deriving {Eq, Show}

type TraitEvidenceAnnotations =
    | dictionaryParameters: List(TraitDictionaryAbiAnnotation)
    | resolvedImplementations: List(TraitResolutionAnnotation)
    deriving {Eq, Show}

let emptyTraitEvidenceAnnotations =
    TraitEvidenceAnnotations(
        dictionaryParameters = [],
        resolvedImplementations = []
    )

type IrFunction =
    | label: Str
    | instructions: List(IrInstruction)
    | localCount: Int
    | tempCount: Int
    | hasEnvAndArgParams: Bool
    | coroutine: Maybe(CoroutineInfo)
    | localNames: List((IrLocal, Str))
    | localTypes: List((IrLocal, SemanticType))
    | origin: Maybe(IrFunctionOrigin)
    | lifetimesPlaced: Bool

type IrProgram =
    | entryFunction: IrFunction
    | functions: List(IrFunction)
    | stringLiterals: List(IrStringLiteral)
    | externalFunctions: List(ExternalFunctionAbi)
    | externalOpaqueTypes: List(Str)
    | usesPrintInt: Bool
    | usesPrintStr: Bool
    | usesPrintBool: Bool
    | usesConcatStr: Bool
    | usesClosures: Bool
    | usesAsync: Bool
    | capabilityHandlerGlobals: Int
    | traitEvidence: TraitEvidenceAnnotations
