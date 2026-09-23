using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace FabWinUIDesigner.CodeGen;

/// <summary>One event handler stub to ensure exists: the XAML event name (informational - not used for matching), the method name to look for/insert, and the fully-qualified sender/event-args parameter types.</summary>
/// <param name="EventName">The XAML event name, e.g. "Click" - carried through for the caller's own bookkeeping, not used by <see cref="EventHandlerCodeGen"/> itself.</param>
/// <param name="MethodName">The handler method name to find-or-insert, e.g. "OkButton_Click".</param>
/// <param name="SenderTypeName">Fully-qualified type name for the stub's <c>sender</c> parameter, e.g. "System.Object".</param>
/// <param name="EventArgsTypeName">Fully-qualified type name for the stub's second parameter, e.g. "Microsoft.UI.Xaml.RoutedEventArgs".</param>
public sealed record EventHandlerStub(string EventName, string MethodName, string SenderTypeName, string EventArgsTypeName);

/// <summary>
/// Roslyn syntax-tree based inserter for XAML event-handler stubs. Framework-agnostic on purpose: callers (the App layer,
/// which already knows how to reflect a live WinUI type for its event delegate signatures - see
/// MainWindow.FindUnknownPropertyErrors) supply fully-qualified sender/event-args type names, so
/// this project never needs a WinUI dependency or any <c>using</c>-directive bookkeeping.
/// </summary>
public static class EventHandlerCodeGen
{
    /// <summary>
    /// Ensures every requested handler stub exists as a method on the given partial class,
    /// inserting only what's missing (matched by method name alone - an existing method with
    /// that name, whatever its body, is left completely untouched, so re-running this or hand-
    /// editing a previously generated stub is always safe). Idempotent: re-running with the same
    /// stubs against this method's own prior output produces byte-identical text.
    /// </summary>
    /// <param name="existingSource">The `.xaml.cs` file's current text, or <see langword="null"/> if the file doesn't exist yet - a minimal file is synthesized in that case.</param>
    /// <param name="fullClassName">The class's full name as it appears in `x:Class`, e.g. "FabWinUIDesigner.Samples.SimplePage".</param>
    /// <param name="stubs">The handler stubs that must exist after this call.</param>
    /// <returns>The resulting file text - only the newly-inserted members are reformatted; everything else in an existing file keeps its original formatting untouched.</returns>
    public static string EnsureEventHandlers(string? existingSource, string fullClassName, IReadOnlyList<EventHandlerStub> stubs)
    {
        var lastDot = fullClassName.LastIndexOf('.');
        var namespaceName = lastDot >= 0 ? fullClassName[..lastDot] : null;
        var className = lastDot >= 0 ? fullClassName[(lastDot + 1)..] : fullClassName;

        var root = existingSource is not null
            ? (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(existingSource).GetRoot()
            : CompilationUnit();

        var existingClass = existingSource is not null ? FindClass(root, namespaceName, className) : null;
        if (existingSource is not null && existingClass is null)
        {
            // A file exists but doesn't contain the expected class - most likely means the
            // caller passed the wrong path/class name. Refuse rather than guess: blindly
            // appending a second class (and, for a file-scoped namespace, a second `namespace`
            // declaration - a compile error) to an unrelated file would silently corrupt it.
            throw new InvalidOperationException(
                $"'{fullClassName}' was not found in the given source - refusing to modify a file that doesn't contain the expected class.");
        }

        var existingMethodNames = existingClass?.Members.OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.Text).ToHashSet() ?? [];

        // Annotated so Formatter.Format (below) re-indents only these new nodes in their real
        // tree context - every other node in an existing file keeps its original trivia as-is.
        var newMethods = stubs
            .Where(s => !existingMethodNames.Contains(s.MethodName))
            .Select(s => MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), s.MethodName)
                .AddModifiers(Token(SyntaxKind.PrivateKeyword))
                .AddParameterListParameters(
                    Parameter(Identifier("sender")).WithType(ParseTypeName(s.SenderTypeName)),
                    Parameter(Identifier("e")).WithType(ParseTypeName(s.EventArgsTypeName)))
                .WithBody(Block())
                .WithAdditionalAnnotations(Formatter.Annotation))
            .ToArray();

        if (existingClass is not null)
        {
            if (newMethods.Length == 0)
            {
                return root.ToFullString();
            }

            var updatedClass = existingClass.AddMembers(newMethods);
            root = root.ReplaceNode(existingClass, updatedClass);

            using var workspace = new AdhocWorkspace();
            return Formatter.Format(root, Formatter.Annotation, workspace).ToFullString();
        }

        // No existing file (or no matching class in it) - synthesize a minimal one from scratch.
        // Nothing pre-existing to preserve, so a full NormalizeWhitespace is fine here.
        var newClass = ClassDeclaration(className)
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.SealedKeyword), Token(SyntaxKind.PartialKeyword))
            .AddMembers(newMethods);

        MemberDeclarationSyntax topLevel = namespaceName is null
            ? newClass
            : FileScopedNamespaceDeclaration(ParseName(namespaceName)).AddMembers(newClass);

        return root.AddMembers(topLevel).NormalizeWhitespace().ToFullString();
    }

    /// <summary>Finds the partial class declaration matching <paramref name="className"/>, inside the namespace matching <paramref name="namespaceName"/> if one is given - handles both file-scoped (`namespace X;`) and block (`namespace X { }`) namespace syntax, since either could appear in a hand-edited file.</summary>
    private static ClassDeclarationSyntax? FindClass(CompilationUnitSyntax root, string? namespaceName, string className) =>
        root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c =>
            c.Identifier.Text == className
            && GetContainingNamespace(c) == namespaceName);

    /// <summary>The dotted namespace name a class declaration sits in (from either a file-scoped or block namespace ancestor), or <see langword="null"/> if it's at the top level.</summary>
    private static string? GetContainingNamespace(ClassDeclarationSyntax classDeclaration) =>
        classDeclaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
}
