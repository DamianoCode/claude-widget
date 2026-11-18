using System.Windows;
using System.Windows.Media;

namespace ClaudeWidget;

public sealed partial class PetControl
{
    private static class Artwork
    {
        private static readonly Pen Seam = Stroke(Paint("#667791"), 1.1);
        private static readonly Brush Bone = Ramp("#FFF2D8", "#E2D9C8", "#A0ADC0");
        private static readonly Brush Ivory = Ramp("#FAEFDB", "#EFE5D3", "#DAD1C2");
        private static readonly Brush Slate = Ramp("#AAB8CC", "#657690", "#34435C");
        private static readonly Brush Red = Ramp("#FF825B", "#EE573C", "#9E382D");
        private static readonly Brush Joint = Paint("#26334C");
        private static readonly Brush CasingDots = Halftone(Paint("#6617243B"), 1.5);
        private static readonly Brush GlassDots = Halftone(Paint("#889EB0CF"), 1.15);
        private static readonly Brush PrintGrain = Halftone(Paint("#5817253D"), 0.92);
        private const string BezelPath = "M36.5,21.1 Q62,19.8 87.5,21.1 Q95,21.4 96,29 L96.5,65.5 Q96.5,73.8 88.5,75.3 Q62,77 35.5,75.3 Q27.5,73.8 27.5,65.5 L28,29 Q29,21.4 36.5,21.1 Z";
        private static readonly Point[] HousingCorners =
        [
            new(36.5, 21.1), new(87.5, 21.1), new(96, 29), new(96.5, 65.5),
            new(88.5, 75.3), new(35.5, 75.3), new(27.5, 65.5), new(28, 29),
        ];
        private static readonly Brush Housing = Ramp("#E9E1D0", "#9EADC1", "#536681");
        private static readonly Geometry Glass = Path("M39,29 Q60,27.5 84.5,29 Q88,29.4 88.8,34 Q90.8,47 89,61 Q88.4,65.5 83.8,66.3 Q61,68 39.6,66.3 Q36,66 35.5,61.5 Q34,48 35.5,35 Q36,30 39,29 Z");

        internal static readonly DrawingGroup Monitor = Art(DrawMonitor);
        internal static readonly DrawingGroup MonitorBack = Art(DrawMonitorBack);
        internal static readonly DrawingGroup BodyBack = Art(DrawBodyBack);
        internal static readonly DrawingGroup BodyFront = Art(DrawBodyFront);
        internal static readonly DrawingGroup UpperArm = Art(DrawUpperArm);
        internal static readonly DrawingGroup Forearm = Art(DrawForearm);
        internal static readonly DrawingGroup OpenHand = Art(DrawOpenHand);
        internal static readonly DrawingGroup RelaxedHand = Art(DrawRelaxedHand);
        internal static readonly DrawingGroup Leg = Art(DrawLeg);
        internal static readonly DrawingGroup Foot = Art(dc => DrawFoot(dc, false));
        // Dalsza noga przy obrocie: ten sam kształt w ciemniejszych kolorach, nakładany z przezroczystością.
        internal static readonly DrawingGroup CuffShade = Art(dc => DrawCuff(dc, Slate));
        internal static readonly DrawingGroup FootShade = Art(dc => DrawFoot(dc, true));

        private static void DrawMonitorBack(DrawingContext dc)
        {
            Shape(dc, BezelPath, Housing, Outline);
            Shape(dc, BezelPath, CasingDots);
            Shape(dc, "M30.5,32 Q30.5,24 37.5,23 Q62,22 86.5,23 Q92,24 93.5,30",
                null, Stroke(Paint("#EDE2CF"), 1.15));
            Shape(dc, "M30.8,55.5 L37,57 M31,59.5 L37.2,61 M31.4,63.5 L37.6,65 M93.2,55.5 L87,57 M93,59.5 L86.8,61 M92.6,63.5 L86.4,65",
                null, Stroke(Ink, 1.25));
        }

        internal static void DrawHousingSides(DrawingContext dc, Matrix front, Matrix back)
        {
            // Każdy pas łączy odpowiadające sobie punkty tego samego odlewu.
            for (var i = 0; i < HousingCorners.Length; i++)
            {
                var a = HousingCorners[i];
                var b = HousingCorners[(i + 1) % HousingCorners.Length];
                var surface = new StreamGeometry();
                using (var path = surface.Open())
                {
                    path.BeginFigure(front.Transform(a), true, true);
                    path.LineTo(back.Transform(a), true, false);
                    path.LineTo(back.Transform(b), true, false);
                    path.LineTo(front.Transform(b), true, false);
                }
                dc.DrawGeometry(Housing, null, surface);
            }
        }

