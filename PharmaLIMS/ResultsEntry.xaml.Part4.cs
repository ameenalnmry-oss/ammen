using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PharmaLIMS
{
    public partial class ResultsEntry
    {
        private string PromptForCancellationReason(string initialReason = "")
        {
            Window dialog = new Window
            {
                Title = "Cancel COA",
                Width = 460,
                Height = 250,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = this
            };

            Grid grid = new Grid
            {
                Margin = new Thickness(16)
            };

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock label = new TextBlock
            {
                Text = "Enter cancellation reason. This reason will be saved in the certificate lifecycle audit.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                FontWeight = FontWeights.SemiBold
            };

            TextBox reasonBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 90,
                Text = initialReason ?? string.Empty
            };

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Button okButton = new Button
            {
                Content = "OK",
                Width = 85,
                Height = 32,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };

            Button cancelButton = new Button
            {
                Content = "Cancel",
                Width = 85,
                Height = 32,
                IsCancel = true
            };

            okButton.Click += (s, args) =>
            {
                if (string.IsNullOrWhiteSpace(reasonBox.Text))
                {
                    MessageBox.Show("Cancellation reason is required.", "Cancel COA", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                dialog.DialogResult = true;
                dialog.Close();
            };

            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);

            Grid.SetRow(label, 0);
            Grid.SetRow(reasonBox, 1);
            Grid.SetRow(buttons, 2);

            grid.Children.Add(label);
            grid.Children.Add(reasonBox);
            grid.Children.Add(buttons);

            dialog.Content = grid;
            reasonBox.Focus();

            bool? result = dialog.ShowDialog();
            return result == true ? reasonBox.Text.Trim() : "";
        }
    }
}
