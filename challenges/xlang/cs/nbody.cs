using System;
using System.Globalization;

struct Body { public double x, y, z, vx, vy, vz, mass; }

static class NBody
{
    const double PI = 3.141592653589793;
    const double SolarMass = 4 * PI * PI;
    const double DaysPerYear = 365.24;

    static Body[] Create() => new[]
    {
        new Body { x = 0, y = 0, z = 0, vx = 0, vy = 0, vz = 0, mass = SolarMass },
        new Body { x = 4.84143144246472090, y = -1.16032004402742839, z = -0.103622044471123109,
            vx = 0.00166007664274403694 * DaysPerYear, vy = 0.00769901118419740425 * DaysPerYear,
            vz = -0.0000690460016972063023 * DaysPerYear, mass = 0.000954791938424326609 * SolarMass },
        new Body { x = 8.34336671824457987, y = 4.12479856412430479, z = -0.403523417114321381,
            vx = -0.00276742510726862411 * DaysPerYear, vy = 0.00499852801234917238 * DaysPerYear,
            vz = 0.0000230417297573763929 * DaysPerYear, mass = 0.000285885980666130812 * SolarMass },
        new Body { x = 12.8943695621391310, y = -15.1111514016986312, z = -0.223307578892655734,
            vx = 0.00296460137564761618 * DaysPerYear, vy = 0.00237847173959480950 * DaysPerYear,
            vz = -0.0000296589568540237556 * DaysPerYear, mass = 0.0000436624404335156298 * SolarMass },
        new Body { x = 15.3796971148509165, y = -25.9193146099879641, z = 0.179258772950371181,
            vx = 0.00268067772490389322 * DaysPerYear, vy = 0.00162824170038242295 * DaysPerYear,
            vz = -0.0000951592254519715870 * DaysPerYear, mass = 0.0000515138902046611451 * SolarMass },
    };

    static void OffsetMomentum(Body[] b)
    {
        double px = 0, py = 0, pz = 0;
        for (int i = 0; i < b.Length; i++) { px += b[i].vx * b[i].mass; py += b[i].vy * b[i].mass; pz += b[i].vz * b[i].mass; }
        b[0].vx = -px / SolarMass; b[0].vy = -py / SolarMass; b[0].vz = -pz / SolarMass;
    }

    static void Advance(Body[] b, double dt)
    {
        for (int i = 0; i < 5; i++)
        {
            for (int j = i + 1; j < 5; j++)
            {
                double dx = b[i].x - b[j].x, dy = b[i].y - b[j].y, dz = b[i].z - b[j].z;
                double d2 = dx * dx + dy * dy + dz * dz;
                double mag = dt / (d2 * Math.Sqrt(d2));
                double mj = b[j].mass * mag, mi = b[i].mass * mag;
                b[i].vx -= dx * mj; b[i].vy -= dy * mj; b[i].vz -= dz * mj;
                b[j].vx += dx * mi; b[j].vy += dy * mi; b[j].vz += dz * mi;
            }
        }
        for (int i = 0; i < 5; i++) { b[i].x += dt * b[i].vx; b[i].y += dt * b[i].vy; b[i].z += dt * b[i].vz; }
    }

    static double Energy(Body[] b)
    {
        double e = 0;
        for (int i = 0; i < 5; i++)
        {
            e += 0.5 * b[i].mass * (b[i].vx * b[i].vx + b[i].vy * b[i].vy + b[i].vz * b[i].vz);
            for (int j = i + 1; j < 5; j++)
            {
                double dx = b[i].x - b[j].x, dy = b[i].y - b[j].y, dz = b[i].z - b[j].z;
                e -= b[i].mass * b[j].mass / Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }
        return e;
    }

    static int Main(string[] args)
    {
        int n = args.Length > 0 && int.TryParse(args[0], out int v) ? v : 1000;
        Body[] b = Create();
        OffsetMomentum(b);
        Console.WriteLine(Energy(b).ToString("F9", CultureInfo.InvariantCulture));
        for (int i = 0; i < n; i++) Advance(b, 0.01);
        Console.WriteLine(Energy(b).ToString("F9", CultureInfo.InvariantCulture));
        return 0;
    }
}
