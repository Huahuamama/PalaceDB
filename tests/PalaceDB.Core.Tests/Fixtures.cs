using PalaceDB.Core.Json;
using PalaceDB.Core.Model;

namespace PalaceDB.Core.Tests;

/// <summary>
/// Small in-memory database used by the model, formatting and metadata tests.
/// It deliberately contains a duplicate id, an optional reference, every scalar
/// type, a flags column and a custom type.
/// </summary>
internal static class Fixtures
{
    public const string SampleJson = """
{
	"sheets": [
		{
			"name": "Items",
			"columns": [
				{
					"typeStr": "0",
					"name": "id"
				},
				{
					"typeStr": "1",
					"name": "name"
				},
				{
					"typeStr": "3",
					"name": "cost"
				},
				{
					"typeStr": "4",
					"name": "weight",
					"display": 1
				},
				{
					"typeStr": "5:Common,Rare,Epic",
					"name": "rarity"
				},
				{
					"typeStr": "10:A,B,C",
					"name": "tags"
				},
				{
					"typeStr": "11",
					"name": "color"
				},
				{
					"typeStr": "6:Items",
					"name": "parent",
					"opt": true
				},
				{
					"typeStr": "2",
					"name": "enabled"
				},
				{
					"typeStr": "1",
					"name": "note",
					"opt": true
				}
			],
			"lines": [
				{
					"id": "sword",
					"name": "Sword",
					"cost": 10,
					"weight": 0.5,
					"rarity": 1,
					"tags": 5,
					"color": 16711680,
					"enabled": true
				},
				{
					"id": "shield",
					"name": "Shield",
					"cost": 5,
					"weight": 2.25,
					"rarity": 0,
					"tags": 0,
					"color": 255,
					"parent": "sword",
					"enabled": false
				},
				{
					"id": "sword",
					"name": "Sword duplicate",
					"cost": 1,
					"weight": 1,
					"rarity": 2,
					"tags": 2,
					"color": 0,
					"parent": "ghost",
					"enabled": true
				}
			],
			"separators": [],
			"props": {
				"displayColumn": "name"
			}
		},
		{
			"name": "Empty",
			"columns": [
				{
					"typeStr": "0",
					"name": "id"
				}
			],
			"lines": [],
			"props": {}
		}
	],
	"customTypes": [
		{
			"name": "Vector2",
			"cases": [
				{
					"name": "Zero"
				},
				{
					"name": "XY"
				}
			]
		}
	],
	"compress": false
}
""";

    public static Database Sample() => CdbReader.ParseDatabase(SampleJson);

    public static SheetData Items(Database database) => database.GetSheet("Items")!;

    public static Column Column(Database database, string sheet, string column) =>
        database.GetSheet(sheet)!.GetColumn(column)!;
}
