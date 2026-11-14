using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SoundpadCompanion;

/// <summary>The icon in Assets\app.ico (also the exe's icon), for windows and the tray.</summary>
internal static class AppIcon
{
    private static readonly Uri Uri = new("pack://application:,,,/SoundpadCompanion;component/Assets/app.ico", UriKind.Absolute);
    private static ImageSource? _imageSource;

    public static ImageSource? ImageSource
    {
        get
        {
            if (_imageSource is null)
            {
                try { _imageSource = BitmapFrame.Create(Uri); }
                catch (Exception e) when (e is IOException or NotSupportedException) { }
            }
            return _imageSource;
        }
    }

    public static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(Uri);
            if (resource is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException) { }
        return System.Drawing.SystemIcons.Application;
    }
}
