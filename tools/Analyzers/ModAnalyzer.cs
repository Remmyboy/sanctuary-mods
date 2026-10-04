using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SanctuaryMods.Analyzers
{
    // Checks for the mistakes this repo has actually made, which the stock
    // analyzers either miss or bury in noise. Severity is set in .editorconfig.
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ModAnalyzer : DiagnosticAnalyzer
    {
        // A `catch { }` with nothing in it and no comment saying why: the
        // failure it eats is gone without a trace (a hook that silently stops,
        // a gate that fails open). Log it, once if it repeats, or say in a
        // comment why ignoring it is right.
        public static readonly DiagnosticDescriptor EmptyCatch = new DiagnosticDescriptor(
            "SMOD001", "Empty catch",
            "Empty catch: log the failure (once, if it can repeat) or say in a comment why it is safe to ignore",
            "Reliability", DiagnosticSeverity.Warning, true);

        // A float turned into text with the player's own culture: "1,5" where
        // Lua, a .cfg or a file format wants "1.5". Pass
        // CultureInfo.InvariantCulture, or FormattableString.Invariant($"...").
        public static readonly DiagnosticDescriptor CultureFloat = new DiagnosticDescriptor(
            "SMOD002", "Float formatted with the current culture",
            "'{0}' becomes text in the player's culture (a comma decimal in much of Europe): pass CultureInfo.InvariantCulture",
            "Globalization", DiagnosticSeverity.Warning, true);

        // HideAndDontSave keeps a made texture, sprite or font alive and out
        // of the scene, so nothing frees it, and every hot reload leaves one
        // more behind. Generated.Keep (shared/Generated.cs) frees them when
        // the mod unloads.
        public static readonly DiagnosticDescriptor HideAndDontSave = new DiagnosticDescriptor(
            "SMOD003", "HideAndDontSave outside Generated.Keep",
            "HideAndDontSave objects are never freed and pile up across hot reloads: make it with Generated.Keep, or destroy it in OnDestroy",
            "Reliability", DiagnosticSeverity.Warning, true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(EmptyCatch, CultureFloat, HideAndDontSave);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(CheckCatch, SyntaxKind.CatchClause);
            context.RegisterSyntaxNodeAction(CheckHideFlags, SyntaxKind.SimpleMemberAccessExpression);
            context.RegisterOperationAction(CheckInterpolation, OperationKind.Interpolation);
            context.RegisterOperationAction(CheckConcat, OperationKind.Binary);
            context.RegisterOperationAction(CheckToString, OperationKind.Invocation);
        }

        private static void CheckCatch(SyntaxNodeAnalysisContext c)
        {
            var clause = (CatchClauseSyntax)c.Node;
            var block = clause.Block;
            if (block.Statements.Count > 0) return;
            var commented = block.OpenBraceToken.TrailingTrivia.Concat(block.CloseBraceToken.LeadingTrivia)
                .Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
            if (!commented) c.ReportDiagnostic(Diagnostic.Create(EmptyCatch, clause.CatchKeyword.GetLocation()));
        }

        private static void CheckHideFlags(SyntaxNodeAnalysisContext c)
        {
            var access = (MemberAccessExpressionSyntax)c.Node;
            if (access.Name.Identifier.Text != "HideAndDontSave") return;
            if (!(c.SemanticModel.GetSymbolInfo(access).Symbol is IFieldSymbol f) || f.ContainingType?.Name != "HideFlags") return;
            // Generated.Keep itself, and the mods that don't compile shared/.
            var type = c.ContainingSymbol?.ContainingType;
            if (type?.Name == "Generated") return;
            if (c.Compilation.GetTypeByMetadataName("SanctuaryHud.Generated") == null) return;
            c.ReportDiagnostic(Diagnostic.Create(HideAndDontSave, access.GetLocation()));
        }

        private static bool IsFloat(ITypeSymbol t) =>
            t != null && (t.SpecialType == SpecialType.System_Single || t.SpecialType == SpecialType.System_Double ||
                          t.SpecialType == SpecialType.System_Decimal);

        // $"{x}" with a float hole, unless the whole string goes through
        // FormattableString.Invariant or into an IFormattable/FormattableString.
        private static void CheckInterpolation(OperationAnalysisContext c)
        {
            var hole = (IInterpolationOperation)c.Operation;
            var value = Unwrap(hole.Expression);
            if (!IsFloat(value.Type)) return;
            var str = hole.Parent as IInterpolatedStringOperation;
            if (str == null) return;
            var target = str.Parent is IConversionOperation conv ? conv.Type : str.Type;
            if (target != null && (target.Name == "FormattableString" || target.Name == "IFormattable")) return;
            c.ReportDiagnostic(Diagnostic.Create(CultureFloat, hole.Syntax.GetLocation(), hole.Expression.Syntax.ToString()));
        }

        // "..." + x with a float operand.
        private static void CheckConcat(OperationAnalysisContext c)
        {
            var op = (IBinaryOperation)c.Operation;
            if (op.OperatorKind != BinaryOperatorKind.Add || op.Type?.SpecialType != SpecialType.System_String) return;
            foreach (var side in new[] { op.LeftOperand, op.RightOperand })
            {
                var value = Unwrap(side);
                if (IsFloat(value.Type))
                    c.ReportDiagnostic(Diagnostic.Create(CultureFloat, side.Syntax.GetLocation(), side.Syntax.ToString()));
            }
        }

        // x.ToString() / x.ToString("0.0") on a float, without a provider.
        private static void CheckToString(OperationAnalysisContext c)
        {
            var call = (IInvocationOperation)c.Operation;
            var m = call.TargetMethod;
            if (m.Name != "ToString" || call.Instance == null || !IsFloat(call.Instance.Type)) return;
            if (m.Parameters.Any(p => p.Type.Name == "IFormatProvider")) return;
            c.ReportDiagnostic(Diagnostic.Create(CultureFloat, call.Syntax.GetLocation(), call.Syntax.ToString()));
        }

        private static IOperation Unwrap(IOperation op)
        {
            while (op is IConversionOperation conv && conv.IsImplicit) op = conv.Operand;
            return op;
        }
    }
}
