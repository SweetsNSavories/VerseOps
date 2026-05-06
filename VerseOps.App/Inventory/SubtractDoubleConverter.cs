using System.Globalization;
using System.Windows.Data;

namespace VerseOps.App.Inventory;

/// <summary>
/// One-way converter that returns <c>value − parameter</c> as a double.
///
/// Used by the env DataGrid's row-details ScrollViewer to compute
/// <c>MaxHeight = ScrollContentPresenter.ActualHeight − N</c>, so the
/// expanded panel grows to fill the env grid's visible viewport (minus the
/// env row's own header band) instead of being pinned to a fixed cap. This
/// makes "focus mode" — where every other env row is hidden while one is
/// expanded — actually use the freed-up vertical real estate.
///
/// Returns 0 (instead of a negative number) if the subtraction would
/// underflow, so layout never blows up on tiny viewports.
/// </summary>
public sealed class SubtractDoubleConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double d) return 0d;
        var sub = 0d;
        if (parameter != null)
            double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out sub);
        var result = d - sub;
        return result < 0 ? 0d : result;
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
