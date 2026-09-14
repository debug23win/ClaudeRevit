using System.Text.Json.Nodes;
using ClaudeRevit.Services;
using Xunit;

namespace ClaudeRevit.Tests;

public class McpSchemaTests
{
    [Theory]
    [InlineData("claude-code", false)]
    [InlineData("Claude Desktop", false)]
    [InlineData("anthropic-mcp-client", false)]
    [InlineData("", false)]              // pre-initialize probe: assume the native client
    [InlineData("openai-agents", true)]
    [InlineData("some-other-client", true)]
    public void PortableSchemasOnlyForNonClaudeClients(string client, bool expected)
        => Assert.Equal(expected, McpSchema.NeedsPortableSchemas(client));

    [Fact]
    public void NullClientNameIsTreatedAsNative()
        => Assert.False(McpSchema.NeedsPortableSchemas(null));

    [Fact]
    public void DropsUnsupportedConstraintsButKeepsThemInTheDescription()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "limit": { "type": "integer", "description": "Max rows.", "minimum": 1, "maximum": 500 }
          },
          "required": []
        }
        """)!.AsObject();

        McpSchema.MakePortable(schema);
        var limit = schema["properties"]!["limit"]!.AsObject();

        Assert.Null(limit["minimum"]);
        Assert.Null(limit["maximum"]);
        // The bound survives as prose so the model still knows the range.
        var desc = limit["description"]!.GetValue<string>();
        Assert.Contains("min 1", desc);
        Assert.Contains("max 500", desc);
        Assert.StartsWith("Max rows.", desc);
    }

    [Fact]
    public void ConstraintNoteIsCreatedWhenThereIsNoDescription()
    {
        var schema = JsonNode.Parse("""
        { "type": "object", "properties": { "ids": { "type": "array", "minItems": 1 } } }
        """)!.AsObject();

        McpSchema.MakePortable(schema);
        Assert.Equal("(min items 1)", schema["properties"]!["ids"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void OneOfBecomesAnyOfAndBranchesAreSanitized()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "value": { "oneOf": [ { "type": "string", "minLength": 2 }, { "type": "number" } ] }
          }
        }
        """)!.AsObject();

        McpSchema.MakePortable(schema);
        var value = schema["properties"]!["value"]!.AsObject();

        Assert.Null(value["oneOf"]);
        var anyOf = value["anyOf"]!.AsArray();
        Assert.Equal(2, anyOf.Count);
        Assert.Null(anyOf[0]!["minLength"]);                       // branches recursed into
        Assert.Contains("min length 2", anyOf[0]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void ObjectsForbidAdditionalPropertiesAtEveryLevel()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "nested": { "type": "object", "properties": { "a": { "type": "string" } } }
          }
        }
        """)!.AsObject();

        McpSchema.MakePortable(schema);

        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.False(schema["properties"]!["nested"]!["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void RecursesIntoArrayItems()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "rows": { "type": "array", "items": { "type": "object",
                       "properties": { "n": { "type": "integer", "minimum": 0 } } } }
          }
        }
        """)!.AsObject();

        McpSchema.MakePortable(schema);
        var n = schema["properties"]!["rows"]!["items"]!["properties"]!["n"]!.AsObject();

        Assert.Null(n["minimum"]);
        Assert.Contains("min 0", n["description"]!.GetValue<string>());
    }

    [Fact]
    public void DropsDefaultAndLeavesSupportedKeywordsAlone()
    {
        var schema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "mode": { "type": "string", "enum": ["a","b"], "default": "a", "description": "Mode." }
          },
          "required": ["mode"]
        }
        """)!.AsObject();

        McpSchema.MakePortable(schema);
        var mode = schema["properties"]!["mode"]!.AsObject();

        Assert.Null(mode["default"]);
        Assert.NotNull(mode["enum"]);                               // enum is supported: keep it
        Assert.Equal("Mode.", mode["description"]!.GetValue<string>());
        Assert.Single(schema["required"]!.AsArray());
    }
}
