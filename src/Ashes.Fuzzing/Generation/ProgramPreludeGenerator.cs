using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation;

internal sealed record GeneratedProgramPrelude(
    IReadOnlyList<TopLevelItem> Items,
    GenerationContext Context,
    GeneratedFeatureSet Features,
    GenerationTrace Trace);

internal static class ProgramPreludeGenerator
{
    internal static GeneratedProgramPrelude Generate(int caseIndex, bool generateTraits = false)
    {
        List<TopLevelItem> items = [];
        GenerationContext context = GenerationContext.Empty;
        GeneratedFeatureSet features = new([GeneratedFeature.TopLevelDeclaration]);
        List<string> trace = [];

        AddStableGenerationTypes(items, ref context);
        if (caseIndex % 4 == 0)
        {
            AddEvolvedTypes(caseIndex, items, ref context, features, trace);
        }
        if (generateTraits)
        {
            GeneratedProgramPrelude traits = TraitPreludeGenerator.Generate(caseIndex, context);
            items.AddRange(traits.Items);
            context = traits.Context;
            features.UnionWith(traits.Features);
            trace.AddRange(traits.Trace.Entries);
        }
        AddTopLevelFunction(caseIndex, items, ref context, features, trace);
        if (caseIndex % 6 == 0)
        {
            AddBinaryConstruction(caseIndex, items, ref context, features, trace);
        }
        if (caseIndex % 5 == 0)
        {
            AddExternalResource(caseIndex, items, features, trace);
        }
        if (caseIndex % 10 == 5)
        {
            AddFfiBuffer(caseIndex, items, features, trace);
            AddFfiOut(caseIndex, items, features, trace);
            AddFfiString(caseIndex, items, features, trace);
            AddForeignBuffer(caseIndex, items, features, trace);
        }

        switch (caseIndex % 3)
        {
            case 0:
                AddGeneratedAdt(caseIndex, items, ref context, trace);
                break;
            case 1:
                AddGeneratedRecord(caseIndex, items, ref context, trace);
                break;
            default:
                AddMutualRecursion(caseIndex, items, ref context, features, trace);
                break;
        }

        if (caseIndex % 7 == 0)
        {
            AddProvider(caseIndex, items, ref context, features, trace);
        }

        return new GeneratedProgramPrelude(items, context, features, new GenerationTrace(trace));
    }

