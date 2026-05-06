using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using AudioScript.ViewModels;
using System.IO;

namespace AudioScript.Services;

public sealed record TranscriptDocumentExportMetadata(
    string Title,
    string SourceAudioFileName,
    DateTimeOffset ExportedAt);

public sealed record TranscriptDocumentExportOptions(
    bool IncludeTimestamps,
    bool IncludeSpeakerLabels,
    TranscriptDocumentFormat Format);

public enum TranscriptDocumentFormat
{
    TabDelimited = 1,
    InterviewLayout = 2,
}

public static class TranscriptDocumentExporter
{
    private const string DocumentHeadingText = "Transcription";
    private const int TableCellPaddingTwips = 72; // 0.05 in
    // Twips (1/20 pt). These values create a fixed speaker column and hanging-indent wrap.
    private const int InterviewTextColumnIndentTwips = 1440; // 1.0 in
    private const int InterviewSpeakerSlotWidthTwips = 1440; // 1.0 in

    public static void ExportDocx(
        string outputPath,
        IReadOnlyCollection<FinalizedTranscriptLineViewModel> lines,
        TranscriptDocumentExportMetadata metadata,
        TranscriptDocumentExportOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);

        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Export path must include a target directory.");
        }

        Directory.CreateDirectory(directory);

        using WordprocessingDocument document = WordprocessingDocument.Create(
            outputPath,
            WordprocessingDocumentType.Document,
            autoSave: true);
        MainDocumentPart mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document();
        var body = new Body();

        body.Append(BuildHeading(metadata.Title));
        body.Append(BuildMetadataParagraph("Source", metadata.SourceAudioFileName));
        body.Append(BuildMetadataParagraph("Exported", metadata.ExportedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")));
        body.Append(new Paragraph(new Run(new Text(string.Empty))));

        if (options.Format == TranscriptDocumentFormat.InterviewLayout)
        {
            AppendInterviewLayout(body, lines);
        }
        else
        {
            body.Append(BuildOptionOneTable(lines, options));
        }

        mainPart.Document.Append(body);
        mainPart.Document.Save();
    }

    private static Paragraph BuildHeading(string title)
    {
        var runProperties = new RunProperties(new Bold());
        var run = new Run(runProperties, new Text(DocumentHeadingText));
        return new Paragraph(CreateSingleSpacingParagraphProperties(), run);
    }

    private static Paragraph BuildMetadataParagraph(string label, string value)
    {
        string safeValue = string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();
        return new Paragraph(
            CreateSingleSpacingParagraphProperties(),
            new Run(new RunProperties(new Bold()), new Text($"{label}: ")),
            new Run(new Text(safeValue) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static Table BuildOptionOneTable(
        IReadOnlyCollection<FinalizedTranscriptLineViewModel> lines,
        TranscriptDocumentExportOptions options)
    {
        var table = new Table(
            new TableProperties(
                new TableLayout { Type = TableLayoutValues.Autofit },
                new TableWidth { Type = TableWidthUnitValues.Auto, Width = "0" },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 },
                    new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        table.Append(new TableRow(
            CreateTableCell("Timestamp", isHeader: true, noWrap: true),
            CreateTableCell("Speaker", isHeader: true, noWrap: true),
            CreateTableCell("Text", isHeader: true, noWrap: false)));

        foreach (FinalizedTranscriptLineViewModel line in lines)
        {
            string timestamp = options.IncludeTimestamps
                ? (line.Timeline?.Trim() ?? string.Empty)
                : string.Empty;
            string speakerLabel = line.SpeakerLabel?.Trim() ?? string.Empty;
            string text = line.Text?.Trim() ?? string.Empty;
            string speaker = options.IncludeSpeakerLabels ? speakerLabel : string.Empty;
            if (string.IsNullOrWhiteSpace(timestamp)
                && string.IsNullOrWhiteSpace(speaker)
                && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            table.Append(new TableRow(
                CreateTableCell(timestamp, noWrap: true),
                CreateTableCell(speaker, noWrap: true),
                CreateTableCell(text, noWrap: false)));
        }

        return table;
    }

    private static TableCell CreateTableCell(string value, bool isHeader = false, bool noWrap = false)
    {
        string safeValue = value ?? string.Empty;
        var run = isHeader
            ? new Run(new RunProperties(new Bold()), new Text(safeValue) { Space = SpaceProcessingModeValues.Preserve })
            : new Run(new Text(safeValue) { Space = SpaceProcessingModeValues.Preserve });
        var paragraph = new Paragraph(
            CreateSingleSpacingParagraphProperties(),
            run);
        var cellProperties = new TableCellProperties(
            new TableCellWidth { Type = TableWidthUnitValues.Auto, Width = "0" });
        cellProperties.Append(
            new TableCellMargin(
                new TopMargin { Width = TableCellPaddingTwips.ToString(), Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = TableCellPaddingTwips.ToString(), Type = TableWidthUnitValues.Dxa },
                new LeftMargin { Width = TableCellPaddingTwips.ToString(), Type = TableWidthUnitValues.Dxa },
                new RightMargin { Width = TableCellPaddingTwips.ToString(), Type = TableWidthUnitValues.Dxa }));
        if (noWrap)
        {
            cellProperties.Append(new NoWrap());
        }

        return new TableCell(
            cellProperties,
            paragraph);
    }

    private static void AppendInterviewLayout(Body body, IReadOnlyCollection<FinalizedTranscriptLineViewModel> lines)
    {
        var speakerStyleByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int distinctSpeakerCount = 0;

        foreach (FinalizedTranscriptLineViewModel line in lines)
        {
            string text = line.Text?.Trim() ?? string.Empty;
            string speakerLabel = line.SpeakerLabel?.Trim() ?? string.Empty;
            string normalizedSpeaker = string.IsNullOrWhiteSpace(speakerLabel) ? "Speaker" : speakerLabel;
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(normalizedSpeaker))
            {
                continue;
            }

            if (!speakerStyleByName.TryGetValue(normalizedSpeaker, out bool isBold))
            {
                // First distinct speaker is bold, second is normal, and so on.
                isBold = distinctSpeakerCount % 2 == 0;
                speakerStyleByName[normalizedSpeaker] = isBold;
                distinctSpeakerCount++;
            }

            var runProperties = isBold ? new RunProperties(new Bold()) : new RunProperties();
            var speakerRun = new Run(
                runProperties.CloneNode(deep: true),
                new Text($"{normalizedSpeaker}:") { Space = SpaceProcessingModeValues.Preserve });
            var separatorRun = new Run(new TabChar());
            var textRun = new Run(
                runProperties.CloneNode(deep: true),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            var paragraph = new Paragraph(
                BuildInterviewParagraphProperties(),
                speakerRun,
                separatorRun,
                textRun);
            body.Append(paragraph);
            body.Append(new Paragraph(CreateSingleSpacingParagraphProperties(), new Run(new Text(string.Empty))));
        }

        body.Append(new Paragraph(
            CreateSingleSpacingParagraphProperties(),
            new Run(new Text("END OF TRANSCRIPT") { Space = SpaceProcessingModeValues.Preserve })));
    }

    private static ParagraphProperties BuildInterviewParagraphProperties()
    {
        return new ParagraphProperties(
            CreateSingleSpacingSpacingBetweenLines(),
            new Tabs(
                new TabStop
                {
                    Val = TabStopValues.Left,
                    Position = InterviewTextColumnIndentTwips,
                }),
            new Indentation
            {
                Left = InterviewTextColumnIndentTwips.ToString(),
                Hanging = InterviewSpeakerSlotWidthTwips.ToString(),
            });
    }

    private static ParagraphProperties CreateSingleSpacingParagraphProperties()
    {
        return new ParagraphProperties(
            CreateSingleSpacingSpacingBetweenLines());
    }

    private static SpacingBetweenLines CreateSingleSpacingSpacingBetweenLines()
    {
        return new SpacingBetweenLines
        {
            Before = "0",
            After = "0",
            Line = "240",
            LineRule = LineSpacingRuleValues.Auto,
        };
    }
}
