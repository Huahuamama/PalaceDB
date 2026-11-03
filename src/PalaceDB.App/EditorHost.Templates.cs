using System.Text.Json;
using System.Text.Json.Nodes;
using PalaceDB.Core.Commands;
using PalaceDB.Core.Metadata;
using PalaceDB.Core.Model;
using PalaceDB.Core.Serialization;
using PalaceDB.Core.Templates;

namespace PalaceDB.App;

/// <summary>
/// Table templates: creating a table from one, keeping the tables of a group in step, and
/// the second confirmation that a destructive column edit needs when other tables share
/// the column.
/// </summary>
public sealed partial class EditorHost
{
    // ---------------------------------------------------------------- shared helpers

    /// <summary>Builds the definition a dialog described, in the form the commands take.</summary>
    private static ColumnSpec BuildSpec(
        string name,
        ColumnType type,
        bool optional,
        ColumnOptions options,
        IReadOnlyList<SubColumnSpec>? fields)
    {
        var spec = new ColumnSpec
        {
            Name = name,
            TypeStr = ColumnTypeCodec.Encode(type),
            Optional = optional,
            Display = options.Display,
            Kind = options.Kind,
            DefaultValue = options.DefaultValue,
        };

        if (fields is not null)
            foreach (var field in fields)
                spec.Fields.Add(new SubFieldSpec(field.Name, ColumnTypeCodec.Encode(field.Type), field.Original));

        return spec;
    }

    /// <summary>
    /// Turns a list of column operations into one undoable command for the edited table
    /// and for every table that follows the same template.
    /// </summary>
    private IEditorCommand? BuildColumnCommand(
        SheetData sheet,
        IReadOnlyList<ColumnOp> ops,
        string label,
        bool fillInsertedRows = false)
    {
        if (_document is null)
            return null;

        return TemplateService.BuildMirrored(
            _document.Database, _document.Meta, sheet, ops, label, fillInsertedRows);
    }

    /// <summary>
    /// Persists what a column edit changed: the metadata side-car follows the new column
    /// set, and the template definition is rewritten from the table that was just edited
    /// (it is derived state, so it has to be told which table is the reference).
    /// </summary>
    private void CommitColumnEdit(SheetData sheet)
    {
        if (_document is null)
            return;

        _document.Meta.Sync(_document.Database);

        if (_document.Meta.GetTableTemplate(sheet.Name) is { } templateId)
            _document.Meta.RefreshTemplate(templateId, sheet);

        _document.SaveMeta();
    }

    /// <summary>Toast text that also names the other tables a change reached.</summary>
    private string DescribeColumnEdit(string message, SheetData sheet, string columnName)
    {
        if (_document is null)
            return message;

        var siblings = TemplateService.Siblings(_document.Database, _document.Meta, sheet);
        if (siblings.Count == 0)
            return message;

        var others = string.Join(", ", siblings.Select(s => s.Name));
        return $"{message} Also applied to {others}.";
    }

    // ---------------------------------------------------------------- second confirmation

    /// <summary>
    /// Refuses a column edit that would throw away values in other tables unless the user
    /// confirmed it. The refusal carries the numbers and a ready-to-send retry payload, so
    /// the interface only has to render the dialog and send it back.
    /// </summary>
    private bool ConfirmSharedLoss(JsonElement payload, SheetData sheet, ColumnSpec target, string action)
    {
        if (_document is null || GetBool(payload, "confirmed"))
            return true;

        var impact = TemplateService.MeasureRetype(_document.Database, _document.Meta, sheet, target);
        if (!impact.Shared || !impact.Lossy)
            return true;

        SendImpact(impact, action, payload);
        return false;
    }

    private bool ConfirmSharedDelete(JsonElement payload, SheetData sheet, string columnName)
    {
        if (_document is null || GetBool(payload, "confirmed"))
            return true;

        var impact = TemplateService.MeasureDelete(_document.Database, _document.Meta, sheet, columnName);
        if (!impact.Shared || !impact.Lossy)
            return true;

        SendImpact(impact, "deleteColumn", payload);
        return false;
    }

