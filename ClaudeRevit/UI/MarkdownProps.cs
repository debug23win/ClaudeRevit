using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace ClaudeRevit.UI;

// Renders markdown into a RichTextBox/TextBlock.
//
// Rendering is COALESCED rather than done on every change. A streaming answer updates the bound
// text once per token, and each render re-parses the whole message and rebuilds the entire
// FlowDocument — quadratic in the length of the answer, which is what made long replies crawl and
// drag Revit's UI thread with them. A short timer collapses a burst of deltas into one render, so
// cost scales with elapsed time instead of token count. The timer always renders the CURRENT value,
// so the final text is never stale.
public static class MarkdownProps
{
    // Long enough to swallow a token burst, short enough to still look live.
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(70);

    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.RegisterAttached(
            "Markdown",
            typeof(string),
            typeof(MarkdownProps),
            new PropertyMetadata(null, OnMarkdownChanged));

    // The pending render for one target, so repeated deltas restart the same timer instead of
    // queueing a render each.
    private static readonly DependencyProperty PendingProperty =
        DependencyProperty.RegisterAttached(
            "Pending", typeof(DispatcherTimer), typeof(MarkdownProps), new PropertyMetadata(null));

    public static void SetMarkdown(DependencyObject obj, string value) =>
        obj.SetValue(MarkdownProperty, value);

    public static string GetMarkdown(DependencyObject obj) =>
        (string)obj.GetValue(MarkdownProperty);

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        try
        {
            var timer = (DispatcherTimer?)d.GetValue(PendingProperty);
            if (timer == null)
            {
                timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Coalesce };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Render(d);
                };
                d.SetValue(PendingProperty, timer);

                // First delta of a message renders immediately, so the bubble doesn't appear blank.
                Render(d);
                return;
            }

            timer.Stop();
            timer.Start();
        }
        catch (Exception ex)
        {
            Services.Log.Error("MarkdownProps.OnMarkdownChanged failed", ex);
        }
    }

    private static void Render(DependencyObject d)
    {
        try
        {
            var text = (string?)d.GetValue(MarkdownProperty) ?? "";
            var rendered = MarkdownInlineRenderer.Render(text).ToList();

            if (d is RichTextBox rtb)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                foreach (var inline in rendered) paragraph.Inlines.Add(inline);
                rtb.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
                return;
            }

            if (d is TextBlock tb)
            {
                tb.Inlines.Clear();
                foreach (var inline in rendered) tb.Inlines.Add(inline);
            }
        }
        catch (Exception ex)
        {
            Services.Log.Error("MarkdownProps.Render failed", ex);
        }
    }
}
