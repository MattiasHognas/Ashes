// Models stable source and compiler-generated lineage for lowered functions.
//
// Invariants:
// - Generated labels identify emitted functions but never replace source identity.
// - Source-derived helpers retain their source origin and immediate generated parent.
// - Shared helpers use a typed compiler owner and stable discriminator instead of label parsing.

export (
    type IrSourceLocation(..),
    type SourceFunctionOrigin(..),
    type IrFunctionOriginKind(..),
    type CompilerFunctionOwnerKind(..),
    type CompilerFunctionOwner(..),
    type IrFunctionOrigin(..),
)

type IrSourceLocation =
    | filePath: Str
    | line: Int
    | column: Int
    deriving {Eq, Show}

type SourceFunctionOrigin =
    | functionSourceName: Str
    | functionQualifiedName: Maybe(Str)
    | declarationLocation: Maybe(IrSourceLocation)
    | declarationOffset: Int
    deriving {Eq, Show}

type IrFunctionOriginKind =
    | ProgramEntryOrigin
    | SourceFunctionOriginKind
    | ClosureHelperOrigin
    | ReuseSpecializationOrigin
    | ParallelSpecializationOrigin
    | TraitOperatorSpecializationOrigin
    | MutualRecursionDispatchOrigin
    | MutualRecursionWrapperOrigin
    | CoroutineOrigin
    | CoroutineFrameDropperOrigin
    | ExternalThunkOrigin
    | RuntimeManagedAdtDropperOrigin
    | ResourceAdtDropperOrigin
    | ClosureEnvironmentNormalizerOrigin
    | RuntimeManagedClosureDropperOrigin
    | ResourceClosureDropperOrigin
    | AdtDeepCopierOrigin
    | ListDeepCopierOrigin
    | StructuralOwnerDropperOrigin
    deriving {Eq, Show}

type CompilerFunctionOwnerKind =
    | ProgramFunctionOwner
    | TypeFunctionOwner
    | ExternalFunctionOwner
    | RuntimeLayoutFunctionOwner
    | MutualRecursionGroupFunctionOwner
    deriving {Eq, Show}

type CompilerFunctionOwner =
    | ownerKind: CompilerFunctionOwnerKind
    | ownerName: Str
    deriving {Eq, Show}

type IrFunctionOrigin =
    | generatedLabel: Str
    | originKind: IrFunctionOriginKind
    | sourceOrigin: Maybe(SourceFunctionOrigin)
    | parentGeneratedLabel: Maybe(Str)
    | compilerOwner: Maybe(CompilerFunctionOwner)
    | stableDiscriminator: Maybe(Str)
    | generationLocation: Maybe(IrSourceLocation)
    deriving {Eq, Show}
