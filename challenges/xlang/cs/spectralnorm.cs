using System;
using System.Globalization;

static class SpectralNorm
{
    static double A(int i, int j) => 1.0 / ((i + j) * (i + j + 1) / 2 + i + 1);

    static void MulAv(double[] v, double[] outv)
    {
        int n = v.Length;
        for (int i = 0; i < n; i++) { double s = 0; for (int j = 0; j < n; j++) s += A(i, j) * v[j]; outv[i] = s; }
    }
    static void MulAtv(double[] v, double[] outv)
    {
        int n = v.Length;
        for (int i = 0; i < n; i++) { double s = 0; for (int j = 0; j < n; j++) s += A(j, i) * v[j]; outv[i] = s; }
    }
    static void MulAtAv(double[] v, double[] outv, double[] tmp) { MulAv(v, tmp); MulAtv(tmp, outv); }

    static int Main(string[] args)
    {
        int n = args.Length > 0 && int.TryParse(args[0], out int x) ? x : 100;
        double[] u = new double[n]; double[] v = new double[n]; double[] tmp = new double[n];
        for (int i = 0; i < n; i++) u[i] = 1.0;
        for (int i = 0; i < 10; i++) { MulAtAv(u, v, tmp); MulAtAv(v, u, tmp); }
        double vbv = 0, vv = 0;
        for (int i = 0; i < n; i++) { vbv += u[i] * v[i]; vv += v[i] * v[i]; }
        Console.WriteLine(Math.Sqrt(vbv / vv).ToString("F9", CultureInfo.InvariantCulture));
        return 0;
    }
}
