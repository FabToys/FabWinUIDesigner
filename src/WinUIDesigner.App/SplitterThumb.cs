using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace WinUIDesigner.App;

/// <summary>
/// A plain Grid with a settable hover cursor. <see cref="UIElement.ProtectedCursor"/> is
/// `protected` - only reachable from inside a UIElement subclass - so a plain XAML &lt;Grid&gt;
/// can never show a custom cursor no matter what code-behind does to it. Grid (not Border or
/// Rectangle - both sealed in this SDK and can't be subclassed at all) is the base here so the
/// existing visual (a solid-color splitter, or a Rectangle nested inside a resize handle) keeps
/// working unchanged. See research/16-splitter-hover-cursor.md and
/// research/17-resize-handle-hover-cursor.md.
/// </summary>
public class CursorGrid : Grid
{
    /// <summary>Sets the cursor shown while the pointer hovers over this element (or a hit-testable descendant with no cursor of its own).</summary>
    /// <param name="shape">The system cursor shape to display, e.g. a resize arrow.</param>
    public void SetCursor(InputSystemCursorShape shape) => ProtectedCursor = InputSystemCursor.Create(shape);
}

/// <summary>Drag splitter between two resizable panels.</summary>
public sealed class SplitterThumb : CursorGrid
{
}

/// <summary>One of the 8 resize handles drawn around a selected element's adorner; wraps the original Rectangle visual.</summary>
public sealed class ResizeHandle : CursorGrid
{
}
