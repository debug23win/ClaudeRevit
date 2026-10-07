using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ClaudeRevit.Tests;

// The tool registry is a contract between three places that drift apart silently: the tool
// classes, their registration in App.cs, and their schemas. A class nobody registered is dead
// code the model never sees; two tools with one name shadow each other; a "required" field that
// is not in the schema makes every call fail validation. (Idea: the registry drift checks in the
// NewLevelHub and rezahanif forks of mcp-servers-for-revit.) Checked statically, so it runs in CI
// without Revit.
public class ToolRegistryContractTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private sealed record ToolClass(string Class, string? Name, string File, HashSet<string> Properties, List<string> Required);

    // Implementations registered at runtime by other means, not in App.cs.
    private static readonly HashSet<string> NotStaticallyRegistered = new(StringComparer.Ordinal) { "DynamicToolProxy" };

    private static List<ToolClass> Tools()
    {
        var result = new List<ToolClass>();
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "ClaudeRevit", "Tools"), "*.cs"))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file));
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (cls.Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword))) continue;
                if (cls.BaseList?.Types.Any(t => t.Type.ToString() == "IRevitTool") != true) continue;
                var nameProp = cls.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == "Name");
                var name = nameProp?.DescendantNodes().OfType<LiteralExpressionSyntax>()
                    .FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression))?.Token.ValueText;
                var schema = cls.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == "InputSchema");
                var props = new HashSet<string>(StringComparer.Ordinal);
                var required = new List<string>();
                if (schema != null)
                {
                    // Top-level keys are written as ["key"] = ... initializers.
                    foreach (var init in schema.DescendantNodes().OfType<ImplicitElementAccessSyntax>())
                        foreach (var arg in init.ArgumentList.Arguments)
                            if (arg.Expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)) props.Add(l.Token.ValueText);
                    // Required = ["a", "b"] or Required = new[] {...} or Schema(fields, "a", "b").
                    foreach (var assign in schema.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.Left.ToString() == "Required"))
                        required.AddRange(assign.Right.DescendantNodes().OfType<LiteralExpressionSyntax>()
                            .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => l.Token.ValueText));
                    foreach (var call in schema.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => c.Expression.ToString().EndsWith("Schema")))
                        required.AddRange(call.ArgumentList.Arguments.Skip(1).Select(a => a.Expression)
                            .OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => l.Token.ValueText));
                }
                result.Add(new ToolClass(cls.Identifier.Text, name, Path.GetFileName(file), props, required));
            }
        }
        return result;
    }

    [Fact]
    public void EveryToolClassIsRegistered()
    {
        var app = File.ReadAllText(Path.Combine(Root, "ClaudeRevit", "App.cs"));
        var missing = Tools().Where(t => !NotStaticallyRegistered.Contains(t.Class) &&
                                         !Regex.IsMatch(app, @"new\s+" + Regex.Escape(t.Class) + @"\s*\("))
            .Select(t => $"{t.Class} ({t.File})").ToList();
        Assert.True(missing.Count == 0, "Tool classes never registered in App.cs:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void ToolNamesAreUniqueAndWellFormed()
    {
        var tools = Tools().Where(t => t.Name != null).ToList();
        var duplicates = tools.GroupBy(t => t.Name).Where(g => g.Count() > 1)
            .Select(g => g.Key + ": " + string.Join(", ", g.Select(t => t.Class))).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate tool names:\n" + string.Join("\n", duplicates));
        var bad = tools.Where(t => !Regex.IsMatch(t.Name!, "^[a-z][a-z0-9_]{1,63}$")).Select(t => t.Name).ToList();
        Assert.True(bad.Count == 0, "Malformed tool names: " + string.Join(", ", bad));
    }

    [Fact]
    public void RequiredFieldsExistInTheSchema()
    {
        var broken = Tools().SelectMany(t => t.Required.Where(r => t.Properties.Count > 0 && !t.Properties.Contains(r))
            .Select(r => $"{t.Name ?? t.Class}: required '{r}' is not a schema property")).ToList();
        Assert.True(broken.Count == 0, string.Join("\n", broken));
    }

    [Fact]
    public void TheCheckActuallySeesTheTools()
    {
        // Guards the test itself: a parsing change that found nothing would pass the others vacuously.
        var tools = Tools();
        Assert.True(tools.Count > 200, $"only {tools.Count} tool classes found");
        Assert.Contains(tools, t => t.Name == "create_wall" && t.Properties.Count > 0);
    }
}