    private void SendImpact(TemplateImpact impact, string action, JsonElement payload)
    {
        var tables = new JsonArray();
        foreach (var table in impact.Tables)
            tables.Add(new JsonObject
            {
                ["table"] = table.Table,
                ["rows"] = table.Rows,
                ["values"] = table.Values,
            });

        // The interface resends this untouched payload with "confirmed" set, so the
        // dialog can never disagree with what the host measured.
        var retry = JsonNode.Parse(payload.GetRawText()) as JsonObject ?? new JsonObject();
        retry["confirmed"] = true;

        Send(new JsonObject
        {
            ["type"] = "columnImpact",
            ["payload"] = new JsonObject
            {
                ["action"] = action,
                ["column"] = impact.Column,
                ["operation"] = impact.Operation,
                ["shared"] = impact.Shared,
                ["total"] = impact.Total,
                ["rows"] = impact.Rows,
                ["tables"] = tables,
                ["retry"] = retry,
            },
        });
    }

    // ---------------------------------------------------------------- creating and linking

    /// <summary>
    /// Creates a table, optionally from a template. The link is part of the same undo step
    /// so that undoing the creation does not leave a table in the group.
    /// </summary>
    private void AddSheetFromTemplate(JsonElement payload, string name)
    {
        if (_document is null)
            return;

        var templateId = GetString(payload, "template");
        var stored = _document.Meta.GetTemplate(templateId);
        if (stored is null)
        {
            SendError("That template no longer exists.");
            return;
        }

        var index = _document.Database.Sheets.Count;
        var create = TemplateService.CreateTable(_document.Database, index, name, stored.ToSpecs());

        var composite = new CompositeCommand($"Add Table '{name}'");
        composite.Add(create);
        composite.Add(new TemplateLinkCommand(_document.Meta, name, templateId, $"Add Table '{name}'"));

        _document.Commands.Execute(composite);
        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshAfterStructuralChange(name, 0, 0);
        SendToast($"Added table '{name}' from the template '{stored.Name}'.", "info");
    }

    // ---------------------------------------------------------------- applying

    /// <summary>
    /// Makes a table follow a template. The table is compared with it first: missing
    /// columns and a different order are fine, anything else is reported and nothing is
    /// changed.
    /// </summary>
    private void ApplyTemplate(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var templateId = GetString(payload, "template");
        var stored = _document.Meta.GetTemplate(templateId);
        if (stored is null)
        {
            SendError("That template no longer exists.");
            return;
        }

        var template = new TemplateSpec { Id = templateId, Name = stored.Name, Columns = stored.ToSpecs() };
        var audit = TemplateService.Audit(sheet, template);

        if (!audit.CanApply)
        {
            SendTemplateReport(sheet, template, audit);
            return;
        }

        var composite = new CompositeCommand($"Apply Template '{template.Name}'");
        composite.Add(new TemplateLinkCommand(_document.Meta, sheet.Name, templateId,
            $"Apply Template '{template.Name}'"));

        if (TemplateService.BuildApply(sheet, template, _document.Database) is { } apply)
            composite.Add(apply);

        _document.Commands.Execute(composite);
        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshAfterStructuralChange(sheet.Name, 0, 0);

        SendToast(
            audit.Missing.Count == 0 && audit.Reordered.Count == 0
                ? $"'{sheet.Name}' now follows the template '{template.Name}'."
                : $"'{sheet.Name}' now follows '{template.Name}' ({audit.Missing.Count} column(s) added).",
            "info");
    }

    /// <summary>
    /// Turns a table's own columns into the definition of the template it follows. Used
    /// when the table has columns the template does not declare and the author decides
    /// the template is the one that is out of date.
    /// </summary>
    private void ExtendTemplate(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var templateId = GetString(payload, "template");
        var stored = _document.Meta.GetTemplate(templateId);
        if (stored is null)
        {
            SendError("That template no longer exists.");
            return;
        }

        // The edited table becomes the reference definition...
        _document.Meta.RefreshTemplate(templateId, sheet);

        var template = new TemplateSpec { Id = templateId, Name = stored.Name, Columns = stored.ToSpecs() };

        var composite = new CompositeCommand($"Update Template '{template.Name}'");
        if (!string.Equals(_document.Meta.GetTableTemplate(sheet.Name), templateId, StringComparison.Ordinal))
            composite.Add(new TemplateLinkCommand(_document.Meta, sheet.Name, templateId,
                $"Update Template '{template.Name}'"));

        // ...and every other table is brought up to it: the columns it is missing are
        // added (empty) and the order is aligned. Nothing is deleted.
        var touched = 0;
        foreach (var member in TemplateService.Members(_document.Database, _document.Meta, templateId))
        {
            if (ReferenceEquals(member, sheet))
                continue;

            if (TemplateService.BuildApply(member, template, _document.Database) is { } apply)
            {
                composite.Add(apply);
                touched++;
            }
        }

        if (composite.Count > 0)
            _document.Commands.Execute(composite);

        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshAfterStructuralChange(sheet.Name, 0, 0);

        SendToast(
            touched == 0
                ? $"The template '{template.Name}' now matches '{sheet.Name}'."
                : $"The template '{template.Name}' now matches '{sheet.Name}'; {touched} other table(s) were updated.",
            "info");
    }

