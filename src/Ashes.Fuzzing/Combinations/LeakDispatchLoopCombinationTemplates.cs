using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A member of a recursive group tail-calls its sibling with a fresh list, a call's result. The
/// group's tail calls run as one dispatch loop, which had none of an ordinary loop's parameter facts:
/// the list went into an arena parameter and nothing released it.
/// </summary>
internal sealed class DispatchLoopFreshArgumentTemplate() : LeakCombinationTemplate(
    "leak.dispatch-loop-fresh-argument", "dispatchLoopFreshArgument", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Match)
{
    private static readonly string[] Identifiers = ["storedValuesOf", "withdrawStoredRetains", "withdrawCellRetains", "total", "probe", "loop"];

    protected override string Declarations(LeakNames names)
        => FixedLeakProgram.Render(names, Program, Identifiers, "loop(" + names.Between(2, 5).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")(seed)");

    private const string Program = """
        let recursive storedValuesOf (cell: Int) (instructions: List(Int)) (values: List(Int)) =
            match instructions with
                | [] -> values
                | source :: rest -> storedValuesOf(cell)(rest)(source :: values)

        let recursive withdrawStoredRetains (values: List(Int)) (slots: List(Int)) =
            match values with
                | [] -> slots
                | _value :: _rest -> slots
        and withdrawCellRetains (cell: Int) (slots: List(Int)) (instructions: List(Int)) = withdrawStoredRetains(storedValuesOf(cell)(instructions)([]))(slots)

        let recursive total (k: Int) (xs: List(Int)) =
            match xs with
                | [] -> k
                | a :: rest -> total(k + a)(rest)

        let probe (n: Int) = withdrawCellRetains(28)([n % 5])([7, n]) |> total(0)

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else loop(n - 1)(acc + probe(n))
        """;
}

/// <summary>
/// The negative of <see cref="DispatchLoopFreshArgumentTemplate"/>: the member hands its sibling a
/// list it only borrows, its own parameter, which the caller reads again after the group returns. The
/// dispatch loop must not release it.
/// </summary>
internal sealed class DispatchLoopBorrowedArgumentTemplate() : LeakCombinationTemplate(
    "leak.dispatch-loop-borrowed-argument", "dispatchLoopBorrowedArgument", GeneratedFeature.RecursiveFunction, GeneratedFeature.TailCall, GeneratedFeature.List, GeneratedFeature.Match)
{
    private static readonly string[] Identifiers = ["withdrawStoredRetains", "withdrawCellRetains", "total", "probe", "loop"];

    protected override string Declarations(LeakNames names)
        => FixedLeakProgram.Render(names, Program, Identifiers, "loop(" + names.Between(2, 5).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")(seed)");

    private const string Program = """
        let recursive withdrawStoredRetains (values: List(Int)) (slots: List(Int)) =
            match values with
                | [] -> slots
                | value :: _rest -> value :: slots
        and withdrawCellRetains (cell: Int) (slots: List(Int)) (instructions: List(Int)) = withdrawStoredRetains(instructions)(cell :: slots)

        let recursive total (k: Int) (xs: List(Int)) =
            match xs with
                | [] -> k
                | a :: rest -> total(k + a)(rest)

        let probe (n: Int) =
            (let instructions = [7, n]
            in total(total(0)(withdrawCellRetains(28)([n % 5])(instructions)))(instructions))

        let recursive loop (n: Int) (acc: Int) =
            if n == 0
            then acc
            else loop(n - 1)(acc + probe(n))
        """;
}
