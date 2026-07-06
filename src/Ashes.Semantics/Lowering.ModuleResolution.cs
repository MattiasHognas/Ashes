using System.Diagnostics;
using Ashes.Frontend;

namespace Ashes.Semantics;

public sealed partial class Lowering
{

    private string ResolveModuleAlias(string moduleName)
    {
        return _moduleAliases.TryGetValue(moduleName, out var resolved) ? resolved : moduleName;
    }

    private (int, TypeRef) LowerQualifiedVar(Expr.QualifiedVar qv)
    {
        // A bare (uncalled) effect operation reference. Direct application is handled in
        // LowerCall; a first-class operation value eta-expands to a lambda performing the
        // operation, so the perform happens where the value is eventually applied. The expansion
        // needs the operation's arity, so an unsigned operation cannot be used as a value.
        if (_capabilitySymbols.TryGetValue(qv.Module, out var bareEffectSym))
        {
            if (!bareEffectSym.Operations.TryGetValue(qv.Name, out var bareOperation))
            {
                ReportDiagnostic(GetSpan(qv), $"Effect '{qv.Module}' has no operation '{qv.Name}'.", UnknownCapabilityCode);
                return ReturnNeverWithDummyTemp();
            }

            if (bareOperation.DeclaredSignature is null)
            {
                ReportDiagnostic(GetSpan(qv), $"Effect operation '{qv.Module}.{qv.Name}' needs an explicit signature to be used as a value.", UnknownCapabilityCode);
                return ReturnNeverWithDummyTemp();
            }

            return LowerExpr(BuildOperationEtaLambda(qv, CountArrows(bareOperation.DeclaredSignature)));
        }

        var resolvedModule = ResolveModuleAlias(qv.Module);

        if (BuiltinRegistry.TryGetModule(resolvedModule, out var builtinModule))
        {
            if (builtinModule.Members.ContainsKey(qv.Name))
            {
                var resolvedStdMember = ResolveBuiltinModuleMember(builtinModule, qv.Name);
                RecordHoverType(GetSpan(qv), $"{resolvedModule}.{qv.Name}", resolvedStdMember.Item2);
                return resolvedStdMember;
            }

            if (builtinModule.ResourceName is null)
            {
                return StdMemberNotFound(resolvedModule, qv.Name, GetSpan(qv));
            }
        }

        var sanitizedModuleName = ProjectSupport.SanitizeModuleBindingName(resolvedModule);
        var exportedBindingName = $"{sanitizedModuleName}_{qv.Name}";
        if (Lookup(exportedBindingName) is not null)
        {
            var resolvedQualifiedBinding = LowerVar(new Expr.Var(exportedBindingName));
            RecordHoverType(GetSpan(qv), $"{resolvedModule}.{qv.Name}", resolvedQualifiedBinding.Item2);
            return resolvedQualifiedBinding;
        }

        // User module: resolve to the sanitized module binding if it exists.
        var binding = Lookup(resolvedModule) ?? Lookup(sanitizedModuleName);
        if (binding is null)
        {
            ReportDiagnostic(GetSpan(qv), $"Unknown module '{qv.Module}'.");
            return ReturnNeverWithDummyTemp();
        }

        // Record field access fallback: `rec.fieldName` where `rec` is a bound record value.
        // Let-bindings create Binding.Scheme; lambda params create Binding.Local.
        int? recordFieldSlot = null;
        TypeRef? recordFieldType = null;
        if (binding is Binding.Local recordLocal)
        {
            recordFieldSlot = recordLocal.Slot;
            recordFieldType = Prune(recordLocal.Type);
        }
        else if (binding is Binding.Scheme recordScheme)
        {
            recordFieldSlot = recordScheme.Slot;
            recordFieldType = Prune(Instantiate(recordScheme.S));
        }

        if (recordFieldSlot.HasValue
            && recordFieldType is TypeRef.TNamedType namedRecordType
            && namedRecordType.Symbol.Constructors.Count == 1
            && namedRecordType.Symbol.Constructors[0].DeclaringSyntax.FieldNames.Count > 0)
        {
            var ctor = namedRecordType.Symbol.Constructors[0];
            var fieldNames = ctor.DeclaringSyntax.FieldNames;
            int fieldIdx = -1;
            for (int fi = 0; fi < fieldNames.Count; fi++)
            {
                if (string.Equals(fieldNames[fi], qv.Name, StringComparison.Ordinal))
                {
                    fieldIdx = fi;
                    break;
                }
            }

            if (fieldIdx >= 0)
            {
                int baseTemp = NewTemp();
                Emit(new IrInst.LoadLocal(baseTemp, recordFieldSlot.Value));
                int fieldTemp = NewTemp();
                Emit(new IrInst.GetAdtField(fieldTemp, baseTemp, fieldIdx));
                var fieldType = InstantiateConstructorParameterType(ctor, fieldIdx, namedRecordType);
                RecordHoverType(GetSpan(qv), $"{qv.Module}.{qv.Name}", fieldType);
                return (fieldTemp, fieldType);
            }
        }

        ReportDiagnostic(GetSpan(qv), $"Module '{qv.Module}' does not export '{qv.Name}'.");
        return ReturnNeverWithDummyTemp();
    }

