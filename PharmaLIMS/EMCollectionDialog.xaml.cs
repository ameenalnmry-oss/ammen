using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PharmaLIMS.Infrastructure;

namespace PharmaLIMS
{
    public partial class EMCollectionDialog : Window
    {
        private readonly ObservableCollection<CollectionItem> _items;
        private bool _loading;
        private CollectionItem? _displayedItem;
        private readonly bool _controlReadingMode;

        public IReadOnlyList<CollectionItem> Results => _items;

        public EMCollectionDialog(string planNo, IEnumerable<EMPlanning.PlanSampleRow> samples, bool controlReadingMode = false)
        {
            InitializeComponent();
            _controlReadingMode = controlReadingMode;
            lblPlan.Text = controlReadingMode
                ? "Plan: " + planNo + " | Record the final incubated negative-control result."
                : "Plan: " + planNo + " | Complete every sample before final electronic signature.";
            IEnumerable<EMPlanning.PlanSampleRow> source = controlReadingMode
                ? samples.Where(s => s.IsNegativeControl)
                : samples;
            _items = new ObservableCollection<CollectionItem>(source.Select(s => new CollectionItem(s)));
            lstSamples.ItemsSource = _items;
            if (_items.Count > 0) lstSamples.SelectedIndex = 0;
        }

        private CollectionItem? Current => lstSamples.SelectedItem as CollectionItem;

