using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MetroHub.Widgets.Catalog.Notepad;

/// <summary>
/// Interaction logic for NotepadWidgetView.xaml.
/// Implements keyboard shortcuts, smart line transformation, and seamless focus management.
/// </summary>
public partial class NotepadWidgetView : UserControl
{
    public NotepadWidgetView()
    {
        InitializeComponent();
    }

    private void BulletListTile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NotepadWidgetViewModel vm) return;

        if (!vm.IsNotesView)
        {
            vm.SwitchToNotes();
        }

        int caret = NotesEditorTextBox.SelectionStart;
        int len = NotesEditorTextBox.SelectionLength;
        vm.TransformLineList(ref caret, ref len, "Bullet");
        NotesEditorTextBox.Focus();
        NotesEditorTextBox.Select(Math.Clamp(caret, 0, NotesEditorTextBox.Text.Length), len);
    }

    private void NumberedListTile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NotepadWidgetViewModel vm) return;

        if (!vm.IsNotesView)
        {
            vm.SwitchToNotes();
        }

        int caret = NotesEditorTextBox.SelectionStart;
        int len = NotesEditorTextBox.SelectionLength;
        vm.TransformLineList(ref caret, ref len, "Numbered");
        NotesEditorTextBox.Focus();
        NotesEditorTextBox.Select(Math.Clamp(caret, 0, NotesEditorTextBox.Text.Length), len);
    }

    private void TodoListTile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NotepadWidgetViewModel vm) return;

        if (vm.IsTodoView)
        {
            vm.SwitchToNotes();
            NotesEditorTextBox.Focus();
        }
        else
        {
            vm.SwitchToTodo();
            NewTaskTextBox.Focus();
        }
    }

    private void NotesEditorTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.None))
        {
            if (DataContext is NotepadWidgetViewModel vm)
            {
                int caret = NotesEditorTextBox.CaretIndex;
                if (vm.HandleEnterKeyPress(ref caret))
                {
                    e.Handled = true;
                    NotesEditorTextBox.CaretIndex = Math.Clamp(caret, 0, NotesEditorTextBox.Text.Length);
                }
            }
        }
        else if (e.Key == Key.Tab)
        {
            if (DataContext is NotepadWidgetViewModel vm)
            {
                int caret = NotesEditorTextBox.SelectionStart;
                int len = NotesEditorTextBox.SelectionLength;
                bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                if (vm.HandleTabKeyPress(ref caret, ref len, isShift))
                {
                    e.Handled = true;
                    NotesEditorTextBox.Focus();
                    NotesEditorTextBox.Select(Math.Clamp(caret, 0, NotesEditorTextBox.Text.Length), len);
                }
            }
        }
    }

    private void NotesEditorTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // When Alt-Tabbing or focusing via keyboard, WPF automatically selects all text.
        // Clear full auto-selection so the entire note isn't highlighted.
        if (NotesEditorTextBox.SelectionLength > 0 && NotesEditorTextBox.SelectionLength == NotesEditorTextBox.Text.Length)
        {
            NotesEditorTextBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (NotesEditorTextBox.SelectionLength == NotesEditorTextBox.Text.Length)
                {
                    NotesEditorTextBox.SelectionLength = 0;
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void NewTaskTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is NotepadWidgetViewModel vm)
            {
                vm.AddTask();
                e.Handled = true;
            }
        }
    }

    private void NewTaskTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (NewTaskTextBox.SelectionLength > 0 && NewTaskTextBox.SelectionLength == NewTaskTextBox.Text.Length)
        {
            NewTaskTextBox.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (NewTaskTextBox.SelectionLength == NewTaskTextBox.Text.Length)
                {
                    NewTaskTextBox.SelectionLength = 0;
                }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }
}
