using System.Diagnostics;
using System.Windows;
using VerseOps.App.Inventory.Models;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace VerseOps.App.Inventory;

/// <summary>
/// Modal dialog that shows the per-user Microsoft 365 / Power Platform
/// license assignments enriched from Microsoft Graph. Mirrors the
/// <see cref="MetadataInspectorWindow"/> chrome (Mica title bar, brand-blue
/// header chip, footer with Copy / Open in Maker / Dismiss) so the two
/// drill-down dialogs feel like one family.
///
/// Source data: <see cref="UserGroupRow.LicenseDetails"/> (newline-separated
/// SKU list populated by <c>GraphLicenseClient</c>) plus the row's
/// <see cref="UserGroupRow.MakerUrl"/> for the deep-link button.
/// </summary>
public partial class UserLicensesWindow : FluentWindow
{
    private string? _makerUrl;
    private string? _copyPayload;

    public UserLicensesWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Show the dialog for <paramref name="row"/>. Splits
    /// <see cref="UserGroupRow.LicenseDetails"/> on newlines into individual
    /// SKU chips. Empty/null details surface the "no licenses" empty state.
    /// </summary>
    public void Show(Window? owner, UserGroupRow row)
    {
        Owner = owner;

        DisplayNameText.Text = string.IsNullOrWhiteSpace(row.DisplayName)
            ? "(unnamed user)"
            : row.DisplayName;
        UpnText.Text = string.IsNullOrWhiteSpace(row.Identity)
            ? "(no UPN — Dataverse-only account)"
            : row.Identity!;
        AvatarInitials.Text = BuildInitials(row.DisplayName, row.Identity);

        AdminBadge.Visibility = row.IsAdmin ? Visibility.Visible : Visibility.Collapsed;

        _makerUrl = row.MakerUrl;
        OpenMakerBtn.Visibility = string.IsNullOrEmpty(_makerUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var skus = SplitLicenses(row.LicenseDetails);
        LicensesList.ItemsSource = skus;

        if (skus.Length == 0)
        {
            EmptyStateText.Visibility = Visibility.Visible;
            EmptyStateText.Text = "No Microsoft 365 / Power Platform licenses assigned to this account "
                + "(or Microsoft Graph hasn't enumerated this user yet — sign-in needs at least User.Read.All).";
            CountLabel.Text = "0 licenses";
        }
        else
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            CountLabel.Text = skus.Length == 1 ? "1 license" : $"{skus.Length} licenses";
        }

        // Pre-build the clipboard payload — header line + one SKU per line.
        _copyPayload = BuildCopyPayload(row, skus);

        ShowDialog();
    }

    private static string[] SplitLicenses(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return System.Array.Empty<string>();
        return details
            .Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
    }

    private static string BuildInitials(string? displayName, string? upn)
    {
        var source = !string.IsNullOrWhiteSpace(displayName)
            ? displayName!
            : (upn ?? string.Empty);
        if (string.IsNullOrWhiteSpace(source)) return "?";

        var parts = source.Split(new[] { ' ', '.', '_', '-', '@' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return char.ToUpperInvariant(source[0]).ToString();
        if (parts.Length == 1) return char.ToUpperInvariant(parts[0][0]).ToString();
        return string.Concat(char.ToUpperInvariant(parts[0][0]), char.ToUpperInvariant(parts[1][0]));
    }

    private static string BuildCopyPayload(UserGroupRow row, string[] skus)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(row.DisplayName);
        if (!string.IsNullOrWhiteSpace(row.Identity)) sb.AppendLine(row.Identity);
        sb.AppendLine();
        sb.AppendLine(skus.Length == 0 ? "(no licenses assigned)" : "Licenses:");
        foreach (var s in skus) sb.AppendLine("  • " + s);
        return sb.ToString();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_copyPayload ?? string.Empty); }
        catch
        {
            try { Clipboard.SetDataObject(_copyPayload ?? string.Empty, copy: true); } catch { /* swallow */ }
        }
    }

    private void OnOpenMaker(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_makerUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_makerUrl) { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this,
                "Could not launch maker portal: " + ex.Message,
                "Open in Maker",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();
}
