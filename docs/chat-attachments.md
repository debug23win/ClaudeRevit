# Documents and request supplements (v3.8.1)

Click the paperclip, select several files and send them with a message or on their
own. You can also drag files onto the pane, or paste copied files with Ctrl+V.
Remove an unsent file with ×. Each project tab keeps its own attachment list and
draft. Sent filenames remain in the conversation and in **Copy log**.

While the model works, write an additional instruction, attach more files if
needed and click **Дополнить** (or press Enter). **Stop** is a separate button.
API agents receive the update before their next model request. Claude Code/Codex
MCP agents receive it as `user_update` in their next tool result. An update sent
after the last tool call continues as another turn in the same conversation.
A running Revit operation finishes before the model can apply the update.
If the task is already stopping/finishing, the draft stays in the input box for
you to send after it returns to idle. Completing a task never clears an unsent draft.

Stop retains updates not yet handed to the model and includes them with your next
explicit request; it does not start another task automatically.

## Supported reading

All extensions can be attached. The available parser determines what the model
can inspect directly; accepting a file does not imply every proprietary format
can be decoded.

| Format | What `read_attachment` provides |
|---|---|
| PDF | Extracted text, by page. Scanned pages need OCR elsewhere; page images and diagrams are not rendered. Encrypted files may need an unencrypted copy. |
| DOCX | Paragraphs, table text, headers and footers. Embedded images/layout are not reconstructed. |
| XLSX | Sheets, cell addresses and stored/cached values, including shared strings. Formulas are not recalculated; macros are not executed. |
| PPTX | Slide and speaker-note text, identified by their archive section names. |
| ODT/ODS/ODP | Text from document content. |
| TXT, CSV, JSON, XML, Markdown, IFC, ASCII DXF and other text | Paged text; UTF-8, BOM encodings and Windows-1251 fallback. |
| PNG, JPEG, GIF, BMP, TIFF, WebP | Native image content for vision. Windows must have a decoder for the format (notably WebP). First frame only; resized to at most 1568 pixels on the longest side, further if required by the 5 MB image limit. |
| ZIP/IFCZIP | Entry listing and small known text entries. No files are extracted to disk. |
| RFA, RVT, DWG, legacy DOC/XLS and other binary files | Stored copy, metadata and local path for a suitable native import/open tool. No universal binary decoder. |

Extraction failure leaves the file attached and reports the reason. Hover over
its name to see the parser notice. File contents are reference data; they do not
replace the user's instructions. No file macros or scripts run on attachment.

## Storage and limits

The plugin copies each file to
`%APPDATA%\ClaudeRevit\attachments\<attachment ID>\<filename>`.
Changing the source afterwards does not change the attachment. Conversation
history stores the reference to this copy. `read_attachment` accepts an ID
authorized for that conversation; it cannot read an arbitrary path or another
project's attachments. Clearing the conversation removes that access. Stored
copies remain on disk, so remove unneeded attachment folders manually if desired.

Limits are 20 files per message, 100 MiB per file and 200 MiB total per message.
Extractors return at most 1,000,000 characters per file and 500 PDF pages, and
report `extractor_truncated` when content was cut. XML entries are limited to
16 MB and cannot resolve external entities. ZIPs may contain up to 10,000 entries;
the generic archive reader lists at most 1,000. Images above 100 megapixels are
rejected before pixel decoding. Parsing runs off Revit's API thread; parsed text
is cached for at most 16 attachments at a time and reloaded as needed.

## Tool contract

The prompt contains a short file manifest. `read_attachment` is always available
in the pane's compact MCP catalogue and API core catalogue:

```json
{"attachment_id":"ID from manifest","section":0,"offset":0,"max_chars":12000}
```

`section` is a zero-based page/sheet/part index, `offset` is a character offset and
`max_chars` is 1–20,000. The result includes `section_count`, a directory of up to
50 sections, `next_section` for the next directory page, and `next_offset` for
more text within the current section. Image reads add native image content to
MCP/API tool results. Binary files provide the local path and parser notice.

Validation includes real PDF/Office/archive/text fixtures, scoped HTTP MCP reads,
single delivery of updates, concurrent inbox completion, history persistence,
WPF image encoding, several attached files, independent project drafts and busy
pane controls. These isolated checks do not run a native Revit modelling benchmark.
