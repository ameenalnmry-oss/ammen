using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PharmaLIMS.Infrastructure
{
    /// <summary>
    /// Shared usability behavior for operational data-entry windows.
    /// It keeps large windows inside the available desktop, enables two-axis
    /// scrolling, and makes Enter advance to the next writable field.
    /// </summary>
    public static class WindowUsability
    {
        public static readonly DependencyProperty EnableResponsiveEntryProperty =
            DependencyProperty.RegisterAttached(
                "EnableResponsiveEntry",
                typeof(bool),
                typeof(WindowUsability),
                new PropertyMetadata(false, OnEnableResponsiveEntryChanged));

        public static readonly DependencyProperty KeepEnterBehaviorProperty =
            DependencyProperty.RegisterAttached(
                "KeepEnterBehavior",
                typeof(bool),
                typeof(WindowUsability),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static void SetEnableResponsiveEntry(DependencyObject element, bool value) =>
            element.SetValue(EnableResponsiveEntryProperty, value);

        public static bool GetEnableResponsiveEntry(DependencyObject element) =>
            (bool)element.GetValue(EnableResponsiveEntryProperty);

        public static void SetKeepEnterBehavior(DependencyObject element, bool value) =>
            element.SetValue(KeepEnterBehaviorProperty, value);

        public static bool GetKeepEnterBehavior(DependencyObject element) =>
            (bool)element.GetValue(KeepEnterBehaviorProperty);

        private static void OnEnableResponsiveEntryChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            if (dependencyObject is not Window window)
                return;

            if ((bool)args.NewValue)
            {
                window.Loaded += Window_Loaded;
                window.PreviewKeyDown += Window_PreviewKeyDown;
            }
            else
            {
                window.Loaded -= Window_Loaded;
                window.PreviewKeyDown -= Window_PreviewKeyDown;
            }
        }

        private static void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window)
                return;

            if (window.ResizeMode != ResizeMode.NoResize)
                window.ResizeMode = ResizeMode.CanResizeWithGrip;

            window.WindowState = WindowState.Maximized;
            EnableTwoAxisScrolling(window);
        }

        private static void EnableTwoAxisScrolling(DependencyObject parent)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is ScrollViewer viewer)
                {
                    // Respect a window's explicit scrollbar policy. User Management, for example,
                    // deliberately disables horizontal scrolling so the editor stays fully visible.
                    // Only supply responsive defaults when the view did not set a local value.
                    if (viewer.ReadLocalValue(ScrollViewer.HorizontalScrollBarVisibilityProperty) == DependencyProperty.UnsetValue)
                        viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                    if (viewer.ReadLocalValue(ScrollViewer.VerticalScrollBarVisibilityProperty) == DependencyProperty.UnsetValue)
                        viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    viewer.PanningMode = PanningMode.Both;
                }

                EnableTwoAxisScrolling(child);
            }
        }

        private static void Window_PreviewKeyDown(object sender, KeyEventArgs e) =>
            TryMoveFocusOnEnter(e);

        public static bool TryMoveFocusOnEnter(KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
                return false;

            DependencyObject? source = e.OriginalSource as DependencyObject;
            if (source == null || GetKeepEnterBehavior(source))
                return false;

            if (FindAncestor<DataGrid>(source) != null || FindAncestor<ButtonBase>(source) != null)
                return false;

            Control? currentInput = FindInputControl(source);
            if (currentInput == null || !IsWritableInput(currentInput))
                return false;

            if (currentInput is TextBox textBox && textBox.AcceptsReturn)
                return false;

            if (currentInput is ComboBox comboBox && comboBox.IsDropDownOpen)
                return false;

            if (!MoveToNextWritableInput(currentInput))
                return false;

            e.Handled = true;
            return true;
        }

        private static bool MoveToNextWritableInput(Control currentInput)
        {
            IInputElement? startingFocus = Keyboard.FocusedElement;
            UIElement? traversalElement = startingFocus as UIElement ?? currentInput;

            for (int attempt = 0; attempt < 96; attempt++)
            {
                if (!traversalElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                    return false;

                IInputElement? nextFocus = Keyboard.FocusedElement;
                if (nextFocus == null || ReferenceEquals(nextFocus, startingFocus))
                    return false;

                DependencyObject? nextObject = nextFocus as DependencyObject;
                Control? nextInput = nextObject == null ? null : FindInputControl(nextObject);
                if (nextInput != null && !ReferenceEquals(nextInput, currentInput) && IsWritableInput(nextInput))
                {
                    ActivateInput(nextInput);
                    return true;
                }

                traversalElement = nextFocus as UIElement;
                if (traversalElement == null)
                    return false;
            }

            return false;
        }

        private static bool IsWritableInput(Control control)
        {
            if (!control.IsEnabled || !control.IsVisible || !control.Focusable)
                return false;

            return control switch
            {
                TextBox textBox => !textBox.IsReadOnly,
                ComboBox => true,
                DatePicker => true,
                PasswordBox => true,
                _ => false
            };
        }

        private static void ActivateInput(Control control)
        {
            control.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                control.BringIntoView();
                control.Focus();
                Keyboard.Focus(control);

                switch (control)
                {
                    case TextBox textBox:
                        textBox.SelectAll();
                        break;
                    case PasswordBox passwordBox:
                        passwordBox.SelectAll();
                        break;
                    case ComboBox comboBox when comboBox.IsEditable:
                        comboBox.ApplyTemplate();
                        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox editableText)
                        {
                            editableText.Focus();
                            editableText.SelectAll();
                        }
                        break;
                    case DatePicker datePicker:
                        datePicker.ApplyTemplate();
                        if (datePicker.Template.FindName("PART_TextBox", datePicker) is TextBox dateText)
                        {
                            dateText.Focus();
                            dateText.SelectAll();
                        }
                        break;
                }
            }));
        }

        private static Control? FindInputControl(DependencyObject source)
        {
            DependencyObject? current = source;
            Control? innerTextInput = null;
            while (current != null)
            {
                if (current is ComboBox comboBox)
                    return comboBox;
                if (current is DatePicker datePicker)
                    return datePicker;
                if (innerTextInput == null && current is TextBox textBox)
                    innerTextInput = textBox;
                if (innerTextInput == null && current is PasswordBox passwordBox)
                    innerTextInput = passwordBox;

                current = GetParent(current);
            }

            return innerTextInput;
        }

        private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is T match)
                    return match;
                current = GetParent(current);
            }
            return null;
        }

        private static DependencyObject? GetParent(DependencyObject child)
        {
            if (child is ContentElement contentElement)
                return ContentOperations.GetParent(contentElement) ??
                       (contentElement is FrameworkContentElement frameworkContent
                           ? frameworkContent.Parent
                           : null);

            return VisualTreeHelper.GetParent(child) ??
                   (child is FrameworkElement frameworkElement ? frameworkElement.Parent : null);
        }
    }
}
