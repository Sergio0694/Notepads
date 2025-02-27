﻿
namespace Notepads.Controls.TextEditor
{
    using Notepads.Commands;
    using Notepads.Services;
    using Notepads.Utilities;
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Windows.ApplicationModel.DataTransfer;
    using Windows.System;
    using Windows.UI.Core;
    using Windows.UI.Text;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Input;
    using Windows.UI.Xaml.Media;

    [TemplatePart(Name = ContentElementName, Type = typeof(ScrollViewer))]
    public class TextEditorCore : RichEditBox
    {
        private string[] _contentLinesCache;

        private bool _isLineCachePendingUpdate = true;

        private string _content = string.Empty;

        private readonly IKeyboardCommandHandler<KeyRoutedEventArgs> _keyboardCommandHandler;

        public event EventHandler<TextWrapping> TextWrappingChanged;

        public event EventHandler<double> FontSizeChanged;

        private const string ContentElementName = "ContentElement";

        private ScrollViewer _contentScrollViewer;

        public new TextWrapping TextWrapping
        {
            get => base.TextWrapping;
            set
            {
                base.TextWrapping = value;
                TextWrappingChanged?.Invoke(this, value);
            }
        }

        public new double FontSize
        {
            get => base.FontSize;
            set
            {
                base.FontSize = value;
                FontSizeChanged?.Invoke(this, value);
            }
        }

        public TextEditorCore()
        {
            IsSpellCheckEnabled = false;
            TextWrapping = EditorSettingsService.EditorDefaultTextWrapping;
            FontFamily = new FontFamily(EditorSettingsService.EditorFontFamily);
            FontSize = EditorSettingsService.EditorFontSize;
            SelectionHighlightColor = Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
            SelectionHighlightColorWhenNotFocused = Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
            SelectionFlyout = null;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            HandwritingView.BorderThickness = new Thickness(0);
            CopyingToClipboard += (sender, args) => CopyPlainTextToWindowsClipboard(args);
            Paste += async (sender, args) => await PastePlainTextFromWindowsClipboard(args);
            TextChanging += OnTextChanging;

            SetDefaultTabStop(FontFamily, FontSize);
            PointerWheelChanged += OnPointerWheelChanged;

            EditorSettingsService.OnFontFamilyChanged += (sender, fontFamily) =>
            {
                FontFamily = new FontFamily(fontFamily);
                SetDefaultTabStop(FontFamily, FontSize);
            };
            EditorSettingsService.OnFontSizeChanged += (sender, fontSize) =>
            {
                FontSize = fontSize;
                SetDefaultTabStop(FontFamily, FontSize);
            };

            EditorSettingsService.OnDefaultTextWrappingChanged += (sender, textWrapping) => { TextWrapping = textWrapping; };
            ThemeSettingsService.OnAccentColorChanged += (sender, color) =>
            {
                SelectionHighlightColor = Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
                SelectionHighlightColorWhenNotFocused = Application.Current.Resources["SystemControlForegroundAccentBrush"] as SolidColorBrush;
            };

            // Init shortcuts
            _keyboardCommandHandler = GetKeyboardCommandHandler();
        }

        private KeyboardCommandHandler GetKeyboardCommandHandler()
        {
            return new KeyboardCommandHandler(new List<IKeyboardCommand<KeyRoutedEventArgs>>
            {
                new KeyboardShortcut<KeyRoutedEventArgs>(true, false, false, VirtualKey.Z, (args) => Undo()),
                new KeyboardShortcut<KeyRoutedEventArgs>(true, false, true, VirtualKey.Z, (args) => Redo()),
                new KeyboardShortcut<KeyRoutedEventArgs>(false, true, false, VirtualKey.Z, (args) => TextWrapping = TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap),
                new KeyboardShortcut<KeyRoutedEventArgs>(true, false, false, VirtualKey.Add, (args) => IncreaseFontSize(2)),
                new KeyboardShortcut<KeyRoutedEventArgs>(true, false, false, (VirtualKey)187, (args) => IncreaseFontSize(2)), // (VirtualKey)187: =
                new KeyboardShortcut<KeyRoutedEventArgs>(true, false, false, VirtualKey.Subtract, (args) => DecreaseFontSize(2)),
                new KeyboardShortcut<KeyRoutedEventArgs>(true, false, false, (VirtualKey)189, (args) => DecreaseFontSize(2)), // (VirtualKey)189: -
                new KeyboardShortcut<KeyRoutedEventArgs>(true, false, false, VirtualKey.Number0, (args) => ResetFontSizeToDefault()),
                new KeyboardShortcut<KeyRoutedEventArgs>(true, false, false, VirtualKey.NumberPad0, (args) => ResetFontSizeToDefault()),
            });
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _contentScrollViewer = GetTemplateChild(ContentElementName) as ScrollViewer;
        }

        public void Undo()
        {
            if (Document.CanUndo() && IsEnabled)
            {
                Document.Undo();
            }
        }