    private (int, TypeRef) ResolveBuiltinModuleMember(BuiltinRegistry.BuiltinModule module, string name)
    {
        var member = module.Members[name];
        return member.Kind switch
        {
            BuiltinRegistry.BuiltinValueKind.Print => LowerQualifiedBuiltinFunctionReference(name, CreatePrintBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Panic => LowerQualifiedBuiltinFunctionReference(name, CreatePanicBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Args => LowerProgramArgs(NewTemp(), CreateArgsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Write => LowerQualifiedBuiltinFunctionReference(name, CreateWriteBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.WriteLine => LowerQualifiedBuiltinFunctionReference(name, CreateWriteLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ReadLine => LowerQualifiedBuiltinFunctionReference(name, CreateReadLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadText => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadAllBytes => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadAllBytesBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileMmap => LowerQualifiedBuiltinFunctionReference(name, CreateFileMmapBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileOpen => LowerQualifiedBuiltinFunctionReference(name, CreateFileOpenBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadChunk => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadChunkBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileReadLine => LowerQualifiedBuiltinFunctionReference(name, CreateFileReadLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileClose => LowerQualifiedBuiltinFunctionReference(name, CreateFileCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.InternalDeepCopy => LowerQualifiedBuiltinFunctionReference(name, CreateInternalDeepCopyBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ParallelBoth => LowerQualifiedBuiltinFunctionReference(name, CreateParallelBothBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ParallelWithWorkers => LowerQualifiedBuiltinFunctionReference(name, CreateParallelWithWorkersBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileWriteText => LowerQualifiedBuiltinFunctionReference(name, CreateFileWriteTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.FileExists => LowerQualifiedBuiltinFunctionReference(name, CreateFileExistsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextUncons => LowerQualifiedBuiltinFunctionReference(name, CreateTextUnconsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextParseInt => LowerQualifiedBuiltinFunctionReference(name, CreateTextParseIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextParseFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextParseFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFromInt => LowerQualifiedBuiltinFunctionReference(name, CreateTextFromIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFromFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextFromFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFormatFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextFormatFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextToHex => LowerQualifiedBuiltinFunctionReference(name, CreateTextToHexBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.HttpGet => LowerQualifiedBuiltinFunctionReference(name, CreateHttpGetBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.HttpPost => LowerQualifiedBuiltinFunctionReference(name, CreateHttpPostBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpListen => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpListenBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpForkWorkers => LowerQualifiedBuiltinFunctionReference(name, CreateNetForkWorkersBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpAccept => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpAcceptBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsServerHandshake => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsServerHandshakeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncRun => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncRunBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncTask => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncTaskBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncFromResult => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncFromResultBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncSleep => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncSleepBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncSpawn => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncSpawnBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncAll => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncAllBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncRace => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncRaceBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesEmpty => LowerQualifiedBuiltinFunctionReference(name, CreateBytesEmptyBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesSingleton => LowerQualifiedBuiltinFunctionReference(name, CreateBytesSingletonBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesLength => LowerQualifiedBuiltinFunctionReference(name, CreateBytesLengthBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesGet => LowerQualifiedBuiltinFunctionReference(name, CreateBytesGetBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesIndexOf => LowerQualifiedBuiltinFunctionReference(name, CreateBytesIndexOfBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesCompare => LowerQualifiedBuiltinFunctionReference(name, CreateBytesCompareBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesScanHash => LowerQualifiedBuiltinFunctionReference(name, CreateBytesScanHashBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesSubText => LowerQualifiedBuiltinFunctionReference(name, CreateBytesSubTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesSubView => LowerQualifiedBuiltinFunctionReference(name, CreateBytesSubViewBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesAppend => LowerQualifiedBuiltinFunctionReference(name, CreateBytesAppendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesAppendByte => LowerQualifiedBuiltinFunctionReference(name, CreateBytesAppendByteBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesFromList => LowerQualifiedBuiltinFunctionReference(name, CreateBytesFromListBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesFromText => LowerQualifiedBuiltinFunctionReference(name, CreateBytesFromTextBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesHash => LowerQualifiedBuiltinFunctionReference(name, CreateBytesHashBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesU16Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesU16LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesU32Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesU32LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesU64Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesU64LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesGetU16Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesGetU16LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesGetU32Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesGetU32LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BytesGetU64Le => LowerQualifiedBuiltinFunctionReference(name, CreateBytesGetU64LeBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.UIntToInt => LowerQualifiedBuiltinFunctionReference(name, CreateUIntToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.UIntFromInt => LowerQualifiedBuiltinFunctionReference(name, CreateUIntFromIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathToFloat => LowerQualifiedBuiltinFunctionReference(name, CreateMathToFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathSqrt => LowerQualifiedBuiltinFunctionReference(name, CreateMathSqrtBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathFloor => LowerQualifiedBuiltinFunctionReference(name, CreateMathFloorBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathCeil => LowerQualifiedBuiltinFunctionReference(name, CreateMathCeilBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathRound => LowerQualifiedBuiltinFunctionReference(name, CreateMathRoundBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathTrunc => LowerQualifiedBuiltinFunctionReference(name, CreateMathTruncBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathFloorToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathFloorToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathRoundToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathRoundToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathTruncToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathTruncToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind k when LibmBuiltinKinds.TryGetValue(k, out var libmKind) => LowerQualifiedBuiltinFunctionReference(name, CreateLibmBinding(libmKind).S.Body),
            BuiltinRegistry.BuiltinValueKind.FileWriteBytes => LowerQualifiedBuiltinFunctionReference(name, CreateFileWriteBytesBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.IoReadExact => LowerQualifiedBuiltinFunctionReference(name, CreateReadExactBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextByteLength => LowerQualifiedBuiltinFunctionReference(name, CreateTextByteLengthBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.SpawnProcess => LowerQualifiedBuiltinFunctionReference(name, CreateSpawnProcessBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessWriteStdin => LowerQualifiedBuiltinFunctionReference(name, CreateProcessWriteStdinBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessReadStdoutLine => LowerQualifiedBuiltinFunctionReference(name, CreateProcessReadStdoutLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessReadStderrLine => LowerQualifiedBuiltinFunctionReference(name, CreateProcessReadStderrLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessWaitForExit => LowerQualifiedBuiltinFunctionReference(name, CreateProcessWaitForExitBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessKill => LowerQualifiedBuiltinFunctionReference(name, CreateProcessKillBinding().S.Body),
            _ => StdMemberNotFound(module.Name, name)
        };
    }

    private (int, TypeRef) LowerQualifiedBuiltinFunctionReference(string name, TypeRef type)
    {
        var temp = NewTemp();
        ReportDiagnostic(0, $"Intrinsic '{name}' must be called directly.");
        Emit(new IrInst.LoadConstInt(temp, 0));
        return (temp, type);
    }

    private (int, TypeRef) StdMemberNotFound(string module, string name)
    {
        return StdMemberNotFound(module, name, TextSpan.FromBounds(0, 1));
    }

    private (int, TypeRef) StdMemberNotFound(string module, string name, TextSpan span)
    {
        ReportDiagnostic(span, $"Unknown member '{name}' in module {module}.");
        return ReturnNeverWithDummyTemp();
    }

    /// <summary>
    /// Peels leading intra-module alias bindings (<c>let helper = Ashes_Mod_helper in ...</c>, which the
    /// import stitcher injects so a module body's unqualified references resolve) and substitutes those
    /// alias names with their stitched targets throughout the remaining expression. The result is a
    /// clean lambda that references top-level stitched names directly — which the reuse-specialization /
    /// inlining registries are keyed by — so stdlib functions become eligible for in-place reuse.
    /// Only used to build the registry copy; the original declaration value is left intact for normal
    /// lowering. Throws on an unhandled Expr shape so the caller can skip registration (never miscompile).
    /// </summary>
    private static Expr StripModuleAliasPrefix(Expr value)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        Expr body = value;
        while (body is Expr.Let { Value: Expr.Var aliasTarget } aliasLet)
        {
            renames[aliasLet.Name] = aliasTarget.Name;
            body = aliasLet.Body;
        }

        return renames.Count == 0 ? value : SubstituteVars(body, renames);
    }
}
