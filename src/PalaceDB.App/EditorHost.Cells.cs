using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Windows;
using PalaceDB.Core.Commands;
using PalaceDB.Core.Cursor;
using PalaceDB.Core.Document;
using PalaceDB.Core.Editing;
using PalaceDB.Core.Export;
using PalaceDB.Core.Json;
using PalaceDB.Core.Media;
using PalaceDB.Core.Merge;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;
using PalaceDB.Core.Settings;
using PalaceDB.Core.Storage;
using PalaceDB.Core.Templates;
using Microsoft.Web.WebView2.Core;

namespace PalaceDB.App;

/// <summary>Cell edits, ranges, clipboard, search and replace.</summary>
public sealed partial class EditorHost
{

    // ---------------------------------------------------------------- editing

    /// <summary>Parses and applies a typed cell value, recording it as one undo step.</summary>
    private void SetCell(JsonElement payload)
    {
        if (_document is null || _formatter is null)
            return;

        var sheetName = GetString(payload, "sheet");
        var rowIndex = GetInt(payload, "row");
        var columnName = GetString(payload, "column");
        var text = GetString(payload, "text");

        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null || rowIndex < 0 || rowIndex >= sheet.Lines.Count)
            return;

        var column = sheet.GetColumn(columnName);
        if (column is null)
            return;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only.");
            SendRowUpdated(sheet, rowIndex);
            return;
        }

        var row = sheet.Lines[rowIndex];

        CdbValue? value;
        try
        {
            value = ValueCodec.ParseText(column, text);
        }
        catch (CdbValidationException ex)
        {
            SendCellRejected(sheetName, rowIndex, columnName, ex.Message);
            return;
        }

        if (ValuesEqual(row.Get(columnName), value))
        {
            SendRowUpdated(sheet, rowIndex);
            return;
        }

        var command = new CellEditCommand(row, columnName, value)
        {
            CursorBefore = CursorResolver.CreateAnchor(sheet, rowIndex, sheet.Columns.IndexOf(column)),
            CursorAfter = CursorResolver.CreateAnchor(sheet, rowIndex, sheet.Columns.IndexOf(column)),
        };

        _document.Commands.Execute(command);
        SendRowUpdated(sheet, rowIndex);
        SendDirty();
    }

    /// <summary>Clears every cell in the given rectangle (bulk clear / Delete key).</summary>
    /// <summary>
    /// Fills the selected rectangle with one value (requirement R5: Ctrl+Enter in the
    /// cell editor). The whole fill is a single undo step.
    /// </summary>
    private void FillCells(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheetName = GetString(payload, "sheet");
        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null || _document.IsReadOnly)
            return;

        if (!payload.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
            return;
        if (!payload.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Array)
            return;

        var rowIndices = rowsElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetInt32()).Where(i => i >= 0 && i < sheet.Lines.Count).Distinct().ToList();
        var columnNames = columnsElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? string.Empty).ToList();

        if (rowIndices.Count == 0 || columnNames.Count == 0)
            return;

        var text = GetString(payload, "text");
        var builder = new MultiCellEditCommand.Builder("Fill selection");

        try
        {
            foreach (var columnName in columnNames)
            {
                var column = sheet.GetColumn(columnName);
                if (column is null || column.Type is ColumnType.Id)
                    continue;

                var value = ValueCodec.ParseText(column, text);
                foreach (var rowIndex in rowIndices)
                    builder.Add(sheet.Lines[rowIndex], columnName, value);
            }
        }
        catch (CdbValidationException ex)
        {
            SendError(ex.Message);
            return;
        }

        var command = builder.Build();
        if (command is null)
            return;

        _document.Commands.Execute(command);
        SendRows(sheetName, rowIndices.Min(), rowIndices.Max() - rowIndices.Min() + 1);
        SendDirty();
    }

    private void ClearCells(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheetName = GetString(payload, "sheet");
        var sheet = _document.Database.GetSheet(sheetName);
        if (sheet is null || _document.IsReadOnly)
            return;

        if (!payload.TryGetProperty("rows", out var rowsElement) || rowsElement.ValueKind != JsonValueKind.Array)
            return;
        if (!payload.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Array)
            return;

        var rowIndices = rowsElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number)
            .Select(v => v.GetInt32()).Where(i => i >= 0 && i < sheet.Lines.Count).Distinct().ToList();
        var columnNames = columnsElement.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? string.Empty).ToList();

        if (rowIndices.Count == 0 || columnNames.Count == 0)
            return;

        var builder = new MultiCellEditCommand.Builder("Clear cells");
        foreach (var rowIndex in rowIndices)
        {
            var row = sheet.Lines[rowIndex];
            foreach (var columnName in columnNames)
            {
                var column = sheet.GetColumn(columnName);
                if (column is null)
                    continue;
                builder.Add(row, columnName, TypeDefaults.Create(column, _document.Database, sheet));
            }
        }

        var command = builder.Build();
        if (command is null)
            return;

        _document.Commands.Execute(command);
        SendRows(sheetName, rowIndices.Min(), rowIndices.Max() - rowIndices.Min() + 1);
        SendDirty();
    }

    /// <summary>Runs a find query and sends the hits back to the find bar.</summary>
    private void SearchCells(JsonElement payload)
    {
        if (_document is null)
            return;

        var query = ReadSearchQuery(payload);
        if (query is null)
        {
            SendSearchResults(null, Array.Empty<SearchMatch>(), false);
            return;
        }

        try
        {
            var result = SearchEngine.Search(_document.Database, query);
            SendSearchResults(query, result.Matches, result.Truncated);
        }
        catch (ArgumentException ex)
        {
            SendError("Invalid regular expression: " + ex.Message);
            SendSearchResults(query, Array.Empty<SearchMatch>(), false);
        }
        catch (RegexMatchTimeoutException)
        {
            SendError("The regular expression took too long to run.");
            SendSearchResults(query, Array.Empty<SearchMatch>(), false);
        }
    }

    /// <summary>
    /// Replaces the match the find bar is sitting on. The match index is resolved again
    /// against the current cell text, so a stale list cannot rewrite the wrong span.
    /// </summary>
    private void ReplaceOne(JsonElement payload)
    {
        if (_document is null)
            return;

        var query = ReadSearchQuery(payload);
        if (query is null)
            return;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only.");
            return;
        }

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        var rowIndex = GetInt(payload, "row");
        var columnIndex = GetInt(payload, "column");
        var matchIndex = GetInt(payload, "matchIndex");
        var replacement = GetString(payload, "replacement");

        if (sheet is null || rowIndex < 0 || rowIndex >= sheet.Lines.Count)
            return;
        if (columnIndex < 0 || columnIndex >= sheet.Columns.Count)
            return;

        var column = sheet.Columns[columnIndex];
        if (!SearchEngine.CanReplace(column))
        {
            SendError($"'{column.Name}' cannot be replaced.");
            return;
        }

        Regex? regex;
        try
        {
            regex = SearchEngine.Compile(query);
        }
        catch (ArgumentException ex)
        {
            SendError("Invalid regular expression: " + ex.Message);
            return;
        }

        var text = SearchEngine.TextOf(column, sheet.Lines[rowIndex]);
        var spans = SearchEngine.FindAll(text, query, regex);
        if (matchIndex < 0 || matchIndex >= spans.Count)
        {
            // The cell changed under the find bar; refreshing the list is the honest answer.
            SearchCells(payload);
            return;
        }

        var updated = SearchEngine.ReplaceOne(text, spans, replacement, matchIndex);
        if (!TryWriteCellText(sheet, rowIndex, column, updated, out var error))
        {
            SendError(error);
            return;
        }

        SendRowUpdated(sheet, rowIndex);
        SendDirty();
        SearchCells(payload);
    }

    /// <summary>Replaces every match in scope as a single undo step.</summary>
    private void ReplaceAllCells(JsonElement payload)
    {
        if (_document is null)
            return;

        var query = ReadSearchQuery(payload);
        if (query is null)
            return;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only.");
            return;
        }

        var replacement = GetString(payload, "replacement");

        Regex? regex;
        try
        {
            regex = SearchEngine.Compile(query);
        }
        catch (ArgumentException ex)
        {
            SendError("Invalid regular expression: " + ex.Message);
            return;
        }

        SearchResult result;
        try
        {
            // No cap: "Replace All" has to mean all, even past the find bar's 5000-hit
            // display limit. The match list is discarded right after this loop.
            result = SearchEngine.Search(_document.Database, query, int.MaxValue);
        }
        catch (RegexMatchTimeoutException)
        {
            SendError("The regular expression took too long to run.");
            return;
        }

        // One rewrite per cell even when several matches share it: the second match must not
        // be applied to text the first one already changed.
        var builder = new MultiCellEditCommand.Builder("Replace all");
        var seen = new HashSet<(string Sheet, int Row, int Column)>();
        var skipped = 0;

        foreach (var match in result.Matches)
        {
            if (!seen.Add((match.SheetName, match.Row, match.Column)))
                continue;

            var sheet = _document.Database.GetSheet(match.SheetName);
            if (sheet is null)
                continue;

            var column = sheet.Columns[match.Column];
            if (!SearchEngine.CanReplace(column))
            {
                skipped++;
                continue;
            }

            var text = SearchEngine.TextOf(column, sheet.Lines[match.Row]);
            var updated = SearchEngine.ReplaceAll(text, query, regex, replacement);

            CdbValue? value;
            try
            {
                value = ValueCodec.ParseText(column, updated);
            }
            catch (CdbValidationException)
            {
                // "Replace 1 with 2" on an identifier column, or a regex that produces an
                // invalid number: leave the cell alone and say how many were skipped.
                skipped++;
                continue;
            }

            builder.Add(sheet.Lines[match.Row], column.Name, value);
        }

        var command = builder.Build();
        if (command is null)
        {
            SendToast("Nothing to replace.", "info");
            return;
        }

        _document.Commands.Execute(command);
        SendDirty();
        // Rows in several tables may have changed; re-sending the description repaints them.
        // The row counts are unchanged, so the grid keeps its scroll position.
        SendDocument();

        var replaced = command.Count;
        var message = $"Replaced {replaced} cell{(replaced == 1 ? string.Empty : "s")}.";
        if (skipped > 0)
            message += $" {skipped} skipped.";
        SendToast(message, "info");

        SearchCells(payload);
    }

    /// <summary>
    /// Parses replacement text through the column's own rules and records it as one undoable
    /// edit. Returns false with a message when the text is not valid for the type.
    /// </summary>
    private bool TryWriteCellText(SheetData sheet, int rowIndex, Column column, string text, out string error)
    {
        error = string.Empty;

        CdbValue? value;
        try
        {
            value = ValueCodec.ParseText(column, text);
        }
        catch (CdbValidationException ex)
        {
            error = ex.Message;
            return false;
        }

        var row = sheet.Lines[rowIndex];
        if (ValuesEqual(row.Get(column.Name), value))
            return true;

        var index = sheet.Columns.IndexOf(column);
        var command = new CellEditCommand(row, column.Name, value)
        {
            CursorBefore = CursorResolver.CreateAnchor(sheet, rowIndex, index),
            CursorAfter = CursorResolver.CreateAnchor(sheet, rowIndex, index),
        };
        _document!.Commands.Execute(command);
        return true;
    }

    private static SearchQuery? ReadSearchQuery(JsonElement payload)
    {
        var text = GetString(payload, "query");
        if (text.Length == 0)
            return null;

        var scope = string.Equals(GetString(payload, "scope"), "document", StringComparison.Ordinal)
            ? SearchScope.Document
            : SearchScope.Table;

        return new SearchQuery(
            text,
            GetBool(payload, "regex"),
            GetBool(payload, "matchCase"),
            scope,
            GetString(payload, "sheet"));
    }

    private void SendSearchResults(SearchQuery? query, IReadOnlyList<SearchMatch> matches, bool truncated)
    {
        var array = new JsonArray();
        foreach (var match in matches)
        {
            array.Add(new JsonObject
            {
                ["sheet"] = match.SheetName,
                ["row"] = match.Row,
                ["column"] = match.Column,
                ["columnName"] = match.ColumnName,
                ["start"] = match.Start,
                ["length"] = match.Length,
                ["text"] = match.Text,
            });
        }

        Send(new JsonObject
        {
            ["type"] = "searchResults",
            ["payload"] = new JsonObject
            {
                ["query"] = query?.Text,
                ["scope"] = query?.Scope == SearchScope.Document ? "document" : "table",
                ["sheet"] = query?.SheetName,
                ["truncated"] = truncated,
                ["matches"] = array,
            },
        });
    }

    /// <summary>Returns the rows of the referenced sheet so the editor can offer them.</summary>
    private void SendRefOptions(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        var column = sheet?.GetColumn(GetString(payload, "column"));
        if (column?.Type is not ColumnType.Ref reference)
            return;

        var target = _document.Database.GetSheet(reference.Sheet);
        var options = new JsonArray();

        if (target is not null)
        {
            var index = _document.Index.For(target);
            var idColumn = target.IdColumn;
            var iconColumn = target.Props.DisplayIcon;

            foreach (var row in target.Lines)
            {
                var id = idColumn is null ? string.Empty : row.GetString(idColumn.Name) ?? string.Empty;
                options.Add(new JsonObject
                {
                    ["id"] = id,
                    ["display"] = index?.GetDisplay(row) ?? id,
                    // Only sent when the table declares an icon column, so the dropdown
                    // does not ask for image data it will never use.
                    ["icon"] = iconColumn is null ? null : row.GetString(iconColumn),
                });
            }
        }

        Send(new JsonObject
        {
            ["type"] = "refOptions",
            ["payload"] = new JsonObject
            {
                ["sheet"] = reference.Sheet,
                ["iconColumn"] = target?.Props.DisplayIcon,
                ["options"] = options,
            },
        });
    }

    /// <summary>Native file picker for Image and File columns.</summary>
    private void BrowseFile(JsonElement payload)
    {
        if (_document is null)
            return;

        var kind = GetString(payload, "kind");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = kind == "image" ? "Select an image" : "Select a file",
            Filter = kind == "image"
                ? "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files (*.*)|*.*"
                : "All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(_window) != true)
            return;

        // Store the path relative to the database so game engines can resolve it.
        var relative = MakeRelative(_document.Path, dialog.FileName);
        Send(new JsonObject
        {
            ["type"] = "browseResult",
            ["payload"] = new JsonObject { ["path"] = relative },
        });
    }

    private static string MakeRelative(string baseFile, string target)
    {
        try
        {
            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(baseFile));
            if (string.IsNullOrEmpty(baseDirectory))
                return target;

            var relative = Path.GetRelativePath(baseDirectory, target);
            return relative.Replace('\\', '/');
        }
        catch
        {
            return target;
        }
    }

    /// <summary>
    /// Puts plain text on the system clipboard.
    /// <para>
    /// Used for text selected outside the table — an error message, a conflict report —
    /// so copying it does not have to go through the engine's own copy accelerator.
    /// </para>
    /// </summary>
    private void CopyText(JsonElement payload)
    {
        var text = GetString(payload, "text");
        if (text.Length == 0)
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            SendError("The clipboard is not available: " + ex.Message);
        }
    }

    /// <summary>Copies (or cuts) the selected rectangle to the system clipboard.</summary>
    private void CopyCells(JsonElement payload, bool cut)
    {
        if (_document is null)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var rows = ReadIntArray(payload, "rows");
        var columns = ReadStringArray(payload, "columns");
        if (rows.Count == 0 || columns.Count == 0)
            return;

        // Copy exactly what was selected. Dropping the identifier column here made
        // copying an id (or a whole row) silently produce an empty payload, so pasting it
        // back did nothing at all.
        var text = ClipboardService.Serialize(sheet, rows, columns, includeIdColumn: true);
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            SendError("The clipboard is not available: " + ex.Message);
            return;
        }

        if (cut && !_document.IsReadOnly)
        {
            var builder = new MultiCellEditCommand.Builder("Cut cells");
            foreach (var rowIndex in rows)
            {
                if (rowIndex < 0 || rowIndex >= sheet.Lines.Count)
                    continue;
                var row = sheet.Lines[rowIndex];
                foreach (var columnName in columns)
                {
                    var column = sheet.GetColumn(columnName);
                    if (column is null || column.Type is ColumnType.Id)
                        continue;
                    builder.Add(row, columnName, TypeDefaults.Create(column, _document.Database, sheet));
                }
            }

            var command = builder.Build();
            if (command is not null)
            {
                _document.Commands.Execute(command);
                SendRows(sheet.Name, rows.Min(), rows.Max() - rows.Min() + 1);
                SendDirty();
            }
        }

        Send(new JsonObject
        {
            ["type"] = "clipboardWritten",
            ["payload"] = new JsonObject
            {
                ["cut"] = cut,
                ["rows"] = rows.Count,
                ["columns"] = columns.Count,
            },
        });
    }

    /// <summary>Pastes the clipboard at the cursor, either overwriting or inserting rows.</summary>
    private void PasteCells(JsonElement payload)
    {
        if (_document is null)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        if (_document.IsReadOnly)
        {
            SendError("This file is read-only.");
            return;
        }

        string? text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception ex)
        {
            SendError("The clipboard is not available: " + ex.Message);
            return;
        }

        var clipboard = ClipboardService.Deserialize(text);
        var mode = GetString(payload, "mode");
        var row = GetInt(payload, "row");
        var column = GetInt(payload, "column");

        if (clipboard is null || clipboard.Rows.Count == 0)
        {
            // Not our format: treat it as tab separated text from a spreadsheet (R5).
            var grid = TsvParser.Parse(text);
            if (grid.Count == 0)
            {
                SendToast("The clipboard does not contain cells.", "warning");
                return;
            }

            clipboard = PasteService.FromText(sheet, column, grid, out var problem);
            if (clipboard is null)
            {
                SendError("Cannot paste: " + problem);
                return;
            }
        }

        var skipped = 0;
        IEditorCommand? command = mode == "insert"
            ? PasteService.BuildInsert(sheet, row, clipboard, _document.Database)
            : PasteService.BuildOverwrite(sheet, row, column, clipboard, out skipped);

        // Rows that arrive by paste are new rows too, so they take the next numbers of an
        // auto-increment column instead of the default the paste service left in place.
        if (command is RowInsertCommand insertCommand)
            AssignAutoNumbers(sheet, insertCommand.Rows);

        if (command is null)
        {
            if (skipped > 0)
                SendToast("Nothing was pasted: the values do not fit the target columns.", "warning");
            return;
        }

        _document.Commands.Execute(command);
        // The two-argument overload parks the cursor on column 0: it is meant for row
        // inserts and deletes, where the row is what moved. A paste is anchored to the
        // cell it was invoked on, so pass that column through — otherwise every Ctrl+V
        // throws the cursor back to the first cell of the row.
        RefreshAfterStructuralChange(sheet.Name, row, column);
        if (skipped > 0)
            SendToast($"Pasted, {skipped} cell(s) skipped: they do not fit the target column.", "warning");
        else
            SendToast(mode == "insert" ? "Rows inserted." : "Pasted.", "info");
    }

    // ---------------------------------------------------------------- images
    /// <summary>
    /// Turns stored image paths into data URLs so the grid can draw thumbnails and
    /// reference dropdowns can draw icons. Paths are resolved against the folder of the
    /// database, which is what PalaceDB's relative paths assume.
    /// </summary>
    private void ResolveImages(JsonElement payload)
    {
        var resolver = _images;
        var entries = new JsonArray();

        if (resolver is not null &&
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("paths", out var paths) &&
            paths.ValueKind == JsonValueKind.Array)
        {
            var requested = new List<string>();
            foreach (var element in paths.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String)
                    requested.Add(element.GetString() ?? string.Empty);
            }

            foreach (var result in resolver.ResolveMany(requested))
            {
                entries.Add(new JsonObject
                {
                    ["path"] = result.Path,
                    ["url"] = result.DataUrl,
                    ["error"] = result.IsImage ? null : ImageResolver.Describe(result.Failure),
                });
            }
        }

        Send(new JsonObject
        {
            ["type"] = "images",
            ["payload"] = new JsonObject { ["entries"] = entries },
        });
    }

    private static bool ValuesEqual(CdbValue? a, CdbValue? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Equals(b);
    }
}
