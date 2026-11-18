using System.Windows;
using System.Windows.Media;

namespace ClaudeWidget;

public sealed partial class PetControl
{
    private static class Hat
    {
        internal static readonly DrawingGroup Drawing = Art(dc =>
        {
            var wool = Ramp("#DF6254", "#BF4843", "#943D3F");
            var fur = Ramp("#FCF5E6", "#E1DBD0", "#BABCB9");

            Shape(dc, "M30,21 C34,5 44,-12 58,-16 C77,-20 89,-1 94,23 L83,28 L36,27 Z",
                wool, Outline);
            Shape(dc, "M60,-15 C75,-15 87,0 91,19 Q82,16 77,6 Q70,-8 60,-15 Z",
                Paint("#34762F38"));
            Shape(dc, "M59,-15 C44,-16 33,-6 29,5 L37,8 Q43,-4 57,-6 L64,-10 Z", wool);
            Shape(dc, "M44,-12 Q33,-6 29,5", null, Outline);
            Shape(dc, "M40,5 Q47,-4 57,-5 M60,-12 Q72,-12 80,0", null,
                Stroke(Paint("#ED9881"), 1.15));
            Shape(dc, "M37,17 Q42,11 46,7 M82,18 Q77,12 76,7", null,
                Stroke(Paint("#9D403E"), 1));

            Shape(dc, "M32,18 Q60,13 88,18 Q95,19 95,24 Q95,30 88,31 Q61,27 34,31 Q27,30 27,25 Q27,20 32,18 Z",
                fur, Outline);
            Shape(dc, "M33,24 Q59,19 89,24", null, Stroke(Paint("#FFF8E8"), 1.5));
            Shape(dc, "M34,28 Q59,24 88,28", null, Stroke(Paint("#B6BDB7"), 0.75));
            Shape(dc, "M38,25 L39,28 M49,23.5 L49.5,26 M72,23.5 L71.5,26 M84,25 L83,28", null,
                Stroke(Paint("#D1CFC5"), 0.7));

            Shape(dc, "M23,3 C26,0 31,0 34,3 C38,5 38,11 34,15 C32,18 26,18 23,15 C19,13 19,6 23,3 Z",
                fur, Outline);
            Shape(dc, "M23,6 Q24,3.5 27,3.5 M23,12 Q25,15 28,15 M33,12 L34,10", null,
                Stroke(Paint("#C1C8BF"), 0.8));
        });
    }
}
