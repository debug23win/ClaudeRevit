using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ClaudeRevit.Tools;

// Add cooperative Stop checks without pumping Revit's message loop or aborting a
// thread. Existing trivia/line breaks are retained for snippet diagnostic lines.
internal sealed class ScriptLoopInstrumentation : CSharpSyntaxRewriter
{
    public static SyntaxTree Apply(SyntaxTree tree) => tree.WithRootAndOptions(new ScriptLoopInstrumentation().Visit(tree.GetRoot())!, tree.Options);
    private static StatementSyntax Checked(StatementSyntax body)
    {
        var check = SyntaxFactory.ParseStatement("global::ClaudeRevit.Tools.ScriptRuntime.CheckCancellation();");
        return body is BlockSyntax block ? block.WithStatements(block.Statements.Insert(0, check)) : SyntaxFactory.Block(check, body);
    }
    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    { var updated = (ForStatementSyntax)base.VisitForStatement(node)!; return updated.WithStatement(Checked(updated.Statement)); }
    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    { var updated = (ForEachStatementSyntax)base.VisitForEachStatement(node)!; return updated.WithStatement(Checked(updated.Statement)); }
    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    { var updated = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!; return updated.WithStatement(Checked(updated.Statement)); }
    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    { var updated = (WhileStatementSyntax)base.VisitWhileStatement(node)!; return updated.WithStatement(Checked(updated.Statement)); }
    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    { var updated = (DoStatementSyntax)base.VisitDoStatement(node)!; return updated.WithStatement(Checked(updated.Statement)); }
}
