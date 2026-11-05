# PalaceDB

PalaceDB is a Windows desktop editor for [CastleDB](https://github.com/ncannasse/castle)-style
`.cdb` data files — the JSON tables that games keep their items, levels and dialogue in.

PalaceDB 是一个 Windows 上的表格编辑器，用来读写游戏数据文件 `.cdb`（本质是带表结构的 JSON）。
它按 CastleDB 的格式读写，**不修改文件里没被编辑过的字节**，所以可以和 CastleDB、游戏引擎混用。

<img width="1316" height="790" alt="image" src="https://github.com/user-attachments/assets/fa2a2323-6d9a-45bc-aec6-6a85ab34250f" />

> **说明：本项目的代码由 AI 生成**，人负责需求、验收与发版。详见文末「[关于代码](#关于代码)」。

---

## 它做什么

- 打开一个 `.cdb`，左侧列出所有表，右侧是表格，单元格按类型给不同的编辑控件。
- 增删改行、列、表；拖动行号排序；行内可以插分隔线和命名分组。
- 列可以引用另一张表（下拉选择），可以是 List / Properties（单击单元格在行下方展开二级表格）。
- 查找 / 替换（`Ctrl+F` / `Ctrl+H`）：范围可选当前表或整个文件，支持正则，替换整批算一步撤销。
- 撤销 / 重做；多窗口打开同一个文件时第二个窗口只读。
- 5 分钟自动备份，崩溃或断电后可以恢复。
- 列宽、行底色、窗口位置、模板定义这些**展示状态**存在旁边的 `X.cdb.meta.json`，不写进数据文件。
  （分隔线本身和它的标题是**数据**，写在 `.cdb` 的 `props.separatorTitles` 里——游戏可能要用它分组。）

## 它不做什么

- 不是游戏引擎，不能预览数据在游戏里的效果。
- 不做 CastleDB 的 tilemap / 图层编辑（那部分类型已从本程序移除，打开含这类列的文件会明确报错）。
- 不自动保存。`Ctrl+S` 才写盘，这是故意的。

## 安装与运行

**只需要 Windows 10/11 (x64)**，另外装两个运行库：

1. [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（x64）
2. [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
   —— Windows 11 自带，Windows 10 一般也已经有

然后：

1. 从 [Releases](https://github.com/Huahuamama/PalaceDB/releases) 下载 `PalaceDB-1.0-win-x64.zip`，
   解压到任意目录（程序是绿色的，不写注册表、不需要安装）。
2. 双击 `PalaceDB.exe`。
3. 把 `.cdb` 文件拖进窗口，或点 **Open a database...**。
   手头没有 `.cdb` 就先打开仓库里的 [`samples/demo.cdb`](samples/demo.cdb) 看看。

注意：自动备份写在 **`.cdb` 旁边**（`X.cdb.autobackup`），所以数据文件所在的目录要可写；
把只读的样本放在 `Program Files` 里会备份失败（不影响编辑和保存）。

卸载就是删掉那个文件夹；程序不在注册表里留东西，配置在
`%LOCALAPPDATA%\PalaceDB\`（最近文件、界面字号、配色）。

## 五分钟上手

1. **打开文件** —— 拖入 `.cdb`，或 `Ctrl+O`。标题栏显示文件名，未保存时带 `*`。
2. **选表** —— 左侧列表点表名；`Ctrl+B` 收起侧栏。
3. **改单元格** —— 双击（或直接打字、`F2`、`Enter`）。`Enter` 提交，`Esc` 取消。
   - 文本列：长文本自动换行，`Shift+Enter` 换行。
   - 数字 / 布尔 / 颜色 / 图片 / 文件：各给各的控件。
   - 枚举 / Flags / 引用：单击弹出下拉框，引用可以打字筛选。
4. **改行** —— 工具栏的 `+` 按钮在表尾追加一行；行号右键可以「在这一行插入」；按住行号上下拖动可以排序。
5. **改列** —— 表头右键：
   - **Insert Column...** 插一列，**Edit Column...** 改名称 / 类型 / 字段 / 默认值 / 显示格式。
   - **Convert ▸** 整列批量改大小写、乘除 10、加减 1；改 Id 列会同步改写全库的引用。
   - **Delete Column** 删列（可以撤销，包括 ID 列）。
6. **List 列** —— 单击单元格，在这一行下面展开二级表格；`+ Row` 加记录，双击子单元格编辑，再点一次收起。
7. **分隔线** —— 行右键 **Add Separator**，会在这一行上方插一条独占一行高的分隔条；
   **Name Separator** 给它起名字。
8. **保存** —— `Ctrl+S`。第一次另存为用 `Ctrl+Shift+S`。
9. **撤销** —— `Ctrl+Z` / `Ctrl+Y`，任何结构改动（加列、删表、套模板）都是一步。

## 关键概念

### `.cdb` 和 `.cdb.meta.json`

- `X.cdb` 是**数据**：表、列定义、行。给游戏引擎读。
- `X.cdb.meta.json` 是**展示状态**：列宽、行底色、模板定义、窗口位置、配色。
  换台机器打开同一个 `.cdb` 也能用，因为它是 JSON 明文；删掉它不影响数据，只是这些"看得见的设置"回到默认。
- 两个文件都可以进版本管理；`.meta.json` 冲突时按行合并即可。
- 例外：分隔线的位置和标题属于**数据**（`props.separatorTitles` 写在 `.cdb` 里），
  因为游戏可能要按它分组。

### 模板（Table Settings）

一组列定义可以存成**模板**，让多张表共用。跟着同一个模板走的表，改一列（改名、改类型、加列）
会同步到其它表，整体算一步撤销。套用模板**只会补列和重排，不会删列**：
表里有模板不知道的列时会被拒绝，报告里会给出「把这些列加进模板」的按钮。

### 只读窗口

同一个文件开第二个窗口时是**只读**的（避免两个窗口互相覆盖）。要编辑请关掉其中一个。

### 备份与恢复

文档有未保存改动时，程序每 5 分钟把当前内容写一份滚动备份到 `X.cdb.autobackup`（只保留一份）。
下次打开这个文件、发现备份比主文件新时会问是否恢复。**恢复前会先校验备份能解析**，
坏备份不会覆盖你的文件。

## 支持的列类型

| 类型 | 说明 |
| --- | --- |
| Id | 主键，必须符合 `[A-Za-z_][A-Za-z0-9_]*` |
| Text | 文本，可多行 |
| Int / Float | 数字，可设百分比显示 |
| Bool | 勾选 |
| Enum | 固定取值，单元格存索引；**允许某一项没有名字**（`,1,2`） |
| Flags | 位集合，按名字勾选 |
| Reference | 指向另一张表的 Id，下拉可选，可设显示列和图标列 |
| Image / File | 存路径字符串；图片单元格显示缩略图 |
| Color | `#RRGGBB`，调色板选色 |
| List / Properties | 结构化子表，单击展开二级表格 |
| Guid | 13 位标识符，新行自动生成，编码与 CastleDB 的 `Guid.ofInt` 兼容 |
| Formula / Dynamic | 原样读写，没有专用编辑器 |

## 快捷键

| 快捷键 | 作用 |
| --- | --- |
| `Ctrl+O` / `Ctrl+S` / `Ctrl+Shift+S` | 打开 / 保存 / 另存为 |
| `Ctrl+N` / `Ctrl+Shift+N` | 新建数据库 / 新窗口 |
| `Ctrl+W` | 关闭文档 |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做 |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | 复制 / 剪切 / 粘贴单元格（粘贴默认覆盖，`Ctrl+Shift+V` 是插入） |
| `Ctrl+A` | 全选当前表 |
| `Ctrl+F` / `Ctrl+H` | 查找 / 查找并替换（当前表或全文件，支持正则） |
| `Ctrl+Enter` | 把当前编辑的值填满选区 |
| `Tab` / `Ctrl+B` | 收起 / 展开表列表 |
| `Ctrl+T` | 深色 / 浅色 |
| `Ctrl+,` | 设置 |
| 方向键 / `Tab` / `Enter` | 移动光标、进入编辑 |

对话框里的文字（比如错误信息、冲突报告）**可以选中复制**：选中后 `Ctrl+C` 复制的是那段文字，
不会再把表格里的单元格塞进剪贴板。

## 设置

`Ctrl+,` 或 View ▸ Settings：

- **界面字号** 12–20 px，**表格字号** 10–20 px，互不影响，行高跟着字号走。
- **九套配色**：Daylight、Midnight、Graphite、Deep Sea、Sandstone、Blush、Lilac、Olive、Mint。
  字号存在本机，配色跟着文件走（写在 `.meta.json` 里），这样两个文件一眼能分开。
- **Restore defaults** 一键回到 15 / 14 / Daylight。

## 已知限制

- 列的高级选项（必填、默认值、显示格式）只能在对话框里改，不能批量导入。
- Custom / Polymorph / Gradient / Curve 类型没有专用编辑器，按原始文本读写，不会丢数据。
- 图片缩略图在会话里第一次显示时会同步读盘，单张大图（≤4 MB）会有几十毫秒停顿。
- 窗口位置只在保存 / 关闭时写回；进程被强杀会丢最后一次移动。

## 更多文档

| 文档 | 内容 |
| --- | --- |
| `docs/03-用户手册-v1.md` | 完整用户手册（每个菜单、每种列类型、每个对话框） |
| `docs/04-功能对照表-v1.md` | 需求 → 功能对照 |
| `docs/05-数据与元数据格式规范-v1.md` | `.cdb` / `.meta.json` 的字段规范 |
| `docs/06-测试与验收报告-v1.md` | 测试与验收记录 |
| `docs/07-开发者指南-v1.md` | **给程序员的**：架构、代码导航、怎么改、怎么发版 |

## 参与开发

见 [`docs/07-开发者指南-v1.md`](docs/07-开发者指南-v1.md)。最短路径：

```bash
dotnet build
dotnet test tests/PalaceDB.Core.Tests/PalaceDB.Core.Tests.csproj   # 696 个单元测试
pip install websocket-client
python tests/ui/test_ui.py                                        # 端到端，511 项检查
```

端到端测试会自己起一个真实的窗口，通过 WebView2 的调试协议操作 DOM；跑的时候别去动那个窗口。
前半段用仓库里的 `TestData2.cdb`，后半段（把每种列类型都过一遍）需要一个 `tests/fixtures/food.cdb`
和它的 `food_img/`、`file/` 目录 —— 那是开发机上的真实数据，不进仓库。缺了它前半段照跑，
后半段会打印 `SKIP` 并以退出码 3 结束。

## 测试数据

`tests/fixtures/` 里的 `TestData1.cdb`、`TestData2.cdb` 跟着仓库走，clone 下来就能跑测试。
其余样本（`food.cdb` 和它的图片、文件目录）是开发机上用的真实数据，被 `.gitignore` 排除，
想跑发布冒烟（`tools/release_smoke.py`）就自己放一个同名文件进去。

`samples/demo.cdb` 是给用户看的示例库：两张表（`items` / `levels`）、九种列类型、
三个小图标，随时可以拿来练手。

## 关于代码

**这个项目的代码是 AI 生成的**：C# 后端、WebView2 前端、单元测试、端到端测试和文档，
都由 AI 编程助手写成，人负责提需求、验收和发版。

这意味着两件事，用之前请先知道：

- **以测试为准，不以"看起来对"为准。** 696 个单元测试 + 511 项端到端检查是功能边界；
  测试没覆盖到的地方，行为不保证。
- **风格不保证统一。** 有些地方会绕、会重复、会有历史包袱（比如同一段列类型判断在前后端各写了一遍）。
  改之前先看 `docs/07-开发者指南-v1.md`，它记录的是"为什么这样做"，比读代码快。

发现 bug 或者写得不好的地方，欢迎直接提 issue / PR —— 人来 review，AI 来改。

## 许可

[CC0 1.0](LICENSE) —— 公共领域奉献，随便用、随便改、不用署名。
