using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MetroHub.Presentation.Dialogs;

public partial class WorkspaceEditDialog : MetroDialog
{
    private string _selectedIconSymbol = "Desktop24";

    public (string Name, string IconSymbol)? Result { get; private set; }

    public WorkspaceEditDialog()
    {
        InitializeComponent();
    }

    public void Setup(string currentName, string? currentIconSymbol = null, bool isCreating = false)
    {
        Title = isCreating ? "New Workspace" : "Edit Workspace";
        Subtitle = isCreating
            ? "Choose a name and icon for your new workspace"
            : "Customize workspace display name and icon";

        WorkspaceNameInput.Text = currentName;
        WorkspaceNameInput.SelectAll();

        string initialSymbol = string.IsNullOrWhiteSpace(currentIconSymbol) ? "Desktop24" : currentIconSymbol;
        _selectedIconSymbol = initialSymbol;

        WorkspaceGlyphPicker.SelectedGlyph = initialSymbol;
        if (Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(initialSymbol, true, out var initialSym))
        {
            SidebarPreviewIcon.Symbol = initialSym;
        }
    }

    protected override IInputElement? InitialFocusedElement => WorkspaceNameInput;

    private void OnGlyphPickerSelectedGlyphChanged(object? sender, string newGlyph)
    {
        _selectedIconSymbol = newGlyph;
        if (Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>(newGlyph, true, out var sym))
        {
            SidebarPreviewIcon.Symbol = sym;
        }
    }

    private void OnWorkspaceNameInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSave();
            e.Handled = true;
        }
    }

    private void OnWorkspaceNameInputTextChanged(object sender, TextChangedEventArgs e)
    {
        bool isValid = !string.IsNullOrWhiteSpace(WorkspaceNameInput.Text);
        WorkspaceNameErrorMessage.Visibility = isValid ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = isValid;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        CommitSave();
    }

    private void CommitSave()
    {
        string name = WorkspaceNameInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            WorkspaceNameErrorMessage.Visibility = Visibility.Visible;
            return;
        }

        Result = (name, _selectedIconSymbol);
        DialogResult = true;
        Close();
    }

    public static (string Name, string IconSymbol)? Show(
        Window? owner,
        string currentName,
        string? currentIconSymbol = null,
        bool isCreating = false)
    {
        var dlg = new WorkspaceEditDialog
        {
            Owner = owner ?? Application.Current?.MainWindow
        };
        dlg.Setup(currentName, currentIconSymbol, isCreating);
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }
}
