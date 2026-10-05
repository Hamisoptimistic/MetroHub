using System.Windows;
using System.Windows.Input;

namespace MetroHub.Presentation.Dialogs;

public partial class WorkspaceDeleteDialog : MetroDialog
{
    public WorkspaceDeleteDialog()
    {
        InitializeComponent();
        KeyDown += OnDialogKeyDown;
    }

    public void Setup(string workspaceName)
    {
        WorkspaceWarningText.Text = $"All tiles in \"{workspaceName}\" will be archived to the workspace trash folder. The active canvas will switch to an available workspace.";
    }

    private void OnDialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogResult = true;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public static bool Show(Window? owner, string workspaceName)
    {
        var dlg = new WorkspaceDeleteDialog
        {
            Owner = owner
        };
        dlg.Setup(workspaceName);
        return dlg.ShowDialog() == true;
    }
}
