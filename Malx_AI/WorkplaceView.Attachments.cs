using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Malx_AI
{
    /// <summary>
    /// Attachment previews for the Workplace composer, and the numbered index the council models
    /// are given so a user can say "the 2nd attached image" and be understood.
    /// </summary>
    public partial class WorkplaceView
    {
        /// <summary>Numbered view of the workplace attachments, in the order they were added.</summary>
        private IReadOnlyList<AttachmentReferenceEntry> BuildWorkplaceAttachmentIndex()
            => AttachmentReferenceResolver.BuildIndex(
                _documents.Select(doc => (doc.Name, doc.IsImage, GetWorkplaceDocumentKindLabel(doc))));

        private static string GetWorkplaceDocumentKindLabel(DocumentInfo document)
        {
            if (document.IsImage)
                return "image";

            string extension = Path.GetExtension(document.Name ?? string.Empty).ToLowerInvariant();
            return extension switch
            {
                ".pdf" => "PDF document",
                ".docx" or ".odt" or ".rtf" => "word-processing document",
                ".xlsx" or ".ods" or ".csv" or ".tsv" => "spreadsheet",
                ".pptx" or ".odp" => "presentation",
                ".epub" => "e-book",
                ".ipynb" => "Jupyter notebook",
                ".md" or ".markdown" or ".txt" => "text document",
                _ => "text/code file"
            };
        }

        /// <summary>
        /// Prompt block naming every attached file with its position, plus the resolution of any
        /// positional phrase in the current request. Injected into every council role so local,
        /// hybrid and cloud runs all resolve "the 3rd attached image" identically.
        /// </summary>
        private string BuildWorkplaceAttachmentIndexBlock(string? userPrompt)
        {
            IReadOnlyList<AttachmentReferenceEntry> index = BuildWorkplaceAttachmentIndex();
            if (index.Count == 0)
                return string.Empty;

            string manifest = AttachmentReferenceResolver.BuildManifest(index);
            string resolution = AttachmentReferenceResolver.BuildResolutionBlock(userPrompt, index);
            return string.IsNullOrEmpty(resolution) ? manifest : manifest + "\n\n" + resolution;
        }

        /// <summary>Rebuilds the preview chips above the Workplace prompt box.</summary>
        private void RefreshWorkplaceAttachmentTray()
        {
            if (WorkplaceAttachmentTrayPanel == null)
                return;

            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(RefreshWorkplaceAttachmentTray);
                return;
            }

            WorkplaceAttachmentTrayPanel.Children.Clear();

            IReadOnlyList<AttachmentReferenceEntry> index = BuildWorkplaceAttachmentIndex();
            if (index.Count == 0)
            {
                WorkplaceAttachmentTrayPanel.Visibility = Visibility.Collapsed;
                return;
            }

            WorkplaceAttachmentTrayPanel.Visibility = Visibility.Visible;
            for (int i = 0; i < _documents.Count && i < index.Count; i++)
            {
                DocumentInfo document = _documents[i];
                AttachmentReferenceEntry entry = index[i];
                bool animate = _recentlyAttachedWorkplaceDocuments.Remove(document.Name);
                var item = new AttachmentPreviewItem
                {
                    Number = entry.Number,
                    Name = entry.Name,
                    KindLabel = entry.KindLabel,
                    SizeLabel = GetWorkplaceDocumentSizeLabel(document),
                    IsImage = document.IsImage,
                    Base64Data = document.Base64Data,
                    FilePath = document.FilePath,
                    PositionLabel = entry.PositionLabel,
                    TotalCount = index.Count
                };

                WorkplaceAttachmentTrayPanel.Children.Add(
                    AttachmentPreviewChip.Build(item, () => RemoveWorkplaceDocument(document), animate));
            }
        }

        private static string GetWorkplaceDocumentSizeLabel(DocumentInfo document)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(document.FilePath) && File.Exists(document.FilePath))
                {
                    long bytes = new FileInfo(document.FilePath).Length;
                    if (bytes >= 1024 * 1024)
                        return $"{bytes / (1024.0 * 1024.0):F1} MB";
                    return bytes >= 1024 ? $"{bytes / 1024.0:F0} KB" : $"{bytes} B";
                }
            }
            catch
            {
                // Size is decoration only — a missing or locked file must not break the tray.
            }

            return document.ChunkCount > 0 ? $"{document.ChunkCount} chunks" : string.Empty;
        }

        /// <summary>Drops the file from the workplace, including its indexed chunks.</summary>
        private void RemoveWorkplaceDocument(DocumentInfo document)
        {
            if (document == null || !_documents.Contains(document))
                return;

            _documents.Remove(document);
            if (!document.IsImage)
                _documentRetriever.RemoveChunksForFile(document.Name);

            if (_documents.Count == 0)
                _documentContextEngaged = false;

            LogActivity($"Removed attachment: {document.Name}");
            AppendChat("system", $"Removed attachment: {document.Name}");
            UpdateContextInfo();
            SavePersistedSession();
        }

        private void InputAreaContainer_PreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = e.Data.GetDataPresent(DataFormats.FileDrop);
        }

        private void InputAreaContainer_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                foreach (string file in files)
                    _recentlyAttachedWorkplaceDocuments.Add(Path.GetFileName(file));
                ProcessFilesAsync(files);
            }
        }

        /// <summary>Names queued for an entrance animation the next time the tray rebuilds.</summary>
        private readonly HashSet<string> _recentlyAttachedWorkplaceDocuments = new(StringComparer.OrdinalIgnoreCase);
    }
}
