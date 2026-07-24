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
        if (_capabilitySymbols.TryGetValue(qv.Module, out var bareCapabilitySym))
        {
            return LowerBareCapabilityOperationReference(qv, bareCapabilitySym);
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

        // A data constructor declared by the aliased module (e.g. `json.JsonInt` where `json` is
        // `Ashes.Text.Json`). Constructors are not module-prefixed like functions are — types are
        // hoisted into the combined source unqualified and stay globally registered in
        // `_constructorSymbols` — so this resolves the same way an unqualified reference to the
        // constructor would, gated on `resolvedModule` actually declaring a constructor by this name
        // (rather than resolving any alias to any same-named constructor from an unrelated module).
        if (TryResolveQualifiedConstructor(qv.Name, resolvedModule, out var qualifiedCtorSym))
        {
            var resolvedCtorReference = LowerConstructorReference(qualifiedCtorSym);
            RecordHoverType(GetSpan(qv), $"{resolvedModule}.{qv.Name}", resolvedCtorReference.Item2);
            return resolvedCtorReference;
        }

        // User module: resolve to the sanitized module binding if it exists.
        var binding = Lookup(resolvedModule) ?? Lookup(sanitizedModuleName);
        if (binding is null)
        {
            ReportDiagnostic(GetSpan(qv), $"Unknown module '{qv.Module}'.");
            return ReturnNeverWithDummyTemp();
        }

        return LowerRecordFieldAccessFallback(qv, binding);
    }

    /// <summary>
    /// Resolves <paramref name="name"/> as a data constructor declared by <paramref name="resolvedModule"/>
    /// (an already alias-resolved module name). Constructors are globally registered by name in
    /// <see cref="_constructorSymbols"/> — types are hoisted unqualified into the combined source, so
    /// there is no per-module constructor registry to look them up in directly — so this additionally
    /// checks <see cref="_constructorModulesByName"/> (which module's own `type` declarations actually
    /// introduce this name) to scope the match: a qualified reference resolves only when the aliased
    /// module really declares a constructor by that name, not to an unrelated same-named constructor
    /// declared elsewhere.
    /// </summary>
    private bool TryResolveQualifiedConstructor(string name, string resolvedModule, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ConstructorSymbol? ctorSym)
    {
        if (_constructorSymbols.TryGetValue(name, out var candidate)
            && _constructorModulesByName.TryGetValue(resolvedModule, out var declaredConstructorNames)
            && declaredConstructorNames.Contains(name))
        {
            ctorSym = candidate;
            return true;
        }

        ctorSym = null;
        return false;
    }

    // A bare (uncalled) capability operation reference. Direct application is handled in
    // LowerCall; a first-class operation value eta-expands to a lambda performing the
    // operation, so the perform happens where the value is eventually applied. The expansion
    // needs the operation's arity, so an unsigned operation cannot be used as a value.
    private (int, TypeRef) LowerBareCapabilityOperationReference(Expr.QualifiedVar qv, CapabilitySymbol capabilitySym)
    {
        if (!capabilitySym.Operations.TryGetValue(qv.Name, out var bareOperation))
        {
            ReportDiagnostic(GetSpan(qv), $"Capability '{qv.Module}' has no operation '{qv.Name}'.", UnknownCapabilityCode);
            return ReturnNeverWithDummyTemp();
        }

        if (bareOperation.DeclaredSignature is null)
        {
            ReportDiagnostic(GetSpan(qv), $"Capability operation '{qv.Module}.{qv.Name}' needs an explicit signature to be used as a value.", UnknownCapabilityCode);
            return ReturnNeverWithDummyTemp();
        }

        return LowerExpr(BuildOperationEtaLambda(qv, CountArrows(bareOperation.DeclaredSignature)));
    }

    // Record field access fallback: `rec.fieldName` where `rec` is a bound record value.
    // Let-bindings create Binding.Scheme; lambda params create Binding.Local; a receiver
    // captured from an enclosing scope arrives as Binding.Env / Binding.EnvScheme.
    private (int, TypeRef) LowerRecordFieldAccessFallback(Expr.QualifiedVar qv, Binding binding)
    {
        int? recordFieldSlot = null;
        bool receiverIsCaptured = false;
        TypeRef? recordFieldType = null;
        switch (binding)
        {
            case Binding.Local recordLocal:
                recordFieldSlot = recordLocal.Slot;
                recordFieldType = Prune(recordLocal.Type);
                break;
            case Binding.Scheme recordScheme:
                recordFieldSlot = recordScheme.Slot;
                recordFieldType = Prune(Instantiate(recordScheme.S));
                break;
            case Binding.Env recordEnv:
                recordFieldSlot = recordEnv.Index;
                receiverIsCaptured = true;
                recordFieldType = Prune(recordEnv.Type);
                break;
            case Binding.EnvScheme recordEnvScheme:
                recordFieldSlot = recordEnvScheme.Index;
                receiverIsCaptured = true;
                recordFieldType = Prune(Instantiate(recordEnvScheme.S));
                break;
        }

        if (recordFieldSlot.HasValue && recordFieldType is TypeRef.TVar)
        {
            recordFieldType = ResolveRecordReceiverByFieldName(qv, recordFieldType);
        }

        if (recordFieldSlot.HasValue
            && recordFieldType is TypeRef.TNamedType namedRecordType
            && namedRecordType.Symbol.Constructors.Count == 1
            && namedRecordType.Symbol.Constructors[0].DeclaringSyntax.FieldNames.Count > 0
            && TryLowerRecordFieldLoad(qv, recordFieldSlot.Value, receiverIsCaptured, namedRecordType) is { } fieldResult)
        {
            return fieldResult;
        }

        // `qv.Module` resolved to a value binding (a local/param/let), not a module — this was a
        // record field access that could not be resolved. Give a field-access-oriented message rather
        // than the misleading "does not export": either the receiver's record type is known but lacks
        // the field, or its type is not yet determined here (single-pass inference) and needs an
        // annotation (also the ambiguous case, where more than one record declares this field).
        if (binding is Binding.Local or Binding.Scheme or Binding.Env or Binding.EnvScheme)
        {
            var message = recordFieldType is TypeRef.TNamedType resolvedRecord
                ? $"Record type '{resolvedRecord.Symbol.Name}' has no field '{qv.Name}'."
                : $"Cannot resolve field access '{qv.Module}.{qv.Name}': the type of '{qv.Module}' is not known here. Annotate it, e.g. `let f : Rec -> _ = given ({qv.Module}) -> {qv.Module}.{qv.Name}`.";
            ReportDiagnostic(GetSpan(qv), message);
            return ReturnNeverWithDummyTemp();
        }

        ReportDiagnostic(GetSpan(qv), $"Module '{qv.Module}' does not export '{qv.Name}'.");
        return ReturnNeverWithDummyTemp();
    }

    // Single-pass inference: a parameter's type is often still an unbound type variable at its
    // first use, so `param.field` reached here with an unresolved receiver and fell through to the
    // misleading "does not export" error. Resolve it structurally by field name: if exactly one
    // record type in scope declares a field named `qv.Name`, unify the receiver with a fresh
    // instance of it and proceed. Ambiguous (two records share the field) or unknown falls through
    // to require a type annotation. (A resolved non-record type is left alone — no false unify.)
    private TypeRef ResolveRecordReceiverByFieldName(Expr.QualifiedVar qv, TypeRef recordFieldType)
    {
        var fieldRecordCandidates = _typeSymbols.Values
            .Where(s => s.Constructors.Count == 1
                && s.Constructors[0].DeclaringSyntax.FieldNames.Count > 0
                && s.Constructors[0].DeclaringSyntax.FieldNames.Contains(qv.Name, StringComparer.Ordinal))
            .ToList();
        if (fieldRecordCandidates.Count == 1)
        {
            var candidate = fieldRecordCandidates[0];
            var freshRecordType = new TypeRef.TNamedType(
                candidate,
                candidate.TypeParameters.Select(_ => (TypeRef)NewTypeVar()).ToList());
            Unify(recordFieldType, freshRecordType);
            return Prune(freshRecordType);
        }

        return recordFieldType;
    }

    private (int, TypeRef)? TryLowerRecordFieldLoad(Expr.QualifiedVar qv, int recordSlot, bool receiverIsCaptured, TypeRef.TNamedType namedRecordType)
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

        if (fieldIdx < 0)
        {
            return null;
        }

        int baseTemp = NewTemp();
        if (receiverIsCaptured)
        {
            Emit(new IrInst.LoadEnv(baseTemp, recordSlot));
        }
        else
        {
            Emit(new IrInst.LoadLocal(baseTemp, recordSlot));
        }
        int fieldTemp = NewTemp();
        Emit(new IrInst.GetAdtField(fieldTemp, baseTemp, fieldIdx));
        var fieldType = InstantiateConstructorParameterType(ctor, fieldIdx, namedRecordType);
        RecordHoverType(GetSpan(qv), $"{qv.Module}.{qv.Name}", fieldType);
        return (fieldTemp, fieldType);
    }

    private (int, TypeRef) ResolveBuiltinModuleMember(BuiltinRegistry.BuiltinModule module, string name)
    {
        // Members are grouped into three switches to keep each one small; the groups are
        // disjoint and tried in declaration order.
        var member = module.Members[name];
        return ResolveIoTextAndBigIntBuiltinMember(name, member.Kind)
            ?? ResolveNetworkBuiltinMember(name, member.Kind)
            ?? ResolveAsyncBuiltinMember(name, member.Kind)
            ?? ResolveBytesBuiltinMember(name, member.Kind)
            ?? ResolveMathBuiltinMember(name, member.Kind)
            ?? ResolveProcessBuiltinMember(name, member.Kind)
            ?? StdMemberNotFound(module.Name, name);
    }

    private (int, TypeRef)? ResolveIoTextAndBigIntBuiltinMember(string name, BuiltinRegistry.BuiltinValueKind kind)
    {
        return kind switch
        {
            BuiltinRegistry.BuiltinValueKind.Print => LowerQualifiedBuiltinFunctionReference(name, CreatePrintBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Panic => LowerQualifiedBuiltinFunctionReference(name, CreatePanicBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Args => LowerProgramArgs(NewTemp(), CreateArgsBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.Write => LowerQualifiedBuiltinFunctionReference(name, CreateWriteBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.IoWriteBytes => LowerQualifiedBuiltinFunctionReference(name, CreateWriteBytesBinding().S.Body),
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
            BuiltinRegistry.BuiltinValueKind.RegexCompile => LowerQualifiedBuiltinFunctionReference(name, CreateRegexCompileBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.RegexCompileError => LowerQualifiedBuiltinFunctionReference(name, CreateRegexCompileErrorBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.RegexFind => LowerQualifiedBuiltinFunctionReference(name, CreateRegexFindBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.RegexCaptures => LowerQualifiedBuiltinFunctionReference(name, CreateRegexCapturesBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.RegexSubstitute => LowerQualifiedBuiltinFunctionReference(name, CreateRegexSubstituteBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextParseInt => LowerQualifiedBuiltinFunctionReference(name, CreateTextParseIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextParseFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextParseFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFromInt => LowerQualifiedBuiltinFunctionReference(name, CreateTextFromIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFromFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextFromFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextFormatFloat => LowerQualifiedBuiltinFunctionReference(name, CreateTextFormatFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntFromInt => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntFromIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntToString => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntToStringBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntToInt => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntFromString => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntFromStringBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntAdd => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntBinaryBinding(IntrinsicKind.BigIntAdd).S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntSub => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntBinaryBinding(IntrinsicKind.BigIntSub).S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntMul => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntBinaryBinding(IntrinsicKind.BigIntMul).S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntDiv => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntBinaryBinding(IntrinsicKind.BigIntDiv).S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntMod => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntBinaryBinding(IntrinsicKind.BigIntMod).S.Body),
            BuiltinRegistry.BuiltinValueKind.BigIntCompare => LowerQualifiedBuiltinFunctionReference(name, CreateBigIntCompareBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextToHex => LowerQualifiedBuiltinFunctionReference(name, CreateTextToHexBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextAsciiUpper => LowerQualifiedBuiltinFunctionReference(name, CreateTextAsciiCaseBinding(upper: true).S.Body),
            BuiltinRegistry.BuiltinValueKind.TextAsciiLower => LowerQualifiedBuiltinFunctionReference(name, CreateTextAsciiCaseBinding(upper: false).S.Body),
            BuiltinRegistry.BuiltinValueKind.UIntToInt => LowerQualifiedBuiltinFunctionReference(name, CreateUIntToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.UIntFromInt => LowerQualifiedBuiltinFunctionReference(name, CreateUIntFromIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind k when LibmBuiltinKinds.TryGetValue(k, out var libmKind) => LowerQualifiedBuiltinFunctionReference(name, CreateLibmBinding(libmKind).S.Body),
            BuiltinRegistry.BuiltinValueKind.FileWriteBytes => LowerQualifiedBuiltinFunctionReference(name, CreateFileWriteBytesBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.IoReadExact => LowerQualifiedBuiltinFunctionReference(name, CreateReadExactBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ConsoleEnableRaw => LowerQualifiedBuiltinFunctionReference(name, CreateConsoleEnableRawBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ConsoleRestore => LowerQualifiedBuiltinFunctionReference(name, CreateConsoleRestoreBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ConsolePoll => LowerQualifiedBuiltinFunctionReference(name, CreateConsolePollBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ConsoleMonotonicMillis => LowerQualifiedBuiltinFunctionReference(name, CreateConsoleMonotonicMillisBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.TextByteLength => LowerQualifiedBuiltinFunctionReference(name, CreateTextByteLengthBinding().S.Body),
            _ => null
        };
    }

    private (int, TypeRef)? ResolveNetworkBuiltinMember(string name, BuiltinRegistry.BuiltinValueKind kind)
    {
        return kind switch
        {
            BuiltinRegistry.BuiltinValueKind.HttpGet => LowerQualifiedBuiltinFunctionReference(name, CreateHttpGetBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.HttpPost => LowerQualifiedBuiltinFunctionReference(name, CreateHttpPostBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpListen => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpListenBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpForkWorkers => LowerQualifiedBuiltinFunctionReference(name, CreateNetForkWorkersBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpSetDrainTimeout => LowerQualifiedBuiltinFunctionReference(name, CreateNetSetDrainTimeoutBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTcpAccept => LowerQualifiedBuiltinFunctionReference(name, CreateNetTcpAcceptBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsConnect => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsConnectBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsSend => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsSendBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsReceive => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsReceiveBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsClose => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsCloseBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.NetTlsServerHandshake => LowerQualifiedBuiltinFunctionReference(name, CreateNetTlsServerHandshakeBinding().S.Body),
            _ => null
        };
    }

    private (int, TypeRef)? ResolveAsyncBuiltinMember(string name, BuiltinRegistry.BuiltinValueKind kind)
    {
        return kind switch
        {
            BuiltinRegistry.BuiltinValueKind.AsyncRun => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncRunBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncTask => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncTaskBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncFromResult => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncFromResultBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncSleep => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncSleepBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncSpawn => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncSpawnBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncAll => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncAllBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.AsyncRace => LowerQualifiedBuiltinFunctionReference(name, CreateAsyncRaceBinding().S.Body),
            _ => null
        };
    }

    private (int, TypeRef)? ResolveBytesBuiltinMember(string name, BuiltinRegistry.BuiltinValueKind kind)
    {
        return kind switch
        {
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
            _ => null
        };
    }

    private (int, TypeRef)? ResolveMathBuiltinMember(string name, BuiltinRegistry.BuiltinValueKind kind)
    {
        return kind switch
        {
            BuiltinRegistry.BuiltinValueKind.MathToFloat => LowerQualifiedBuiltinFunctionReference(name, CreateMathToFloatBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathSqrt => LowerQualifiedBuiltinFunctionReference(name, CreateMathSqrtBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathFloor => LowerQualifiedBuiltinFunctionReference(name, CreateMathFloorBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathCeil => LowerQualifiedBuiltinFunctionReference(name, CreateMathCeilBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathRound => LowerQualifiedBuiltinFunctionReference(name, CreateMathRoundBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathTrunc => LowerQualifiedBuiltinFunctionReference(name, CreateMathTruncBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathFloorToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathFloorToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathRoundToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathRoundToIntBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.MathTruncToInt => LowerQualifiedBuiltinFunctionReference(name, CreateMathTruncToIntBinding().S.Body),
            _ => null
        };
    }

    private (int, TypeRef)? ResolveProcessBuiltinMember(string name, BuiltinRegistry.BuiltinValueKind kind)
    {
        return kind switch
        {
            BuiltinRegistry.BuiltinValueKind.SpawnProcess => LowerQualifiedBuiltinFunctionReference(name, CreateSpawnProcessBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessWriteStdin => LowerQualifiedBuiltinFunctionReference(name, CreateProcessWriteStdinBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessReadStdoutLine => LowerQualifiedBuiltinFunctionReference(name, CreateProcessReadStdoutLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessReadStderrLine => LowerQualifiedBuiltinFunctionReference(name, CreateProcessReadStderrLineBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessWaitForExit => LowerQualifiedBuiltinFunctionReference(name, CreateProcessWaitForExitBinding().S.Body),
            BuiltinRegistry.BuiltinValueKind.ProcessKill => LowerQualifiedBuiltinFunctionReference(name, CreateProcessKillBinding().S.Body),
            _ => null
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
