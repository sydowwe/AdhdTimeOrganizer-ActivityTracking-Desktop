using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using AdhdTimeOrganizer.ActivityTracking.Desktop.Services;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop;

/// <summary>
/// The tray's three icons (<see cref="TrayIconState"/>), drawn once at start from the embedded one and disposed on exit. Only
/// the tray's UI timer hands them to the <see cref="NotifyIcon"/>.
/// </summary>
/// <remarks>
/// Each drawn icon is written as a one-image ICO holding a PNG and loaded with <c>new Icon(stream)</c>, so the
/// <see cref="Icon"/> owns its handle: <c>Icon.FromHandle(Bitmap.GetHicon())</c> leaks one GDI handle per icon.
/// </remarks>
internal sealed class TrayIcons : IDisposable
{
    private static readonly Color Amber = Color.FromArgb(0xF5, 0xA6, 0x23);

    private readonly Icon _normal;
    private readonly Icon _paused;
    private readonly Icon _attention;

    public TrayIcons(Icon source)
    {
        var size = SystemInformation.SmallIconSize;
        using var sized = new Icon(source, size);
        using var bitmap = sized.ToBitmap();

        _normal = (Icon)sized.Clone();
        _paused = Draw(bitmap, DrawPaused);
        _attention = Draw(bitmap, DrawAttention);
    }

    public Icon For(TrayIconState state) => state switch
    {
        TrayIconState.Paused => _paused,
        TrayIconState.Attention => _attention,
        _ => _normal
    };

    private static Icon Draw(Bitmap source, Action<Graphics, Bitmap> draw)
    {
        using var canvas = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            draw(g, source);
        }
        return ToIcon(canvas);
    }

    /// <summary>Greyed and faded, with two pause bars in the lower right on a white plate so they read on any taskbar.</summary>
    private static void DrawPaused(Graphics g, Bitmap source)
    {
        var grey = new ColorMatrix(
        [
            [0.30f, 0.30f, 0.30f, 0, 0],
            [0.59f, 0.59f, 0.59f, 0, 0],
            [0.11f, 0.11f, 0.11f, 0, 0],
            [0, 0, 0, 0.55f, 0],
            [0, 0, 0, 0, 1]
        ]);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(grey);
        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height,
            GraphicsUnit.Pixel, attributes);

        var badge = Badge(source);
        using var plate = new SolidBrush(Color.White);
        g.FillRectangle(plate, badge);
        var bar = Math.Max(2f, badge.Width / 3.5f);
        var inset = (badge.Width - 2 * bar) / 3f;
        using var ink = new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20));
        g.FillRectangle(ink, badge.X + inset, badge.Y + 1, bar, badge.Height - 2);
        g.FillRectangle(ink, badge.Right - inset - bar, badge.Y + 1, bar, badge.Height - 2);
    }

    /// <summary>The normal icon with an amber dot, ringed in white, in the lower right.</summary>
    private static void DrawAttention(Graphics g, Bitmap source)
    {
        g.DrawImage(source, 0, 0, source.Width, source.Height);
        var badge = Badge(source);
        using var ring = new SolidBrush(Color.White);
        g.FillEllipse(ring, badge);
        badge.Inflate(-1, -1);
        using var dot = new SolidBrush(Amber);
        g.FillEllipse(dot, badge);
    }

    /// <summary>The lower-right square a badge sits in: a bit over half the icon, so it shows at 16 px.</summary>
    private static RectangleF Badge(Bitmap source)
    {
        var side = (float)Math.Ceiling(source.Width * 0.56);
        return new RectangleF(source.Width - side, source.Height - side, side, side);
    }

    /// <summary>A one-image ICO holding <paramref name="bitmap"/> as a PNG (Windows Vista and later read those).</summary>
    private static Icon ToIcon(Bitmap bitmap)
    {
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);

        var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((short)0);  // reserved
            writer.Write((short)1);  // type: icon
            writer.Write((short)1);  // one image
            writer.Write((byte)(bitmap.Width >= 256 ? 0 : bitmap.Width));
            writer.Write((byte)(bitmap.Height >= 256 ? 0 : bitmap.Height));
            writer.Write((byte)0);   // no palette
            writer.Write((byte)0);   // reserved
            writer.Write((short)1);  // planes
            writer.Write((short)32); // bits per pixel
            writer.Write((int)png.Length);
            writer.Write(6 + 16);    // where the image starts
            writer.Write(png.ToArray());
        }
        ico.Position = 0;
        return new Icon(ico);
    }

    public void Dispose()
    {
        _normal.Dispose();
        _paused.Dispose();
        _attention.Dispose();
    }
}
