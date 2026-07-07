using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace DinoRand.App
{
    // Avalonia port of the WPF LeftPriorityContainer: lays children out in a single row, measuring
    // the "important" child first against the full width and the rest against what remains; on
    // arrange, leftover width is shared among auto-width (NaN) children. Avalonia's Size/availableSize
    // are immutable, so the running width/height are tracked in locals; InternalChildren → Children
    // and the FrameworkElement check → Control (Width lives on Layoutable).
    internal class LeftPriorityContainer : Panel
    {
        private int _importantChildIndex;

        public int ImportantChildIndex
        {
            get => _importantChildIndex;
            set
            {
                if (_importantChildIndex != value)
                {
                    _importantChildIndex = value;
                    InvalidateMeasure();
                }
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double sizeWidth = 0, sizeHeight = 0;
            if (Children.Count > ImportantChildIndex && ImportantChildIndex >= 0)
            {
                var importantChild = Children[ImportantChildIndex];
                importantChild.Measure(availableSize);
                sizeWidth = importantChild.DesiredSize.Width;
                sizeHeight = importantChild.DesiredSize.Height;

                var availWidth = availableSize.Width - sizeWidth;
                var availHeight = sizeHeight;
                for (int i = 0; i < Children.Count; i++)
                {
                    if (i == ImportantChildIndex)
                        continue;

                    var child = Children[i];
                    child.Measure(new Size(availWidth, availHeight));
                    sizeWidth += child.DesiredSize.Width;
                    availWidth -= child.DesiredSize.Width;
                }
            }
            return new Size(sizeWidth, sizeHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var maxWidth = 0.0;
            var sizable = new List<Control>();
            foreach (Control child in Children)
            {
                maxWidth += child.DesiredSize.Width;
                if (double.IsNaN(child.Width))
                {
                    sizable.Add(child);
                }
            }
            var remainder = Math.Max(0, finalSize.Width - maxWidth);
            var sharedRemainder = sizable.Count == 0 ? 0 : remainder / sizable.Count;

            var x = 0.0;
            foreach (Control child in Children)
            {
                var childWidth = child.DesiredSize.Width;
                if (sizable.Contains(child))
                {
                    childWidth += sharedRemainder;
                }
                child.Arrange(new Rect(x, 0, childWidth, finalSize.Height));
                x += childWidth;
            }
            return finalSize;
        }
    }
}
