using System.Diagnostics;
using System.Windows;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace VerseOps.App.Inventory;

/// <summary>
/// Modal dialog that shows the raw underlying JSON record for any drill-down
/// row (Solution / Power Page / User / asset). Mirrors the PCF dashboard's
/// "Metadata Inspector" pop-out — same dark chrome, same Copy / Open-in-Maker
/// / Dismiss footer. Inherits FluentWindow so the dialog shows a Fluent v2
/// title bar and Mica backdrop consistent with the main window.
/// </summary>
public partial class MetadataInspectorWindow : FluentWindow
{
    private string? _makerUrl;

    public MetadataInspectorWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Show the inspector. <paramref name="subtitle"/> is the small line under
    /// the title (e.g. "Access Team — solution"); <paramref name="rawJson"/>
    /// is the indented JSON payload; <paramref name="makerUrl"/> populates the
    /// "Open in Maker" button (button is hidden when null/empty).
    /// </summary>
    public void Show(Window? owner, string subtitle, string rawJson, string? makerUrl, DateTime? captureUtc = null)
    {
        Owner = owner;
        HeaderSubtitle.Text = string.IsNullOrEmpty(subtitle) ? "Technical JSON schema" : subtitle.ToUpperInvariant();
        JsonBox.Text = string.IsNullOrEmpty(rawJson)
            ? "// (no underlying record captured)"
            : rawJson;
        _makerUrl = makerUrl;
        OpenMakerBtn.Visibility = string.IsNullOrEmpty(makerUrl) ? Visibility.Collapsed : Visibility.Visible;

        var capture = captureUtc ?? DateTime.UtcNow;
        CaptureLabel.Text = $"Capture date: {capture:yyyy-MM-dd HH:mm} UTC";

        ShowDialog();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(JsonBox.Text ?? string.Empty); }
        catch
        {
            // Clipboard can throw if another app holds the lock — retry once.
            try { Clipboard.SetDataObject(JsonBox.Text ?? string.Empty, copy: true); } catch { /* swallow */ }
        }
    }

    private void OnOpenMaker(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_makerUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_makerUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Could not launch maker portal: " + ex.Message,
                "Open in Maker",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();
}
