using System.Drawing;
using System.Drawing.Drawing2D;
using ClaudeWidget.Native;

namespace ClaudeWidget;

/// <summary>Ikona zasobnika: kropka w kolorze najpilniejszego stanu. Port New-TrayIconHandle.</summary>
public static class TrayIconFactory
{
    public static Icon Create(string hex)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var fill = new SolidBrush(ColorTranslator.FromHtml(hex));
            using var outline = new Pen(Color.FromArgb(200, 24, 24, 24), 2);
            graphics.FillEllipse(fill, 4, 4, 24, 24);
            graphics.DrawEllipse(outline, 4, 4, 24, 24);
        }
        var handle = bitmap.GetHicon();
        // Icon.FromHandle nie przejmuje właściciela uchwytu — wywołujący musi go potem zniszczyć
        // przez NativeMethods.DestroyIcon, inaczej wycieka (GDI nie sprząta go samo).
        return Icon.FromHandle(handle);
    }

    public static void Destroy(Icon icon) => NativeMethods.DestroyIcon(icon.Handle);
}
