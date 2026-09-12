package main

import ("os"; "strconv"; "strings"; "fmt")

type Tree struct{ l, r *Tree }

func make_(depth int) *Tree {
	if depth == 0 { return nil }
	return &Tree{make_(depth - 1), make_(depth - 1)}
}
func check(t *Tree) int64 {
	if t == nil { return 1 }
	return 1 + check(t.l) + check(t.r)
}

func main() {
	n := 10
	if len(os.Args) > 1 { if x, err := strconv.Atoi(os.Args[1]); err == nil { n = x } }
	minDepth := 4
	maxDepth := n
	if minDepth+2 > n { maxDepth = minDepth + 2 }
	stretchDepth := maxDepth + 1
	longLived := make_(maxDepth)
	var out strings.Builder
	fmt.Fprintf(&out, "stretch tree of depth %d\t check: %d\n", stretchDepth, check(make_(stretchDepth)))
	for depth := minDepth; depth <= maxDepth; depth += 2 {
		iterations := int64(1) << uint(maxDepth-depth+minDepth)
		var sum int64
		for i := int64(0); i < iterations; i++ { sum += check(make_(depth)) }
		fmt.Fprintf(&out, "%d\t trees of depth %d\t check: %d\n", iterations, depth, sum)
	}
	fmt.Fprintf(&out, "long lived tree of depth %d\t check: %d\n", maxDepth, check(longLived))
	os.Stdout.WriteString(out.String())
}
