using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MetroHub.Presentation.Dialogs;

public partial class WorkspaceRenameDialog : MetroDialog
{
    public string? ResultName { get; private set; }

    public WorkspaceRenameDialog()
    {
        InitializeComponent();
    }

    public void Setup(string currentName)
    {
        WorkspaceNameInput.Text = currentName;
        WorkspaceNameInput.SelectAll();
    }

    protected override IInputElement? InitialFocusedElement => WorkspaceNameInput;

    private void OnWorkspaceNameInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSave();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
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

        ResultName = name;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static string? Show(Window? owner, string currentName)
    {
        var dlg = new WorkspaceRenameDialog
        {
            Owner = owner
        };
        dlg.Setup(currentName);
        return dlg.ShowDialog() == true ? dlg.ResultName : null;
    }
}
