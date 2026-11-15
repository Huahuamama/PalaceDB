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

/// <summary>Row insertion, deletion, duplication and reordering.</summary>
public sealed partial class EditorHost
{

    private void InsertRow(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var index = Math.Clamp(GetInt(payload, "row"), 0, sheet.Lines.Count);
        var row = CreateEmptyRow(sheet, index);

        var command = new RowInsertCommand(sheet, index, new[] { row });
        _document.Commands.Execute(command);
        RefreshAfterStructuralChange(sheet.Name, index);
    }

    private void DeleteRows(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var rows = ReadIntArray(payload, "rows");
        if (rows.Count == 0)
            return;

        var start = rows.Min();
        var count = rows.Max() - start + 1;

        // Contiguous selection is the common case; delete it in one undo step.
        var command = new RowDeleteCommand(sheet, start, count);
        _document.Commands.Execute(command);

        // Requirement R3: land on the next row, or the previous one at the end.
        var target = CursorRules.RowAfterDelete(start, count, sheet.Lines.Count);
        RefreshAfterStructuralChange(sheet.Name, Math.Max(target, 0));
    }

    private void DuplicateRows(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var rows = ReadIntArray(payload, "rows");
        if (rows.Count == 0)
            return;

        var idColumn = sheet.IdColumn;
        var existing = new HashSet<string>(
            sheet.Lines.Select(r => idColumn is null ? string.Empty : r.GetString(idColumn.Name) ?? string.Empty)
                .Where(id => id.Length > 0),
            StringComparer.Ordinal);

        var copies = new List<CdbObject>();
        // A copy is a new record: a Guid identifies a row, and an auto-increment column
        // numbers rows, so neither may simply be cloned along with the rest.
        var nextAuto = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var column in sheet.Columns)
            if (_document.Meta.Document.IsAutoIncrement(sheet.Name, column.Name))
                nextAuto[column.Name] = AutoNumber.Next(sheet, column.Name);

        foreach (var index in rows.OrderBy(i => i))
        {
            if (index < 0 || index >= sheet.Lines.Count)
                continue;

            var copy = sheet.Lines[index].Clone();
            if (idColumn is not null)
            {
                var id = copy.GetString(idColumn.Name) ?? string.Empty;
                copy.Set(idColumn.Name, new CdbValue.Str(
                    PasteService.MakeUnique(id.Length == 0 ? "copy" : id, existing)));
            }

            foreach (var column in sheet.Columns)
            {
                if (column.Type is ColumnType.Guid)
                    copy.Set(column.Name, new CdbValue.Str(GuidGenerator.New()));
                else if (nextAuto.TryGetValue(column.Name, out var next))
                {
                    copy.Set(column.Name, new CdbValue.Number(next, null));
                    nextAuto[column.Name] = next + 1;
                }
            }

            copies.Add(copy);
        }

        if (copies.Count == 0)
            return;

        var insertAt = rows.Max() + 1;
        _document.Commands.Execute(new RowInsertCommand(sheet, insertAt, copies));
        RefreshAfterStructuralChange(sheet.Name, insertAt);
    }

    private void MoveRow(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var from = GetInt(payload, "from");
        var to = GetInt(payload, "to");
        if (from < 0 || from >= sheet.Lines.Count || to < 0 || to >= sheet.Lines.Count || from == to)
            return;

        _document.Commands.Execute(new RowMoveCommand(sheet, from, to));
        RefreshAfterStructuralChange(sheet.Name, to);
    }

    /// <summary>Builds a row pre-filled with the column defaults.</summary>
    private CdbObject CreateEmptyRow(SheetData sheet, int index)
    {
        var row = new CdbObject();
        var idColumn = sheet.IdColumn;

        foreach (var column in sheet.Columns)
        {
            if (column.Type is ColumnType.Id)
                continue;

            // An auto-increment column numbers itself: one more than the highest number
            // already in the table, wherever the row is inserted.
            if (_document!.Meta.Document.IsAutoIncrement(sheet.Name, column.Name))
            {
                row.Set(column.Name, new CdbValue.Number(AutoNumber.Next(sheet, column.Name), null));
                continue;
            }

            var value = TypeDefaults.Create(column, _document!.Database, sheet);
            if (value is not null)
                row.Set(column.Name, value);
        }

        // The identifier is the author's to choose: a generated "row12" only ever gets in
        // the way, and a missing id is reported in the status bar instead.
        if (idColumn is not null)
            row.Set(idColumn.Name, new CdbValue.Str(string.Empty));

        return row;
    }

    /// <summary>
    /// Numbers freshly built rows that are about to be inserted: each auto-increment
    /// column continues from the highest number already in the table.
    /// </summary>
    private void AssignAutoNumbers(SheetData sheet, IReadOnlyList<CdbObject> rows)
    {
        if (_document is null || rows.Count == 0)
            return;

        foreach (var column in sheet.Columns)
        {
            if (!_document.Meta.Document.IsAutoIncrement(sheet.Name, column.Name))
                continue;

            var next = AutoNumber.Next(sheet, column.Name);
            foreach (var row in rows)
                row.Set(column.Name, new CdbValue.Number(next++, null));
        }
    }
}