    private void DetachTemplate(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var link = new TemplateLinkCommand(_document.Meta, sheet.Name, null, "Stop Following the Template");
        if (link.IsNoOp)
            return;

        _document.Commands.Execute(link);
        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshKeepingCursor(sheet.Name);
        SendToast($"'{sheet.Name}' no longer follows a template. Its columns are unchanged.", "info");
    }

    // ---------------------------------------------------------------- template management

    private void SaveTemplate(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var sheet = _document.Database.GetSheet(GetString(payload, "sheet"));
        if (sheet is null)
            return;

        var name = GetString(payload, "name").Trim();
        if (name.Length == 0)
        {
            SendError("A template needs a name.");
            return;
        }

        if (sheet.Columns.Count == 0)
        {
            SendError("A table with no columns cannot be used as a template.");
            return;
        }

        var columns = TemplateSpec.Snapshot(sheet);
        var id = _document.Meta.CreateTemplate(name, columns);

        // The template itself is not part of the undo history (it may be used by other
        // tables); the link is, so undoing leaves this table as it was.
        _document.Commands.Execute(
            new TemplateLinkCommand(_document.Meta, sheet.Name, id, $"Save Template '{name}'"));

        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        RefreshKeepingCursor(sheet.Name);
        SendToast($"Saved the columns of '{sheet.Name}' as the template '{name}'.", "info");
    }

    private void RenameTemplate(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var id = GetString(payload, "template");
        var name = GetString(payload, "name").Trim();
        if (name.Length == 0)
            return;

        if (!_document.Meta.RenameTemplate(id, name))
            return;

        _document.SaveMeta();
        SendDocument();
        SendToast($"Renamed the template to '{name}'.", "info");
    }

    private void DeleteTemplate(JsonElement payload)
    {
        if (_document is null || _document.IsReadOnly)
            return;

        var id = GetString(payload, "template");
        var stored = _document.Meta.GetTemplate(id);
        if (stored is null)
            return;

        var members = TemplateService.Members(_document.Database, _document.Meta, id).Count;
        if (!_document.Meta.DeleteTemplate(id))
            return;

        _document.Meta.Sync(_document.Database);
        _document.SaveMeta();
        SendDocument();
        SendToast(
            members == 0
                ? $"Deleted the template '{stored.Name}'."
                : $"Deleted the template '{stored.Name}'. {members} table(s) keep their columns.",
            "info");
    }

    /// <summary>Sends the conflict report of an apply attempt so the dialog can explain it.</summary>
    private void SendTemplateReport(SheetData sheet, TemplateSpec template, TemplateAudit audit)
    {
        var conflicts = new JsonArray();
        foreach (var conflict in audit.Conflicts)
            conflicts.Add(new JsonObject
            {
                ["column"] = conflict.Column,
                ["kind"] = conflict.Kind.ToString(),
                ["detail"] = conflict.Detail,
            });

        Send(new JsonObject
        {
            ["type"] = "templateReport",
            ["payload"] = new JsonObject
            {
                ["sheet"] = sheet.Name,
                ["template"] = template.Id,
                ["templateName"] = template.Name,
                ["conflicts"] = conflicts,
                ["missing"] = new JsonArray(audit.Missing.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
                ["reordered"] = new JsonArray(audit.Reordered.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
                // Only "the table has columns the template does not know" can be solved by
                // extending the template; the other conflicts cannot.
                ["canExtend"] = audit.Conflicts.Count > 0 &&
                                audit.Conflicts.All(c => c.Kind == TemplateConflictKind.ExtraColumn),
            },
        });
    }
}
