using System.Windows;
using System.Windows.Media;

namespace ClaudeWidget;

public sealed partial class PetControl
{
    private sealed record Outfit(
        DrawingGroup BodyBack, DrawingGroup BodyFront, DrawingGroup Hood, Brush Fabric,
        DrawingGroup Leg, DrawingGroup CuffShade, DrawingGroup Foot, DrawingGroup FootShade,
        double Headroom);

    private static class Clothes
    {
        private static readonly Brush Cotton = Ramp("#FCF5E6", "#E1DBD0", "#BABCB9");
        private static readonly Brush RedWool = Ramp("#DF6254", "#BF4843", "#943D3F");
        private static readonly Brush Denim = Ramp("#454B57", "#292F3C", "#1B2332");
        private static readonly Brush Sole = Ramp("#FFF2DA", "#C6CCCF");
        private static readonly Brush Shoe = Ramp("#4A5260", "#202A3B");
        private static readonly Pen Stitch = Stroke(Paint("#727D8D"), 0.65);
        private static readonly Pen Fold = Stroke(Paint("#737D89"), 0.85);
        private static readonly Pen Lace = Stroke(Paint("#F5ECD9"), 0.85);

        internal static readonly Outfit Portfolio = Make(false);
        internal static readonly Outfit Christmas = Make(true);

        private static Outfit Make(bool holiday)
        {
            var fabric = holiday ? RedWool : Cotton;
            return new Outfit(
                Art(dc => DrawBack(dc, fabric)),
                Art(dc => DrawFront(dc, fabric, holiday)),
                Art(dc => DrawHood(dc, fabric, holiday)),
                fabric,
                Art(dc => DrawTrousers(dc, Denim)),
                Art(dc => DrawTrousers(dc, Paint("#242B39"))),
                Art(dc => DrawSneaker(dc, false)),
                Art(dc => DrawSneaker(dc, true)),
                holiday ? 32 : 0);
        }

        private static void DrawHood(DrawingContext dc, Brush fabric, bool holiday)
        {
            Shape(dc, "M44,75 C38,73 33,76 34,82 C34,90 45,96 58,97 C71,96 82,90 82,82 C83,76 78,73 72,75 Q58,80 44,75 Z",
                fabric, Stroke(Ink, 1.25));
            Shape(dc, "M44,76 Q58,82 72,76 Q78,79 74,83 Q59,90 42,83 Q39,79 44,76 Z",
                holiday ? Paint("#9B413F") : Paint("#A9ACA9"));
            Shape(dc, "M37,81 Q40,89 53,93 M79,81 Q76,89 63,93 M53,93 Q58,95 63,93", null,
                Stroke(holiday ? Paint("#A44440") : Paint("#939E9F"), 0.8));
            Shape(dc, "M39,84 Q43,89 50,90 M77,84 Q73,89 66,90", null,
                Stroke(holiday ? Paint("#EB8B76") : Paint("#F7EDDB"), 0.9));
        }

        private static void DrawBack(DrawingContext dc, Brush fabric)
        {
            Shape(dc, "M45,74 Q57,71 70,74 L73,86 L43,87 Z", Paint("#303A4B"), Outline);
            Shape(dc, "M42,110 L74,110 L72,115 Q58,119 44,115 Z", Denim, Outline);
            Shape(dc, "M40,83 Q58,81 76,83 Q79,84 79,90 L77,107 Q76,111 70,112 H46 Q40,111 39,107 L37,91 Q36,85 40,83 Z",
                fabric);
            Shape(dc, "M39,105 Q39,110 46,111 Q58,112 70,111 Q76,110 77,105", null, Stroke(Ink, 1.25));
        }

