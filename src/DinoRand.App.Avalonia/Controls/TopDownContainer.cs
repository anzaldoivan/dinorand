using Avalonia;
using Avalonia.Controls;

namespace DinoRand.App
{
    // Avalonia port of the WPF TopDownContainer: reports a fixed MinWidth/MinHeight desired size and
    // overlays all children to fill the final size. MinWidth/MinHeight live on Layoutable in Avalonia.
    internal class TopDownContainer : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            return new Size(MinWidth, MinHeight);
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
