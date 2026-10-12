#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ModPlus.Headless;

public static class NativeContracts
{
    private static readonly JsonElement Root = Parse();
    private static JsonElement Parse()
    {
        using JsonDocument document = JsonDocument.Parse(Data);
        return document.RootElement.Clone();
    }
    public static IEnumerable<JsonElement> ForModule(string module) => Root.GetProperty("operations").EnumerateArray().Where(o => o.GetProperty("module").GetString() == module);
    internal static void Validate(string module, string operation, JsonElement args)
    {
        JsonElement entry = ForModule(module).FirstOrDefault(o => o.GetProperty("name").GetString() == operation);
        if (entry.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("Unknown native operation.");
        Check(entry.GetProperty("inputSchema"), args, "args", 0);
    }
    private static void Check(JsonElement schema, JsonElement value, string path, int depth)
    {
        if (depth > 20) throw new ArgumentException("Native input exceeds the depth limit.");
        if (schema.TryGetProperty("oneOf", out JsonElement choices))
        {
            int accepted = 0;
            foreach (JsonElement choice in choices.EnumerateArray())
            { try { Check(choice, value, path, depth + 1); accepted++; } catch (ArgumentException) { } }
            if (accepted != 1) throw new ArgumentException(path + ": value must match exactly one allowed input form.");
            return;
        }
        string type = schema.GetProperty("type").GetString();
        bool correct = type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double n) && double.IsFinite(n),
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false
        };
        if (!correct) throw new ArgumentException(path + ": expected " + type + ".");
        if (schema.TryGetProperty("enum", out JsonElement allowed) && !allowed.EnumerateArray().Any(a => a.GetRawText() == value.GetRawText())) throw new ArgumentException(path + ": unknown enum value.");
        if (type == "object")
        {
            JsonElement properties = schema.GetProperty("properties");
            if (schema.TryGetProperty("required", out JsonElement required)) foreach (JsonElement name in required.EnumerateArray()) if (!value.TryGetProperty(name.GetString(), out _)) throw new ArgumentException(path + ": missing " + name.GetString());
            int count = 0;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                count++;
                if (!properties.TryGetProperty(property.Name, out JsonElement child)) throw new ArgumentException(path + ": unknown argument " + property.Name);
                Check(child, property.Value, path + "." + property.Name, depth + 1);
            }
            Bounds(schema, count, "minProperties", "maxProperties", path);
        }
        if (type == "array")
        {
            Bounds(schema, value.GetArrayLength(), "minItems", "maxItems", path);
            int index = 0; foreach (JsonElement item in value.EnumerateArray()) Check(schema.GetProperty("items"), item, path + "[" + index++ + "]", depth + 1);
        }
        if (type is "integer" or "number") Bounds(schema, value.GetDouble(), "minimum", "maximum", path);
    }
    private static void Bounds(JsonElement schema, double count, string min, string max, string path)
    {
        if (schema.TryGetProperty(min, out JsonElement lower) && count < lower.GetDouble() || schema.TryGetProperty(max, out JsonElement upper) && count > upper.GetDouble()) throw new ArgumentException(path + ": value is outside allowed bounds.");
    }
    private const string Data = """
{
  "version": 1,
  "targetRevit": 2027,
  "semantics": "Granular automatic API operations; no complete GUI workflow equivalence is claimed.",
  "operations": [
    {
      "module": "mprPhases",
      "name": "list_phases",
      "description": "List document phases.",
      "readOnly": true,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {},
        "required": [],
        "additionalProperties": false
      }
    },
    {
      "module": "mprPhases",
      "name": "assign_phases",
      "description": "Set created/demolished phases by explicit IDs.",
      "readOnly": false,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "created_phase_id": {
            "type": "integer"
          },
          "demolished_phase_id": {
            "oneOf": [
              {
                "type": "integer"
              },
              {
                "type": "null"
              }
            ]
          }
        },
        "required": [
          "element_ids"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprNotes",
      "name": "create_text_note",
      "description": "Create one text note.",
      "readOnly": false,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {
          "view_id": {
            "type": "integer"
          },
          "text_type_id": {
            "type": "integer"
          },
          "point_mm": {
            "type": "array",
            "minItems": 3,
            "maxItems": 3,
            "items": {
              "type": "number"
            }
          },
          "text": {
            "type": "string"
          }
        },
        "required": [
          "view_id",
          "text_type_id",
          "point_mm",
          "text"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprScheduleLookup",
      "name": "read_schedule_cells",
      "description": "Read displayed body cells from a schedule.",
      "readOnly": true,
      "limitations": "Returns cell text, not a mapping from schedule rows to model element IDs.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "schedule_id": {
            "type": "integer"
          },
          "start_row": {
            "type": "integer"
          },
          "row_count": {
            "type": "integer",
            "minimum": 1,
            "maximum": 1000
          }
        },
        "required": [
          "schedule_id"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprSheetRenumber",
      "name": "renumber_sheets",
      "description": "Assign unique sheet numbers, including swaps.",
      "readOnly": false,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {
          "sheets": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "object",
              "properties": {
                "sheet_id": {
                  "type": "integer"
                },
                "number": {
                  "type": "string"
                }
              },
              "required": [
                "sheet_id",
                "number"
              ],
              "additionalProperties": false
            }
          }
        },
        "required": [
          "sheets"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprRebarSolidAndVisible",
      "name": "set_rebar_unobscured",
      "description": "Set rebar unobscured state in a view.",
      "readOnly": false,
      "limitations": "Only Rebar/RebarInSystem IDs; no host expansion or deprecated solid-display setting.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "view_id": {
            "type": "integer"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "unobscured": {
            "type": "boolean"
          }
        },
        "required": [
          "view_id",
          "element_ids",
          "unobscured"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprCopyCropBox",
      "name": "copy_crop_box",
      "description": "Copy rectangular crop-box properties.",
      "readOnly": false,
      "limitations": "Does not copy a custom crop shape, scope-box parameters or annotation crop.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "source_view_id": {
            "type": "integer"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          }
        },
        "required": [
          "source_view_id",
          "element_ids"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprCopyViewTemplateFilters",
      "name": "copy_view_filters",
      "description": "Copy filter overrides, visibility and enabled state.",
      "readOnly": false,
      "limitations": "replace=true recreates the ordered filter list. Otherwise existing filters/order are preserved and missing filters appended.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "source_view_id": {
            "type": "integer"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "replace": {
            "type": "boolean"
          }
        },
        "required": [
          "source_view_id",
          "element_ids"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprFlipped",
      "name": "inspect_flipped",
      "description": "Inspect flipped/mirrored flags.",
      "readOnly": true,
      "limitations": "FamilyInstance and Wall flags only; unsupported elements are marked.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          }
        },
        "required": [
          "element_ids"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprViewRange",
      "name": "set_view_range",
      "description": "Assign plan view range planes.",
      "readOnly": false,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {
          "view_id": {
            "type": "integer"
          },
          "planes": {
            "type": "object",
            "minProperties": 1,
            "properties": {
              "TopClipPlane": {
                "type": "object",
                "properties": {
                  "level_id": {
                    "oneOf": [
                      {
                        "type": "integer"
                      },
                      {
                        "type": "string",
                        "enum": [
                          "current",
                          "unlimited"
                        ]
                      }
                    ]
                  },
                  "offset_mm": {
                    "type": "number"
                  }
                },
                "required": [
                  "level_id",
                  "offset_mm"
                ],
                "additionalProperties": false
              },
              "CutPlane": {
                "type": "object",
                "properties": {
                  "level_id": {
                    "oneOf": [
                      {
                        "type": "integer"
                      },
                      {
                        "type": "string",
                        "enum": [
                          "current",
                          "unlimited"
                        ]
                      }
                    ]
                  },
                  "offset_mm": {
                    "type": "number"
                  }
                },
                "required": [
                  "level_id",
                  "offset_mm"
                ],
                "additionalProperties": false
              },
              "BottomClipPlane": {
                "type": "object",
                "properties": {
                  "level_id": {
                    "oneOf": [
                      {
                        "type": "integer"
                      },
                      {
                        "type": "string",
                        "enum": [
                          "current",
                          "unlimited"
                        ]
                      }
                    ]
                  },
                  "offset_mm": {
                    "type": "number"
                  }
                },
                "required": [
                  "level_id",
                  "offset_mm"
                ],
                "additionalProperties": false
              },
              "ViewDepthPlane": {
                "type": "object",
                "properties": {
                  "level_id": {
                    "oneOf": [
                      {
                        "type": "integer"
                      },
                      {
                        "type": "string",
                        "enum": [
                          "current",
                          "unlimited"
                        ]
                      }
                    ]
                  },
                  "offset_mm": {
                    "type": "number"
                  }
                },
                "required": [
                  "level_id",
                  "offset_mm"
                ],
                "additionalProperties": false
              }
            },
            "additionalProperties": false
          }
        },
        "required": [
          "view_id",
          "planes"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprOverrideGraphicManager",
      "name": "clear_element_overrides",
      "description": "Clear all per-element graphic overrides in a view.",
      "readOnly": false,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {
          "view_id": {
            "type": "integer"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          }
        },
        "required": [
          "view_id",
          "element_ids"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprAlignViews",
      "name": "align_viewport_centers",
      "description": "Align viewport centers on a sheet.",
      "readOnly": false,
      "limitations": "Uses box centers, not view origins or label outlines.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "reference_viewport_id": {
            "type": "integer"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "axis": {
            "type": "string",
            "enum": [
              "x",
              "y",
              "xy"
            ]
          }
        },
        "required": [
          "reference_viewport_id",
          "element_ids",
          "axis"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprFontReplacer",
      "name": "set_text_type_font",
      "description": "Set TEXT_FONT on explicit TextNoteType IDs.",
      "readOnly": false,
      "limitations": "Does not traverse family documents, dimensions or labels.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "font": {
            "type": "string"
          }
        },
        "required": [
          "element_ids",
          "font"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprToposolidFloorConverter",
      "name": "convert_floors_toposolids",
      "description": "Convert Floor to Toposolid and Toposolid to Floor.",
      "readOnly": false,
      "limitations": "Both destination type IDs are required. Default delete_source=false, allow_partial=false. Complex geometry needs validation in Revit.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "floor_type_id": {
            "type": "integer"
          },
          "toposolid_type_id": {
            "type": "integer"
          },
          "delete_source": {
            "type": "boolean"
          },
          "allow_partial": {
            "type": "boolean"
          }
        },
        "required": [
          "element_ids",
          "floor_type_id",
          "toposolid_type_id"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprParameters",
      "name": "set_parameters",
      "description": "Set explicitly selected parameters on elements.",
      "readOnly": false,
      "limitations": "Parameter values use actual storage types. Numeric double values without a unit object mean Revit internal units. No parameter creation/removal.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "values": {
            "type": "array",
            "minItems": 1,
            "maxItems": 100,
            "items": {
              "type": "object",
              "properties": {
                "parameter": {
                  "type": "object",
                  "minProperties": 1,
                  "maxProperties": 1,
                  "properties": {
                    "built_in": {
                      "type": "string"
                    },
                    "guid": {
                      "type": "string"
                    },
                    "id": {
                      "type": "integer"
                    },
                    "name": {
                      "type": "string"
                    }
                  },
                  "additionalProperties": false
                },
                "value": {
                  "oneOf": [
                    {
                      "type": "string"
                    },
                    {
                      "type": "number"
                    },
                    {
                      "type": "object",
                      "properties": {
                        "value": {
                          "type": "number"
                        },
                        "unit": {
                          "type": "string",
                          "enum": [
                            "internal",
                            "mm",
                            "m",
                            "ft",
                            "deg",
                            "rad"
                          ]
                        }
                      },
                      "required": [
                        "value",
                        "unit"
                      ],
                      "additionalProperties": false
                    }
                  ]
                }
              },
              "required": [
                "parameter",
                "value"
              ],
              "additionalProperties": false
            }
          }
        },
        "required": [
          "element_ids",
          "values"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprNumbering",
      "name": "number_elements",
      "description": "Number elements in the supplied order.",
      "readOnly": false,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "parameter": {
            "type": "object",
            "minProperties": 1,
            "maxProperties": 1,
            "properties": {
              "built_in": {
                "type": "string"
              },
              "guid": {
                "type": "string"
              },
              "id": {
                "type": "integer"
              },
              "name": {
                "type": "string"
              }
            },
            "additionalProperties": false
          },
          "start": {
            "type": "integer"
          },
          "step": {
            "type": "integer"
          },
          "prefix": {
            "type": "string"
          },
          "suffix": {
            "type": "string"
          },
          "width": {
            "type": "integer",
            "minimum": 0,
            "maximum": 32
          }
        },
        "required": [
          "element_ids",
          "parameter",
          "start"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprFindAndReplace",
      "name": "replace_parameter_text",
      "description": "Replace literal text in one string parameter.",
      "readOnly": false,
      "limitations": "Literal replacement; no regex or whole-word/whole-model search.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "parameter": {
            "type": "object",
            "minProperties": 1,
            "maxProperties": 1,
            "properties": {
              "built_in": {
                "type": "string"
              },
              "guid": {
                "type": "string"
              },
              "id": {
                "type": "integer"
              },
              "name": {
                "type": "string"
              }
            },
            "additionalProperties": false
          },
          "find": {
            "type": "string"
          },
          "replace": {
            "type": "string"
          },
          "ignore_case": {
            "type": "boolean"
          }
        },
        "required": [
          "element_ids",
          "parameter",
          "find",
          "replace"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprVisibility",
      "name": "set_element_visibility",
      "description": "Hide/unhide explicit elements in a view.",
      "readOnly": false,
      "limitations": "Permanent element visibility only; category/filter/temporary visibility are separate.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "view_id": {
            "type": "integer"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "hidden": {
            "type": "boolean"
          }
        },
        "required": [
          "view_id",
          "element_ids",
          "hidden"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprColorizer",
      "name": "set_element_color",
      "description": "Set projection/cut line colors.",
      "readOnly": false,
      "limitations": "Existing other overrides are preserved. No surface patterns or generated color legends.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "view_id": {
            "type": "integer"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "color": {
            "type": "string"
          }
        },
        "required": [
          "view_id",
          "element_ids",
          "color"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprCleaner",
      "name": "delete_elements",
      "description": "Delete explicit elements and Revit-dependent elements.",
      "readOnly": false,
      "limitations": "No automatic unused-item discovery. Preview reports temporary deletion results and rolls back.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          }
        },
        "required": [
          "element_ids"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprJoin",
      "name": "join_geometry",
      "description": "Apply geometry join operation to explicit element pairs.",
      "readOnly": false,
      "limitations": "Does not discover intersections or join MEP connectors.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "pairs": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "array",
              "minItems": 2,
              "maxItems": 2,
              "items": {
                "type": "integer"
              }
            }
          }
        },
        "required": [
          "pairs"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprJoin",
      "name": "unjoin_geometry",
      "description": "Apply geometry join operation to explicit element pairs.",
      "readOnly": false,
      "limitations": "Does not discover intersections or join MEP connectors.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "pairs": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "array",
              "minItems": 2,
              "maxItems": 2,
              "items": {
                "type": "integer"
              }
            }
          }
        },
        "required": [
          "pairs"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprJoin",
      "name": "switch_join_order",
      "description": "Apply geometry join operation to explicit element pairs.",
      "readOnly": false,
      "limitations": "Does not discover intersections or join MEP connectors.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "pairs": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "array",
              "minItems": 2,
              "maxItems": 2,
              "items": {
                "type": "integer"
              }
            }
          }
        },
        "required": [
          "pairs"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprViewCopy",
      "name": "duplicate_views",
      "description": "Duplicate explicit views using Revit duplication options.",
      "readOnly": false,
      "limitations": "No sheet duplication or automatic template/name propagation.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "option": {
            "type": "string",
            "enum": [
              "Duplicate",
              "WithDetailing",
              "AsDependent"
            ]
          }
        },
        "required": [
          "element_ids",
          "option"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprTypeManager",
      "name": "change_element_type",
      "description": "Change type for compatible explicit elements.",
      "readOnly": false,
      "limitations": "Does not rename/delete/merge type definitions.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "type_id": {
            "type": "integer"
          }
        },
        "required": [
          "element_ids",
          "type_id"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprWorksets",
      "name": "list_worksets",
      "description": "List user worksets.",
      "readOnly": true,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {},
        "required": [],
        "additionalProperties": false
      }
    },
    {
      "module": "mprWorksets",
      "name": "assign_workset",
      "description": "Set user workset on explicit elements.",
      "readOnly": false,
      "limitations": "Requires an existing workshared document and user workset; no worksharing enable/create/sync.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "workset_id": {
            "type": "integer"
          }
        },
        "required": [
          "element_ids",
          "workset_id"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprWarningViewer",
      "name": "list_warnings",
      "description": "Return existing document warnings and element IDs.",
      "readOnly": true,
      "limitations": "",
      "inputSchema": {
        "type": "object",
        "properties": {},
        "required": [],
        "additionalProperties": false
      }
    },
    {
      "module": "mprCopyAlongCurve",
      "name": "divide_curve",
      "description": "Return equally spaced positions and tangents.",
      "readOnly": true,
      "limitations": "Choose exactly one contiguous path input. Coordinates returned in feet. Closed-path duplicate endpoint omitted.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "curve_element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 1000,
            "items": {
              "type": "integer"
            }
          },
          "polyline_mm": {
            "type": "array",
            "minItems": 2,
            "maxItems": 1001,
            "items": {
              "type": "array",
              "minItems": 3,
              "maxItems": 3,
              "items": {
                "type": "number"
              }
            }
          },
          "spacing_mm": {
            "type": "number"
          },
          "include_end": {
            "type": "boolean"
          },
          "projection_parameter": {
            "type": "number"
          }
        },
        "required": [
          "spacing_mm"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprCopyAlongCurve",
      "name": "copy_along_curve",
      "description": "Copy explicit elements at path positions.",
      "readOnly": false,
      "limitations": "Sources represent the first path position; initial offset is preserved. Groups remain groups. Choose exactly one path input.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "curve_element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 1000,
            "items": {
              "type": "integer"
            }
          },
          "polyline_mm": {
            "type": "array",
            "minItems": 2,
            "maxItems": 1001,
            "items": {
              "type": "array",
              "minItems": 3,
              "maxItems": 3,
              "items": {
                "type": "number"
              }
            }
          },
          "spacing_mm": {
            "type": "number"
          },
          "include_end": {
            "type": "boolean"
          },
          "projection_parameter": {
            "type": "number"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "rotate": {
            "type": "boolean"
          }
        },
        "required": [
          "spacing_mm",
          "element_ids"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprCopyParameterValues",
      "name": "copy_parameter_values",
      "description": "Copy selected values from one element to explicit targets.",
      "readOnly": false,
      "limitations": "Active document only. Source/target parameter storage and data types must match. Does not create parameters or pair source/target geometry automatically.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "source_element_id": {
            "type": "integer"
          },
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "parameters": {
            "type": "array",
            "minItems": 1,
            "maxItems": 100,
            "items": {
              "type": "object",
              "properties": {
                "from": {
                  "type": "object",
                  "minProperties": 1,
                  "maxProperties": 1,
                  "properties": {
                    "built_in": {
                      "type": "string"
                    },
                    "guid": {
                      "type": "string"
                    },
                    "id": {
                      "type": "integer"
                    },
                    "name": {
                      "type": "string"
                    }
                  },
                  "additionalProperties": false
                },
                "to": {
                  "type": "object",
                  "minProperties": 1,
                  "maxProperties": 1,
                  "properties": {
                    "built_in": {
                      "type": "string"
                    },
                    "guid": {
                      "type": "string"
                    },
                    "id": {
                      "type": "integer"
                    },
                    "name": {
                      "type": "string"
                    }
                  },
                  "additionalProperties": false
                }
              },
              "required": [
                "from",
                "to"
              ],
              "additionalProperties": false
            }
          }
        },
        "required": [
          "source_element_id",
          "element_ids",
          "parameters"
        ],
        "additionalProperties": false
      }
    },
    {
      "module": "mprFloorByRoom",
      "name": "create_floors_by_rooms",
      "description": "Create one new floor from each enclosed room boundary.",
      "readOnly": false,
      "limitations": "Only Room IDs in the active document. Uses the room level plus explicit offset_mm (default 0). Existing floors are not discovered/replaced. No linked rooms or sloped-floor inference.",
      "inputSchema": {
        "type": "object",
        "properties": {
          "element_ids": {
            "type": "array",
            "minItems": 1,
            "maxItems": 10000,
            "items": {
              "type": "integer"
            }
          },
          "floor_type_id": {
            "type": "integer"
          },
          "offset_mm": {
            "type": "number"
          },
          "boundary": {
            "type": "string",
            "enum": [
              "Finish",
              "Center",
              "CoreBoundary",
              "CoreCenter"
            ]
          },
          "structural": {
            "type": "boolean"
          }
        },
        "required": [
          "element_ids",
          "floor_type_id"
        ],
        "additionalProperties": false
      }
    }
  ]
}
""";
}
