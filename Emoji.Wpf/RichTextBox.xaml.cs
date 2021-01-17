﻿//
//  Emoji.Wpf — Emoji support for WPF
//
//  Copyright © 2017—2021 Sam Hocevar <sam@hocevar.net>
//
//  This library is free software. It comes without any warranty, to
//  the extent permitted by applicable law. You can redistribute it
//  and/or modify it under the terms of the Do What the Fuck You Want
//  to Public License, Version 2, as published by the WTFPL Task Force.
//  See http://www.wtfpl.net/ for more details.
//

using System.Text;
#if DEBUG
using System.Text.RegularExpressions;
#endif
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Emoji.Wpf
{
    public sealed class TextSelection : TextRange
    {
        internal TextSelection(TextPointer start, TextPointer end)
          : base(start, end) { }

        /// <summary>
        /// Override selection to text conversion in order to convert back all
        /// EmojiInline instances to their equivalent UTF-8 sequences.
        /// </summary>
        public new string Text
        {
            get
            {
                var buf = new StringBuilder();

                for (TextPointer p = Start, next = null;
                     p != null && p.CompareTo(End) < 0;
                     p = next)
                {
                    next = p.GetNextContextPosition(LogicalDirection.Forward);
                    if (next == null)
                        break;

                    switch (p.GetPointerContext(LogicalDirection.Forward))
                    {
                        case TextPointerContext.ElementStart:
                            if (p.GetAdjacentElement(LogicalDirection.Forward) is EmojiInline emoji)
                                buf.Append(emoji.Text);
                            break;
                        case TextPointerContext.ElementEnd:
                        case TextPointerContext.EmbeddedElement:
                            break;
                        case TextPointerContext.Text:
                            // Get text from the Run but don’t go past end
                            buf.Append(new TextRange(p, next.CompareTo(End) < 0 ? next : End).Text);
                            break;
                    }
                }

                return buf.ToString();
            }
        }
    }

    public partial class RichTextBox : System.Windows.Controls.RichTextBox
    {
        public RichTextBox()
        {
            CommandManager.AddPreviewExecutedHandler(this, PreviewExecuted);
            SetValue(Block.LineHeightProperty, 1.0);
            Selection = new TextSelection(Document.ContentStart, Document.ContentStart);
        }

        protected override void OnSelectionChanged(RoutedEventArgs e)
        {
            base.OnSelectionChanged(e);
            Selection = new TextSelection(base.Selection.Start, base.Selection.End);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            // Override MouseDown for emoji elements because the default behaviour is
            // to select the whole InlineUIContainer instead of positioning the caret.
            var pos = e.GetPosition(this);
            var hit = VisualTreeHelper.HitTest(this, pos);
            if (hit.VisualHit is EmojiCanvas emoji && emoji.Parent is InlineUIContainer container)
            {
                var middle = emoji.TranslatePoint(new Point(0, 0), this).X + emoji.ActualWidth / 2;
                CaretPosition = pos.X < middle ? container.ContentStart : container.ContentEnd;
                e.Handled = true;
            }
            base.OnMouseDown(e);
        }

        private static void PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
            => (sender as RichTextBox)?.OnPreviewExecuted(e);

        /// <summary>
        /// Intercept some high level commands to ensure consistency.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected void OnPreviewExecuted(ExecutedRoutedEventArgs e)
        {
            if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut)
            {
                /// Make sure the clipboard contains the proper emoji characters.
                var selection = Selection.Text;
                if (e.Command == ApplicationCommands.Cut)
                    Cut();
                try { Clipboard.SetText(selection); } catch { }
                e.Handled = true;
            }
        }

        /// <summary>
        /// Replace Emoji characters with EmojiInline objects inside the document.
        /// </summary>
        /// <param name="e"></param>
        protected override void OnTextChanged(TextChangedEventArgs e)
        {
            if (m_pending_change)
                return;

            m_pending_change = true;

            base.OnTextChanged(e);

            // Prevent our operation from polluting the undo buffer
            BeginChange();

            TextPointer cur = Document.ContentStart;
            while (cur.CompareTo(Document.ContentEnd) < 0)
            {
                TextPointer next = cur.GetNextInsertionPosition(LogicalDirection.Forward);
                if (next == null)
                    break;

                var emoji_range = new TextRange(cur, next);
                if (EmojiData.MatchOne.IsMatch(emoji_range.Text))
                {
                    // Preserve caret position
                    bool caret_was_next = (0 == next.CompareTo(CaretPosition));

                    // We found an emoji, but it’s possible that GetNextInsertionPosition
                    // did not pick enough characters and the emoji sequence is actually
                    // longer. To avoid this, we look up to 50 characters ahead and retry
                    // the match.
                    var lookup = next.GetNextContextPosition(LogicalDirection.Forward);
                    if (cur.GetOffsetToPosition(lookup) > 50)
                        lookup = cur.GetPositionAtOffset(50, LogicalDirection.Forward);
                    var full_text = new TextRange(cur, lookup).Text;
                    var match = EmojiData.MatchOne.Match(new TextRange(cur, lookup).Text);
                    while (match.Length > emoji_range.Text.Length)
                    {
                        next = next.GetNextInsertionPosition(LogicalDirection.Forward);
                        if (next == null)
                            break;
                        emoji_range = new TextRange(cur, next);
                    }

                    // Delete the Unicode characters and insert our emoji inline instead.
                    emoji_range.Text = "";
                    Inline inline = new EmojiInline(cur)
                    {
                        FontSize = FontSize,
                        Foreground = Foreground,
                        Text = match.Value,
                    };

                    next = inline.ContentEnd;
                    if (caret_was_next)
                        CaretPosition = next;
                }

                cur = next;
            }

            EndChange();

            m_pending_change = false;

            // FIXME: this could be done on-demand by detecting GetValue() calls maybe
            SetValue(TextProperty, new TextSelection(Document.ContentStart, Document.ContentEnd).Text);
#if DEBUG
            try
            {
                var xaml = XamlWriter.Save(Document);
                xaml = Regex.Replace(xaml, "<FlowDocument[^>]*>", "<FlowDocument>");
                SetValue(XamlTextProperty, xaml);
            }
            catch { }
#endif
        }

        private bool m_pending_change = false;

        public new TextSelection Selection { get; private set; }

        public string Text => (string)GetValue(TextProperty);

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(RichTextBox),
            new PropertyMetadata(""));

#if DEBUG
        public string XamlText => (string)GetValue(XamlTextProperty);

        public static readonly DependencyProperty XamlTextProperty = DependencyProperty.Register(
            nameof(XamlText), typeof(string), typeof(RichTextBox),
            new PropertyMetadata(""));
#endif
    }
}

