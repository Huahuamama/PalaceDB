using System.Text.Json;
using PalaceDB.Core.Commands;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;

namespace PalaceDB.App;

/// <summary>
/// Public enum / flags types: the sidebar panel's operations, the binding a column keeps
/// in the side-car, and the check that runs when a file is opened.
/// <para>
/// The definition lives in the side-car and every bound column carries the same
/// <c>typeStr</c> in the .cdb, so the file stays a plain CastleDB file while PalaceDB is
/// the one keeping the copies equal.
/// </para>
/// </summary>
public sealed partial class EditorHost
{
    /// <summary>Creates, renames or re-values a public type, in one undoable step.</summary>
    private void SavePublicType(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var name = GetString(payload, "name").Trim();
        if (name.Length == 0)
        {
            SendError("A public type needs a name.");
            return;
        }

        var meta = _document.Meta.Document;
        var renameFrom = GetString(payload, "renameFrom").Trim();

        if (renameFrom.Length == 0 && meta.PublicTypes.ContainsKey(name))
        {
            SendError($"There already is a public type called '{name}'.");
            return;
        }

        if (renameFrom.Length > 0 &&
            !string.Equals(renameFrom, name, StringComparison.Ordinal) &&
            meta.PublicTypes.ContainsKey(name))
        {
            SendError($"There already is a public type called '{name}'.");
            return;
        }

        if (renameFrom.Length > 0 && !meta.PublicTypes.ContainsKey(renameFrom))
        {
            SendError($"The public type '{renameFrom}' no longer exists.");
            return;
        }

        var values = GetString(payload, "values")
            .Split(',')
            .Select(value => value.Trim())
            .ToList();

        if (values.Count == 0 || values.All(value => value.Length == 0))
        {
            SendError("A public type needs at least one value.");
            return;
        }

        var kind = string.Equals(GetString(payload, "kind"), "flags", StringComparison.OrdinalIgnoreCase)
            ? "flags"
            : "enum";

        var command = PublicTypeService.BuildUpdate(
            _document.Database, meta, name, kind, values,
            renameFrom.Length > 0 ? renameFrom : null);

        _document.Commands.Execute(command);
        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshKeepingCursor(_currentSheet ?? _document.Database.VisibleSheets.FirstOrDefault()?.Name ?? string.Empty);

        var columns = PublicTypeService.BoundColumns(_document.Database, meta, name).Count;
        SendToast(
            columns == 0
                ? $"Saved the public {(kind == "flags" ? "flags" : "enum")} '{name}'."
                : $"Saved '{name}'. {columns} column(s) now use these values.",
            "info");
    }

    /// <summary>
    /// Forgets a public type. The columns keep the values they carry; they simply stop
    /// following the definition, which is why nothing is rewritten here.
    /// </summary>
    private void DeletePublicType(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var name = GetString(payload, "name").Trim();
        var meta = _document.Meta.Document;
        if (!meta.PublicTypes.ContainsKey(name))
            return;

        var columns = PublicTypeService.BoundColumns(_document.Database, meta, name).Count;
        meta.DropPublicType(name);
        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshKeepingCursor(_currentSheet ?? _document.Database.VisibleSheets.FirstOrDefault()?.Name ?? string.Empty);

        SendToast(
            columns == 0
                ? $"Deleted the public type '{name}'."
                : $"Deleted the public type '{name}'. {columns} column(s) keep their values and are now independent.",
            "info");
    }

    /// <summary>Public type a payload names, or null when it names none.</summary>
    private PublicTypeMeta? PublicTypeOf(JsonElement payload, out string name)
    {
        name = GetString(payload, "publicType").Trim();
        if (name.Length == 0 || _document is null)
            return null;
        return _document.Meta.Document.GetPublicType(name);
    }

    /// <summary>
    /// Records what the column dialog said about the side-car parts of a column: which
    /// public type it follows and whether it numbers its own rows.
    /// </summary>
    private void RememberColumnMeta(SheetData sheet, string columnName, string? publicType, bool autoIncrement)
    {
        if (_document is null)
            return;

        // Ids are handed out by Sync, so the column has to be known before it is bound.
        _document.Meta.Sync(_document.Database);
        _document.Meta.Document.SetColumnPublicType(
            sheet.Name, columnName, string.IsNullOrEmpty(publicType) ? null : publicType);
        _document.Meta.Document.SetAutoIncrement(sheet.Name, columnName, autoIncrement);
    }

    /// <summary>
    /// Records a column edit that changes nothing about the column itself: choosing a
    /// public type with the values it already has, or ticking auto-increment on an Int
    /// column, produces no schema command, and would otherwise be silently dropped.
    /// Returns true when something was written.
    /// </summary>
    private bool TryApplyColumnMetaOnly(
        SheetData sheet, string columnName, string? publicType, bool autoIncrement, int index)
    {
        if (_document is null)
            return false;

        var wanted = string.IsNullOrEmpty(publicType) ? null : publicType;
        var currentPublic = _document.Meta.Document.GetColumnPublicType(sheet.Name, columnName);
        var currentAuto = _document.Meta.Document.IsAutoIncrement(sheet.Name, columnName);

        if (string.Equals(currentPublic ?? string.Empty, wanted ?? string.Empty, StringComparison.Ordinal) &&
            currentAuto == autoIncrement)
            return false;

        RememberColumnMeta(sheet, columnName, wanted, autoIncrement);
        CommitColumnEdit(sheet);
        RefreshAfterStructuralChange(sheet.Name, 0, index);
        SendToast($"Updated column '{columnName}'.", "info");
        return true;
    }

    /// <summary>
    /// Brings every bound column back to the definition of its public type when a file is
    /// opened.A column edited outside the application can end up with a different value
    /// list; the definition wins, and the repair is an ordinary undoable step so the user
    /// is not left with a file that silently disagrees with itself.
    /// </summary>
    private void RepairPublicTypes()
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var meta = _document.Meta.Document;
        var repaired = 0;
        var types = 0;

        foreach (var (name, definition) in meta.PublicTypes.ToList())
        {
            var encoded = PublicTypeService.Encode(definition);
            var drifted = PublicTypeService.BoundColumns(_document.Database, meta, name)
                .Any(entry => !string.Equals(
                    ColumnTypeCodec.Encode(entry.Column.Type), encoded, StringComparison.Ordinal));

            if (!drifted)
                continue;

            _document.Commands.Execute(PublicTypeService.BuildUpdate(
                _document.Database, meta, name, definition.Kind, definition.Values));
            repaired += PublicTypeService.BoundColumns(_document.Database, meta, name).Count;
            types++;
        }

        if (repaired == 0)
            return;

        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        SendToast(
            $"{repaired} column(s) did not match their public type and were brought back in line " +
            $"({types} type(s)). Save to keep the fix.",
            "warning");
    }
}
