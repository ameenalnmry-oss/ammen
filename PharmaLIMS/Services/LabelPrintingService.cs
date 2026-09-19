using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Printing;
using System.Windows.Input;

namespace PharmaLIMS.Services
{
    public static class LabelPrintingService
    {
        private const double DipPerMm = 96.0 / 25.4;
        private const double SafeMarginMm = 5.0;

        public static bool PrintWaterSampleLabel(
            Window owner,
            string sampleNumber,
            string sampleType,
            string samplingPoint,
            string samplingDate,
            string sampledBy,
            string status)
        {
            var fields = new List<(string Label, string Value)>
            {
                ("TYPE", sampleType),
                ("POINT", samplingPoint),
                ("SAMPLED", samplingDate),
                ("BY", sampledBy),
                ("STATUS", status),
                ("HOLD", "Keep protected; test promptly")
            };

            return PrintLabel(owner, "WATER SAMPLE", sampleNumber, fields, 100, 50);
        }

        public static bool PrintCultureMediaLabel(
            Window owner,
            string preparationNumber,
            string mediaName,
            string lotNumber,
            DateTime? preparationDate,
            DateTime? expiryDate,
            string preparedBy,
            string releaseStatus,
            string finalPh,
            string quantity,
            string additives)
        {
            var fields = new List<(string Label, string Value)>
            {
                ("Medium", mediaName),
                ("Quantity", quantity),
                ("Preparation No.", preparationNumber),
                ("Date/Sign", FormatDate(preparationDate) + FormatSign(preparedBy)),
                ("Use before Date", FormatDate(expiryDate)),
                ("Additives", additives),
                ("Source Lot", lotNumber),
                ("Status", releaseStatus),
                ("pH", finalPh)
            };

            return PrintLabel(owner, "LABEL FOR MEDIUM", preparationNumber, fields, 100, 60);
        }

        public static bool PrintDehydratedMediumPackLabel(
            Window owner,
            string serialNumber,
            string mpmNumber,
            string packIdentity,
            DateTime? receiptDate,
            string packNumber,
            string totalPacks,
            DateTime? openingDate,
            DateTime? releaseDate,
            string sign,
            string mediaName,
            string lotNumber,
            DateTime? expiryDate,
            string status)
        {
            var fields = new List<(string Label, string Value)>
            {
                ("Serial Number of medium", serialNumber),
                ("MPM No.", mpmNumber),
                ("Pack Identity no.", BuildPackIdentity(packIdentity, receiptDate, packNumber, totalPacks)),
                ("Sign", sign),
                ("Date of opening / Sign", FormatDate(openingDate) + FormatSign(sign)),
                ("Date of release of lot / Sign", FormatDate(releaseDate) + FormatSign(sign)),
                ("Medium", mediaName),
                ("Batch / Lot No.", lotNumber),
                ("Expiry", FormatDate(expiryDate)),
                ("Status", status)
            };

            return PrintLabel(owner, "LABEL FOR PACK OF DEHYDRATED MEDIUM", serialNumber, fields, 100, 60);
        }

        public static bool PrintReleasedMediaLabel(
            Window owner,
            string mediaName,
            string lotNumber,
            DateTime? releaseDate,
            DateTime? validUpToOrExpiry,
            string signedBy)
        {
            var fields = new List<(string Label, string Value)>
            {
                ("Name of Media", mediaName),
                ("B. No / Lot No.", lotNumber),
                ("Date of Release", FormatDate(releaseDate)),
                ("Valid upto / Expiry", FormatDate(validUpToOrExpiry)),
                ("Sign / Date", signedBy + FormatSign(FormatDate(DateTime.Today))),
                ("Status", "Released Media")
            };

            return PrintLabel(owner, "RELEASED MEDIA", lotNumber, fields, 100, 50);
        }

        private static bool PrintLabel(
            Window owner,
            string title,
            string primaryNumber,
            IReadOnlyList<(string Label, string Value)> fields,
            double widthMm,
            double heightMm)
        {
            return ShowPreviewAndPrint(owner, title, primaryNumber, fields, widthMm, heightMm);
        }

