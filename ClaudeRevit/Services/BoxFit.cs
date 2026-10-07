using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeRevit.Services;

// Smallest rectangle around an element's geometry in plan (rotating calipers over the convex
// hull) plus its vertical extent: how an IFC DirectShape wall, column or beam becomes a native
// element's length, width, height and direction. Pure logic, unit-tested.
public sealed record PlanBox(Pt Center, double Length, double Width, double AngleRad, double ZMin, double ZMax)
{
    public Pt Axis => new(Math.Cos(AngleRad), Math.Sin(AngleRad));
    public double Height => ZMax - ZMin;
    public double Volume => Length * Width * Height;
}

public static class BoxFit
{
    public static PlanBox Fit(IReadOnlyList<(double X, double Y, double Z)> points)
    {
        if (points.Count < 3) throw new ArgumentException("Too few points to fit a box.");
        var hull = NormRules.Hull(points.Select(p => (Math.Round(p.X, 3), Math.Round(p.Y, 3))));
        if (hull.Count < 3) throw new ArgumentException("The geometry is flat in plan.");
        double bestArea = double.MaxValue; PlanBox? best = null;
        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % hull.Count];
            var angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
            double c = Math.Cos(angle), s = Math.Sin(angle);
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (var p in hull) { var u = p.X * c + p.Y * s; var v = -p.X * s + p.Y * c; u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v); }
            var area = (u1 - u0) * (v1 - v0);
            if (area >= bestArea - 1e-9) continue;
            bestArea = area;
            double um = (u0 + u1) / 2, vm = (v0 + v1) / 2;
            var center = new Pt(um * c - vm * s, um * s + vm * c);
            double len = u1 - u0, wid = v1 - v0, ang = angle;
            if (wid > len) { (len, wid) = (wid, len); ang += Math.PI / 2; }
            while (ang <= -Math.PI / 2) ang += Math.PI; while (ang > Math.PI / 2) ang -= Math.PI;
            best = new(center, len, wid, ang, 0, 0);
        }
        return best! with { ZMin = points.Min(p => p.Z), ZMax = points.Max(p => p.Z) };
    }
}