    private static void AddBinaryConstruction(
        int caseIndex,
        List<TopLevelItem> items,
        ref GenerationContext context,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string name = "fuzzBytes" + caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Expr allocated = ApplyByteIntrinsic("allocate", new Expr.IntLit(16));
        int operation = caseIndex / 6 % 6;
        Expr value = operation switch
        {
            0 => allocated,
            1 => ApplyByteIntrinsic(
                "set",
                allocated,
                new Expr.IntLit(caseIndex % 16),
                new Expr.UIntLit((ulong)(caseIndex & 0xFF), 8)),
            2 => ApplyByteIntrinsic(
                "setU16Le",
                allocated,
                new Expr.IntLit(caseIndex % 15),
                new Expr.UIntLit(unchecked((ulong)caseIndex * 257UL) & 0xFFFFUL, 16)),
            3 => ApplyByteIntrinsic(
                "setU32Le",
                allocated,
                new Expr.IntLit(caseIndex % 13),
                new Expr.UIntLit(unchecked((ulong)caseIndex * 0x01010101UL) & 0xFFFFFFFFUL, 32)),
            4 => ApplyByteIntrinsic(
                "setU64Le",
                allocated,
                new Expr.IntLit(caseIndex % 9),
                new Expr.UIntLit(unchecked((ulong)caseIndex * 0x0101010101010101UL), 64)),
            _ => ApplyByteIntrinsic(
                "copyRange",
                allocated,
                new Expr.IntLit(caseIndex % 9),
                ApplyByteIntrinsic("allocate", new Expr.IntLit(8)),
                new Expr.IntLit(0),
                new Expr.IntLit(8)),
        };
        items.Add(new TopLevelItem.LetDecl(name, value, IsRecursive: false));
        context = context.WithBinding(new GeneratedBinding(name, AshesType.Bytes));
        features.Add(GeneratedFeature.BinaryConstruction);
        if (operation != 0)
        {
            features.Add(GeneratedFeature.ReuseCandidate);
        }

        trace.Add("program:binary-construction:" + operation.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static Expr ApplyByteIntrinsic(string member, params Expr[] arguments)
    {
        Expr expression = new Expr.QualifiedVar("Ashes.Byte", member);
        foreach (Expr argument in arguments)
        {
            expression = new Expr.Call(expression, argument);
        }

        return expression;
    }

    private static void AddFfiBuffer(
        int caseIndex,
        List<TopLevelItem> items,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string typeName = "FuzzOpaque" + suffix;
        items.Add(new TopLevelItem.External(new ExternalDecl.OpaqueType(typeName)));
        items.Add(new TopLevelItem.External(new ExternalDecl.Function(
            "fuzzBufferInspect" + suffix,
            [new ParsedType.Buffer(new ParsedType.Named(typeName)), new ParsedType.Named("u64")],
            new ParsedType.Named("Int"))));
        features.Add(GeneratedFeature.FfiBuffer);
        trace.Add("program:ffi-buffer");
    }

    private static void AddFfiOut(
        int caseIndex,
        List<TopLevelItem> items,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        items.Add(new TopLevelItem.External(new ExternalDecl.Function(
            "fuzzResolve" + suffix,
            [
                new ParsedType.Named("Str"),
                new ParsedType.Out(new ParsedType.Named("FuzzOpaque" + suffix)),
                new ParsedType.Out(new ParsedType.Pointer(new ParsedType.Named("u8")))
            ],
            new ParsedType.Named("Bool"))));
        features.Add(GeneratedFeature.FfiOut);
        trace.Add("program:ffi-out");
    }

    private static void AddFfiString(
        int caseIndex,
        List<TopLevelItem> items,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string destructorName = "fuzzDisposeMessage" + suffix;
        items.Add(new TopLevelItem.External(new ExternalDecl.Function(
            destructorName,
            [new ParsedType.Pointer(new ParsedType.Named("u8"))],
            new ParsedType.Named("void"))));
        items.Add(new TopLevelItem.External(new ExternalDecl.Function(
            "fuzzNativeName" + suffix,
            [],
            new ParsedType.NativeString(false, FfiStringOwnership.Owned, destructorName))));
        features.Add(GeneratedFeature.FfiString);
        trace.Add("program:ffi-string");
    }

    private static void AddExternalResource(
        int caseIndex,
        List<TopLevelItem> items,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string typeName = "FuzzResource" + suffix;
        string destructorName = "fuzzResourceClose" + suffix;
        items.Add(new TopLevelItem.External(new ExternalDecl.OpaqueType(typeName)
        {
            DestructorName = destructorName,
        }));
        items.Add(new TopLevelItem.External(new ExternalDecl.Function(
            "fuzzResourceInspect" + suffix,
            [new ParsedType.Named(typeName)],
            new ParsedType.Named("Int"))
        {
            ParameterOwnerships = [ExternalParameterOwnership.Borrow],
            Needs = new NeedsRowSyntax(
                [new CapabilityRefSyntax("Entropy", [])],
                TailVar: null),
        }));
        items.Add(new TopLevelItem.External(new ExternalDecl.Function(
            destructorName,
            [new ParsedType.Named(typeName)],
            new ParsedType.Named("void"))
        {
            ParameterOwnerships = [ExternalParameterOwnership.Consume],
        }));
        features.Add(GeneratedFeature.ExternalResource);
        features.Add(GeneratedFeature.AmbientAuthority);
        trace.Add("program:external-resource");
        trace.Add("program:ambient-authority");
    }

    private static void AddForeignBuffer(
        int caseIndex,
        List<TopLevelItem> items,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string resourceName = "FuzzResource" + suffix;
        items.Add(new TopLevelItem.External(new ExternalDecl.Function(
            "fuzzBufferStart" + suffix,
            [new ParsedType.Named(resourceName)],
            new ParsedType.Pointer(new ParsedType.Named("u8")))
        {
            ParameterOwnerships = [ExternalParameterOwnership.Borrow],
        }));
        items.Add(new TopLevelItem.External(new ExternalDecl.Function(
            "fuzzBufferSize" + suffix,
            [new ParsedType.Named(resourceName)],
            new ParsedType.Named("u64"))
        {
            ParameterOwnerships = [ExternalParameterOwnership.Borrow],
        }));
        features.Add(GeneratedFeature.ForeignBuffer);
        trace.Add("program:foreign-buffer");
    }

    private static void AddEvolvedTypes(
        int caseIndex,
        List<TopLevelItem> items,
        ref GenerationContext context,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string aliasName = "FuzzIdentifier" + suffix;
        items.Add(new TopLevelItem.TypeAlias(new TypeAliasDecl(
            aliasName,
            [],
            new TypeExpr.Named("Int"))));

        string typeName = "FuzzUserId" + suffix;
        string constructorName = "FuzzUserIdValue" + suffix;
        items.Add(new TopLevelItem.ZeroCostType(new ZeroCostTypeDecl(
            typeName,
            [],
            new TypeConstructor(constructorName, [new TypeExpr.Named(aliasName)]))));
        context = context.WithAdt(new GeneratedAdt(
            typeName,
            0,
            [(constructorName, [AshesType.Int])]));
        features.Add(GeneratedFeature.TypeAlias);
        features.Add(GeneratedFeature.ZeroCostType);
        trace.Add("program:type-alias");
        trace.Add("program:zero-cost-type");
    }

    private static void AddStableGenerationTypes(List<TopLevelItem> items, ref GenerationContext context)
    {
        TypeDecl box = new(
            "FuzzBoxType",
            [new TypeParameter("a")],
            [new TypeConstructor("FuzzBox", [new TypeExpr.Named("a")])]);
        items.Add(new TopLevelItem.Type(box));
        context = context.WithAdt(new GeneratedAdt("FuzzBoxType", 1, [("FuzzBox", [new AshesType.GenericParameter(0)])]));

        TypeExpr treeOfA = new TypeExpr.Applied("FuzzTree", [new TypeExpr.Named("a")]);
        TypeDecl tree = new(
            "FuzzTree",
            [new TypeParameter("a")],
            [
                new TypeConstructor("FuzzEmpty", []),
                new TypeConstructor("FuzzLeaf", [new TypeExpr.Named("a")]),
                new TypeConstructor("FuzzBranch", [treeOfA, treeOfA]),
            ]);
        items.Add(new TopLevelItem.Type(tree));
        context = context.WithAdt(new GeneratedAdt(
            "FuzzTree",
            1,
            [("FuzzEmpty", []), ("FuzzLeaf", [new AshesType.GenericParameter(0)]), ("FuzzBranch", [new AshesType.Adt("FuzzTree", [new AshesType.GenericParameter(0)]), new AshesType.Adt("FuzzTree", [new AshesType.GenericParameter(0)])])]));

        TypeDecl maybe = new(
            "FuzzMaybe",
            [new TypeParameter("a")],
            [
                new TypeConstructor("FuzzNone", []),
                new TypeConstructor("FuzzSome", [new TypeExpr.Named("a")]),
            ]);
        items.Add(new TopLevelItem.Type(maybe));
        context = context.WithAdt(new GeneratedAdt(
            "FuzzMaybe",
            1,
            [("FuzzNone", []), ("FuzzSome", [new AshesType.GenericParameter(0)])]));

        TypeConstructor recordFields = new("FuzzRecord", [AshesType.Int.ToSyntax(), AshesType.Bool.ToSyntax()])
        {
            FieldNames = ["first", "second"],
        };
        items.Add(new TopLevelItem.Type(new TypeDecl("FuzzRecord", [], [recordFields]) { IsRecord = true }));
        context = context.WithRecord(new GeneratedRecord("FuzzRecord", [("first", AshesType.Int), ("second", AshesType.Bool)]));
    }

    private static void AddTopLevelFunction(
        int caseIndex,
        List<TopLevelItem> items,
        ref GenerationContext context,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string name = "fuzzTopFunction" + caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string parameter = "number";
        Expr body = new Expr.Call(new Expr.QualifiedVar("Ashes.Text", "fromInt"), new Expr.Var(parameter));
        Expr lambda = new Expr.Lambda(parameter, body) { ParamAnnotation = AshesType.Int.ToSyntax() };
        TopLevelItem.LetDecl declaration = new(name, lambda, IsRecursive: false)
        {
            TypeAnnotation = new AshesType.Function(AshesType.Int, AshesType.Str).ToSyntax(),
        };
        items.Add(declaration);
        context = context.WithBinding(new GeneratedBinding(name, new AshesType.Function(AshesType.Int, AshesType.Str), IsFunction: true));
        features.Add(GeneratedFeature.TopLevelFunction);
        trace.Add("program:top-level-function");
    }

    private static void AddGeneratedAdt(int caseIndex, List<TopLevelItem> items, ref GenerationContext context, List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string typeName = "FuzzChoice" + suffix;
        string left = "FuzzLeft" + suffix;
        string right = "FuzzRight" + suffix;
        string both = "FuzzBoth" + suffix;
        TypeDecl declaration = new(
            typeName,
            [new TypeParameter("a"), new TypeParameter("b")],
            [
                new TypeConstructor(left, [new TypeExpr.Named("a")]),
                new TypeConstructor(right, [new TypeExpr.Named("b")]),
                new TypeConstructor(both, [new TypeExpr.Named("a"), new TypeExpr.Named("b")]),
            ]);
        items.Add(new TopLevelItem.Type(declaration));
        AshesType.Adt generatedType = new(typeName, [AshesType.Int, AshesType.Str]);
        context = context.WithAdt(new GeneratedAdt(typeName, 2, [(left, [new AshesType.GenericParameter(0)]), (right, [new AshesType.GenericParameter(1)]), (both, [new AshesType.GenericParameter(0), new AshesType.GenericParameter(1)])]));

        string valueName = "fuzzChoiceValue" + suffix;
        items.Add(new TopLevelItem.LetDecl(valueName, new Expr.Call(new Expr.Var(left), new Expr.IntLit(caseIndex)), IsRecursive: false)
        {
            TypeAnnotation = generatedType.ToSyntax(),
        });
        context = context.WithBinding(new GeneratedBinding(valueName, generatedType));
        trace.Add("program:generated-adt");
    }

    private static void AddGeneratedRecord(int caseIndex, List<TopLevelItem> items, ref GenerationContext context, List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string typeName = "FuzzRecordShape" + suffix;
        TypeConstructor fields = new(typeName, [AshesType.Str.ToSyntax(), AshesType.Int.ToSyntax(), AshesType.Bool.ToSyntax()])
        {
            FieldNames = ["label", "count", "enabled"],
        };
        items.Add(new TopLevelItem.Type(new TypeDecl(typeName, [], [fields]) { IsRecord = true }));
        AshesType.Record generatedType = new(typeName);
        context = context.WithRecord(new GeneratedRecord(typeName, [("label", AshesType.Str), ("count", AshesType.Int), ("enabled", AshesType.Bool)]));

        string valueName = "fuzzRecordValue" + suffix;
        Expr value = new Expr.RecordLit(typeName, [("label", new Expr.StrLit("generated")), ("count", new Expr.IntLit(caseIndex)), ("enabled", new Expr.BoolLit(caseIndex % 2 == 0))]);
        items.Add(new TopLevelItem.LetDecl(valueName, value, IsRecursive: false) { TypeAnnotation = generatedType.ToSyntax() });
        context = context.WithBinding(new GeneratedBinding(valueName, generatedType));
        trace.Add("program:generated-record");
    }

    private static void AddMutualRecursion(
        int caseIndex,
        List<TopLevelItem> items,
        ref GenerationContext context,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string even = "fuzzEven" + suffix;
        string odd = "fuzzOdd" + suffix;
        Expr evenValue = ParityLambda(odd, expectedAtZero: true);
        Expr oddValue = ParityLambda(even, expectedAtZero: false);
        items.Add(new TopLevelItem.RecursiveGroup([(even, evenValue), (odd, oddValue)]));
        AshesType.Function functionType = new(AshesType.Int, AshesType.Bool);
        context = context.WithBinding(new GeneratedBinding(even, functionType, IsFunction: true));
        context = context.WithBinding(new GeneratedBinding(odd, functionType, IsFunction: true));
        features.Add(GeneratedFeature.RecursiveFunction);
        features.Add(GeneratedFeature.MutualRecursion);
        trace.Add("program:mutual-recursion");
    }

    private static Expr ParityLambda(string otherFunction, bool expectedAtZero)
    {
        const string parameter = "remaining";
        Expr condition = new Expr.Equal(new Expr.Var(parameter), new Expr.IntLit(0));
        Expr recursiveCall = new Expr.Call(
            new Expr.Var(otherFunction),
            new Expr.Subtract(new Expr.Var(parameter), new Expr.IntLit(1)));
        return new Expr.Lambda(parameter, new Expr.If(condition, new Expr.BoolLit(expectedAtZero), recursiveCall))
        {
            ParamAnnotation = AshesType.Int.ToSyntax(),
        };
    }

    private static void AddProvider(
        int caseIndex,
        List<TopLevelItem> items,
        ref GenerationContext context,
        GeneratedFeatureSet features,
        List<string> trace)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string capabilityName = "FuzzProvided" + suffix;
        TypeExpr signature = new TypeExpr.Arrow(AshesType.Int.ToSyntax(), AshesType.Int.ToSyntax());
        items.Add(new TopLevelItem.Capability(new CapabilityDecl(capabilityName, [], [new CapabilityOperation("transform", signature)])));
        Expr implementation = new Expr.Lambda(
            "providedValue",
            new Expr.Add(new Expr.Var("providedValue"), new Expr.IntLit((caseIndex % 5) + 1)))
        {
            ParamAnnotation = AshesType.Int.ToSyntax(),
        };
        items.Add(new TopLevelItem.Provide(new ProvideDecl(capabilityName, [], [new ProvideBinding("transform", implementation)])));
        string resultName = "fuzzProvidedValue" + suffix;
        Expr operation = new Expr.Call(new Expr.QualifiedVar(capabilityName, "transform"), new Expr.IntLit(caseIndex));
        items.Add(new TopLevelItem.LetDecl(resultName, operation, IsRecursive: false) { TypeAnnotation = AshesType.Int.ToSyntax() });
        context = context.WithCapability(new GeneratedCapability(
            capabilityName,
            [new GeneratedCapabilityOperation("transform", AshesType.Int, AshesType.Int)]))
            .WithBinding(new GeneratedBinding(resultName, AshesType.Int));
        features.Add(GeneratedFeature.Capability);
        features.Add(GeneratedFeature.Provider);
        trace.Add("program:provider");
    }
}
