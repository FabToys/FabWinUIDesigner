using Microsoft.UI.Xaml;

namespace FabWinUIDesigner.Core;

/// <summary>Outcome of one <see cref="XamlPreviewLoader.TryLoad"/> attempt - a simple success/failure union instead of throwing, since a failed attempt is an expected, handled case (see <see cref="XamlPreviewSanitizer"/>'s multi-attempt fallback).</summary>
public sealed class XamlLoadResult
{
    private XamlLoadResult(bool isSuccess, UIElement? root, Exception? error)
    {
        IsSuccess = isSuccess;
        Root = root;
        Error = error;
    }

    /// <summary>True if loading succeeded and <see cref="Root"/> is set.</summary>
    public bool IsSuccess { get; }

    /// <summary>The loaded live element, if <see cref="IsSuccess"/> is true; otherwise null.</summary>
    public UIElement? Root { get; }

    /// <summary>The exception that caused the load to fail, if <see cref="IsSuccess"/> is false; otherwise null.</summary>
    public Exception? Error { get; }

    /// <summary>Creates a successful result.</summary>
    /// <param name="root">The successfully loaded live element.</param>
    public static XamlLoadResult Success(UIElement root) => new(true, root, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The exception that caused the failure.</param>
    public static XamlLoadResult Failure(Exception error) => new(false, null, error);
}
