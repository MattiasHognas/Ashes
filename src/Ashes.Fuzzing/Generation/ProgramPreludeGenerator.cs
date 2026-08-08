using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation;

internal sealed record GeneratedProgramPrelude(
    IReadOnlyList<TopLevelItem> Items,
    GenerationContext Context,
    GeneratedFeatureSet Features,
    GenerationTrace Trace);

internal static class ProgramPreludeGenerator
{
    internal static GeneratedProgramPrelude Generate(int caseIndex)
    {
        List<TopLevelItem> items = [];
        GenerationContext context = GenerationContext.Empty;
        GeneratedFeatureSet features = new([GeneratedFeature.TopLevelDeclaration]);
        List<string> trace = [];

        AddStableGenerationTypes(items, ref context);
        AddTopLevelFunction(caseIndex, items, ref context, features, trace);

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

    private static void AddStableGenerationTypes(List<TopLevelItem> items, ref GenerationContext context)
    {
        TypeDecl box = new(
            "FuzzBoxType",
            [new TypeParameter("a")],
            [new TypeConstructor("FuzzBox", [new TypeExpr.Named("a")])]);
        items.Add(new TopLevelItem.Type(box));
        context = context.WithAdt(new GeneratedAdt("FuzzBoxType", [("FuzzBox", [AshesType.Int])]));

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
            [("FuzzEmpty", []), ("FuzzLeaf", [AshesType.Int]), ("FuzzBranch", [new AshesType.Adt("FuzzTree", [AshesType.Int]), new AshesType.Adt("FuzzTree", [AshesType.Int])])]));

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
        context = context.WithAdt(new GeneratedAdt(typeName, [(left, [AshesType.Int]), (right, [AshesType.Str]), (both, [AshesType.Int, AshesType.Str])]));

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