        private static void DrawFront(DrawingContext dc, Brush fabric, bool holiday)
        {
            Shape(dc, "M43,83 Q58,86 73,82 L76,88 L74,108 Q59,111 43,108 L41,89 Q40,85 43,83 Z",
                fabric);
            Shape(dc, "M45,83 Q50,86 58,86 Q67,86 72,83 M43,89 Q45,93 44,97 M73,90 L71,97",
                null, Fold);
            Shape(dc, "M42,94 Q45,101 47,107 L44,108 Z M74,95 L71,108 L69.5,107 Z",
                holiday ? Paint("#20702431") : Paint("#20727D8D"));
            // Sznurki kaptura i kieszeń przesuwają się razem z przednim panelem bluzy.
            Shape(dc, "M52,86 L51.3,93 M64,86 L64.7,93", null,
                Stroke(holiday ? Paint("#FFE7CC") : Paint("#667181"), 0.85));
            dc.DrawEllipse(Ink, null, new Point(51.3, 93.4), 0.75, 0.75);
            dc.DrawEllipse(Ink, null, new Point(64.7, 93.4), 0.75, 0.75);
            dc.DrawRoundedRectangle(fabric, Stroke(holiday ? Paint("#953E3D") : Paint("#A4A69F"), 0.6),
                new Rect(54.7, 91.1, 7.5, 7.7), 0.6, 0.6);
            Shape(dc, "M48,100 L46,104 Q58,105.4 70,104 L68,100", null,
                Stroke(holiday ? Paint("#822D38") : Paint("#8C9299"), 0.8));
            Shape(dc, "M42,108 Q58,110 75,108 M44,110 Q58,112 73,110 M47,109 V111 M52,109.6 V111.5 M65,109.6 V111.5 M70,109 V111",
                null,
                Stroke(holiday ? Paint("#F1A297") : Paint("#F8F0DD"), 0.8));
            if (holiday)
            {
                // Mały wzór dzianiny daje strój świąteczny także bez czapki.
                Shape(dc, "M47,96 L49,94 L51,96 M66,96 L68,94 L70,96", null,
                    Stroke(Paint("#F7DDCA"), 0.8));
            }
        }

        internal static void DrawSleeve(DrawingContext dc, Brush fabric, double elbowAngle)
        {
            var shoulder = new Point(0, 0);
            var elbow = new Point(6, 12);
            var lower = new RotateTransform(elbowAngle).Transform(new Point(5, 13));
            var wrist = elbow + new Vector(lower.X, lower.Y);
            var upperDirection = elbow - shoulder;
            upperDirection.Normalize();
            var lowerDirection = wrist - elbow;
            lowerDirection.Normalize();
            var tangent = upperDirection + lowerDirection;
            tangent.Normalize();
            var upperNormal = new Vector(upperDirection.Y, -upperDirection.X);
            var lowerNormal = new Vector(lowerDirection.Y, -lowerDirection.X);
            var elbowNormal = new Vector(tangent.Y, -tangent.X);

            var a = shoulder + upperNormal * 6.7;
            var b = elbow + elbowNormal * 7.2;
            var c = wrist + lowerNormal * 4.6;
            var d = wrist - lowerNormal * 4.6;
            var e = elbow - elbowNormal * 6.4;
            var f = shoulder - upperNormal * 6.7;
            var sleeve = new StreamGeometry();
            using (var path = sleeve.Open())
            {
                path.BeginFigure(a, true, true);
                path.BezierTo(a + upperDirection * 7, b - tangent * 5, b, true, true);
                path.BezierTo(b + tangent * 5, c - lowerDirection * 4, c, true, true);
                path.LineTo(d, true, false);
                path.BezierTo(d - lowerDirection * 4, e + tangent * 5, e, true, true);
                path.BezierTo(e - tangent * 5, f + upperDirection * 7, f, true, true);
                path.BezierTo(f - upperDirection * 7, a - upperDirection * 7, a, true, true);
            }
            dc.DrawGeometry(fabric, null, sleeve);
            // U nasady obrys rękawa otwiera się w korpus bluzy.
            var edge = new StreamGeometry();
            using (var path = edge.Open())
            {
                path.BeginFigure(a + upperDirection * 4, false, false);
                path.BezierTo(a + upperDirection * 8, b - tangent * 5, b, true, true);
                path.BezierTo(b + tangent * 5, c - lowerDirection * 4, c, true, true);
                path.LineTo(d, true, false);
                path.BezierTo(d - lowerDirection * 4, e + tangent * 5, e, true, true);
                path.BezierTo(e - tangent * 5, f + upperDirection * 8, f + upperDirection * 4, true, true);
            }
            dc.DrawGeometry(null, Outline, edge);

            // Krótkie fałdy kończą się wewnątrz rękawa, bez szwu dzielącego łokieć.
            var folds = new StreamGeometry();
            using (var path = folds.Open())
            {
                path.BeginFigure(shoulder - upperNormal * 3 + upperDirection * 3, false, false);
                path.QuadraticBezierTo(shoulder + upperDirection * 5, shoulder + upperNormal * 2 + upperDirection * 6, true, false);
                path.BeginFigure(elbow - elbowNormal * 4 - tangent * 2, false, false);
                path.QuadraticBezierTo(elbow + tangent, elbow + elbowNormal * 2 + tangent * 3, true, false);
            }
            dc.DrawGeometry(null, Fold, folds);

            var cuffStart = wrist - lowerDirection * 3.4;
            var cuff = new StreamGeometry();
            using (var path = cuff.Open())
            {
                path.BeginFigure(cuffStart + lowerNormal * 4.8, true, true);
                path.LineTo(c, true, false);
                path.QuadraticBezierTo(wrist + lowerDirection * 1.1, d, true, false);
                path.LineTo(cuffStart - lowerNormal * 4.8, true, false);
                path.QuadraticBezierTo(cuffStart - lowerDirection * 1.2, cuffStart + lowerNormal * 4.8, true, false);
            }
            dc.DrawGeometry(fabric, Stroke(Ink, 1), cuff);
            for (var rib = -3; rib <= 3; rib += 2)
                dc.DrawLine(Stitch, cuffStart + lowerNormal * rib, wrist + lowerNormal * rib);
        }

