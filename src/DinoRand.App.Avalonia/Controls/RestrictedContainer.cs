using System;
using Avalonia;
using Avalonia.Controls;

namespace DinoRand.App
{
    // Avalonia port of the WPF RestrictedContainer: measures children to the largest of their
    // desired sizes, but reports 0 along any axis that is constrained (non-infinite), so it never
    // forces the parent to grow on a bounded axis. Avalonia's Size is immutable, so the running
    // max is kept in locals rather than mutated on the struct.
    internal class RestrictedContainer : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            double width = 0, height = 0;
            foreach (Control child in Children)
            {
                child.Measure(availableSize);
                width = Math.Max(width, child.DesiredSize.Width);
                height = Math.Max(height, child.DesiredSize.Height);
            }
            if (!double.IsInfinity(availableSize.Width))
                width = 0;
            if (!double.IsInfinity(availableSize.Height))
                height = 0;
            return new Size(width, height);
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
