using Ashes.Frontend;

namespace Ashes.Fuzzing.Generation;

internal static class TraitPreludeGenerator
{
    internal static GeneratedProgramPrelude Generate(int caseIndex, GenerationContext context)
    {
        string suffix = caseIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string traitName = "FuzzSelect" + suffix;
        string methodName = "select";
        string constrainedFunction = "fuzzSelect" + suffix;
        string boxType = "FuzzTraitBox" + suffix;
        string boxConstructor = "FuzzTraitBoxValue" + suffix;
        string convertTrait = "FuzzConvert" + suffix;
        List<TopLevelItem> items = [];

        items.Add(CreateDerivedBox(boxType, boxConstructor));
        items.Add(new TopLevelItem.Trait(new TraitDecl(
            traitName,
            [new TypeParameter("a")],
            [],
            [new TraitMethodDecl(methodName, BinaryType("a", "a"), null)])));
        items.Add(CreateSimpleImplementation(traitName, methodName, "Int"));
        items.Add(CreateSimpleImplementation(traitName, methodName, "Bool"));
        items.Add(CreateSimpleImplementation(traitName, methodName, "Str"));
        items.Add(CreateBoxImplementation(traitName, methodName, boxType, boxConstructor));
        items.Add(CreateConstrainedFunction(traitName, methodName, constrainedFunction));
        items.Add(CreateResolvedValue(constrainedFunction, suffix));
        items.Add(CreateMultiParameterTrait(convertTrait));
        items.Add(CreateMultiParameterImplementation(convertTrait));
        items.Add(CreateMultiParameterResolvedValue(convertTrait, suffix));

        AshesType[] supportedTypes = [AshesType.Int, AshesType.Bool, AshesType.Str];
        GeneratedTrait generatedTrait = new(
            traitName,
            methodName,
            constrainedFunction,
            supportedTypes,
            boxType,
            boxConstructor);
        GenerationContext generatedContext = context
            .WithAdt(new GeneratedAdt(
                boxType,
                1,
                [(boxConstructor, [new AshesType.GenericParameter(0)])]))
            .WithTrait(generatedTrait)
            .WithBinding(new GeneratedBinding(
                constrainedFunction,
                new AshesType.Function(
                    AshesType.Int,
                    new AshesType.Function(AshesType.Int, AshesType.Int)),
                IsFunction: true));
        GeneratedFeatureSet features = new(
        [
            GeneratedFeature.Adt,
            GeneratedFeature.Call,
            GeneratedFeature.DerivedImplementation,
            GeneratedFeature.Match,
            GeneratedFeature.MultiParameterTrait,
            GeneratedFeature.TopLevelDeclaration,
            GeneratedFeature.TopLevelFunction,
            GeneratedFeature.TraitConstraint,
            GeneratedFeature.TraitDeclaration,
            GeneratedFeature.TraitImplementation,
            GeneratedFeature.TraitMethodCall,
            GeneratedFeature.TraitResolution,
        ]);
        GenerationTrace trace = new(
        [
            "trait:declaration:" + traitName,
            "trait:implementation:Int",
            "trait:implementation:Bool",
            "trait:implementation:Str",
            "trait:implementation:conditional-box",
            "trait:constrained-function:" + constrainedFunction,
            "trait:resolution:Int",
            "trait:multi-parameter:" + convertTrait,
            "trait:deriving:" + boxType,
        ]);
        return new GeneratedProgramPrelude(items, generatedContext, features, trace);
    }

    private static TopLevelItem.Type CreateDerivedBox(string typeName, string constructor)
    {
        TypeDecl declaration = new(
            typeName,
            [new TypeParameter("a")],
            [new TypeConstructor(constructor, [new TypeExpr.Named("a")])])
        {
            Deriving = ["Eq", "Show", "Hash"],
        };
        return new TopLevelItem.Type(declaration);
    }

    private static TopLevelItem.Implementation CreateSimpleImplementation(
        string traitName,
        string methodName,
        string typeName)
    {
        Expr body = new Expr.Lambda(
            "left",
            new Expr.Lambda("right", new Expr.Var("left")));
        TraitImplementationDecl declaration = new(
            traitName,
            [new TypeExpr.Named(typeName)],
            [],
            [new TraitImplementationMethodBinding(methodName, body)]);
        return new TopLevelItem.Implementation(declaration);
    }

