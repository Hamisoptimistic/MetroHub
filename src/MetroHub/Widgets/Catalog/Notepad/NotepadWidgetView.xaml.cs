using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace MetroHub.Widgets.Catalog.Notepad;

public partial class NotepadWidgetView : UserControl
{
    private bool _isSynchronizing;
    private bool _isDocumentDirty;
    private DispatcherTimer? _debounceTimer;
    private NotepadWidgetViewModel? _hookedVm;

    public NotepadWidgetView()
    {
        InitializeComponent();

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            if (_isDocumentDirty)
            {
                SynchronizeToViewModel();
            }
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PreviewKeyDown -= OnPreviewKeyDown;
        PreviewKeyDown += OnPreviewKeyDown;
        EditorBox.TextChanged -= OnEditorTextChanged;
        EditorBox.TextChanged += OnEditorTextChanged;
        EditorBox.SelectionChanged -= OnEditorSelectionChanged;
        EditorBox.SelectionChanged += OnEditorSelectionChanged;
        EditorBox.LostFocus -= OnEditorLostFocus;
        EditorBox.LostFocus += OnEditorLostFocus;

        HookViewModel(DataContext as NotepadWidgetViewModel);
        LoadDocumentFromViewModel();
        EnforceEditorTypography();
        ApplyWordWrap(_hookedVm?.IsWordWrapEnabled ?? true);
        UpdatePlaceholder();
        UpdateFormattingState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _debounceTimer?.Stop();
        SynchronizeToViewModel();

        PreviewKeyDown -= OnPreviewKeyDown;
        EditorBox.TextChanged -= OnEditorTextChanged;
        EditorBox.SelectionChanged -= OnEditorSelectionChanged;
        EditorBox.LostFocus -= OnEditorLostFocus;

        UnhookViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is NotepadWidgetViewModel oldVm)
        {
            oldVm.RequestDocumentSync = null;
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        UnhookViewModel();
        HookViewModel(e.NewValue as NotepadWidgetViewModel);

        if (IsLoaded)
        {
            LoadDocumentFromViewModel();
            EnforceEditorTypography();
            ApplyWordWrap(_hookedVm?.IsWordWrapEnabled ?? true);
            UpdatePlaceholder();
            UpdateFormattingState();
        }
    }

    private void HookViewModel(NotepadWidgetViewModel? vm)
    {
        if (vm == null) return;
        if (_hookedVm != null && _hookedVm != vm)
        {
            UnhookViewModel();
        }

        _hookedVm = vm;
        _hookedVm.PropertyChanged -= OnViewModelPropertyChanged;
        _hookedVm.PropertyChanged += OnViewModelPropertyChanged;
        _hookedVm.RequestDocumentSync = SynchronizeToViewModel;
    }

