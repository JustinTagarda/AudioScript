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
            foreach (FinalizedTranscriptLineViewModel line in lines)
            {
                string text = BuildTabDelimitedLineText(line, options);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                body.Append(new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
            }
        }

        mainPart.Document.Append(body);
        mainPart.Document.Save();
    }

    private static Paragraph BuildHeading(string title)
    {
        var runProperties = new RunProperties(new Bold());
        var run = new Run(runProperties, new Text(string.IsNullOrWhiteSpace(title) ? "Transcript Export" : title));
        return new Paragraph(run);
    }

    private static Paragraph BuildMetadataParagraph(string label, string value)
    {
        string safeValue = string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();
        var labelRun = new Run(new RunProperties(new Bold()), new Text($"{label}: "));
        var valueRun = new Run(new Text(safeValue) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(labelRun, valueRun);
    }

    private static string BuildTabDelimitedLineText(FinalizedTranscriptLineViewModel line, TranscriptDocumentExportOptions options)
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
            return string.Empty;
        }

        return string.Join('\t', [timestamp, speaker, text]);
    }

    private static void AppendInterviewLayout(Body body, IReadOnlyCollection<FinalizedTranscriptLineViewModel> lines)
    {
        var speakerStyleByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int distinctSpeakerCount = 0;
        List<(string Speaker, string Text, bool IsBold)> rows = [];
        int maxSpeakerLabelLength = "Speaker".Length;

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

            rows.Add((normalizedSpeaker, text, isBold));
            if (normalizedSpeaker.Length > maxSpeakerLabelLength)
            {
                maxSpeakerLabelLength = normalizedSpeaker.Length;
            }
        }

        foreach ((string speaker, string text, bool isBold) in rows)
        {
            string paddedSpeaker = $"{speaker}:".PadRight(maxSpeakerLabelLength + 2);
            var runProperties = isBold ? new RunProperties(new Bold()) : new RunProperties();
            var speakerRun = new Run(
                runProperties.CloneNode(deep: true),
                new Text(paddedSpeaker) { Space = SpaceProcessingModeValues.Preserve });
            var textRun = new Run(
                runProperties.CloneNode(deep: true),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            body.Append(new Paragraph(speakerRun, textRun));
        }

        body.Append(new Paragraph(new Run(new Text(string.Empty))));
        body.Append(new Paragraph(new Run(new Text("END OF TRANSCRIPT") { Space = SpaceProcessingModeValues.Preserve })));
    }
}
