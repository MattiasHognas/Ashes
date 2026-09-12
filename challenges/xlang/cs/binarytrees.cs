using System;
using System.Text;

sealed class Tree { public Tree L, R; public Tree(Tree l, Tree r) { L = l; R = r; } }

static class BinaryTrees
{
    static Tree Make(int depth) => depth == 0 ? null : new Tree(Make(depth - 1), Make(depth - 1));
    static long Check(Tree t) => t == null ? 1 : 1 + Check(t.L) + Check(t.R);

    static int Main(string[] args)
    {
        int n = args.Length > 0 && int.TryParse(args[0], out int x) ? x : 10;
        int minDepth = 4;
        int maxDepth = minDepth + 2 > n ? minDepth + 2 : n;
        int stretchDepth = maxDepth + 1;
        Tree longLived = Make(maxDepth);
        StringBuilder outp = new StringBuilder();
        outp.Append($"stretch tree of depth {stretchDepth}\t check: {Check(Make(stretchDepth))}\n");
        for (int depth = minDepth; depth <= maxDepth; depth += 2)
        {
            long iterations = 1L << (maxDepth - depth + minDepth);
            long sum = 0;
            for (long i = 0; i < iterations; i++) sum += Check(Make(depth));
            outp.Append($"{iterations}\t trees of depth {depth}\t check: {sum}\n");
        }
        outp.Append($"long lived tree of depth {maxDepth}\t check: {Check(longLived)}\n");
        Console.Out.Write(outp.ToString());
        return 0;
    }
}
