using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Winhance.UI.Features.Common.Helpers;

/// <summary>
/// Applies fast-scroll keyboard handling (PageUp/PageDown/Home/End) to WinUI 3
/// <see cref="ScrollView"/> hosts.
///
/// Background: all of our feature-detail pages host a <see cref="ListView"/> with
/// inner scrolling disabled inside an outer <see cref="ScrollView"/>. The ListView
/// consumes PageUp/PageDown for focus traversal, which emergently scrolls the outer
/// ScrollView all the way to the top/bottom. That's the "broken-feeling" behavior
/// issue #581 tracks.
///
/// This helper replaces that with viewport-sized paging on the outer ScrollView:
/// PageUp/PageDown scroll by ~one viewport; Home/End jump to the very top/bottom.
/// Focus does not move — matching mouse-wheel-chord / browser semantics, which is
/// what the issue requested.
///
/// Guards: we do NOT handle the key when the focused element is inside a control
/// that should own its own paging (open ComboBox popup, AutoSuggestBox with its
/// suggestion list open, multi-line TextBox, or a nested ScrollViewer/ScrollView
/// other than the host we were asked to scroll). In those cases we let the event
/// bubble untouched.
/// </summary>
internal static class PageScrollHelper
{
    /// <summary>
    /// Fraction of the viewport a single PageUp/PageDown press scrolls.
    /// Kept small so content with only modest overflow doesn't jump straight to the end.
    /// </summary>
    private const double PageStepFraction = 0.15;

    /// <summary>
    /// Attaches fast-scroll handling to <paramref name="keyEventSource"/> for the
    /// given <paramref name="scrollView"/>.
    ///
    /// Why PreviewKeyDown: the inner ListView's built-in key handling reacts to
    /// PageUp/PageDown by moving focus to the first/last item in the viewport,
    /// which raises <c>BringIntoViewRequested</c> and makes the outer ScrollView
    /// jump to the top/bottom — the exact emergent behavior we're trying to
    /// replace. A bubbling (KeyDown) handler would see the event only AFTER
    /// the ListView has already performed that focus change, so by then it's
    /// too late to prevent the jump. PreviewKeyDown tunnels root → target, so
    /// we can set <c>e.Handled = true</c> before the ListView's own key handler
    /// gets a chance to run.
    ///
    /// We also keep a bubbling KeyDown handler with <c>handledEventsToo: true</c>
    /// as a belt-and-braces fallback for guard paths (e.g. nested ScrollViewer)
    /// where Preview returns without handling — but in practice the Preview path
    /// is what does the real work.
    /// </summary>
    /// <param name="keyEventSource">
    /// The element whose <c>PreviewKeyDown</c>/<c>KeyDown</c> events we subscribe
    /// to. Typically the Page or UserControl root, so the handler sees keys
    /// regardless of which descendant has focus.
    /// </param>
    /// <param name="scrollView">The outer <see cref="ScrollView"/> to scroll.</param>
    public static void Attach(UIElement keyEventSource, ScrollView scrollView)
    {
        if (keyEventSource == null || scrollView == null) return;

        // Tunneling handler — fires BEFORE any descendant's KeyDown. This is how
        // we stop the ListView from converting PageUp/PageDown into
        // first-item/last-item focus traversal (which would otherwise scroll
        // the outer ScrollView to the top/bottom via BringIntoViewRequested).
        keyEventSource.AddHandler(
            UIElement.PreviewKeyDownEvent,
            new KeyEventHandler((s, e) => HandleKey(scrollView, e)),
            handledEventsToo: true);

        // Bubbling fallback — covers the case where focus is on the ScrollView
        // itself (or another element that doesn't route through the preview
        // chain as we expect) and the key would otherwise be unhandled.
        keyEventSource.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((s, e) => HandleKey(scrollView, e)),
            handledEventsToo: true);
    }

    /// <summary>
    /// Inspects <paramref name="e"/> and scrolls <paramref name="scrollView"/> if
    /// the key is PageUp/PageDown/Home/End and no guard applies. Sets
    /// <c>e.Handled = true</c> only when it actually scrolled.
    /// </summary>
    public static void HandleKey(ScrollView scrollView, KeyRoutedEventArgs e)
    {
        if (scrollView == null || e == null) return;
        if (!IsPagingKey(e.Key)) return;

        if (ShouldSkipForFocusedElement(e.OriginalSource as DependencyObject, scrollView))
            return;

        // Nothing to scroll — don't consume the key.
        if (scrollView.ScrollableHeight <= 0) return;

        var options = new ScrollingScrollOptions(ScrollingAnimationMode.Disabled);

        switch (e.Key)
        {
            case VirtualKey.PageUp:
                scrollView.ScrollBy(0, -scrollView.ViewportHeight * PageStepFraction, options);
                e.Handled = true;
                break;

            case VirtualKey.PageDown:
                scrollView.ScrollBy(0, scrollView.ViewportHeight * PageStepFraction, options);
                e.Handled = true;
                break;

            case VirtualKey.Home:
                scrollView.ScrollTo(scrollView.HorizontalOffset, 0, options);
                e.Handled = true;
                break;

            case VirtualKey.End:
                scrollView.ScrollTo(scrollView.HorizontalOffset, scrollView.ScrollableHeight, options);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// True for the four keys this helper intercepts.
    /// Exposed for unit testing.
    /// </summary>
    internal static bool IsPagingKey(VirtualKey key) =>
        key == VirtualKey.PageUp ||
        key == VirtualKey.PageDown ||
        key == VirtualKey.Home ||
        key == VirtualKey.End;

    /// <summary>
    /// Returns true if the focused element (or any ancestor up to, but not
    /// including, <paramref name="scrollViewHost"/>) is a control that should
    /// own its own paging behavior. Walking the tree — rather than just checking
    /// the immediate focused element — is important because focus typically sits
    /// on a small inner control (e.g. the editable TextBox inside an
    /// AutoSuggestBox) rather than the container.
    /// </summary>
    /// <param name="focused">The focused element (typically <c>e.OriginalSource</c>).</param>
    /// <param name="scrollViewHost">
    /// The ScrollView we were asked to scroll. Any ScrollViewer/ScrollView we
    /// encounter on the way up that isn't this host means a nested scroller
    /// owns the key.
    /// </param>
    internal static bool ShouldSkipForFocusedElement(DependencyObject? focused, ScrollView scrollViewHost)
    {
        for (var current = focused; current != null; current = VisualTreeHelper.GetParent(current))
        {
            // Nested scroll host — let it handle its own paging.
            if (current is ScrollViewer) return true;
            if (current is ScrollView sv && !ReferenceEquals(sv, scrollViewHost)) return true;

            // ComboBox (and our ComboBoxEx subclass) with dropdown open.
            if (current is ComboBox combo && combo.IsDropDownOpen) return true;

            // AutoSuggestBox with suggestion list open.
            if (current is AutoSuggestBox asb && asb.IsSuggestionListOpen) return true;

            // Multi-line TextBox — PageUp/PageDown move the caret between lines there.
            if (current is TextBox tb && (tb.AcceptsReturn || tb.TextWrapping != TextWrapping.NoWrap))
                return true;
        }

        return false;
    }
}