        private static void DrawMonitor(DrawingContext dc)
        {
            Shape(dc, BezelPath, Ivory, Outline);
            Shape(dc, BezelPath, PrintGrain);
            Shape(dc, "M30.5,32 Q30.5,24 37.5,23 Q61,22 85,23 Q90,23 92,27",
                null, Stroke(Paint("#FFF9EC"), 1.25));
            const string bottomEdge = "M29.5,64 Q31,72.2 38.5,73.1 Q62,74.4 85.5,73.1 Q93,72.2 94.5,64 L94.4,68.4 Q93,73.7 86,74.8 Q62,76.3 38,74.8 Q31,73.7 29.6,68.4 Z";
            Shape(dc, bottomEdge, Paint("#A2A9B2"));
            Shape(dc, bottomEdge, CasingDots);
            Shape(dc, "M33,28 L34.5,26.5 M32.5,59 L33,61 M86.5,25 L88,25.5 M83,72 H84.5",
                null, Stroke(Paint("#7D7E80"), 0.55));

            Shape(dc, "M38,27 Q60,25.5 85,27 Q90,27.5 91,33 L91,61 Q90.5,67.5 84,68.5 Q60,70 39,68.5 Q34,68 33.5,62 L33.5,34 Q34,28 38,27 Z",
                Slate, Stroke(Ink, 1.3));
            var phosphor = new RadialGradientBrush
            {
                Center = new Point(0.42, 0.33),
                GradientOrigin = new Point(0.42, 0.33),
                RadiusX = 0.8,
                RadiusY = 0.86,
                GradientStops =
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString("#263854"), 0),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#1A2942"), 0.58),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#0D1628"), 1),
                },
            };
            dc.DrawGeometry(phosphor, Stroke(Ink, 0.7), Glass);
            dc.PushClip(Glass);
            Shape(dc, "M35,34 Q60,26 88,33 L88.8,38 Q62,30.5 35,42 Z", Ramp("#26A8C2E6", "#00A8C2E6"));
            Shape(dc, "M36.8,41 Q35.4,48 37.2,56.5", null, Stroke(Paint("#637995"), 0.8));
            Shape(dc, "M35,60 Q59,66.4 89,60.2 L89,67 H35 Z", Paint("#3910192A"));
            dc.Pop();
            Shape(dc, "M36.5,34 Q37,31 40.5,30.6 Q61,29.1 84.5,30.6 Q87,31 87.8,34",
                null, Stroke(Paint("#788BA7"), 0.84));
            Shape(dc, "M37,61 Q37.7,64.6 41,64.9 Q60,66.4 83,64.9", null, Stroke(Paint("#455875"), 0.8));

