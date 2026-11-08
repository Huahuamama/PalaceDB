[中文说明](#中文说明)
# PalaceDB

PalaceDB is a Windows desktop editor for CastleDB .cdb game data files. It provides a spreadsheet-style interface for editing structured game data.

> PalaceDB's source code, tests, and documentation were generated with AI coding tools.

<img width="1316" height="790" alt="PalaceDB screenshot" src="https://github.com/user-attachments/assets/fa2a2323-6d9a-45bc-aec6-6a85ab34250f" />

## Features

- Spreadsheet-style editing of `.cdb` files.
- Create, rename, and delete tables, columns, and rows.
- Drag-and-drop row reordering.
- Type-specific cell editors.
- Nested tables with `List` and `Properties` columns.
- Row separators and named groups.
- Table templates for synchronizing column definitions.
- Undo and redo.
- Automatic backups and recovery.
- Read-only access when the same database is open in another window.

## Installation

**Requirements:** Windows 10/11 (x64), [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0), and [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

1. Download `PalaceDB-1.0-win-x64.zip` from [Releases](https://github.com/Huahuamama/PalaceDB/releases).
2. Extract the archive.
3. Run `PalaceDB.exe`.

Open a `.cdb` file by dragging it into the window or selecting **Open a database...**.

Sample database: [`samples/demo.cdb`](samples/demo.cdb).

## Usage

- Select a table from the sidebar.
- Double-click a cell or press `F2` to edit it.
- Use the toolbar to add rows.
- Right-click row numbers to insert rows or add separators.
- Right-click column headers to insert, edit, convert, or delete columns.
- Click a `List` or `Properties` cell to expand its nested table.
- Use `Ctrl+Z` and `Ctrl+Y` to undo and redo.
- Use `Ctrl+S` to save.

## File Format

PalaceDB uses two files:

| File | Contents |
| --- | --- |
| `.cdb` | Table definitions, column definitions, row data, and separator metadata. |
| `.cdb.meta.json` | Column widths, row background colors, table templates, window position, and theme. |

The `.cdb` format is compatible with CastleDB. Untouched file content is preserved when saving.

## Column Types

| Type | Description |
| --- | --- |
| `Id` | Primary key. Uniquely identifies each row. Must start with a letter or underscore and contain only letters, numbers, and underscores. |
| `Text` | Text value. Supports multiple lines. |
| `Int` | Integer value. |
| `Float` | Floating-point value. Supports percentage display. |
| `Bool` | Boolean value, edited with a checkbox. |
| `Enum` | Fixed set of options, stored as an option index and edited through a dropdown. |
| `Flags` | Bit flags, edited by selecting named options. |
| `Reference` | Reference to an ID in another table. Supports configurable display and icon columns. |
| `Image` | Image file path, displayed as a thumbnail. |
| `File` | File path stored as a string. |
| `Color` | Color stored in `#RRGGBB` format, with a color picker. |
| `List` | List of nested records, displayed in an expandable subtable. |
| `Properties` | Structured nested properties, displayed in an expandable subtable. |
| `Guid` | 13-character identifier generated automatically for new rows. Compatible with CastleDB's `Guid.ofInt` encoding. |
| `Formula` | Formula data preserved without a dedicated editor. |
| `Dynamic` | Dynamic data preserved without a dedicated editor. |

## Development

See the [Developer Guide](docs/07-开发者指南-v1.md).

Build:

```bash
dotnet build
```

Run unit tests:

```bash
dotnet test tests/PalaceDB.Core.Tests/PalaceDB.Core.Tests.csproj
```

Run UI tests:

```bash
pip install websocket-client
python tests/ui/test_ui.py
```

UI tests launch the application and interact with its interface through the WebView2 debugging protocol.

# 中文说明

PalaceDB 是一个 Windows 桌面端编辑器，用于编辑 [CastleDB](https://github.com/ncannasse/castle) 格式的 `.cdb` 游戏数据文件，提供类似电子表格的编辑界面。

## 功能

- 以表格形式编辑 `.cdb` 数据。
- 创建、重命名和删除数据表、列和行。
- 拖动行号调整行顺序。
- 根据列类型提供对应的单元格编辑器。
- 支持 `List` 和 `Properties` 嵌套表。
- 支持行分隔线和命名分组。
- 支持表模板，可同步多个数据表的列结构。
- 支持撤销和重做。
- 自动备份与数据恢复。
- 同一数据库在多个窗口打开时，后续窗口以只读模式运行。

## 安装

**运行环境：** Windows 10/11 (x64)、[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)、[WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

1. 从 [Releases](https://github.com/Huahuamama/PalaceDB/releases) 下载 `PalaceDB-1.0-win-x64.zip`。
2. 解压到任意目录。
3. 运行 `PalaceDB.exe`。

将 `.cdb` 文件拖入窗口，或点击 **Open a database...** 打开数据库。

示例文件：[`samples/demo.cdb`](samples/demo.cdb)。

## 基本操作

- 在左侧侧栏选择数据表。
- 双击单元格或按 `F2` 编辑。
- 使用工具栏添加行。
- 右键行号可插入行或添加分隔线。
- 右键列标题可插入、编辑、转换或删除列。
- 单击 `List` 或 `Properties` 单元格展开嵌套表。
- 使用 `Ctrl+Z` 和 `Ctrl+Y` 撤销和重做。
- 使用 `Ctrl+S` 保存。

## 文件格式

PalaceDB 使用两个文件：

| 文件 | 内容 |
| --- | --- |
| `.cdb` | 数据表定义、列定义、行数据及分隔线信息。 |
| `.cdb.meta.json` | 列宽、行底色、表模板、窗口位置和配色。 |

`.cdb` 与 CastleDB 格式兼容。保存时会保留未修改的文件内容。

## 列类型

| 类型 | 说明 |
| --- | --- |
| `Id` | 主键列，用于唯一标识数据表中的每一行，格式为字母或下划线开头的字母、数字和下划线组合，或**纯数字**（`0`、`123`、`007`）。 |
| `Text` | 文本列，支持多行文本。 |
| `Int` | 整数列。 |
| `Float` | 浮点数列，支持百分比显示。 |
| `Bool` | 布尔值列，以复选框形式编辑。 |
| `Enum` | 枚举列，值以选项索引存储，通过下拉列表选择。 |
| `Flags` | 位标记列，可通过勾选选项组合多个标记。 |
| `Reference` | 引用列，存储目标数据表中的 ID，并通过下拉列表选择引用对象。支持指定显示列和图标列。 |
| `Image` | 图片路径列，存储图片文件路径，并在单元格中显示缩略图。 |
| `File` | 文件路径列，存储文件路径字符串。 |
| `Color` | 颜色列，以 `#RRGGBB` 格式存储，支持调色板选色。 |
| `List` | 列表列，用于存储一组嵌套记录，可展开为二级表格编辑。 |
| `Properties` | 属性列，用于存储结构化的嵌套属性，可展开为二级表格编辑。 |
| `Guid` | 标识符列，自动生成 13 位标识符，编码方式兼容 CastleDB 的 `Guid.ofInt`。 |
| `Formula` | 公式列，保留原始数据，不提供专用编辑器。 |
| `Dynamic` | 动态类型列，保留原始数据，不提供专用编辑器。 |

## 开发

开发文档：[Developer Guide](docs/07-开发者指南-v1.md)。

编译：

```bash
dotnet build
```

运行单元测试：

```bash
dotnet test tests/PalaceDB.Core.Tests/PalaceDB.Core.Tests.csproj
```

运行 UI 自动化测试：

```bash
pip install websocket-client
python tests/ui/test_ui.py
```

UI 测试会启动实际程序窗口，并通过 WebView2 调试协议操作界面。