        private static bool ShowPreviewAndPrint(
            Window owner,
            string title,
            string primaryNumber,
            IReadOnlyList<(string Label, string Value)> fields,
            double widthMm,
            double heightMm)
        {
            double labelWidth = widthMm * DipPerMm;
            double labelHeight = heightMm * DipPerMm;
            double previewScale = Math.Min(1.6, Math.Min(720 / labelWidth, 430 / labelHeight));

            var copiesBox = new TextBox
            {
                Text = "1",
                Width = 70,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var previewPage = CreateLabelPage(title, primaryNumber, fields, labelWidth, labelHeight);
            var previewLabel = new Viewbox
            {
                Width = labelWidth * previewScale,
                Height = labelHeight * previewScale,
                Stretch = Stretch.Uniform,
                Child = previewPage
            };
            var printed = false;

            var previewWindow = new Window
            {
                Title = title + " Label Preview",
                Owner = owner,
                Width = Math.Max(760, (labelWidth * previewScale) + 90),
                Height = Math.Max(560, (labelHeight * previewScale) + 170),
                MinWidth = 720,
                MinHeight = 520,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(244, 247, 251))
            };

            var root = new DockPanel { Margin = new Thickness(16) };

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var titleBlock = new TextBlock
            {
                Text = title + " Label Preview",
                FontFamily = new FontFamily("Arial"),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };
            header.Children.Add(titleBlock);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var commandPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            commandPanel.Children.Add(new TextBlock
            {
                Text = "Copies",
                FontFamily = new FontFamily("Arial"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            });
            commandPanel.Children.Add(copiesBox);

            var printButton = new Button
            {
                Content = "Print",
                Width = 110,
                Height = 34,
                Margin = new Thickness(12, 0, 0, 0),
                FontWeight = FontWeights.Bold
            };
            var closeButton = new Button
            {
                Content = "Close",
                Width = 100,
                Height = 34,
                Margin = new Thickness(8, 0, 0, 0)
            };
            commandPanel.Children.Add(printButton);
            commandPanel.Children.Add(closeButton);
            DockPanel.SetDock(commandPanel, Dock.Bottom);
            root.Children.Add(commandPanel);

            var previewHost = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = previewLabel
            };

            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = previewHost
            });

