using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;

namespace Meimad.Planner.Client.Windows.Presentation;

/// <summary>
/// Prints saved Shift Roster weeks in black and white on landscape pages, one week per page; a week
/// with more employees than fit continues on a further page with the header repeated. The cell text
/// names the shift, so nothing relies on colour.
/// </summary>
internal static class ShiftRosterPrinter
{
    private const double Margin = 36;
    private const double NameColumnWidth = 190;
    private const double RowHeight = 40;
    private const double HeaderHeight = 24;
    private const double TitleBlockHeight = 52;
    private const double FooterHeight = 20;

    internal static void Print(IReadOnlyList<ShiftRosterPrintWeek> weeks)
    {
        var dialog = new PrintDialog();
        dialog.PrintTicket.PageOrientation = System.Printing.PageOrientation.Landscape;
        if (dialog.ShowDialog() != true) return;
        // The roster is wide, so landscape is kept even if the dialog was switched to portrait.
        dialog.PrintTicket.PageOrientation = System.Printing.PageOrientation.Landscape;

        var pageWidth = Math.Max(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        var pageHeight = Math.Min(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
        if (pageWidth <= 0 || pageHeight <= 0) { pageWidth = 1056; pageHeight = 816; }

        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(pageWidth, pageHeight);
        var rowsPerPage = Math.Max(1, (int)((pageHeight - 2 * Margin - TitleBlockHeight - HeaderHeight - FooterHeight) / RowHeight));
        var printedAt = DateTime.Now.ToString("g", CultureInfo.CurrentCulture);
        foreach (var week in weeks)
        {
            var parts = Math.Max(1, (int)Math.Ceiling(week.Rows.Count / (double)rowsPerPage));
            for (var part = 0; part < parts; part++)
            {
                var rows = week.Rows.Skip(part * rowsPerPage).Take(rowsPerPage).ToArray();
                var page = BuildPage(pageWidth, pageHeight, week, rows, part + 1, parts, printedAt);
                var content = new PageContent();
                ((IAddChild)content).AddChild(page);
                document.Pages.Add(content);
            }
        }

        dialog.PrintDocument(document.DocumentPaginator, "Shift Roster");
    }

    private static FixedPage BuildPage(
        double width, double height, ShiftRosterPrintWeek week,
        IReadOnlyList<ShiftRosterRow> rows, int part, int parts, string printedAt)
    {
        var page = new FixedPage { Width = width, Height = height, Background = Brushes.White };
        TextElement.SetForeground(page, Brushes.Black);
        TextElement.SetFontFamily(page, new FontFamily("Segoe UI"));

        var root = new Grid { Width = width - 2 * Margin, Height = height - 2 * Margin };
        FixedPage.SetLeft(root, Margin);
        FixedPage.SetTop(root, Margin);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBlockHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FooterHeight) });

        var culture = CultureInfo.CurrentCulture;
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "Shift Roster", FontSize = 20, FontWeight = FontWeights.Bold });
        title.Children.Add(new TextBlock
        {
            Text = $"Week of {week.WeekStart.ToString("ddd dd/MM/yyyy", culture)} – {week.WeekStart.AddDays(6).ToString("ddd dd/MM/yyyy", culture)}"
                + (parts > 1 ? $"  (page {part} of {parts})" : string.Empty),
            FontSize = 14
        });
        root.Children.Add(title);

        var header = DayGrid();
        header.Children.Add(Label("EMPLOYEE", 0, HorizontalAlignment.Left));
        for (var day = 0; day < 7; day++)
            header.Children.Add(Label(week.WeekStart.AddDays(day).ToString("ddd dd/MM", culture), day + 1, HorizontalAlignment.Center));
        Grid.SetRow(header, 1);
        root.Children.Add(header);

        var body = new StackPanel();
        foreach (var row in rows)
        {
            var line = DayGrid();
            line.Height = RowHeight;
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            name.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeights.SemiBold, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            name.Children.Add(new TextBlock { Text = row.Detail, FontSize = 8, TextTrimming = TextTrimming.CharacterEllipsis });
            line.Children.Add(name);
            for (var day = 0; day < Math.Min(7, row.Cells.Count); day++)
            {
                var cell = row.Cells[day];
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0) };
                text.Children.Add(new TextBlock { Text = cell.ShiftText, FontWeight = FontWeights.SemiBold, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = cell.DetailText, FontSize = 8, TextTrimming = TextTrimming.CharacterEllipsis });
                var border = new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(0.75), Child = text };
                Grid.SetColumn(border, day + 1);
                line.Children.Add(border);
            }
            body.Children.Add(new Border { BorderBrush = Brushes.Black, BorderThickness = new Thickness(0, 0, 0, 0.5), Child = line });
        }
        if (rows.Count == 0)
            body.Children.Add(new TextBlock { Text = "No active employee is on a shift rotation.", Margin = new Thickness(4, 8, 0, 0) });
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        var footer = new TextBlock { Text = $"Printed {printedAt}", FontSize = 8, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        page.Children.Add(root);
        return page;
    }

    private static Grid DayGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameColumnWidth) });
        for (var day = 0; day < 7; day++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static TextBlock Label(string text, int column, HorizontalAlignment alignment)
    {
        var label = new TextBlock
        {
            Text = text, FontWeight = FontWeights.Bold, FontSize = 11,
            HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 4, 2)
        };
        Grid.SetColumn(label, column);
        return label;
    }
}
