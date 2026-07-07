using Avalonia;
using Avalonia.Controls;

namespace DinoRand.App
{
    // Avalonia port of the WPF GrowlessContainer: reports zero desired size (never grows the
    // layout) but still arranges its children to fill whatever space it is given — an overlay
    // panel. Children/Control replace WPF's InternalChildren/UIElement.
    internal class GrowlessContainer : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(0, 0);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (Control child in Children)
            {
                child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            }
            return finalSize;
        }
    }
}