        public void Redo()
        {
            if (Document.CanRedo() && IsEnabled)
            {
                Document.Redo();
            }
        }

        public void SetText(string text)
        {
            Document.SetText(TextSetOptions.None, text);
        }

        public string GetText()
        {
            return _content;
        }

        //TODO This method I wrote is pathetic, need to find a way to implement it in a better way 
        public void GetCurrentLineColumn(out int lineIndex, out int columnIndex, out int selectedCount)
        {
            if (_isLineCachePendingUpdate)
            {
                _contentLinesCache = (_content + "\r").Split("\r");
                _isLineCachePendingUpdate = false;
            }

            var start = Document.Selection.StartPosition;
            var end = Document.Selection.EndPosition;

            lineIndex = 1;
            columnIndex = 1;
            selectedCount = 0;

            var length = 0;
            bool startLocated = false;
            for (int i = 0; i < _contentLinesCache.Length; i++)
            {
                var line = _contentLinesCache[i];

                if (line.Length + length >= start && !startLocated)
                {
                    lineIndex = i + 1;
                    columnIndex = start - length + 1;
                    startLocated = true;
                }

                if (line.Length + length >= end)
                {
                    if (i == lineIndex - 1)
                        selectedCount = end - start;
                    else
                        selectedCount = end - start + (i - lineIndex);
                    return;
                }

                length += line.Length + 1;
            }
        }

        public void CopyPlainTextToWindowsClipboard(TextControlCopyingToClipboardEventArgs args)
        {
            if (args != null)
            {
                args.Handled = true;
            }

            try
            {
                DataPackage dataPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                dataPackage.SetText(Document.Selection.Text);
                Clipboard.SetContentWithOptions(dataPackage, new ClipboardContentOptions() { IsAllowedInHistory = true, IsRoamable = true });
                Clipboard.Flush();
            }
            catch (Exception)
            {
                // Ignore
            }
        }

        public async Task PastePlainTextFromWindowsClipboard(TextControlPasteEventArgs args)
        {
            if (args != null)
            {
                args.Handled = true;
            }

            if (!Document.CanPaste()) return;

            try
            {
                var dataPackageView = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (!dataPackageView.Contains(StandardDataFormats.Text)) return;
                var text = await dataPackageView.GetTextAsync();
                Document.Selection.SetText(TextSetOptions.None, text);
                Document.Selection.StartPosition = Document.Selection.EndPosition;
            }
            catch (Exception)
            {
                // ignore
            }
        }

        public void ClearUndoQueue()
        {
            // Clear UndoQueue by setting its limit to 0 and set it back
            var undoLimit = Document.UndoLimit;

            // Check to prevent the undo limit stuck on zero
            // because it returns 0 even if the undo limit isn't set yet
            if (undoLimit != 0)
            {
                Document.UndoLimit = 0;
                Document.UndoLimit = undoLimit;
            }
        }

        public bool FindNextAndReplace(string searchText, string replaceText, bool matchCase, bool matchWholeWord)
        {
            if (FindNextAndSelect(searchText, matchCase, matchWholeWord))
            {
                Document.Selection.SetText(TextSetOptions.None, replaceText);
                return true;
            }

            return false;
        }

        public bool FindAndReplaceAll(string searchText, string replaceText, bool matchCase, bool matchWholeWord)
        {
            var found = false;

            var pos = 0;
            var searchTextLength = searchText.Length;
            var replaceTextLength = replaceText.Length;

            var text = GetText();

            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            pos = matchWholeWord ? IndexOfWholeWord(text, pos, searchText, comparison) : text.IndexOf(searchText, pos, comparison);

            while (pos != -1)
            {
                found = true;
                text = text.Remove(pos, searchTextLength).Insert(pos, replaceText);
                pos += replaceTextLength;
                pos = matchWholeWord ? IndexOfWholeWord(text, pos, searchText, comparison) : text.IndexOf(searchText, pos, comparison);
            }

            if (found)
            {
                SetText(text);
                Document.Selection.StartPosition = Int32.MaxValue;
                Document.Selection.EndPosition = Document.Selection.StartPosition;
            }

            return found;
        }

        public bool FindNextAndSelect(string searchText, bool matchCase, bool matchWholeWord, bool stopAtEof = true)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                return false;
            }

            var text = GetText();

            StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            if (Document.Selection.EndPosition > text.Length) Document.Selection.EndPosition = text.Length;

            var index = matchWholeWord ? IndexOfWholeWord(text, Document.Selection.EndPosition, searchText, comparison) : text.IndexOf(searchText, Document.Selection.EndPosition, comparison);

            if (index != -1)
            {
                Document.Selection.StartPosition = index;
                Document.Selection.EndPosition = index + searchText.Length;
            }
            else
            {
                if (!stopAtEof)
                {
                    index = matchWholeWord ? IndexOfWholeWord(text, 0, searchText, comparison) : text.IndexOf(searchText, 0, comparison);

                    if (index != -1)
                    {
                        Document.Selection.StartPosition = index;
                        Document.Selection.EndPosition = index + searchText.Length;
                    }
                }
            }

