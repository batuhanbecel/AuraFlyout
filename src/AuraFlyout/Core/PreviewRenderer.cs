using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AuraFlyout.Core;

/// <summary>
/// Development aid: "AuraFlyout.exe --render-previews &lt;folder&gt;" renders flyouts and settings
/// pages to PNG files without showing any window (useful while a game is in the foreground).
/// </summary>
public static class PreviewRenderer
{
    public static void Render(string folder, IEnumerable<(string Name, Func<FrameworkElement> Create, double Width)> items)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, create, width) in items)
        {
            try
            {
                var element = create();
                var host = new Border
                {
                    Child = element,
                    Padding = new Thickness(16),
                    Width = width > 0 ? width : double.NaN,
                };
                host.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
                host.Measure(new Size(width > 0 ? width : double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();

                int w = (int)Math.Ceiling(host.ActualWidth), h = (int)Math.Ceiling(host.ActualHeight);
                if (w <= 0 || h <= 0) continue;
                var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(host);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(folder, name + ".png"));
                enc.Save(fs);
                host.Child = null;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, name + ".error.txt"), ex.ToString());
            }
        }
    }

    /// <summary>Takes the root element out of a window that is never shown.</summary>
    public static FrameworkElement Detach(Window window, Action? prepare = null)
    {
        prepare?.Invoke();
        var root = (FrameworkElement)window.Content;
        var resources = window.Resources;
        var dataContext = window.DataContext;
        window.Content = null;
        if (root.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue) root.DataContext = dataContext;
        // keep the window's local styles (Tile, Cap, ...) available to the detached content
        foreach (var key in resources.Keys) root.Resources[key] = resources[key];
        if (root is Border b) b.SetResourceReference(Border.BackgroundProperty, "AuraFlyoutSolidBrush");
        return root;
    }
}
