using System;
using System.Collections.Generic;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PyConnectKiosk;

/// <summary>
/// Prints a thermal receipt using WPF's built-in printing stack.
/// Supports multiple OTC codes per rack.
/// </summary>
public static class ReceiptPrinter
{
    public static void Print(PrintJob job)
    {
        var doc = BuildDocument(job);
        var dlg = new PrintDialog();

        if (!string.IsNullOrWhiteSpace(PrinterSettings.PrinterName))
        {
            var queue = FindPrinter(PrinterSettings.PrinterName);
            if (queue != null)
            {
                dlg.PrintQueue = queue;
                dlg.PrintTicket.PageMediaSize = new PageMediaSize(
                    PageMediaSizeName.ISOA4);
            }
        }

        dlg.PrintDocument(
            ((IDocumentPaginatorSource)doc).DocumentPaginator,
            "Access Receipt");
    }

    private static FlowDocument BuildDocument(PrintJob job)
    {
        var doc = new FlowDocument
        {
            FontFamily  = new FontFamily("Courier New"),
            FontSize    = 11,
            PageWidth   = 280,
            PagePadding = new Thickness(10),
            ColumnWidth = 260,
            Background  = Brushes.White,
            Foreground  = Brushes.Black,
        };

        void AddLine(string text, double size = 11, bool bold = false, bool center = false)
        {
            doc.Blocks.Add(new Paragraph(new Run(text))
            {
                FontSize      = size,
                FontWeight    = bold ? FontWeights.Bold : FontWeights.Normal,
                TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
                Margin        = new Thickness(0, 1, 0, 1),
            });
        }

        void Divider(char c = '-') => AddLine(new string(c, 36));

        // ── Header ────────────────────────────────────────────────────────
        AddLine(PrinterSettings.DataCentreName, size: 12, bold: true, center: true);
        AddLine("ACCESS RECEIPT", size: 12, bold: true, center: true);
        Divider('=');

        // ── Session ───────────────────────────────────────────────────────
        AddLine($"Date    : {job.IssuedAt:yyyy-MM-dd}");
        AddLine($"Time    : {job.IssuedAt:HH:mm:ss}");
        AddLine($"Ticket  : {job.Ticket}", size: 9);
        Divider();

        // ── People ────────────────────────────────────────────────────────
        AddLine($"Escort  : {job.EscortName}");
        AddLine($"User    : {job.UserName}");
        AddLine($"Codes   : {job.CodesPerRack} per rack  ({job.Results.Count * job.CodesPerRack} total)");
        Divider();

        // ── Codes ─────────────────────────────────────────────────────────
        AddLine("ONE-TIME CODES", bold: true);

        foreach (var result in job.Results)
        {
            doc.Blocks.Add(new Paragraph(new Run(""))
                { Margin = new Thickness(0, 6, 0, 0) });

            AddLine($"{result.Rack.Tag}  {result.Rack.DisplayName}", size: 9);
            Divider('·');

            if (result.Success)
            {
                for (int i = 0; i < result.Codes.Count; i++)
                {
                    if (result.Codes.Count > 1)
                        AddLine($"  Code {i + 1}:", size: 9);

                    AddLine(result.Codes[i], size: 18, bold: true, center: true);
                }
            }
            else
            {
                AddLine($"ERROR: {result.ErrorCode}", center: true);
            }
        }

        Divider('=');
        AddLine("Keep this receipt secure.", size: 9, center: true);
        AddLine("Codes are single-use only.", size: 9, center: true);
        AddLine("*** END OF RECEIPT ***", bold: true, center: true);

        return doc;
    }

    private static PrintQueue? FindPrinter(string name)
    {
        try
        {
            var server = new PrintServer();
            foreach (var q in server.GetPrintQueues())
                if (q.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return q;
        }
        catch { /* fall through to default */ }
        return null;
    }
}

public record PrintJob(
    string          EscortName,
    string          UserName,
    string          Ticket,
    DateTime        IssuedAt,
    List<OtcResult> Results,
    int             CodesPerRack
);