            if (index == -1)
            {
                Document.Selection.StartPosition = Document.Selection.EndPosition;
                return false;
            }

            return true;
        }

        protected override void OnKeyDown(KeyRoutedEventArgs e)
        {
            var ctrl = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
            var alt = Window.Current.CoreWindow.GetKeyState(VirtualKey.Menu);

            if (ctrl.HasFlag(CoreVirtualKeyStates.Down) && !alt.HasFlag(CoreVirtualKeyStates.Down))
            {
                // Disable RichEditBox default shortcuts (Bold, Underline, Italic)
                // https://docs.microsoft.com/en-us/windows/desktop/controls/about-rich-edit-controls
                if (e.Key == VirtualKey.B || e.Key == VirtualKey.I || e.Key == VirtualKey.U ||
                    e.Key == VirtualKey.Number1 || e.Key == VirtualKey.Number2 ||
                    e.Key == VirtualKey.Number3 || e.Key == VirtualKey.Number4 ||
                    e.Key == VirtualKey.Number5 || e.Key == VirtualKey.Number6 ||
                    e.Key == VirtualKey.Number7 || e.Key == VirtualKey.Number8 ||
                    e.Key == VirtualKey.Number9 || e.Key == VirtualKey.Tab)
                {
                    return;
                }
            }

            _keyboardCommandHandler.Handle(e);

            if (!e.Handled)
            {
                base.OnKeyDown(e);
            }
        }

        private void SetDefaultTabStop(FontFamily font, double fontSize)
        {
            Document.DefaultTabStop = (float)FontUtility.GetTextSize(font, fontSize, "text").Width;
        }

        private void IncreaseFontSize(double delta)
        {
            SetDefaultTabStop(FontFamily, FontSize + delta);
            FontSize += delta;
        }

        private void DecreaseFontSize(double delta)
        {
            if (FontSize < delta + 2) return;
            SetDefaultTabStop(FontFamily, FontSize - delta);
            FontSize -= delta;
        }

        private void ResetFontSizeToDefault()
        {
            SetDefaultTabStop(FontFamily, EditorSettingsService.EditorFontSize);
            FontSize = EditorSettingsService.EditorFontSize;
        }

        private string TrimRichEditBoxText(string text)
        {
            // Trim end \r
            if (!string.IsNullOrEmpty(text) && text[text.Length - 1] == '\r')
            {
                text = text.Substring(0, text.Length - 1);
            }

            return text;
        }

        private void OnTextChanging(RichEditBox sender, RichEditBoxTextChangingEventArgs args)
        {
            if (args.IsContentChanging)
            {
                Document.GetText(TextGetOptions.None, out _content);
                _content = TrimRichEditBoxText(_content);
                _isLineCachePendingUpdate = true;
            }
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var ctrl = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
            var alt = Window.Current.CoreWindow.GetKeyState(VirtualKey.Menu);
            var shift = Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift);

            if (ctrl.HasFlag(CoreVirtualKeyStates.Down) &&
                !alt.HasFlag(CoreVirtualKeyStates.Down) &&
                !shift.HasFlag(CoreVirtualKeyStates.Down))
            {
                var mouseWheelDelta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
                if (mouseWheelDelta > 0)
                {
                    IncreaseFontSize(1);
                }
                else if (mouseWheelDelta < 0)
                {
                    DecreaseFontSize(1);
                }
            }

            if (!ctrl.HasFlag(CoreVirtualKeyStates.Down) &&
                !alt.HasFlag(CoreVirtualKeyStates.Down) &&
                !shift.HasFlag(CoreVirtualKeyStates.Down))
            {
                if (Document.Selection.Type == SelectionType.Normal ||
                    Document.Selection.Type == SelectionType.InlineShape ||
                    Document.Selection.Type == SelectionType.Shape)
                {
                    var mouseWheelDelta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
                    _contentScrollViewer.ChangeView(_contentScrollViewer.HorizontalOffset,
                            _contentScrollViewer.VerticalOffset + -1 * mouseWheelDelta, null, true);
                }
            }
        }

        private static int IndexOfWholeWord(string target, int startIndex, string value, StringComparison comparison)
        {
            int pos = startIndex;

            while (pos < target.Length && (pos = target.IndexOf(value, pos, comparison)) != -1)
            {
                bool startBoundary = true;
                if (pos > 0)
                    startBoundary = !Char.IsLetterOrDigit(target[pos - 1]);

                bool endBoundary = true;
                if (pos + value.Length < target.Length)
                    endBoundary = !Char.IsLetterOrDigit(target[pos + value.Length]);

                if (startBoundary && endBoundary)
                    return pos;

                pos++;
            }
            return -1;
        }
    }
}
