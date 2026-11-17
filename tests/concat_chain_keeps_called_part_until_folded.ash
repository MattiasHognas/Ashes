// expect: Missing("Ord", 3) Broken Missing("x", 1)
// A string built by a chain of `+` whose first part is a call result that owns its string: the
// chain folds into one concatenation at its end, so the release of that first part has to move
// past the folded concatenation. Released where the first link read it, the part was freed before
// the fold copied it and a later part's allocation showed through in its place.
type Problem =
    | Missing(Str, Int)
    | Broken
    deriving {Eq, Show}

let describe (problem: Problem) = Ashes.Trait.Show.show(problem)

Ashes.IO.print(describe(Missing("Ord")(3)) + " " + Ashes.Trait.Show.show(Broken) + " " + Ashes.Trait.Show.show(Missing("x")(1)))