    private void UnhookViewModel()
    {
        if (_hookedVm != null)
        {
            _hookedVm.RequestDocumentSync = null;
            _hookedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _hookedVm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotepadWidgetViewModel.IsWordWrapEnabled) && _hookedVm != null)
        {
            ApplyWordWrap(_hookedVm.IsWordWrapEnabled);
        }
        else if (e.PropertyName is nameof(NotepadWidgetViewModel.CurrentMode) or nameof(NotepadWidgetViewModel.IsTodoMode))
        {
            UpdateFormattingState();
        }
    }

    private void EnforceEditorTypography()
    {
        if (EditorBox.Document != null)
        {
            ApplyFlowDocumentStyles(EditorBox.Document);
        }
    }

    private void ApplyFlowDocumentStyles(FlowDocument doc)
    {
        doc.PagePadding = new Thickness(0);
        doc.Background = Brushes.Transparent;

        if (TryFindResource("TextPrimaryBrush") is Brush brush)
        {
            doc.Foreground = brush;
            EditorBox.Foreground = brush;
        }
        if (TryFindResource("AppFontFamily") is FontFamily font)
        {
            doc.FontFamily = font;
            EditorBox.FontFamily = font;
        }
        if (TryFindResource("TypeBodyFontSize") is double size)
        {
            doc.FontSize = size;
            EditorBox.FontSize = size;
        }

        // Tightly calibrated List and ListItem styles for sleek, premium indentation
        var listStyle = new Style(typeof(List));
        listStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(14, 2, 0, 2)));
        listStyle.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(0)));
        listStyle.Setters.Add(new Setter(List.MarkerOffsetProperty, 6.0));
        doc.Resources[typeof(List)] = listStyle;

        var listItemStyle = new Style(typeof(ListItem));
        listItemStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 1, 0, 1)));
        listItemStyle.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(0)));
        doc.Resources[typeof(ListItem)] = listItemStyle;

        var paraStyle = new Style(typeof(Paragraph));
        paraStyle.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0, 1, 0, 1)));
        doc.Resources[typeof(Paragraph)] = paraStyle;
    }

    private void ApplyWordWrap(bool isEnabled)
    {
        if (EditorBox?.Document == null) return;
        if (isEnabled)
        {
            EditorBox.Document.PageWidth = double.NaN;
            EditorBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        else
        {
            EditorBox.Document.PageWidth = 4000;
            EditorBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SynchronizeAndSave();
            e.Handled = true;
            return;
        }

        // Ctrl+0: Reset zoom to 100%
        if ((e.Key == Key.D0 || e.Key == Key.NumPad0) && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        // Handle Enter in List: exit gracefully if pressing enter on an empty bullet/numbered item
        if (e.Key == Key.Enter)
        {
            if (EditorBox.IsFocused)
            {
                var caret = EditorBox.CaretPosition;
                var para = caret.Paragraph;
                if (para != null && para.Parent is ListItem listItem && listItem.Parent is List parentList)
                {
                    var range = new TextRange(para.ContentStart, para.ContentEnd);
                    var text = range.Text.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        // Current list item is blank: toggle list off to exit list cleanly
                        if (parentList.MarkerStyle == TextMarkerStyle.Decimal)
                        {
                            EditingCommands.ToggleNumbering.Execute(null, EditorBox);
                        }
                        else
                        {
                            EditingCommands.ToggleBullets.Execute(null, EditorBox);
                        }
                        e.Handled = true;
                        UpdatePlaceholder();
                        UpdateFormattingState();
                        SynchronizeToViewModel();
                        return;
                    }
                }
            }
        }
    }

    private double _zoomScale = 1.0;

    private void OnEditorPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control) return;

        e.Handled = true;
        double step = e.Delta > 0 ? 0.1 : -0.1;
        _zoomScale = Math.Clamp(Math.Round(_zoomScale + step, 2), 0.7, 2.0);

        if (Math.Abs(_zoomScale - 1.0) < 0.01)
        {
            _zoomScale = 1.0;
            EditorBox.LayoutTransform = Transform.Identity;
        }
        else
        {
            EditorBox.LayoutTransform = new ScaleTransform(_zoomScale, _zoomScale);
        }
    }

    private void ResetZoom()
    {
        _zoomScale = 1.0;
        if (EditorBox != null)
        {
            EditorBox.LayoutTransform = Transform.Identity;
        }
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholder();

        if (_isSynchronizing || DataContext is not NotepadWidgetViewModel vm) return;

        _isDocumentDirty = true;
        _debounceTimer?.Stop();
        _debounceTimer?.Start();

        try
        {
            var range = new TextRange(EditorBox.Document.ContentStart, EditorBox.Document.ContentEnd);
            var plainText = range.Text.TrimEnd('\r', '\n');
            vm.UpdateTextMetrics(plainText);
        }
        catch
        {
            // Suppress intermediate parsing faults
        }
    }

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateFormattingState();
    }

    private void UpdateFormattingState()
    {
        try
        {
            var accentBrush = (TryFindResource("SystemAccentColorSolidBrush") ?? TryFindResource("SystemAccentColorPrimaryBrush")) as Brush;
            var primaryBrush = TryFindResource("TextPrimaryBrush") as Brush;
            if (primaryBrush == null || accentBrush == null) return;

            // In To-Do mode, rich text document tools are inactive
            if (DataContext is NotepadWidgetViewModel vm && vm.IsTodoMode)
            {
                if (BoldIcon != null) BoldIcon.Foreground = primaryBrush;
                if (ItalicIcon != null) ItalicIcon.Foreground = primaryBrush;
                if (UnderlineIcon != null) UnderlineIcon.Foreground = primaryBrush;
                if (NumberListIcon != null) NumberListIcon.Foreground = primaryBrush;
                if (BulletListIcon != null) BulletListIcon.Foreground = primaryBrush;
                return;
            }

            if (EditorBox?.Selection?.Start == null) return;

            // Bold detection
            var weight = EditorBox.Selection.GetPropertyValue(TextElement.FontWeightProperty);
            var isBold = weight is FontWeight fw && (fw == FontWeights.Bold || fw == FontWeights.SemiBold || fw == FontWeights.ExtraBold);
            if (BoldIcon != null)
            {
                BoldIcon.Foreground = isBold ? accentBrush : primaryBrush;
            }

            // Italic detection
            var style = EditorBox.Selection.GetPropertyValue(TextElement.FontStyleProperty);
            var isItalic = style is FontStyle fs && fs == FontStyles.Italic;
            if (ItalicIcon != null)
            {
                ItalicIcon.Foreground = isItalic ? accentBrush : primaryBrush;
            }

            // Underline detection
            var deco = EditorBox.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
            var isUnderline = deco is TextDecorationCollection tdc && tdc.Count > 0;
            if (UnderlineIcon != null)
            {
                UnderlineIcon.Foreground = isUnderline ? accentBrush : primaryBrush;
            }

            // List detection (Numbering vs Bullets)
            var currentPara = EditorBox.Selection.Start.Paragraph;
            var parentListItem = currentPara?.Parent as ListItem;
            var parentList = parentListItem?.Parent as List;

            var isNumbered = parentList != null && parentList.MarkerStyle == TextMarkerStyle.Decimal;
            if (NumberListIcon != null)
            {
                NumberListIcon.Foreground = isNumbered ? accentBrush : primaryBrush;
            }

            var isBulleted = parentList != null && (parentList.MarkerStyle == TextMarkerStyle.Disc || parentList.MarkerStyle == TextMarkerStyle.Circle || parentList.MarkerStyle == TextMarkerStyle.Square);
            if (BulletListIcon != null)
            {
                BulletListIcon.Foreground = isBulleted ? accentBrush : primaryBrush;
            }
        }
        catch
        {
            // Ignore format inspection faults on transitional edits
        }
    }

    private void UpdatePlaceholder()
    {
        if (PlaceholderText == null || EditorBox?.Document == null) return;
        var range = new TextRange(EditorBox.Document.ContentStart, EditorBox.Document.ContentEnd);
        var text = range.Text.Trim();
        PlaceholderText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        SynchronizeToViewModel();
    }

    private void EnsureNotesMode()
    {
        if (DataContext is NotepadWidgetViewModel vm && vm.IsTodoMode)
        {
            vm.SwitchToNotesMode();
        }
    }

    // Direct, reliable execution of formatting commands
    private void OnToggleNumberingClick(object sender, RoutedEventArgs e)
    {
        EnsureNotesMode();
        EditorBox.Focus();
        EditingCommands.ToggleNumbering.Execute(null, EditorBox);
        UpdatePlaceholder();
        UpdateFormattingState();
        SynchronizeToViewModel();
    }

    private void OnToggleBulletsClick(object sender, RoutedEventArgs e)
    {
        EnsureNotesMode();
        EditorBox.Focus();
        EditingCommands.ToggleBullets.Execute(null, EditorBox);
        UpdatePlaceholder();
        UpdateFormattingState();
        SynchronizeToViewModel();
    }

    private void OnToggleBoldClick(object sender, RoutedEventArgs e)
    {
        EnsureNotesMode();
        EditorBox.Focus();
        EditingCommands.ToggleBold.Execute(null, EditorBox);
        UpdateFormattingState();
        SynchronizeToViewModel();
    }

    private void OnToggleItalicClick(object sender, RoutedEventArgs e)
    {
        EnsureNotesMode();
        EditorBox.Focus();
        EditingCommands.ToggleItalic.Execute(null, EditorBox);
        UpdateFormattingState();
        SynchronizeToViewModel();
    }

    private void OnToggleUnderlineClick(object sender, RoutedEventArgs e)
    {
        EnsureNotesMode();
        EditorBox.Focus();
        EditingCommands.ToggleUnderline.Execute(null, EditorBox);
        UpdateFormattingState();
        SynchronizeToViewModel();
    }

    private void SynchronizeToViewModel()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(SynchronizeToViewModel);
            return;
        }

        _debounceTimer?.Stop();
        if (_isSynchronizing || DataContext is not NotepadWidgetViewModel vm || EditorBox?.Document == null) return;

        try
        {
            _isSynchronizing = true;
            _isDocumentDirty = false;
            var range = new TextRange(EditorBox.Document.ContentStart, EditorBox.Document.ContentEnd);
            var plainText = range.Text.TrimEnd('\r', '\n');
            var xaml = XamlWriter.Save(EditorBox.Document);
            vm.UpdateTextMetrics(plainText, xaml);
        }
        catch
        {
            // Suppress serialization faults
        }
        finally
        {
            _isSynchronizing = false;
        }
    }

    private void SynchronizeAndSave()
    {
        SynchronizeToViewModel();
        if (DataContext is NotepadWidgetViewModel vm)
        {
            vm.SaveCommand.Execute(null);
        }
    }

    private void LoadDocumentFromViewModel()
    {
        if (DataContext is not NotepadWidgetViewModel vm) return;

        try
        {
            _isSynchronizing = true;
            if (!string.IsNullOrWhiteSpace(vm.DocumentXaml))
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(vm.DocumentXaml));
                if (XamlReader.Load(stream) is FlowDocument doc)
                {
                    ApplyFlowDocumentStyles(doc);
                    EditorBox.Document = doc;
                    _isDocumentDirty = false;
                    return;
                }
            }

            if (!string.IsNullOrEmpty(vm.PlainText))
            {
                var fallbackDoc = new FlowDocument();
                var para = new Paragraph(new Run(vm.PlainText));
                fallbackDoc.Blocks.Add(para);
                ApplyFlowDocumentStyles(fallbackDoc);
                EditorBox.Document = fallbackDoc;
                _isDocumentDirty = false;
            }
        }
        catch
        {
            // Retain plain text fallback on corrupt XAML
            if (!string.IsNullOrEmpty(vm.PlainText))
            {
                var fallbackDoc = new FlowDocument();
                var para = new Paragraph(new Run(vm.PlainText));
                fallbackDoc.Blocks.Add(para);
                ApplyFlowDocumentStyles(fallbackDoc);
                EditorBox.Document = fallbackDoc;
                _isDocumentDirty = false;
            }
        }
        finally
        {
            _isSynchronizing = false;
        }
    }
}
