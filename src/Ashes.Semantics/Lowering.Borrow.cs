using Ashes.Frontend;

namespace Ashes.Semantics;

// Borrow inference. A resource passed to a user function normally MOVES into the callee (the caller
// must not use it afterwards). But if the callee only READS the resource — never closing, moving,
// storing, returning, or capturing it — the caller can keep ownership and the pass is a borrow, not a
// move. This lets a read-only helper take a Socket/FileHandle/Process without consuming it.
//
// Soundness is by construction, fail-closed: a parameter is classified "borrows-only" ONLY when every
// use in the body is positively verified as the resource argument of a known borrow-read built-in; any
// other use, any unhandled node, any shadowing or closure capture makes it consume (the move
// behaviour). A wrongly-permitted borrow of a CONSUMING op would double-close, so the borrow-read set
// below is exactly the ops proven to read without consuming; close/handshake and (conservatively)
// Process.waitForExit / Process.kill are excluded.
public sealed partial class Lowering
{
    // Built-in ops that take a resource as their first curried argument and only read it (no close,
    // move, or store), keyed by fully-qualified name. Adding a consuming op here would be unsound
    // (double-close); a missing borrow-read op is merely a missed optimization.
    private static readonly HashSet<string> BorrowReadResourceOps = new(StringComparer.Ordinal)
    {
        "Ashes.IO.File.readChunk",
        "Ashes.IO.File.readLine",
        "Ashes.Net.Tcp.send",
        "Ashes.Net.Tcp.receive",
        "Ashes.Net.Tcp.Server.accept",
        "Ashes.Net.Tls.send",
        "Ashes.Net.Tls.receive",
        "Ashes.IO.Process.writeStdin",
        "Ashes.IO.Process.readStdoutLine",
        "Ashes.IO.Process.readStderrLine",
    };

    private readonly Dictionary<(string Func, int Param), bool> _borrowOnlyMemo = new();

