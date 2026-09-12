using System;

static class Fannkuch
{
    static (int, int) Run(int n)
    {
        int[] perm = new int[n];
        int[] perm1 = new int[n];
        int[] count = new int[n];
        for (int i = 0; i < n; i++) perm1[i] = i;
        int maxFlips = 0, checksum = 0;
        long permCount = 0;
        int r = n;
        while (true)
        {
            while (r != 1) { count[r - 1] = r; r--; }
            Array.Copy(perm1, perm, n);
            int flips = 0;
            while (true)
            {
                int k = perm[0];
                if (k == 0) break;
                int k2 = (k + 1) >> 1;
                for (int i = 0; i < k2; i++) { (perm[i], perm[k - i]) = (perm[k - i], perm[i]); }
                flips++;
            }
            if (flips > maxFlips) maxFlips = flips;
            checksum += permCount % 2 == 0 ? flips : -flips;
            while (true)
            {
                if (r == n) return (checksum, maxFlips);
                int perm0 = perm1[0];
                int i = 0;
                while (i < r) { perm1[i] = perm1[i + 1]; i++; }
                perm1[r] = perm0;
                count[r]--;
                if (count[r] > 0) break;
                r++;
            }
            permCount++;
        }
    }

    static int Main(string[] args)
    {
        int n = args.Length > 0 && int.TryParse(args[0], out int x) ? x : 7;
        (int checksum, int maxFlips) = Run(n);
        Console.WriteLine($"{checksum}\nPfannkuchen({n}) = {maxFlips}");
        return 0;
    }
}
