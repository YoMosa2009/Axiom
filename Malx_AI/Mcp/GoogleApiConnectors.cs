using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Mcp
{
    /// <summary>
    /// Full practical Gmail + Google Drive tool surface for cloud models.
    /// Covers the day-to-day actions a human does in both products (not every obscure Admin API).
    /// </summary>
    internal static class GoogleApiConnectors
    {
        private static readonly HttpClient Http = CreateClient();
        private const string GmailBase = "https://gmail.googleapis.com/gmail/v1/users/me";
        private const string DriveBase = "https://www.googleapis.com/drive/v3";
        private const string DriveFields = "id,name,mimeType,modifiedTime,createdTime,owners,webViewLink,size,parents,trashed,starred,shared,description";

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Axiom-MCP/1.0");
            return client;
        }

        // ── Tool definitions ────────────────────────────────────────────────

        public static IReadOnlyList<McpToolDefinition> BuildGmailTools(string connectorId)
        {
            JsonObject EmailWriteProps() => new()
            {
                ["to"] = Str("Recipient email address(es), comma-separated if multiple."),
                ["subject"] = Str("Email subject line."),
                ["body"] = Str("Plain-text email body."),
                ["cc"] = Str("Optional CC addresses, comma-separated."),
                ["bcc"] = Str("Optional BCC addresses, comma-separated.")
            };

            return
            [
                Tool(connectorId, "gmail_search",
                    "Search the mailbox with Gmail query syntax (from:, subject:, is:unread, newer_than:7d, label:, in:inbox, etc.). Returns message ids and summaries.",
                    Props(("query", Str("Gmail search query")), ("max_results", Int("1-20, default 10"))),
                    "query"),
                Tool(connectorId, "gmail_get_message",
                    "Read a full email by message_id (headers + plain-text body + attachment names).",
                    Props(("message_id", Str("Gmail message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_get_thread",
                    "Read an entire conversation thread by thread_id (all messages, newest last).",
                    Props(("thread_id", Str("Gmail thread id")), ("max_messages", Int("1-20, default 10"))),
                    "thread_id"),
                Tool(connectorId, "gmail_list_labels",
                    "List all Gmail labels (system + user) with ids and names. Use before apply/remove label.",
                    Props()),
                Tool(connectorId, "gmail_create_label",
                    "Create a user label.",
                    Props(("name", Str("Label name"))),
                    "name"),
                Tool(connectorId, "gmail_delete_label",
                    "Delete a user label by label_id (from gmail_list_labels).",
                    Props(("label_id", Str("Label id"))),
                    "label_id"),
                Tool(connectorId, "gmail_modify_labels",
                    "Add/remove labels on a message. Use system ids: INBOX, STARRED, IMPORTANT, UNREAD, TRASH, SPAM, CATEGORY_PERSONAL, etc., or user label ids.",
                    Props(
                        ("message_id", Str("Message id")),
                        ("add_label_ids", Str("Comma-separated label ids to add")),
                        ("remove_label_ids", Str("Comma-separated label ids to remove"))),
                    "message_id"),
                Tool(connectorId, "gmail_mark_read",
                    "Mark a message as read (remove UNREAD).",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_mark_unread",
                    "Mark a message as unread (add UNREAD).",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_star",
                    "Star a message.",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_unstar",
                    "Remove star from a message.",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_archive",
                    "Archive a message (remove from INBOX).",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_move_to_inbox",
                    "Move a message to Inbox.",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_trash",
                    "Move a message to Trash.",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_untrash",
                    "Restore a message from Trash.",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_spam",
                    "Mark a message as spam.",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_unspam",
                    "Remove spam and return to Inbox.",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_create_draft",
                    "Create a draft in Gmail Drafts. Use for draft/compose-for-later.",
                    new JsonObject { ["type"] = "object", ["properties"] = EmailWriteProps(), ["required"] = new JsonArray("to", "subject", "body"), ["additionalProperties"] = false }),
                Tool(connectorId, "gmail_list_drafts",
                    "List recent drafts with draft_id, to, subject, snippet.",
                    Props(("max_results", Int("1-20, default 10")))),
                Tool(connectorId, "gmail_get_draft",
                    "Read a draft by draft_id.",
                    Props(("draft_id", Str("Draft id"))),
                    "draft_id"),
                Tool(connectorId, "gmail_update_draft",
                    "Replace a draft's to/subject/body (optional cc/bcc).",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = Merge(EmailWriteProps(), PropsObj(("draft_id", Str("Draft id to update")))),
                        ["required"] = new JsonArray("draft_id", "to", "subject", "body"),
                        ["additionalProperties"] = false
                    }),
                Tool(connectorId, "gmail_delete_draft",
                    "Permanently delete a draft.",
                    Props(("draft_id", Str("Draft id"))),
                    "draft_id"),
                Tool(connectorId, "gmail_send_draft",
                    "Send an existing draft by draft_id.",
                    Props(("draft_id", Str("Draft id"))),
                    "draft_id"),
                Tool(connectorId, "gmail_send",
                    "Send a new email immediately. Do not claim success without this tool.",
                    new JsonObject { ["type"] = "object", ["properties"] = EmailWriteProps(), ["required"] = new JsonArray("to", "subject", "body"), ["additionalProperties"] = false }),
                Tool(connectorId, "gmail_reply",
                    "Reply in-thread to an existing message. Uses the original subject/thread; set reply_all=true to reply-all.",
                    Props(
                        ("message_id", Str("Message id to reply to")),
                        ("body", Str("Reply body (plain text)")),
                        ("reply_all", Str("true/false — default false")),
                        ("cc", Str("Optional extra CC")),
                        ("bcc", Str("Optional BCC"))),
                    "message_id", "body"),
                Tool(connectorId, "gmail_forward",
                    "Forward an existing message to new recipients (includes original body).",
                    Props(
                        ("message_id", Str("Message id to forward")),
                        ("to", Str("Forward-to addresses")),
                        ("body", Str("Optional note above the forwarded content")),
                        ("cc", Str("Optional CC")),
                        ("bcc", Str("Optional BCC"))),
                    "message_id", "to"),
                Tool(connectorId, "gmail_list_attachments",
                    "List attachment filenames and attachment_ids on a message.",
                    Props(("message_id", Str("Message id"))),
                    "message_id"),
                Tool(connectorId, "gmail_get_attachment_text",
                    "Download a text-like attachment and return decoded text (small text/json/csv/xml only).",
                    Props(
                        ("message_id", Str("Message id")),
                        ("attachment_id", Str("Attachment id from gmail_list_attachments"))),
                    "message_id", "attachment_id")
            ];
        }

        public static IReadOnlyList<McpToolDefinition> BuildDriveTools(string connectorId)
        {
            return
            [
                Tool(connectorId, "drive_search",
                    "Search Drive by name/fullText. Optional folder_id limits to that folder.",
                    Props(
                        ("query", Str("Search text")),
                        ("folder_id", Str("Optional parent folder id")),
                        ("max_results", Int("1-25, default 12")),
                        ("include_trashed", Str("true to include trash, default false"))),
                    "query"),
                Tool(connectorId, "drive_list_folder",
                    "List files/folders inside a folder. Use folder_id 'root' for My Drive root.",
                    Props(
                        ("folder_id", Str("Folder id or 'root'")),
                        ("max_results", Int("1-50, default 25"))),
                    "folder_id"),
                Tool(connectorId, "drive_get_file",
                    "Get file metadata and text content/export when possible (Docs→text, Sheets→csv, plain text).",
                    Props(("file_id", Str("File id"))),
                    "file_id"),
                Tool(connectorId, "drive_export",
                    "Export a Google Workspace file. mime: text/plain, text/csv, application/pdf, application/vnd.openxmlformats-officedocument.wordprocessingml.document, etc. Returns text or notes binary not supported.",
                    Props(
                        ("file_id", Str("Google Doc/Sheet/Slide id")),
                        ("mime_type", Str("Export MIME type, default text/plain for docs, text/csv for sheets"))),
                    "file_id"),
                Tool(connectorId, "drive_create_folder",
                    "Create a folder. Optional parent_id (default root).",
                    Props(
                        ("name", Str("Folder name")),
                        ("parent_id", Str("Optional parent folder id"))),
                    "name"),
                Tool(connectorId, "drive_create_text_file",
                    "Create a plain text/markdown/csv file with content. Optional parent_id and mime_type (default text/plain).",
                    Props(
                        ("name", Str("File name including extension")),
                        ("content", Str("File text content")),
                        ("parent_id", Str("Optional parent folder id")),
                        ("mime_type", Str("Optional MIME, default text/plain"))),
                    "name", "content"),
                Tool(connectorId, "drive_create_google_doc",
                    "Create a Google Doc with title and optional plain-text body.",
                    Props(
                        ("title", Str("Document title")),
                        ("content", Str("Optional initial text")),
                        ("parent_id", Str("Optional parent folder id"))),
                    "title"),
                Tool(connectorId, "drive_update_text_file",
                    "Overwrite content of a non-Google-Workspace file (text media upload).",
                    Props(
                        ("file_id", Str("File id")),
                        ("content", Str("New full text content")),
                        ("mime_type", Str("Optional MIME, default text/plain"))),
                    "file_id", "content"),
                Tool(connectorId, "drive_rename",
                    "Rename a file or folder.",
                    Props(("file_id", Str("File id")), ("name", Str("New name"))),
                    "file_id", "name"),
                Tool(connectorId, "drive_move",
                    "Move a file into a folder (sets parent).",
                    Props(
                        ("file_id", Str("File id")),
                        ("parent_id", Str("Destination folder id")),
                        ("remove_from_current_parents", Str("true/false, default true"))),
                    "file_id", "parent_id"),
                Tool(connectorId, "drive_copy",
                    "Copy a file. Optional new_name and parent_id.",
                    Props(
                        ("file_id", Str("Source file id")),
                        ("new_name", Str("Optional new name")),
                        ("parent_id", Str("Optional destination folder"))),
                    "file_id"),
                Tool(connectorId, "drive_trash",
                    "Move a file/folder to trash.",
                    Props(("file_id", Str("File id"))),
                    "file_id"),
                Tool(connectorId, "drive_untrash",
                    "Restore a file/folder from trash.",
                    Props(("file_id", Str("File id"))),
                    "file_id"),
                Tool(connectorId, "drive_delete_forever",
                    "Permanently delete a file (skips trash). Destructive — only when user clearly asks.",
                    Props(("file_id", Str("File id"))),
                    "file_id"),
                Tool(connectorId, "drive_star",
                    "Star a file.",
                    Props(("file_id", Str("File id"))),
                    "file_id"),
                Tool(connectorId, "drive_unstar",
                    "Unstar a file.",
                    Props(("file_id", Str("File id"))),
                    "file_id"),
                Tool(connectorId, "drive_share",
                    "Share a file. role: reader|commenter|writer|owner. type: user|group|domain|anyone. For user/group set email_address; for anyone leave email empty.",
                    Props(
                        ("file_id", Str("File id")),
                        ("role", Str("reader, commenter, writer, or owner")),
                        ("type", Str("user, group, domain, or anyone")),
                        ("email_address", Str("Required for user/group")),
                        ("send_notification", Str("true/false, default true")),
                        ("message", Str("Optional share email message"))),
                    "file_id", "role", "type"),
                Tool(connectorId, "drive_list_permissions",
                    "List who has access to a file.",
                    Props(("file_id", Str("File id"))),
                    "file_id"),
                Tool(connectorId, "drive_remove_permission",
                    "Remove a share permission by permission_id.",
                    Props(
                        ("file_id", Str("File id")),
                        ("permission_id", Str("Permission id from drive_list_permissions"))),
                    "file_id", "permission_id"),
                Tool(connectorId, "drive_set_description",
                    "Set a file's description metadata.",
                    Props(("file_id", Str("File id")), ("description", Str("Description text"))),
                    "file_id", "description")
            ];
        }

        // ── Gmail execution ─────────────────────────────────────────────────

        public static async Task<string> ExecuteGmailAsync(string toolName, JsonElement args, string accessToken, CancellationToken token)
        {
            string name = toolName.Trim();

            if (Eq(name, "gmail_search"))
            {
                string query = GetString(args, "query");
                int max = Clamp(GetInt(args, "max_results", 10), 1, 20);
                if (string.IsNullOrWhiteSpace(query))
                    return "gmail_search requires query.";

                using JsonDocument listDoc = await GetJsonAsync(
                        $"{GmailBase}/messages?q={Uri.EscapeDataString(query.Trim())}&maxResults={max}",
                        accessToken, token)
                    .ConfigureAwait(false);
                if (!TryArray(listDoc.RootElement, "messages", out JsonElement messages) || messages.GetArrayLength() == 0)
                    return "No Gmail messages matched that query.";

                var sb = new StringBuilder();
                sb.AppendLine($"Gmail search: {query.Trim()}");
                int i = 0;
                foreach (JsonElement msg in messages.EnumerateArray())
                {
                    if (i >= max) break;
                    string? id = GetProp(msg, "id");
                    string? threadId = GetProp(msg, "threadId");
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    using JsonDocument meta = await GetJsonAsync(
                            $"{GmailBase}/messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date",
                            accessToken, token)
                        .ConfigureAwait(false);
                    i++;
                    sb.AppendLine();
                    sb.AppendLine($"{i}. message_id={id}  thread_id={threadId}");
                    sb.AppendLine($"   From: {GetHeader(meta.RootElement, "From") ?? "?"}");
                    sb.AppendLine($"   To: {GetHeader(meta.RootElement, "To") ?? "?"}");
                    sb.AppendLine($"   Date: {GetHeader(meta.RootElement, "Date") ?? "?"}");
                    sb.AppendLine($"   Subject: {GetHeader(meta.RootElement, "Subject") ?? "(no subject)"}");
                    if (meta.RootElement.TryGetProperty("snippet", out JsonElement sn))
                        sb.AppendLine($"   Snippet: {sn.GetString()}");
                    if (meta.RootElement.TryGetProperty("labelIds", out JsonElement labels) && labels.ValueKind == JsonValueKind.Array)
                        sb.AppendLine($"   Labels: {string.Join(", ", labels.EnumerateArray().Select(x => x.GetString()).Where(x => x != null))}");
                }
                return sb.ToString().TrimEnd();
            }

            if (Eq(name, "gmail_get_message"))
            {
                string messageId = Req(args, "message_id", out string err);
                if (err != null) return err;
                using JsonDocument doc = await GetJsonAsync(
                        $"{GmailBase}/messages/{Uri.EscapeDataString(messageId)}?format=full",
                        accessToken, token)
                    .ConfigureAwait(false);
                return FormatFullMessage(doc.RootElement, messageId);
            }

            if (Eq(name, "gmail_get_thread"))
            {
                string threadId = Req(args, "thread_id", out string err);
                if (err != null) return err;
                int max = Clamp(GetInt(args, "max_messages", 10), 1, 20);
                using JsonDocument doc = await GetJsonAsync(
                        $"{GmailBase}/threads/{Uri.EscapeDataString(threadId)}?format=full",
                        accessToken, token)
                    .ConfigureAwait(false);
                if (!TryArray(doc.RootElement, "messages", out JsonElement messages))
                    return "Thread has no messages.";
                var sb = new StringBuilder();
                sb.AppendLine($"Thread {threadId} ({messages.GetArrayLength()} messages)");
                int i = 0;
                foreach (JsonElement msg in messages.EnumerateArray())
                {
                    if (i >= max) break;
                    i++;
                    string mid = GetProp(msg, "id") ?? "";
                    sb.AppendLine();
                    sb.AppendLine($"--- Message {i} message_id={mid} ---");
                    sb.AppendLine(FormatFullMessage(msg, mid, includeAttachments: false));
                }
                return Bound(sb.ToString(), 14000);
            }

            if (Eq(name, "gmail_list_labels"))
            {
                using JsonDocument doc = await GetJsonAsync($"{GmailBase}/labels", accessToken, token).ConfigureAwait(false);
                if (!TryArray(doc.RootElement, "labels", out JsonElement labels))
                    return "No labels found.";
                var sb = new StringBuilder("Gmail labels:");
                foreach (JsonElement label in labels.EnumerateArray().OrderBy(l => GetProp(l, "name")))
                {
                    sb.AppendLine();
                    sb.AppendLine($"- {GetProp(label, "name")}  id={GetProp(label, "id")}  type={GetProp(label, "type")}");
                }
                return sb.ToString();
            }

            if (Eq(name, "gmail_create_label"))
            {
                string labelName = Req(args, "name", out string err);
                if (err != null) return err;
                using JsonDocument doc = await PostJsonAsync($"{GmailBase}/labels", accessToken,
                    new JsonObject { ["name"] = labelName, ["labelListVisibility"] = "labelShow", ["messageListVisibility"] = "show" }.ToJsonString(),
                    token).ConfigureAwait(false);
                return $"Label created: {GetProp(doc.RootElement, "name")} id={GetProp(doc.RootElement, "id")}";
            }

            if (Eq(name, "gmail_delete_label"))
            {
                string labelId = Req(args, "label_id", out string err);
                if (err != null) return err;
                await DeleteAsync($"{GmailBase}/labels/{Uri.EscapeDataString(labelId)}", accessToken, token).ConfigureAwait(false);
                return $"Label {labelId} deleted.";
            }

            if (Eq(name, "gmail_modify_labels"))
            {
                string messageId = Req(args, "message_id", out string err);
                if (err != null) return err;
                var add = SplitIds(GetString(args, "add_label_ids"));
                var remove = SplitIds(GetString(args, "remove_label_ids"));
                if (add.Count == 0 && remove.Count == 0)
                    return "Provide add_label_ids and/or remove_label_ids.";
                return await ModifyLabelsAsync(messageId, add, remove, accessToken, token).ConfigureAwait(false);
            }

            if (Eq(name, "gmail_mark_read")
                || Eq(name, "gmail_mark_unread")
                || Eq(name, "gmail_star")
                || Eq(name, "gmail_unstar")
                || Eq(name, "gmail_archive")
                || Eq(name, "gmail_move_to_inbox")
                || Eq(name, "gmail_spam")
                || Eq(name, "gmail_unspam"))
            {
                string messageId = Req(args, "message_id", out string labelErr);
                if (labelErr != null) return labelErr;
                return name.ToLowerInvariant() switch
                {
                    "gmail_mark_read" => await ModifyLabelsAsync(messageId, Array.Empty<string>(), new[] { "UNREAD" }, accessToken, token).ConfigureAwait(false),
                    "gmail_mark_unread" => await ModifyLabelsAsync(messageId, new[] { "UNREAD" }, Array.Empty<string>(), accessToken, token).ConfigureAwait(false),
                    "gmail_star" => await ModifyLabelsAsync(messageId, new[] { "STARRED" }, Array.Empty<string>(), accessToken, token).ConfigureAwait(false),
                    "gmail_unstar" => await ModifyLabelsAsync(messageId, Array.Empty<string>(), new[] { "STARRED" }, accessToken, token).ConfigureAwait(false),
                    "gmail_archive" => await ModifyLabelsAsync(messageId, Array.Empty<string>(), new[] { "INBOX" }, accessToken, token).ConfigureAwait(false),
                    "gmail_move_to_inbox" => await ModifyLabelsAsync(messageId, new[] { "INBOX" }, new[] { "TRASH", "SPAM" }, accessToken, token).ConfigureAwait(false),
                    "gmail_spam" => await ModifyLabelsAsync(messageId, new[] { "SPAM" }, new[] { "INBOX" }, accessToken, token).ConfigureAwait(false),
                    "gmail_unspam" => await ModifyLabelsAsync(messageId, new[] { "INBOX" }, new[] { "SPAM" }, accessToken, token).ConfigureAwait(false),
                    _ => "Unknown label action."
                };
            }
            if (Eq(name, "gmail_trash"))
            {
                string messageId = Req(args, "message_id", out string err);
                if (err != null) return err;
                await PostJsonAsync($"{GmailBase}/messages/{Uri.EscapeDataString(messageId)}/trash", accessToken, "{}", token).ConfigureAwait(false);
                return $"Message {messageId} moved to Trash.";
            }
            if (Eq(name, "gmail_untrash"))
            {
                string messageId = Req(args, "message_id", out string err);
                if (err != null) return err;
                await PostJsonAsync($"{GmailBase}/messages/{Uri.EscapeDataString(messageId)}/untrash", accessToken, "{}", token).ConfigureAwait(false);
                return $"Message {messageId} restored from Trash.";
            }
            if (Eq(name, "gmail_create_draft"))
            {
                if (!TryReadEmailFields(args, out string to, out string subject, out string body, out string cc, out string bcc, out string error))
                    return error;
                using JsonDocument doc = await PostJsonAsync($"{GmailBase}/drafts", accessToken,
                    new JsonObject { ["message"] = new JsonObject { ["raw"] = BuildRfc2822RawMessage(to, subject, body, cc, bcc) } }.ToJsonString(),
                    token).ConfigureAwait(false);
                return FormatDraftResult("Draft created", doc.RootElement, to, subject, cc);
            }

            if (Eq(name, "gmail_list_drafts"))
            {
                int max = Clamp(GetInt(args, "max_results", 10), 1, 20);
                using JsonDocument list = await GetJsonAsync($"{GmailBase}/drafts?maxResults={max}", accessToken, token).ConfigureAwait(false);
                if (!TryArray(list.RootElement, "drafts", out JsonElement drafts) || drafts.GetArrayLength() == 0)
                    return "No drafts found.";
                var sb = new StringBuilder("Gmail drafts:");
                int i = 0;
                foreach (JsonElement d in drafts.EnumerateArray())
                {
                    string? draftId = GetProp(d, "id");
                    if (string.IsNullOrWhiteSpace(draftId)) continue;
                    using JsonDocument full = await GetJsonAsync($"{GmailBase}/drafts/{Uri.EscapeDataString(draftId)}?format=metadata", accessToken, token).ConfigureAwait(false);
                    JsonElement msg = full.RootElement.TryGetProperty("message", out JsonElement m) ? m : default;
                    i++;
                    sb.AppendLine();
                    sb.AppendLine($"{i}. draft_id={draftId}");
                    if (msg.ValueKind == JsonValueKind.Object)
                    {
                        sb.AppendLine($"   To: {GetHeader(msg, "To")}");
                        sb.AppendLine($"   Subject: {GetHeader(msg, "Subject")}");
                        if (msg.TryGetProperty("snippet", out JsonElement sn))
                            sb.AppendLine($"   Snippet: {sn.GetString()}");
                    }
                }
                return sb.ToString();
            }

            if (Eq(name, "gmail_get_draft"))
            {
                string draftId = Req(args, "draft_id", out string err);
                if (err != null) return err;
                using JsonDocument doc = await GetJsonAsync($"{GmailBase}/drafts/{Uri.EscapeDataString(draftId)}?format=full", accessToken, token).ConfigureAwait(false);
                JsonElement msg = doc.RootElement.TryGetProperty("message", out JsonElement m) ? m : doc.RootElement;
                return $"Draft {draftId}\n" + FormatFullMessage(msg, GetProp(msg, "id") ?? "");
            }

            if (Eq(name, "gmail_update_draft"))
            {
                string draftId = Req(args, "draft_id", out string err);
                if (err != null) return err;
                if (!TryReadEmailFields(args, out string to, out string subject, out string body, out string cc, out string bcc, out string error))
                    return error;
                using JsonDocument doc = await PutJsonAsync($"{GmailBase}/drafts/{Uri.EscapeDataString(draftId)}", accessToken,
                    new JsonObject
                    {
                        ["id"] = draftId,
                        ["message"] = new JsonObject { ["raw"] = BuildRfc2822RawMessage(to, subject, body, cc, bcc) }
                    }.ToJsonString(), token).ConfigureAwait(false);
                return FormatDraftResult("Draft updated", doc.RootElement, to, subject, cc);
            }

            if (Eq(name, "gmail_delete_draft"))
            {
                string draftId = Req(args, "draft_id", out string err);
                if (err != null) return err;
                await DeleteAsync($"{GmailBase}/drafts/{Uri.EscapeDataString(draftId)}", accessToken, token).ConfigureAwait(false);
                return $"Draft {draftId} deleted.";
            }

            if (Eq(name, "gmail_send_draft"))
            {
                string draftId = Req(args, "draft_id", out string err);
                if (err != null) return err;
                using JsonDocument doc = await PostJsonAsync($"{GmailBase}/drafts/send", accessToken,
                    new JsonObject { ["id"] = draftId }.ToJsonString(), token).ConfigureAwait(false);
                return $"Draft sent. message_id={GetProp(doc.RootElement, "id")} thread_id={GetProp(doc.RootElement, "threadId")}";
            }

            if (Eq(name, "gmail_send"))
            {
                if (!TryReadEmailFields(args, out string to, out string subject, out string body, out string cc, out string bcc, out string error))
                    return error;
                using JsonDocument doc = await PostJsonAsync($"{GmailBase}/messages/send", accessToken,
                    new JsonObject { ["raw"] = BuildRfc2822RawMessage(to, subject, body, cc, bcc) }.ToJsonString(),
                    token).ConfigureAwait(false);
                return $"Email sent.\nTo: {to}\nSubject: {subject}\nmessage_id={GetProp(doc.RootElement, "id")}\nthread_id={GetProp(doc.RootElement, "threadId")}";
            }

            if (Eq(name, "gmail_reply"))
            {
                string messageId = Req(args, "message_id", out string err);
                if (err != null) return err;
                string body = GetString(args, "body");
                if (string.IsNullOrWhiteSpace(body)) return "body is required.";
                bool replyAll = IsTruthy(GetString(args, "reply_all"));

                using JsonDocument orig = await GetJsonAsync(
                        $"{GmailBase}/messages/{Uri.EscapeDataString(messageId)}?format=full",
                        accessToken, token)
                    .ConfigureAwait(false);
                string threadId = GetProp(orig.RootElement, "threadId") ?? "";
                string subject = GetHeader(orig.RootElement, "Subject") ?? "";
                if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
                    subject = "Re: " + subject;
                string from = GetHeader(orig.RootElement, "From") ?? "";
                string replyTo = GetHeader(orig.RootElement, "Reply-To") ?? from;
                string to = ExtractEmailAddresses(replyTo);
                string cc = "";
                if (replyAll)
                {
                    string origTo = GetHeader(orig.RootElement, "To") ?? "";
                    string origCc = GetHeader(orig.RootElement, "Cc") ?? "";
                    cc = string.Join(", ",
                        (origTo + "," + origCc).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(a => !string.IsNullOrWhiteSpace(a)));
                }
                string extraCc = GetString(args, "cc");
                if (!string.IsNullOrWhiteSpace(extraCc))
                    cc = string.IsNullOrWhiteSpace(cc) ? extraCc : cc + ", " + extraCc;
                string messageIdHeader = GetHeader(orig.RootElement, "Message-ID") ?? GetHeader(orig.RootElement, "Message-Id") ?? "";
                string references = GetHeader(orig.RootElement, "References") ?? "";
                if (!string.IsNullOrWhiteSpace(messageIdHeader))
                    references = string.IsNullOrWhiteSpace(references) ? messageIdHeader : references + " " + messageIdHeader;

                string raw = BuildRfc2822RawMessage(to, subject, body, cc, GetString(args, "bcc"),
                    inReplyTo: messageIdHeader, references: references);
                var payload = new JsonObject { ["raw"] = raw };
                if (!string.IsNullOrWhiteSpace(threadId))
                    payload["threadId"] = threadId;
                using JsonDocument doc = await PostJsonAsync($"{GmailBase}/messages/send", accessToken, payload.ToJsonString(), token).ConfigureAwait(false);
                return $"Reply sent.\nTo: {to}\nSubject: {subject}\nmessage_id={GetProp(doc.RootElement, "id")}\nthread_id={GetProp(doc.RootElement, "threadId")}";
            }

            if (Eq(name, "gmail_forward"))
            {
                string messageId = Req(args, "message_id", out string err);
                if (err != null) return err;
                string to = Req(args, "to", out err);
                if (err != null) return err;
                using JsonDocument orig = await GetJsonAsync(
                        $"{GmailBase}/messages/{Uri.EscapeDataString(messageId)}?format=full",
                        accessToken, token)
                    .ConfigureAwait(false);
                string subject = GetHeader(orig.RootElement, "Subject") ?? "";
                if (!subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase)
                    && !subject.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase))
                    subject = "Fwd: " + subject;
                string originalBody = ExtractGmailPlainText(orig.RootElement);
                string note = GetString(args, "body");
                string body =
                    (string.IsNullOrWhiteSpace(note) ? "" : note.Trim() + "\n\n") +
                    "---------- Forwarded message ---------\n" +
                    $"From: {GetHeader(orig.RootElement, "From")}\n" +
                    $"Date: {GetHeader(orig.RootElement, "Date")}\n" +
                    $"Subject: {GetHeader(orig.RootElement, "Subject")}\n" +
                    $"To: {GetHeader(orig.RootElement, "To")}\n\n" +
                    originalBody;
                using JsonDocument doc = await PostJsonAsync($"{GmailBase}/messages/send", accessToken,
                    new JsonObject { ["raw"] = BuildRfc2822RawMessage(to, subject, body, GetString(args, "cc"), GetString(args, "bcc")) }.ToJsonString(),
                    token).ConfigureAwait(false);
                return $"Forwarded.\nTo: {to}\nSubject: {subject}\nmessage_id={GetProp(doc.RootElement, "id")}";
            }

            if (Eq(name, "gmail_list_attachments"))
            {
                string messageId = Req(args, "message_id", out string err);
                if (err != null) return err;
                using JsonDocument doc = await GetJsonAsync(
                        $"{GmailBase}/messages/{Uri.EscapeDataString(messageId)}?format=full",
                        accessToken, token)
                    .ConfigureAwait(false);
                var atts = new List<string>();
                CollectAttachments(doc.RootElement.TryGetProperty("payload", out JsonElement payload) ? payload : default, atts);
                return atts.Count == 0 ? "No attachments on this message." : "Attachments:\n" + string.Join("\n", atts);
            }

            if (Eq(name, "gmail_get_attachment_text"))
            {
                string messageId = Req(args, "message_id", out string err);
                if (err != null) return err;
                string attachmentId = Req(args, "attachment_id", out err);
                if (err != null) return err;
                using JsonDocument doc = await GetJsonAsync(
                        $"{GmailBase}/messages/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}",
                        accessToken, token)
                    .ConfigureAwait(false);
                string? data = GetProp(doc.RootElement, "data");
                if (string.IsNullOrWhiteSpace(data))
                    return "Attachment had no data.";
                string text = DecodeBase64Url(data);
                return Bound(text, 12000);
            }

            return $"Unsupported Gmail tool: {toolName}";
        }

        // ── Drive execution ─────────────────────────────────────────────────

        public static async Task<string> ExecuteDriveAsync(string toolName, JsonElement args, string accessToken, CancellationToken token)
        {
            string name = toolName.Trim();

            if (Eq(name, "drive_search"))
            {
                string query = GetString(args, "query");
                int max = Clamp(GetInt(args, "max_results", 12), 1, 25);
                if (string.IsNullOrWhiteSpace(query)) return "drive_search requires query.";
                string escaped = EscapeDriveQuery(query.Trim());
                bool includeTrashed = IsTruthy(GetString(args, "include_trashed"));
                string q = $"(name contains '{escaped}' or fullText contains '{escaped}')";
                if (!includeTrashed) q += " and trashed = false";
                string folderId = GetString(args, "folder_id");
                if (!string.IsNullOrWhiteSpace(folderId))
                    q += $" and '{folderId.Trim()}' in parents";
                return await ListDriveFilesAsync(q, max, $"Search: {query}", accessToken, token).ConfigureAwait(false);
            }

            if (Eq(name, "drive_list_folder"))
            {
                string folderId = GetString(args, "folder_id");
                if (string.IsNullOrWhiteSpace(folderId)) return "folder_id is required (use 'root' for My Drive root).";
                if (string.Equals(folderId.Trim(), "root", StringComparison.OrdinalIgnoreCase))
                    folderId = "root";
                int max = Clamp(GetInt(args, "max_results", 25), 1, 50);
                string q = $"'{folderId.Trim()}' in parents and trashed = false";
                return await ListDriveFilesAsync(q, max, $"Folder {folderId}", accessToken, token).ConfigureAwait(false);
            }

            if (Eq(name, "drive_get_file"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                using JsonDocument metaDoc = await GetJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, token)
                    .ConfigureAwait(false);
                JsonElement meta = metaDoc.RootElement;
                string mime = GetProp(meta, "mimeType") ?? "";
                string content = await TryReadDriveContentAsync(fileId, mime, accessToken, token).ConfigureAwait(false);
                var sb = new StringBuilder();
                sb.AppendLine(FormatDriveMeta(meta));
                sb.AppendLine();
                sb.AppendLine(string.IsNullOrWhiteSpace(content)
                    ? "(No text preview for this type — use drive_export for Workspace files or note binary files.)"
                    : content);
                return Bound(sb.ToString(), 14000);
            }

            if (Eq(name, "drive_export"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                using JsonDocument metaDoc = await GetJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?fields=id,name,mimeType",
                        accessToken, token)
                    .ConfigureAwait(false);
                string mime = GetProp(metaDoc.RootElement, "mimeType") ?? "";
                string exportMime = GetString(args, "mime_type");
                if (string.IsNullOrWhiteSpace(exportMime))
                {
                    exportMime = mime switch
                    {
                        "application/vnd.google-apps.spreadsheet" => "text/csv",
                        "application/vnd.google-apps.presentation" => "text/plain",
                        _ => "text/plain"
                    };
                }
                if (!mime.StartsWith("application/vnd.google-apps.", StringComparison.OrdinalIgnoreCase))
                    return "drive_export is for Google Docs/Sheets/Slides. Use drive_get_file for binary/text files.";
                if (exportMime.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    || exportMime.Contains("openxml", StringComparison.OrdinalIgnoreCase)
                    || exportMime.Contains("msword", StringComparison.OrdinalIgnoreCase))
                    return $"Export MIME {exportMime} is binary. Use text/plain or text/csv for readable tool output, or open the file in Drive.";
                string url = $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}/export?mimeType={Uri.EscapeDataString(exportMime)}";
                string text = await GetStringAsync(url, accessToken, token).ConfigureAwait(false);
                return Bound($"Exported {GetProp(metaDoc.RootElement, "name")} as {exportMime}:\n\n{text}", 14000);
            }

            if (Eq(name, "drive_create_folder"))
            {
                string folderName = Req(args, "name", out string err);
                if (err != null) return err;
                var meta = new JsonObject
                {
                    ["name"] = folderName,
                    ["mimeType"] = "application/vnd.google-apps.folder"
                };
                ApplyParent(meta, GetString(args, "parent_id"));
                using JsonDocument doc = await PostJsonAsync(
                        $"{DriveBase}/files?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, meta.ToJsonString(), token)
                    .ConfigureAwait(false);
                return "Folder created.\n" + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_create_text_file"))
            {
                string fileName = Req(args, "name", out string err);
                if (err != null) return err;
                string content = GetString(args, "content");
                string mime = string.IsNullOrWhiteSpace(GetString(args, "mime_type")) ? "text/plain" : GetString(args, "mime_type").Trim();
                var meta = new JsonObject { ["name"] = fileName, ["mimeType"] = mime };
                ApplyParent(meta, GetString(args, "parent_id"));
                using JsonDocument doc = await MultipartUploadAsync(meta, content, mime, accessToken, token).ConfigureAwait(false);
                return "Text file created.\n" + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_create_google_doc"))
            {
                string title = Req(args, "title", out string err);
                if (err != null) return err;
                var meta = new JsonObject
                {
                    ["name"] = title,
                    ["mimeType"] = "application/vnd.google-apps.document"
                };
                ApplyParent(meta, GetString(args, "parent_id"));
                string content = GetString(args, "content");
                // Create empty Doc then optionally set content via multipart as text/plain import.
                if (!string.IsNullOrWhiteSpace(content))
                {
                    using JsonDocument doc = await MultipartUploadAsync(meta, content, "text/plain", accessToken, token).ConfigureAwait(false);
                    return "Google Doc created with content.\n" + FormatDriveMeta(doc.RootElement);
                }
                using JsonDocument empty = await PostJsonAsync(
                        $"{DriveBase}/files?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, meta.ToJsonString(), token)
                    .ConfigureAwait(false);
                return "Google Doc created.\n" + FormatDriveMeta(empty.RootElement);
            }

            if (Eq(name, "drive_update_text_file"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                string content = GetString(args, "content");
                string mime = string.IsNullOrWhiteSpace(GetString(args, "mime_type")) ? "text/plain" : GetString(args, "mime_type").Trim();
                using var request = new HttpRequestMessage(HttpMethod.Patch,
                    $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(fileId)}?uploadType=media");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                request.Content = new StringContent(content ?? "", Encoding.UTF8, mime);
                using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(FormatApiError((int)response.StatusCode, body));
                using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                return "File content updated.\n" + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_rename"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                string newName = Req(args, "name", out err);
                if (err != null) return err;
                using JsonDocument doc = await PatchJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, new JsonObject { ["name"] = newName }.ToJsonString(), token)
                    .ConfigureAwait(false);
                return "Renamed.\n" + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_move"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                string parentId = Req(args, "parent_id", out err);
                if (err != null) return err;
                bool removeCurrent = !string.Equals(GetString(args, "remove_from_current_parents"), "false", StringComparison.OrdinalIgnoreCase);
                string removeParents = "";
                if (removeCurrent)
                {
                    using JsonDocument cur = await GetJsonAsync(
                            $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?fields=parents",
                            accessToken, token)
                        .ConfigureAwait(false);
                    if (cur.RootElement.TryGetProperty("parents", out JsonElement parents) && parents.ValueKind == JsonValueKind.Array)
                        removeParents = string.Join(",", parents.EnumerateArray().Select(p => p.GetString()).Where(p => !string.IsNullOrWhiteSpace(p)));
                }
                string url = $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?addParents={Uri.EscapeDataString(parentId.Trim())}"
                    + (string.IsNullOrWhiteSpace(removeParents) ? "" : $"&removeParents={Uri.EscapeDataString(removeParents)}")
                    + $"&fields={Uri.EscapeDataString(DriveFields)}";
                using JsonDocument doc = await PatchJsonAsync(url, accessToken, "{}", token).ConfigureAwait(false);
                return "Moved.\n" + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_copy"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                var body = new JsonObject();
                if (!string.IsNullOrWhiteSpace(GetString(args, "new_name")))
                    body["name"] = GetString(args, "new_name").Trim();
                ApplyParent(body, GetString(args, "parent_id"));
                using JsonDocument doc = await PostJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}/copy?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, body.ToJsonString(), token)
                    .ConfigureAwait(false);
                return "Copied.\n" + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_trash"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                using JsonDocument doc = await PatchJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, new JsonObject { ["trashed"] = true }.ToJsonString(), token)
                    .ConfigureAwait(false);
                return "Moved to trash.\n" + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_untrash"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                using JsonDocument doc = await PatchJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, new JsonObject { ["trashed"] = false }.ToJsonString(), token)
                    .ConfigureAwait(false);
                return "Restored from trash.\n" + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_delete_forever"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                await DeleteAsync($"{DriveBase}/files/{Uri.EscapeDataString(fileId)}", accessToken, token).ConfigureAwait(false);
                return $"Permanently deleted file_id={fileId}.";
            }

            if (Eq(name, "drive_star") || Eq(name, "drive_unstar"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                bool starred = Eq(name, "drive_star");
                using JsonDocument doc = await PatchJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, new JsonObject { ["starred"] = starred }.ToJsonString(), token)
                    .ConfigureAwait(false);
                return (starred ? "Starred.\n" : "Unstarred.\n") + FormatDriveMeta(doc.RootElement);
            }

            if (Eq(name, "drive_share"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                string role = Req(args, "role", out err);
                if (err != null) return err;
                string type = Req(args, "type", out err);
                if (err != null) return err;
                var perm = new JsonObject
                {
                    ["role"] = role.Trim().ToLowerInvariant(),
                    ["type"] = type.Trim().ToLowerInvariant()
                };
                string email = GetString(args, "email_address");
                if (!string.IsNullOrWhiteSpace(email))
                    perm["emailAddress"] = email.Trim();
                bool notify = !string.Equals(GetString(args, "send_notification"), "false", StringComparison.OrdinalIgnoreCase);
                string url = $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}/permissions?sendNotificationEmail={(notify ? "true" : "false")}";
                string message = GetString(args, "message");
                if (!string.IsNullOrWhiteSpace(message))
                    url += "&emailMessage=" + Uri.EscapeDataString(message);
                using JsonDocument doc = await PostJsonAsync(url, accessToken, perm.ToJsonString(), token).ConfigureAwait(false);
                return $"Shared. permission_id={GetProp(doc.RootElement, "id")} role={GetProp(doc.RootElement, "role")} type={GetProp(doc.RootElement, "type")} email={GetProp(doc.RootElement, "emailAddress")}";
            }

            if (Eq(name, "drive_list_permissions"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                using JsonDocument doc = await GetJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}/permissions?fields=permissions(id,role,type,emailAddress,domain,displayName)",
                        accessToken, token)
                    .ConfigureAwait(false);
                if (!TryArray(doc.RootElement, "permissions", out JsonElement perms) || perms.GetArrayLength() == 0)
                    return "No permissions found.";
                var sb = new StringBuilder("Permissions:");
                foreach (JsonElement p in perms.EnumerateArray())
                {
                    sb.AppendLine();
                    sb.AppendLine($"- id={GetProp(p, "id")} role={GetProp(p, "role")} type={GetProp(p, "type")} email={GetProp(p, "emailAddress")} name={GetProp(p, "displayName")}");
                }
                return sb.ToString();
            }

            if (Eq(name, "drive_remove_permission"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                string permissionId = Req(args, "permission_id", out err);
                if (err != null) return err;
                await DeleteAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}/permissions/{Uri.EscapeDataString(permissionId)}",
                        accessToken, token)
                    .ConfigureAwait(false);
                return $"Removed permission {permissionId} from file {fileId}.";
            }

            if (Eq(name, "drive_set_description"))
            {
                string fileId = Req(args, "file_id", out string err);
                if (err != null) return err;
                string description = GetString(args, "description");
                using JsonDocument doc = await PatchJsonAsync(
                        $"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?fields={Uri.EscapeDataString(DriveFields)}",
                        accessToken, new JsonObject { ["description"] = description }.ToJsonString(), token)
                    .ConfigureAwait(false);
                return "Description updated.\n" + FormatDriveMeta(doc.RootElement);
            }

            return $"Unsupported Drive tool: {toolName}";
        }

        // ── Shared helpers ──────────────────────────────────────────────────

        private static async Task<string> ListDriveFilesAsync(string q, int max, string title, string accessToken, CancellationToken token)
        {
            string url =
                $"{DriveBase}/files?pageSize={max}"
                + "&fields=" + Uri.EscapeDataString($"files({DriveFields})")
                + "&q=" + Uri.EscapeDataString(q)
                + "&orderBy=modifiedTime desc";
            using JsonDocument doc = await GetJsonAsync(url, accessToken, token).ConfigureAwait(false);
            if (!TryArray(doc.RootElement, "files", out JsonElement files) || files.GetArrayLength() == 0)
                return "No Drive files matched.";
            var sb = new StringBuilder(title);
            int i = 0;
            foreach (JsonElement file in files.EnumerateArray())
            {
                i++;
                sb.AppendLine();
                sb.AppendLine($"{i}. {FormatDriveMeta(file)}");
            }
            return sb.ToString().TrimEnd();
        }

        private static string FormatDriveMeta(JsonElement file)
        {
            string id = GetProp(file, "id") ?? "";
            string name = GetProp(file, "name") ?? "";
            string mime = GetProp(file, "mimeType") ?? "";
            string modified = GetProp(file, "modifiedTime") ?? "";
            string link = GetProp(file, "webViewLink") ?? "";
            string starred = file.TryGetProperty("starred", out JsonElement s) && s.ValueKind == JsonValueKind.True ? " starred" : "";
            string trashed = file.TryGetProperty("trashed", out JsonElement t) && t.ValueKind == JsonValueKind.True ? " TRASHED" : "";
            string parents = "";
            if (file.TryGetProperty("parents", out JsonElement p) && p.ValueKind == JsonValueKind.Array)
                parents = " parents=[" + string.Join(",", p.EnumerateArray().Select(x => x.GetString())) + "]";
            return $"id={id} name={name} type={mime} modified={modified}{starred}{trashed}{parents}"
                + (string.IsNullOrWhiteSpace(link) ? "" : $" link={link}");
        }

        private static void ApplyParent(JsonObject meta, string parentId)
        {
            if (string.IsNullOrWhiteSpace(parentId))
                return;
            string id = parentId.Trim();
            if (string.Equals(id, "root", StringComparison.OrdinalIgnoreCase))
                id = "root";
            meta["parents"] = new JsonArray(id);
        }

        private static async Task<JsonDocument> MultipartUploadAsync(
            JsonObject metadata,
            string content,
            string contentMime,
            string accessToken,
            CancellationToken token)
        {
            string boundary = "axiom_" + Guid.NewGuid().ToString("N");
            var body = new StringBuilder();
            body.Append("--").Append(boundary).Append("\r\n");
            body.Append("Content-Type: application/json; charset=UTF-8\r\n\r\n");
            body.Append(metadata.ToJsonString()).Append("\r\n");
            body.Append("--").Append(boundary).Append("\r\n");
            body.Append("Content-Type: ").Append(contentMime).Append("\r\n\r\n");
            body.Append(content ?? "").Append("\r\n");
            body.Append("--").Append(boundary).Append("--");

            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields={Uri.EscapeDataString(DriveFields)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(body.ToString(), Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/related; boundary={boundary}");

            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string respBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatApiError((int)response.StatusCode, respBody));
            return JsonDocument.Parse(respBody);
        }

        private static async Task<string> ModifyLabelsAsync(
            string messageId,
            IReadOnlyList<string> add,
            IReadOnlyList<string> remove,
            string accessToken,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(messageId))
                return "message_id is required.";
            var addArr = new JsonArray();
            foreach (string id in add)
                addArr.Add(id);
            var removeArr = new JsonArray();
            foreach (string id in remove)
                removeArr.Add(id);
            var payload = new JsonObject
            {
                ["addLabelIds"] = addArr,
                ["removeLabelIds"] = removeArr
            };
            using JsonDocument doc = await PostJsonAsync(
                    $"{GmailBase}/messages/{Uri.EscapeDataString(messageId.Trim())}/modify",
                    accessToken, payload.ToJsonString(), token)
                .ConfigureAwait(false);
            string labels = "";
            if (doc.RootElement.TryGetProperty("labelIds", out JsonElement labs) && labs.ValueKind == JsonValueKind.Array)
                labels = string.Join(", ", labs.EnumerateArray().Select(x => x.GetString()));
            return $"Message {messageId} updated. Labels now: {labels}";
        }

        private static string FormatFullMessage(JsonElement root, string messageId, bool includeAttachments = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"message_id={messageId} thread_id={GetProp(root, "threadId")}");
            sb.AppendLine($"From: {GetHeader(root, "From")}");
            sb.AppendLine($"To: {GetHeader(root, "To")}");
            string cc = GetHeader(root, "Cc") ?? "";
            if (!string.IsNullOrWhiteSpace(cc)) sb.AppendLine($"Cc: {cc}");
            sb.AppendLine($"Date: {GetHeader(root, "Date")}");
            sb.AppendLine($"Subject: {GetHeader(root, "Subject")}");
            if (root.TryGetProperty("labelIds", out JsonElement labels) && labels.ValueKind == JsonValueKind.Array)
                sb.AppendLine($"Labels: {string.Join(", ", labels.EnumerateArray().Select(x => x.GetString()))}");
            if (includeAttachments && root.TryGetProperty("payload", out JsonElement payload))
            {
                var atts = new List<string>();
                CollectAttachments(payload, atts);
                if (atts.Count > 0)
                {
                    sb.AppendLine("Attachments:");
                    foreach (string a in atts) sb.AppendLine("  " + a);
                }
            }
            sb.AppendLine();
            string body = ExtractGmailPlainText(root);
            sb.AppendLine(string.IsNullOrWhiteSpace(body) ? "(no plain-text body)" : Bound(body, 10000));
            return sb.ToString().TrimEnd();
        }

        private static string FormatDraftResult(string title, JsonElement root, string to, string subject, string cc)
        {
            string draftId = GetProp(root, "id") ?? "";
            string messageId = "";
            if (root.TryGetProperty("message", out JsonElement msg))
                messageId = GetProp(msg, "id") ?? "";
            return $"{title}.\nTo: {to}\n" +
                   (string.IsNullOrWhiteSpace(cc) ? "" : $"Cc: {cc}\n") +
                   $"Subject: {subject}\ndraft_id={draftId}\n" +
                   (string.IsNullOrWhiteSpace(messageId) ? "" : $"message_id={messageId}\n");
        }

        private static void CollectAttachments(JsonElement part, List<string> sink)
        {
            if (part.ValueKind != JsonValueKind.Object)
                return;
            string filename = GetProp(part, "filename") ?? "";
            if (!string.IsNullOrWhiteSpace(filename)
                && part.TryGetProperty("body", out JsonElement body)
                && body.TryGetProperty("attachmentId", out JsonElement attId))
            {
                string? id = attId.GetString();
                string mime = GetProp(part, "mimeType") ?? "";
                string size = body.TryGetProperty("size", out JsonElement sz) ? sz.ToString() : "";
                sink.Add($"filename={filename} attachment_id={id} mime={mime} size={size}");
            }
            if (part.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in parts.EnumerateArray())
                    CollectAttachments(child, sink);
            }
        }

        private static async Task<string> TryReadDriveContentAsync(string fileId, string mime, string accessToken, CancellationToken token)
        {
            try
            {
                if (string.Equals(mime, "application/vnd.google-apps.document", StringComparison.OrdinalIgnoreCase))
                    return Bound(await GetStringAsync($"{DriveBase}/files/{Uri.EscapeDataString(fileId)}/export?mimeType={Uri.EscapeDataString("text/plain")}", accessToken, token).ConfigureAwait(false), 10000);
                if (string.Equals(mime, "application/vnd.google-apps.spreadsheet", StringComparison.OrdinalIgnoreCase))
                    return Bound(await GetStringAsync($"{DriveBase}/files/{Uri.EscapeDataString(fileId)}/export?mimeType={Uri.EscapeDataString("text/csv")}", accessToken, token).ConfigureAwait(false), 10000);
                if (string.Equals(mime, "application/vnd.google-apps.presentation", StringComparison.OrdinalIgnoreCase))
                    return Bound(await GetStringAsync($"{DriveBase}/files/{Uri.EscapeDataString(fileId)}/export?mimeType={Uri.EscapeDataString("text/plain")}", accessToken, token).ConfigureAwait(false), 10000);
                if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mime, "application/json", StringComparison.OrdinalIgnoreCase)
                    || mime.Contains("xml", StringComparison.OrdinalIgnoreCase)
                    || mime.Contains("javascript", StringComparison.OrdinalIgnoreCase)
                    || mime.Contains("csv", StringComparison.OrdinalIgnoreCase))
                {
                    return Bound(await GetStringAsync($"{DriveBase}/files/{Uri.EscapeDataString(fileId)}?alt=media", accessToken, token).ConfigureAwait(false), 10000);
                }
            }
            catch (Exception ex)
            {
                return $"(Could not load content: {ex.Message})";
            }
            return string.Empty;
        }

        private static bool TryReadEmailFields(JsonElement args, out string to, out string subject, out string body, out string cc, out string bcc, out string error)
        {
            to = GetString(args, "to").Trim();
            subject = GetString(args, "subject").Trim();
            body = GetString(args, "body");
            cc = GetString(args, "cc").Trim();
            bcc = GetString(args, "bcc").Trim();
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(to)) { error = "A recipient (to) is required."; return false; }
            if (string.IsNullOrWhiteSpace(subject)) { error = "A subject is required."; return false; }
            if (string.IsNullOrWhiteSpace(body)) { error = "An email body is required."; return false; }
            return true;
        }

        private static string BuildRfc2822RawMessage(
            string to,
            string subject,
            string body,
            string cc,
            string bcc,
            string? inReplyTo = null,
            string? references = null)
        {
            var sb = new StringBuilder();
            sb.Append("To: ").Append(to.Trim()).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(cc)) sb.Append("Cc: ").Append(cc.Trim()).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(bcc)) sb.Append("Bcc: ").Append(bcc.Trim()).Append("\r\n");
            sb.Append("Subject: ").Append(EncodeSubjectHeader(subject)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(inReplyTo)) sb.Append("In-Reply-To: ").Append(inReplyTo.Trim()).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(references)) sb.Append("References: ").Append(references.Trim()).Append("\r\n");
            sb.Append("MIME-Version: 1.0\r\n");
            sb.Append("Content-Type: text/plain; charset=\"UTF-8\"\r\n");
            sb.Append("Content-Transfer-Encoding: 8bit\r\n\r\n");
            sb.Append(body ?? string.Empty);
            if (!(body ?? "").EndsWith("\n", StringComparison.Ordinal))
                sb.Append("\r\n");
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string EncodeSubjectHeader(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return string.Empty;
            subject = subject.Replace("\r", " ").Replace("\n", " ");
            if (!subject.Any(c => c > 127)) return subject;
            return "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(subject)) + "?=";
        }

        private static string ExtractEmailAddresses(string headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return string.Empty;
            // Keep angle-addr if present, otherwise whole token.
            var matches = System.Text.RegularExpressions.Regex.Matches(headerValue, @"<([^>]+)>|([^\s,;]+@[^\s,;]+)");
            if (matches.Count == 0) return headerValue.Trim();
            return string.Join(", ", matches.Select(m =>
                !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value));
        }

        private static string EscapeDriveQuery(string value)
            => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

        private static IReadOnlyList<string> SplitIds(string raw)
            => string.IsNullOrWhiteSpace(raw)
                ? Array.Empty<string>()
                : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static bool IsTruthy(string value)
            => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

        private static string Bound(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text[..max] + "\n…[truncated]";
        }

        private static string FormatApiError(int status, string body)
        {
            string hint = body.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
                || body.Contains("ACCESS_TOKEN_SCOPE", StringComparison.OrdinalIgnoreCase)
                || status == 403
                ? " Permission denied — Disconnect Google in Settings → Cloud Connectors, then Connect again and accept the updated Gmail/Drive permissions."
                : "";
            return $"Google API {status}: {Truncate(body, 500)}{hint}";
        }

        private static async Task<JsonDocument> GetJsonAsync(string url, string accessToken, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatApiError((int)response.StatusCode, body));
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }

        private static async Task<string> GetStringAsync(string url, string accessToken, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatApiError((int)response.StatusCode, body));
            return body;
        }

        private static async Task<JsonDocument> PostJsonAsync(string url, string accessToken, string jsonBody, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatApiError((int)response.StatusCode, body));
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }

        private static async Task<JsonDocument> PutJsonAsync(string url, string accessToken, string jsonBody, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatApiError((int)response.StatusCode, body));
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }

        private static async Task<JsonDocument> PatchJsonAsync(string url, string accessToken, string jsonBody, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatApiError((int)response.StatusCode, body));
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }

        private static async Task DeleteAsync(string url, string accessToken, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(FormatApiError((int)response.StatusCode, body));
        }

        private static string? GetHeader(JsonElement messageRoot, string headerName)
        {
            if (!messageRoot.TryGetProperty("payload", out JsonElement payload))
                return null;
            if (!payload.TryGetProperty("headers", out JsonElement headers) || headers.ValueKind != JsonValueKind.Array)
                return null;
            foreach (JsonElement header in headers.EnumerateArray())
            {
                string name = GetProp(header, "name") ?? "";
                if (string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase))
                    return GetProp(header, "value");
            }
            return null;
        }

        private static string ExtractGmailPlainText(JsonElement messageRoot)
        {
            if (!messageRoot.TryGetProperty("payload", out JsonElement payload))
                return messageRoot.TryGetProperty("snippet", out JsonElement sn) ? sn.GetString() ?? "" : "";
            string? body = FindPartBody(payload, "text/plain");
            if (!string.IsNullOrWhiteSpace(body)) return body;
            body = FindPartBody(payload, "text/html");
            if (!string.IsNullOrWhiteSpace(body)) return StripSimpleHtml(body);
            return messageRoot.TryGetProperty("snippet", out JsonElement snippet) ? snippet.GetString() ?? "" : "";
        }

        private static string? FindPartBody(JsonElement part, string mimeType)
        {
            string mime = GetProp(part, "mimeType") ?? "";
            if (string.Equals(mime, mimeType, StringComparison.OrdinalIgnoreCase)
                && part.TryGetProperty("body", out JsonElement body)
                && body.TryGetProperty("data", out JsonElement dataEl))
            {
                string? data = dataEl.GetString();
                if (!string.IsNullOrWhiteSpace(data))
                    return DecodeBase64Url(data);
            }
            if (part.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in parts.EnumerateArray())
                {
                    string? found = FindPartBody(child, mimeType);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
            }
            return null;
        }

        private static string DecodeBase64Url(string data)
        {
            string padded = data.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
            }
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }

        private static string StripSimpleHtml(string html)
        {
            string text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static string GetString(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement el))
                return string.Empty;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString() ?? "",
                JsonValueKind.Number => el.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => el.ToString()
            };
        }

        private static int GetInt(JsonElement root, string name, int defaultValue)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement el))
                return defaultValue;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n)) return n;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int parsed)) return parsed;
            return defaultValue;
        }

        private static string? GetProp(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out JsonElement p)
                ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString()
                : null;

        private static bool TryArray(JsonElement root, string name, out JsonElement array)
        {
            array = default;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out array))
                return false;
            return array.ValueKind == JsonValueKind.Array;
        }

        private static string Req(JsonElement args, string name, out string? error)
        {
            string value = GetString(args, name).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{name} is required.";
                return string.Empty;
            }
            error = null;
            return value;
        }

        private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));
        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static string Truncate(string text, int max)
            => string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text[..max] + "…";

        private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };

        private static JsonObject Props(params (string name, JsonObject schema)[] fields)
        {
            var props = new JsonObject();
            foreach (var (n, s) in fields)
                props[n] = s;
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["additionalProperties"] = false
            };
        }

        private static JsonObject PropsObj(params (string name, JsonObject schema)[] fields)
        {
            var props = new JsonObject();
            foreach (var (n, s) in fields)
                props[n] = s;
            return props;
        }

        private static JsonObject Merge(JsonObject a, JsonObject b)
        {
            var merged = new JsonObject();
            foreach (var kv in a)
                merged[kv.Key] = kv.Value?.DeepClone();
            foreach (var kv in b)
                merged[kv.Key] = kv.Value?.DeepClone();
            return merged;
        }

        private static McpToolDefinition Tool(string connectorId, string name, string description, JsonObject schema, params string[] required)
        {
            if (required.Length > 0)
            {
                var req = new JsonArray();
                foreach (string r in required) req.Add(r);
                schema["required"] = req;
            }
            if (!schema.ContainsKey("type"))
                schema["type"] = "object";
            return new McpToolDefinition
            {
                Name = name,
                ConnectorId = connectorId,
                Description = description,
                ParametersSchema = schema
            };
        }
    }
}