    /// <summary>
    /// True if <paramref name="rootExpr"/> is a call to a known user function whose parameter at
    /// <paramref name="argIndex"/> is borrows-only — so a resource passed there is borrowed, not moved.
    /// Only plain-named user functions are considered; builtins and higher-order callees fall through to
    /// the conservative move. Requires the move analysis to have registered the function.
    /// </summary>
    private bool CalleeParamBorrowsOnly(Expr rootExpr, int argIndex)
    {
        if (rootExpr is not Expr.Var v || !_maAnalyzed || !_maFuncs.TryGetValue(v.Name, out var info))
        {
            return false;
        }

        if (argIndex < 0 || argIndex >= info.Params.Count)
        {
            return false;
        }

        var key = (v.Name, argIndex);
        if (_borrowOnlyMemo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        // The scan is purely syntactic over one body (it never recurses into another function), so no
        // cycle is possible; compute once and cache.
        bool result = ParamUsedOnlyAsBorrowRead(info.Body, info.Params[argIndex]);
        _borrowOnlyMemo[key] = result;
        return result;
    }

    // True if every occurrence of <paramref name="p"/> in <paramref name="e"/> is the resource (first)
    // argument of a borrow-read built-in. Fail-closed: a bare use, a store, a pass to another function,
    // a return, a closure capture, a shadowing binder, or any unhandled node yields false.
    private bool ParamUsedOnlyAsBorrowRead(Expr e, string p)
    {
        switch (e)
        {
            case Expr.IntLit or Expr.BigIntLit or Expr.UIntLit or Expr.FloatLit
                or Expr.StrLit or Expr.BoolLit or Expr.QualifiedVar:
                return true;

            case Expr.Var v:
                // A bare occurrence of p is a non-borrow use (return / store / alias).
                return !string.Equals(v.Name, p, StringComparison.Ordinal);

            case Expr.Call:
                return CallUsesParamOnlyAsBorrowRead(e, p);

            case Expr.If i:
                return ParamUsedOnlyAsBorrowRead(i.Cond, p)
                    && ParamUsedOnlyAsBorrowRead(i.Then, p)
                    && ParamUsedOnlyAsBorrowRead(i.Else, p);

            case Expr.Let l: return LetLikeUsesParamOnlyAsBorrowRead(l.Name, l.Value, l.Body, p);
            case Expr.LetResult lr: return LetLikeUsesParamOnlyAsBorrowRead(lr.Name, lr.Value, lr.Body, p);
            case Expr.LetRecursive lrec: return LetLikeUsesParamOnlyAsBorrowRead(lrec.Name, lrec.Value, lrec.Body, p);

            case Expr.Lambda lam:
                // Capturing p in a closure is not a simple borrow (the closure may outlive the call or
                // run many times). Allow only if p is shadowed by the lambda param or does not appear.
                return string.Equals(lam.ParamName, p, StringComparison.Ordinal)
                    || !MentionsVar(lam.Body, p);

            case Expr.Match m:
                return MatchUsesParamOnlyAsBorrowRead(m, p);

            case Expr.TupleLit t:
                return t.Elements.All(x => ParamUsedOnlyAsBorrowRead(x, p));

            case Expr.ListLit ls:
                return ls.Elements.All(x => ParamUsedOnlyAsBorrowRead(x, p));

            case Expr.Cons cons:
                return ParamUsedOnlyAsBorrowRead(cons.Head, p) && ParamUsedOnlyAsBorrowRead(cons.Tail, p);

            case Expr.Await aw:
                return ParamUsedOnlyAsBorrowRead(aw.Task, p);

            case Expr.BitwiseNot bn:
                return ParamUsedOnlyAsBorrowRead(bn.Operand, p);

            default:
                return BinaryUsesParamOnlyAsBorrowRead(e, p);
        }
    }

    // A binder that shadows p hides our parameter; bail. Otherwise the value and body must both be
    // borrow-only in p.
    private bool LetLikeUsesParamOnlyAsBorrowRead(string boundName, Expr value, Expr body, string p)
        => !string.Equals(boundName, p, StringComparison.Ordinal)
            && ParamUsedOnlyAsBorrowRead(value, p)
            && ParamUsedOnlyAsBorrowRead(body, p);

    private bool CallUsesParamOnlyAsBorrowRead(Expr call, string p)
    {
        var args = new List<Expr>();
        var root = CollectCallArgs(call, args);
        if (root is Expr.QualifiedVar qv
            && BorrowReadResourceOps.Contains($"{qv.Module}.{qv.Name}")
            && args.Count >= 1
            && args[0] is Expr.Var rv && string.Equals(rv.Name, p, StringComparison.Ordinal))
        {
            // p is the borrowed resource arg; the remaining args must not use p.
            for (int i = 1; i < args.Count; i++)
            {
                if (!ParamUsedOnlyAsBorrowRead(args[i], p))
                {
                    return false;
                }
            }

            return true;
        }

        if (!ParamUsedOnlyAsBorrowRead(root, p))
        {
            return false;
        }

        return args.All(a => ParamUsedOnlyAsBorrowRead(a, p));
    }

    private bool MatchUsesParamOnlyAsBorrowRead(Expr.Match m, string p)
    {
        if (!ParamUsedOnlyAsBorrowRead(m.Value, p))
        {
            return false;
        }

        foreach (var c in m.Cases)
        {
            if (PatternBinds(c.Pattern, p)
                || !ParamUsedOnlyAsBorrowRead(c.Body, p)
                || (c.Guard is not null && !ParamUsedOnlyAsBorrowRead(c.Guard, p)))
            {
                return false;
            }
        }

        return true;
    }

    // The binary/operator arms of ParamUsedOnlyAsBorrowRead; any other node type is fail-closed.
    private bool BinaryUsesParamOnlyAsBorrowRead(Expr e, string p)
    {
        (Expr Left, Expr Right)? ops = e switch
        {
            Expr.Add x => (x.Left, x.Right),
            Expr.Subtract x => (x.Left, x.Right),
            Expr.Multiply x => (x.Left, x.Right),
            Expr.Divide x => (x.Left, x.Right),
            Expr.Modulo x => (x.Left, x.Right),
            Expr.BitwiseAnd x => (x.Left, x.Right),
            Expr.BitwiseOr x => (x.Left, x.Right),
            Expr.BitwiseXor x => (x.Left, x.Right),
            Expr.ShiftLeft x => (x.Left, x.Right),
            Expr.ShiftRight x => (x.Left, x.Right),
            Expr.GreaterThan x => (x.Left, x.Right),
            Expr.LessThan x => (x.Left, x.Right),
            Expr.GreaterOrEqual x => (x.Left, x.Right),
            Expr.LessOrEqual x => (x.Left, x.Right),
            Expr.Equal x => (x.Left, x.Right),
            Expr.NotEqual x => (x.Left, x.Right),
            Expr.ResultPipe x => (x.Left, x.Right),
            Expr.ResultMapErrorPipe x => (x.Left, x.Right),
            _ => null,
        };

        if (ops is not { } b)
        {
            // Unmodeled node (RecordLit / RecordUpdate / Perform / Handle / …): fail-closed.
            return !MentionsVar(e, p);
        }

        return ParamUsedOnlyAsBorrowRead(b.Left, p) && ParamUsedOnlyAsBorrowRead(b.Right, p);
    }

    // Conservative: does p occur free anywhere in e (used to reject closure capture and unmodeled nodes)?
    private bool MentionsVar(Expr e, string p)
        => FreeVars(e, new HashSet<string>(StringComparer.Ordinal)).Contains(p);
}