            previewWindow.Content = root;
            closeButton.Click += (_, _) => previewWindow.Close();
            copiesBox.PreviewTextInput += NumericOnly;
            copiesBox.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Space)
                    e.Handled = true;
            };

            printButton.Click += (_, _) =>
            {
                int copies = ParseCopies(copiesBox.Text);
                if (copies <= 0)
                {
                    MessageBox.Show(previewWindow, "Copies must be at least 1.", "Label Print", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (PrintLabelCopies(previewWindow, title, primaryNumber, fields, widthMm, heightMm, copies))
                {
                    printed = true;
                    previewWindow.Close();
                }
            };

            previewWindow.ShowDialog();
            return printed;
        }

        private static bool PrintLabelCopies(
            Window owner,
            string title,
            string primaryNumber,
            IReadOnlyList<(string Label, string Value)> fields,
            double widthMm,
            double heightMm,
            int copies)
        {
            var dialog = new PrintDialog();

            double width = widthMm * DipPerMm;
            double height = heightMm * DipPerMm;

            try
            {
                PrintTicket ticket = dialog.PrintTicket ?? new PrintTicket();
                ticket.PageOrientation = PageOrientation.Portrait;
                ticket.PageMediaSize = new PageMediaSize(width, height);
                dialog.PrintTicket = ticket;
            }
            catch
            {
                // Some label-printer drivers require the stock size to be selected manually.
            }

            if (dialog.ShowDialog() != true)
                return false;

            var document = new FixedDocument();

            for (int copy = 0; copy < copies; copy++)
            {
                var page = CreateLabelPage(title, primaryNumber, fields, width, height);
                var pageContent = new PageContent();
                ((IAddChild)pageContent).AddChild(page);
                document.Pages.Add(pageContent);
            }

            dialog.PrintDocument(document.DocumentPaginator, $"{title} - {primaryNumber}");
            return true;
        }

        private static FixedPage CreateLabelPage(
            string title,
            string primaryNumber,
            IReadOnlyList<(string Label, string Value)> fields,
            double width,
            double height)
        {
            double safeMargin = SafeMarginMm * DipPerMm;
            double contentWidth = Math.Max(40, width - (safeMargin * 2));
            double contentHeight = Math.Max(30, height - (safeMargin * 2));

            var page = new FixedPage
            {
                Width = width,
                Height = height,
                Background = Brushes.White
            };

            var outer = new Border
            {
                Width = contentWidth,
                Height = contentHeight,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1.2),
                Padding = new Thickness(6, 4, 6, 4),
                Background = Brushes.White,
                Child = BuildContent(title, primaryNumber, fields)
            };

            FixedPage.SetLeft(outer, safeMargin);
            FixedPage.SetTop(outer, safeMargin);
            page.Children.Add(outer);
            page.Measure(new Size(width, height));
            page.Arrange(new Rect(new Size(width, height)));
            page.UpdateLayout();

            return page;
        }

        private static int ParseCopies(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int copies))
                return 0;

            return Math.Max(0, Math.Min(500, copies));
        }

        private static void NumericOnly(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        private static UIElement BuildContent(
            string title,
            string primaryNumber,
            IReadOnlyList<(string Label, string Value)> fields)
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            UIElement header = BuildLabelHeader();
            root.Children.Add(header);

            var titleBlock = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            titleBlock.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Arial"),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center
            });
            titleBlock.Children.Add(new TextBlock
            {
                Text = primaryNumber,
                FontFamily = new FontFamily("Arial"),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center
            });
            Grid.SetRow(titleBlock, 1);
            root.Children.Add(titleBlock);

            var details = new Grid();
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;
            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field.Value))
                    continue;

                details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = field.Label + ":",
                    FontFamily = new FontFamily("Arial"),
                    FontSize = 8.2,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 0.5, 4, 0.5)
                };
                var value = new TextBlock
                {
                    Text = field.Value.Trim(),
                    FontFamily = new FontFamily("Arial"),
                    FontSize = 8.2,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0.5, 0, 0.5)
                };
                Grid.SetRow(label, row);
                Grid.SetColumn(label, 0);
                Grid.SetRow(value, row);
                Grid.SetColumn(value, 1);
                details.Children.Add(label);
                details.Children.Add(value);
                row++;
            }

            Grid.SetRow(details, 2);
            root.Children.Add(details);

            var footer = new TextBlock
            {
                Text = "Generated by PharmaLIMS - " + DateTime.Now.ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture),
                FontFamily = new FontFamily("Arial"),
                FontSize = 6.5,
                Foreground = Brushes.DimGray,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            };
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            return root;
        }

        private static UIElement BuildLabelHeader()
        {
            var panel = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Image? logo = TryCreateLogo(30, 18);
            if (logo != null)
            {
                Grid.SetColumn(logo, 0);
                panel.Children.Add(logo);
            }

            var company = new TextBlock
            {
                Text = "MEDICA PHARMACEUTICAL INDUSTRY",
                FontFamily = new FontFamily("Arial"),
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(company, 1);
            panel.Children.Add(company);
            return panel;
        }

        private static Image? TryCreateLogo(double width, double height)
        {
            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string localLogo = Path.Combine(baseDirectory, "medica-logo.png");

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = File.Exists(localLogo)
                    ? new Uri(localLogo, UriKind.Absolute)
                    : new Uri("pack://siteoforigin:,,,/medica-logo.png", UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                return new Image
                {
                    Source = bitmap,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            catch
            {
                return null;
            }
        }

        private static string FormatDate(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static string FormatSign(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : " / " + value.Trim();
        }

        private static string BuildPackIdentity(string packIdentity, DateTime? receiptDate, string packNumber, string totalPacks)
        {
            if (!string.IsNullOrWhiteSpace(packIdentity))
                return packIdentity.Trim();

            string datePart = receiptDate.HasValue ? receiptDate.Value.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture) : string.Empty;
            string packPart = string.IsNullOrWhiteSpace(packNumber) ? "1" : packNumber.Trim();
            string totalPart = string.IsNullOrWhiteSpace(totalPacks) ? "1" : totalPacks.Trim();
            return $"{datePart}/{packPart}/{totalPart}";
        }
    }
}
