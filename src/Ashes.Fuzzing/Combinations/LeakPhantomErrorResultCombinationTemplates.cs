using Ashes.Fuzzing.Generation;

namespace Ashes.Fuzzing.Combinations;

/// <summary>
/// A function that matches a fresh record and returns <c>Ok</c> of its pattern-bound fields, so the
/// result's error type is a variable nothing gives a type: the arena <c>Ok</c> pointed into the
/// record, which the function could then never release. Read with the variable as <c>Never</c>
/// the result is detached like any <c>Result</c> of lists and the record is released behind it.
/// </summary>
internal sealed class OkOfMatchedRecordFieldsTemplate() : LeakCombinationTemplate(
    "leak.ok-of-matched-record-fields", "okOfMatchedRecordFields", GeneratedFeature.Record, GeneratedFeature.Match, GeneratedFeature.Adt)
{
    protected override string Declarations(LeakNames names)
    {
        string record = names.Of("LeakFields");
        string make = names.Of("make");
        string outer = names.Of("outer");
        string word = names.Word();
        (string Body, string Read) shape = names.Variant("payload", 3) switch
        {
            0 => ("Ok((xs, (ps, js)))", "| Ok((a, _rest)) -> Ashes.Collection.List.length(a)"),
            1 => ("Ok(xs)", "| Ok(a) -> Ashes.Collection.List.length(a)"),
            _ => ("Ok((js, xs))", "| Ok((words, a)) -> Ashes.Collection.List.length(a) + Ashes.Collection.List.length(words)"),
        };
        return $$"""
            type {{record}} =
                | xs: List(Int)
                | ps: List((Int, Bool))
                | js: List(Str)

            let {{make}} (k: Int) = {{record}}(xs = [k, k + {{LeakNames.Text(names.Between(1, 9))}}], ps = [(k, true)], js = [{{word}}])

            let {{outer}} (k: Int) =
                match {{make}}(k) with
                    | {{record}} { xs = xs, ps = ps, js = js } -> {{shape.Body}}

            let {{names.Entry}} (seed: Int) =
                match {{outer}}(seed) with
                    {{shape.Read}}
                    | Error(_) -> seed
            """;
    }
}
