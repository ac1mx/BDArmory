using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BDAStealth;

// Aspect interpolation: stock BDA (3 nearest, barycentric, else nearest) vs Gaussian kernel, on real rendered aspect data.
static class Aspect
{
    // Port of BDArmory 1.13.1 RadarUtils.RCSMatrixEval.
    public static float Stock(float[,] m, float overall, float weight, float az, float el)
    {
        if (el > 90f) el = 180f - el; else if (el < -90f) el = -180f - el;
        float a1 = 0, e1 = 0, d1 = float.MaxValue, v1 = 0, a2 = 0, e2 = 0, d2 = float.MaxValue, v2 = 0, a3 = 0, e3 = 0, d3 = float.MaxValue, v3 = 0;
        for (int i = 0; i < m.GetLength(0); i++)
        {
            float d = (m[i, 0] - az) * (m[i, 0] - az) + (m[i, 1] - el) * (m[i, 1] - el);
            if (!(d < d3)) continue;
            if (d < d2)
            {
                if (d < d1) { d3 = d2; a3 = a2; e3 = e2; v3 = v2; d2 = d1; a2 = a1; e2 = e1; v2 = v1; d1 = d; a1 = m[i, 0]; e1 = m[i, 1]; v1 = m[i, 2]; }
                else { d3 = d2; a3 = a2; e3 = e2; v3 = v2; d2 = d; a2 = m[i, 0]; e2 = m[i, 1]; v2 = m[i, 2]; }
            }
            else { d3 = d; a3 = m[i, 0]; e3 = m[i, 1]; v3 = m[i, 2]; }
        }
        float den = (e2 - e3) * (a1 - a3) + (a3 - a2) * (e1 - e3);
        float w1 = ((e2 - e3) * (az - a3) + (a3 - a2) * (el - e3)) / den;
        float w2 = ((e3 - e1) * (az - a3) + (a1 - a3) * (el - e3)) / den;
        float w3 = 1f - w1 - w2;
        float v = (w1 > 0f && w2 > 0f && w3 > 0f) ? w1 * v1 + w2 * v2 + w3 * v3 : v1;
        return (1f - weight) * v + weight * overall;
    }

    public static float[,] Parse(string reportPath, string craft)
    {
        string[] lines = File.ReadAllLines(reportPath);
        int start = Array.FindIndex(lines, l => l.StartsWith("=== " + craft));
        string row = lines.Skip(start).First(l => l.StartsWith("Aspect matrix"));
        var vals = row.Substring(row.IndexOf(':') + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray()).ToList();
        var m = new float[vals.Count, 3];
        for (int i = 0; i < vals.Count; i++) { m[i, 0] = vals[i][0]; m[i, 1] = vals[i][1]; m[i, 2] = vals[i][2]; }
        return m;
    }

    /// <summary>Overall RCS like BDA: 75th percentile of the first 83 aspect samples.</summary>
    public static float Overall(float[,] m)
    {
        var v = Enumerable.Range(0, Math.Min(83, m.GetLength(0))).Select(i => (double)m[i, 2]).OrderBy(x => x).ToArray();
        double rank = 0.75 * (v.Length - 1); int lo = (int)rank; double f = rank - lo;
        return (float)(v[lo] + f * (v[Math.Min(lo + 1, v.Length - 1)] - v[lo]));
    }

    public static (float maxStep, int bigSteps) Sweep(Func<float, float> f)
    {
        float maxStep = 0; int big = 0; float prev = f(0f);
        for (float az = 0.5f; az <= 180f; az += 0.5f)
        {
            float cur = f(az);
            float step = Math.Abs(cur - prev) / Math.Max(Math.Min(cur, prev), 1e-4f);
            maxStep = Math.Max(maxStep, step);
            if (step > 0.25f) big++;
            prev = cur;
        }
        return (maxStep, big);
    }
}