        private void lstSamples_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Current == null) return;
            if (_displayedItem != null) CaptureControls(_displayedItem);
            _displayedItem = Current;
            LoadItem(Current);
        }

        private void LoadItem(CollectionItem item)
        {
            _loading = true;
            txtCode.Text = item.SampleCode;
            txtArea.Text = item.AreaName;
            txtMethod.Text = item.Method;
            txtLocation.Text = item.SamplingLocation;
            SelectCombo(cboPlateCondition, item.PlateCondition);
            SelectCombo(cboKitCondition, item.KitCondition);
            txtStart.Text = item.SamplingStartText;
            txtEnd.Text = item.SamplingEndText;
            txtMin.Text = item.TransportMinText;
            txtMax.Text = item.TransportMaxText;
            SelectCombo(cboControl, item.NegativeControlResult);
            pnlControl.Visibility = item.IsNegativeControl ? Visibility.Visible : Visibility.Collapsed;
            txtStart.IsEnabled = !item.IsNegativeControl;
            txtEnd.IsEnabled = !item.IsNegativeControl;
            validationBanner.Visibility = Visibility.Collapsed;
            lblProgress.Text = $"Sample {lstSamples.SelectedIndex + 1} of {_items.Count}";
            _loading = false;
        }

        private bool SaveCurrent(bool showValidation)
        {
            CollectionItem? item = _displayedItem;
            if (item == null) return false;
            CaptureControls(item);
            string error = ValidateItem(item, _controlReadingMode);
            if (string.IsNullOrEmpty(error))
            {
                NormalizeSamplingTimes(item);
                txtStart.Text = item.SamplingStartText;
                txtEnd.Text = item.SamplingEndText;
                validationBanner.Visibility = Visibility.Collapsed;
                return true;
            }
            if (showValidation)
            {
                lblValidation.Text = error;
                validationBanner.Visibility = Visibility.Visible;
            }
            return false;
        }

        private void CaptureControls(CollectionItem item)
        {
            item.PlateCondition = SelectedText(cboPlateCondition);
            item.KitCondition = SelectedText(cboKitCondition);
            item.SamplingStartText = txtStart.Text.Trim();
            item.SamplingEndText = txtEnd.Text.Trim();
            item.TransportMinText = txtMin.Text.Trim();
            item.TransportMaxText = txtMax.Text.Trim();
            item.NegativeControlResult = SelectedText(cboControl);
        }

        private static string ValidateItem(CollectionItem item, bool controlReadingMode)
        {
            if (string.IsNullOrWhiteSpace(item.PlateCondition))
                return "Select the actual Plate / Swab Condition. Excursions are recorded and controlled; they are not hidden by validation.";
            if (string.IsNullOrWhiteSpace(item.KitCondition))
                return "Select the actual Kit / Media Release Status. Excursions are recorded and controlled; they are not hidden by validation.";
            if (!decimal.TryParse(item.TransportMinText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal min) ||
                !decimal.TryParse(item.TransportMaxText, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal max))
                return "Enter valid transport minimum and maximum temperatures using numbers.";
            if (max < min) return "Transport maximum temperature cannot be less than minimum temperature.";
            if (item.IsNegativeControl)
            {
                if (controlReadingMode &&
                    !item.NegativeControlResult.Equals("No Growth", StringComparison.OrdinalIgnoreCase) &&
                    !item.NegativeControlResult.Equals("Growth Detected", StringComparison.OrdinalIgnoreCase))
                    return "Select the final negative-control result after incubation.";
                return string.Empty;
            }
            if (!TryDate(item.SamplingStartText, out DateTime start) || !TryDate(item.SamplingEndText, out DateTime end))
                return "Enter sampling start and end using yyyy-MM-dd HH:mm.";
            if (end < start) return "Sampling end cannot be earlier than sampling start.";
            return string.Empty;
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveCurrent(true)) return;
            if (lstSamples.SelectedIndex < _items.Count - 1) lstSamples.SelectedIndex++;
            else MessageBox.Show("Last sample reached. Select Finish Collection to run final validation.", "EM Collection", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Previous_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrent(false);
            if (lstSamples.SelectedIndex > 0) lstSamples.SelectedIndex--;
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveCurrent(true)) return;
            for (int index = 0; index < _items.Count; index++)
            {
                string error = ValidateItem(_items[index], _controlReadingMode);
                if (!string.IsNullOrEmpty(error))
                {
                    lstSamples.SelectedIndex = index;
                    lblValidation.Text = error;
                    validationBanner.Visibility = Visibility.Visible;
                    MessageBox.Show($"Sample {_items[index].SampleCode} is incomplete.\n\n{error}", "Collection Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                Next_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            WindowUsability.TryMoveFocusOnEnter(e);
        }

        private static string SelectedText(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim() ?? string.Empty;

        private static void SelectCombo(ComboBox combo, string value)
        {
            combo.SelectedIndex = -1;
            foreach (object entry in combo.Items)
                if (entry is ComboBoxItem item && string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                { combo.SelectedItem = item; return; }
        }

        private static readonly string[] AcceptedDateTimeFormats =
        {
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd H:mm",
            "yyyy-dd-MM HH:mm",
            "yyyy-dd-MM H:mm"
        };

        private static bool TryDate(string text, out DateTime value) =>
            DateTime.TryParseExact(text?.Trim(), AcceptedDateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out value);

        private static void NormalizeSamplingTimes(CollectionItem item)
        {
            if (item.IsNegativeControl) return;
            if (TryDate(item.SamplingStartText, out DateTime start))
                item.SamplingStartText = start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (TryDate(item.SamplingEndText, out DateTime end))
                item.SamplingEndText = end.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        public sealed class CollectionItem
        {
            public CollectionItem(EMPlanning.PlanSampleRow source)
            {
                PlanSampleID=source.PlanSampleID; AreaID=source.AreaID; AreaName=source.AreaName; Method=source.Method;
                SamplingLocation=source.SamplingLocation; SampleCode=source.SampleCode; IsNegativeControl=source.IsNegativeControl;
                PlateCondition=source.PlateCondition; KitCondition=source.KitCondition; SamplingStartText=source.SamplingStartText;
                SamplingEndText=source.SamplingEndText; TransportMinText=source.TransportMinText; TransportMaxText=source.TransportMaxText;
                NegativeControlResult=source.NegativeControlResult;
            }
            public int PlanSampleID { get; } public int? AreaID { get; } public string AreaName { get; } public string Method { get; }
            public string SamplingLocation { get; } public string SampleCode { get; } public bool IsNegativeControl { get; }
            public string PlateCondition { get; set; } = ""; public string KitCondition { get; set; } = "";
            public string SamplingStartText { get; set; } = ""; public string SamplingEndText { get; set; } = "";
            public string TransportMinText { get; set; } = ""; public string TransportMaxText { get; set; } = "";
            public string NegativeControlResult { get; set; } = "";
        }
    }
}
