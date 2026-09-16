using Microsoft.UI.Xaml;

namespace WinUIDesigner.Core;

public sealed class XamlLoadResult
{
    private XamlLoadResult(bool isSuccess, UIElement? root, Exception? error)
    {
        IsSuccess = isSuccess;
        Root = root;
        Error = error;
    }

    public bool IsSuccess { get; }

    public UIElement? Root { get; }

    public Exception? Error { get; }

    public static XamlLoadResult Success(UIElement root) => new(true, root, null);

    public static XamlLoadResult Failure(Exception error) => new(false, null, error);
}
