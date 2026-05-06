using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VerseOps.App.Inventory;

/// <summary>
/// Maps a string to <see cref="Visibility"/>: empty/null/whitespace becomes
/// <see cref="Visibility.Collapsed"/>; anything else stays <see cref="Visibility.Visible"/>.
///
/// Used by the env grid's Instance URL column so the "open in browser" icon
/// only shows for envs that actually have a Dataverse instance URL (Teams /
/// Developer envs without Dataverse have an empty <c>InstanceUrl</c>, and
/// clicking a launch button there would do nothing useful).
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
