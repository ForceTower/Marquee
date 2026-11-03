using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Marquee.Controls;

/// <summary>
/// A page's content column: centered, as wide as the space allows up to MaxWidth. A plain StackPanel
/// with MaxWidth doesn't stay put, because WinUI centers a width-limited stretched element by its
/// desired width; a tab with narrow content would sit further right than one with wide content.
/// This one always asks for the full width it's offered.
/// </summary>
public sealed partial class PageColumn : StackPanel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        Size content = base.MeasureOverride(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? content.Width : availableSize.Width, content.Height);
    }
}