        private static void DrawTrousers(DrawingContext dc, Brush fabric)
        {
            Shape(dc, "M-7,-1 Q0,-4 8,-1 Q10,4 8.5,8 Q11,13 7,17 L5.5,20 L-6,20 L-8,16 Q-11,11 -8.5,7 Q-10,2 -7,-1 Z",
                fabric, Outline);
            Shape(dc, "M-5,0 Q-6,4 -3,7 M6,2 L3,6 M-7,9 Q-2,11 4,9 M6,12 L-3,14 M-6,16 L3,17",
                null, Stitch);
            Shape(dc, "M-6,18 Q0,16 6.5,17.5 L6.5,20 Q0,21 -6,20 Z", Paint("#46505E"), Stroke(Ink, 0.9));
            Shape(dc, "M-4.5,19 Q0,18 4.8,19", null, Stroke(Paint("#82909C"), 0.6));
        }

        private static void DrawSneaker(DrawingContext dc, bool shade)
        {
            Shape(dc, "M-7.5,18.5 Q0,16 7.5,18.5 L10,23 Q11,26.5 6,27 L-6,27 Q-11,26.5 -10,23 Z",
                shade ? Paint("#B6BFC6") : Sole, Outline);
            Shape(dc, "M-6.5,17.8 Q0,16 6.5,17.8 L8.5,22.6 Q0,25 -8.5,22.6 Z",
                shade ? Paint("#202938") : Shoe, Stroke(Ink, 1));
            Shape(dc, "M-6,22 Q0,20 6,22 L7,24 Q0,26 -7,24 Z",
                shade ? Paint("#D9D8CF") : Paint("#F6EBDA"), Stroke(Ink, 0.6));
            Shape(dc, "M-3,18 L2.5,19 M3,18 L-2.5,19 M-3.5,19.6 L3,20.7 M3.5,19.6 L-3,20.7", null, Lace);
            Shape(dc, "M-8.5,25 Q0,27 8.5,25", null, Stroke(Paint("#7B838C"), 0.7));
        }

    }
}
