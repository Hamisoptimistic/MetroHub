using System.Windows;
using MetroHub.Presentation.Dialogs;

namespace MetroHub.Widgets.Catalog.Template;

/// <summary>
/// OFFICIAL BOILERPLATE DIALOG: Copy this file for your widget's modal dialog!
/// Inherits from MetroDialog to automatically gain:
/// - 155px Frosted DWM Acrylic sidebar
/// - Dark Obsidian form panel with Title and Close [X] button
/// - Escape-key dismissal and screen centering
/// </summary>
public partial class TemplateDialog : MetroDialog
{
    public string ResultLabel { get; private set; } = string.Empty;
    public bool ResultIsFeatureEnabled { get; private set; }

    public TemplateDialog()
    {
        InitializeComponent();
    }

    public void Populate(string label, bool isFeatureEnabled)
    {
        LabelInput.Text = label;
        FeatureToggleCheckbox.IsChecked = isFeatureEnabled;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ResultLabel = LabelInput.Text.Trim();
        ResultIsFeatureEnabled = FeatureToggleCheckbox.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