    private static TopLevelItem.Implementation CreateBoxImplementation(
        string traitName,
        string methodName,
        string boxType,
        string boxConstructor)
    {
        Expr selected = CallTwice(
            new Expr.QualifiedVar(traitName, methodName),
            new Expr.Var("leftValue"),
            new Expr.Var("rightValue"));
        Expr body = new Expr.Lambda(
            "left",
            new Expr.Lambda(
                "right",
                new Expr.Match(
                    new Expr.TupleLit([new Expr.Var("left"), new Expr.Var("right")]),
                    [
                        new MatchCase(
                            new Pattern.Tuple(
                            [
                                new Pattern.Constructor(boxConstructor, [new Pattern.Var("leftValue")]),
                                new Pattern.Constructor(boxConstructor, [new Pattern.Var("rightValue")]),
                            ]),
                            new Expr.Call(new Expr.Var(boxConstructor), selected)),
                    ])));
        TraitImplementationDecl declaration = new(
            traitName,
            [new TypeExpr.Applied(boxType, [new TypeExpr.Named("a")])],
            [new TraitConstraintSyntax(traitName, [new TypeExpr.Named("a")])],
            [new TraitImplementationMethodBinding(methodName, body)]);
        return new TopLevelItem.Implementation(declaration);
    }

    private static TopLevelItem.LetDecl CreateConstrainedFunction(
        string traitName,
        string methodName,
        string name)
    {
        Expr body = new Expr.Lambda(
            "left",
            new Expr.Lambda(
                "right",
                CallTwice(
                    new Expr.QualifiedVar(traitName, methodName),
                    new Expr.Var("left"),
                    new Expr.Var("right"))));
        return new TopLevelItem.LetDecl(name, body, IsRecursive: false)
        {
            TypeAnnotation = BinaryType("a", "a"),
            Requires = [new TraitConstraintSyntax(traitName, [new TypeExpr.Named("a")])],
        };
    }

    private static TopLevelItem.LetDecl CreateResolvedValue(string constrainedFunction, string suffix)
    {
        Expr value = CallTwice(
            new Expr.Var(constrainedFunction),
            new Expr.IntLit(10),
            new Expr.IntLit(20));
        return new TopLevelItem.LetDecl("fuzzTraitResolved" + suffix, value, IsRecursive: false)
        {
            TypeAnnotation = AshesType.Int.ToSyntax(),
        };
    }

    private static TopLevelItem.Trait CreateMultiParameterTrait(string name) =>
        new(new TraitDecl(
            name,
            [new TypeParameter("source"), new TypeParameter("target")],
            [],
            [
                new TraitMethodDecl(
                    "convert",
                    new TypeExpr.Arrow(
                        new TypeExpr.Named("source"),
                        new TypeExpr.Named("target")),
                    null),
            ]));

    private static TopLevelItem.Implementation CreateMultiParameterImplementation(string traitName)
    {
        TraitImplementationDecl declaration = new(
            traitName,
            [new TypeExpr.Named("Int"), new TypeExpr.Named("Bool")],
            [],
            [
                new TraitImplementationMethodBinding(
                    "convert",
                    new Expr.Lambda("value", new Expr.BoolLit(true))),
            ]);
        return new TopLevelItem.Implementation(declaration);
    }

    private static TopLevelItem.LetDecl CreateMultiParameterResolvedValue(string traitName, string suffix)
    {
        Expr value = new Expr.Call(
            new Expr.QualifiedVar(traitName, "convert"),
            new Expr.IntLit(1));
        return new TopLevelItem.LetDecl("fuzzConverted" + suffix, value, IsRecursive: false)
        {
            TypeAnnotation = AshesType.Bool.ToSyntax(),
        };
    }

    private static TypeExpr BinaryType(string argument, string result) =>
        new TypeExpr.Arrow(
            new TypeExpr.Named(argument),
            new TypeExpr.Arrow(
                new TypeExpr.Named(argument),
                new TypeExpr.Named(result)));

    private static Expr CallTwice(Expr function, Expr first, Expr second) =>
        new Expr.Call(new Expr.Call(function, first), second);
}