            var fade = new RadialGradientBrush(Color.FromArgb(0, 255, 255, 255), Colors.White)
            {
                Center = new Point(0.39, 0.36),
                GradientOrigin = new Point(0.39, 0.36),
                RadiusX = 0.69,
                RadiusY = 0.74,
            };
            fade.GradientStops.Insert(1, new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.59));
            dc.PushOpacityMask(fade);
            dc.DrawGeometry(GlassDots, null, Glass);
            dc.Pop();

            Shape(dc, "M54,72 H62", null, Stroke(Paint("#9197A0"), 0.9));
            dc.DrawEllipse(Paint("#54637A"), null, new Point(85, 71.3), 1.05, 1.05);
            dc.DrawEllipse(Paint("#F7E7C9"), null, new Point(85, 71.1), 0.45, 0.45);
        }

        private static void DrawBodyBack(DrawingContext dc)
        {
            Shape(dc, "M47,73 Q57,74 68,71 L67,83 Q60,89 49,86 Z", Joint, Outline);
            Shape(dc, "M50,79 Q59,82 66,77", null, Seam);
            Shape(dc, "M44,102 L72,102 L71,112 Q58,118 45,112 Z", Slate, Outline);
            Shape(dc, "M39,84 L50,79 L67,79 L77,84 L76,101 Q75,110 67,112 L49,112 Q40,110 39,102 Z",
                Slate, Outline);
        }

        private static void DrawBodyFront(DrawingContext dc)
        {
            Shape(dc, "M46,83 Q60,81 73,83 Q77,84 76,89 L74,101 Q73,107 67,108 L51,108 Q46,107 45,102 L43,89 Q42,85 46,83 Z",
                Bone, Outline);
            Shape(dc, "M45,89 Q46,85 52,85 L69,85", null, Stroke(Porcelain, 1.15));
            dc.DrawRoundedRectangle(Joint, null, new Rect(52, 89, 17, 12), 4, 4);
            dc.DrawEllipse(Paint("#5C708C"), null, new Point(58.5, 94.9), 4.15, 4.15);
            Shape(dc, "M64,93.5 H66 M64,96.5 H66 M52,104 H64", null, Seam);
        }

        private static void DrawUpperArm(DrawingContext dc)
        {
            Shape(dc, "M-3,-4 Q2,-6 5,-2 L10,9 Q12,14 8,16 Q3,17 1,13 L-5,4 Q-7,-1 -3,-4 Z",
                Slate, Outline);
            dc.DrawEllipse(Red, Outline, new Point(0, 0), 4.8, 4.8);
            dc.DrawEllipse(Joint, null, new Point(0, 0), 1.75, 1.75);
            dc.DrawEllipse(Joint, null, new Point(6, 12), 3.6, 3.6);
        }

        private static void DrawForearm(DrawingContext dc)
        {
            Shape(dc, "M-3,0 Q1,-4 5,0 L10,10 Q11,15 6,17 Q2,18 0,14 L-4,4 Z", Bone, Outline);
            Shape(dc, "M-2,4 L1,11 M4,14 L8,12", null, Seam);
            dc.DrawEllipse(Joint, null, new Point(5, 13), 3.4, 3.1);
        }

        private static void DrawOpenHand(DrawingContext dc)
        {
            // Kciuk i trzy palce w oddzielnych profilach, czytelnych także w trakcie machania.
            Shape(dc, "M-4,-2 Q-1,-4 3,-2 Q5,-1 6,2 L9,2 Q12,3 11,5 Q10,7 7,6 L7,8 Q7,11 4.5,11 L4,9 Q4,12 1.8,12 L0.5,10 Q-1,12 -3,10 L-5,6 Q-8,6 -8,4 Q-8,2 -5,2 Z",
                Red, Outline);
            Shape(dc, "M-4,3 Q-1,1 2,3 M-2,6 L0,8 M1,6 L3,8", null, Stroke(Paint("#81332E"), 0.95));
            Shape(dc, "M-4,1 Q-1,-1 2,0", null, Stroke(Paint("#FFB085"), 1.15));
        }

        private static void DrawRelaxedHand(DrawingContext dc)
        {
            Shape(dc, "M-3,-1 Q0,-3 3,-1 Q5,0 5.5,3 L5.5,6.5 Q5.5,9 3.1,9 L1.8,8.5 Q0,10 -1.8,8 L-3,6 Q-5.5,6 -5.5,4 Q-5.4,2 -3.7,1.5 Z",
                Red, Outline);
            Shape(dc, "M-3.4,3.2 Q-1.1,1.4 0,4.2 M1.6,5.2 L2.2,7.4", null, Stroke(Paint("#80372F"), 0.9));
            Shape(dc, "M-1.8,0.3 Q0,-0.9 2,0.2", null, Stroke(Paint("#FFB085"), 1.05));
        }

        private static void DrawLeg(DrawingContext dc)
        {
            Shape(dc, "M-6,-2 Q1,-4 6,-1 L6,10 Q5,14 0,14 Q-6,14 -6,10 Z", Slate, Outline);
            dc.DrawEllipse(Joint, Outline, new Point(0, 10), 5.1, 4);
            DrawCuff(dc, Bone);
        }

        private static void DrawCuff(DrawingContext dc, Brush fill) =>
            Shape(dc, "M-5.5,12 Q0,10.5 5.5,12 L6.5,19 L-6.5,19 Z", fill, Outline);

        /// <summary>But z przodu, symetryczny. Skręt w 3/4 robi PetControl.DrawLeg (pochylenie czubka).</summary>
        private static void DrawFoot(DrawingContext dc, bool far)
        {
            Shape(dc, "M-7,18 Q0,15.5 7,18 L9.5,22 Q10.5,26.5 6,27 L-6,27 Q-10.5,26.5 -9.5,22 Z", far ? Slate : Bone, Outline);
            Shape(dc, "M-9,23.6 Q0,25.6 9,23.6", null, Stroke(far ? Paint("#A94B42") : Paint("#D65C45"), 1.55));
            Shape(dc, "M-3,18.6 H3", null, Stroke(far ? Paint("#A2B1C6") : Porcelain, 1.1));
        }

        private static Brush Halftone(Brush ink, double radius)
        {
            var drawing = Art(dc =>
            {
                dc.DrawEllipse(ink, null, new Point(2, 2), radius, radius * 0.88);
                dc.DrawEllipse(ink, null, new Point(6.1, 5.6), radius * 0.72, radius * 0.7);
                dc.DrawEllipse(ink, null, new Point(3.4, 9.1), radius * 0.58, radius * 0.46);
            });
            var tile = new DrawingBrush(drawing)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, 9, 11),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 9, 11),
                TileMode = TileMode.Tile,
                Transform = new ScaleTransform(0.17, 0.17),
            };
            tile.Freeze();
            return tile;
        }
    }
}
