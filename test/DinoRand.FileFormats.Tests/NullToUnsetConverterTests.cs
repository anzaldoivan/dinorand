using System.Globalization;
using Avalonia;
using DinoRand.App;
using Xunit;

namespace DinoRand.FileFormats.Tests;

/// <summary>
/// The seed-border / status-label brush converter: a concrete brush passes through, a <c>null</c>
/// brush becomes <see cref="AvaloniaProperty.UnsetValue"/> so the bound target falls back to its
/// themed default (the WPF code-behind's <c>ClearValue</c> semantics). ConvertBack is always Unset.
/// </summary>
public class NullToUnsetConverterTests
{
    private static object Convert(object? value) =>
        NullToUnsetConverter.Instance.Convert(value!, typeof(object), null!, CultureInfo.InvariantCulture);

    [Fact]
    public void Non_null_value_passes_through()
    {
        var brush = new object();
        Assert.Same(brush, Convert(brush));
    }

    [Fact]
    public void Null_maps_to_unset_so_the_target_uses_its_themed_default()
    {
        Assert.Equal(AvaloniaProperty.UnsetValue, Convert(null));
    }

    [Fact]
    public void ConvertBack_is_always_unset()
    {
        Assert.Equal(AvaloniaProperty.UnsetValue,
            NullToUnsetConverter.Instance.ConvertBack("x", typeof(object), null!, CultureInfo.InvariantCulture));
    }
}
