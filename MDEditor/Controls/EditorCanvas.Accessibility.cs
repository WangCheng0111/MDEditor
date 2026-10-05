using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MDEditor.Core.Text;
using MDEditor.Typesetting.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MDEditor.Controls;

public sealed partial class EditorCanvas
{
    private EditorAutomationPeer? _automationPeer;
    private AutomationTextDocument? _automationText;
    private ITextInteractionMap? _automationMap;
    private AutomationTextGeometry? _automationGeometry;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        _automationPeer ??= new EditorAutomationPeer(this);

    private EditorAutomationPeer AccessibilityPeer =>
        (EditorAutomationPeer)FrameworkElementAutomationPeer.CreatePeerForElement(this);

    private ITextInteractionMap? CurrentAutomationMap()
    {
        var source = _editorText.Capture().Source;
        var map = _renderer?.Document?.BodyInteraction;
        // A pending edit may still display an older frame. Do not claim stale coordinates.
        return map?.Source.Version == source.Version && ReferenceEquals(map.Source, source) ? map : null;
    }

    private AutomationTextDocument AutomationText()
    {
        EnsureAutomationAvailable();
        var source = _editorText.Capture().Source;
        var map = CurrentAutomationMap();
        if (_automationText is null || !ReferenceEquals(_automationText.Source, source) ||
            !ReferenceEquals(_automationMap, map))
        {
            _automationText = new(source, map?.Lines.Select(line => line.Source.Start));
            _automationMap = map;
            _automationGeometry = map is null ? null : new(map);
        }
        return _automationText;
    }

    private void EnsureAutomationAvailable()
    {
        if (_disposed) throw new COMException("The editor is closed.", unchecked((int)0x80040201));
    }

    private bool AutomationHasFocus => !_searchPanel.ContainsFocus &&
        (FocusState != FocusState.Unfocused || _imeInput.FocusState != FocusState.Unfocused);

    private SourceRange AutomationSelection => CurrentEditableSelection()?.Range ?? new(0, 0);
    private int AutomationCaret => CurrentEditableSelection()?.Focus.Offset ?? 0;

    private void FocusForAutomation()
    {
        EnsureAutomationAvailable();
        if (!IsEnabled) throw new InvalidOperationException("The editor is disabled.");
        if (CurrentEditableSelection() is null)
        {
            var caret = new TextCaret(TextSurface.Body, _editorText.Capture().Source.Version, 0, CaretAffinity.Downstream);
            _selection = new(caret, caret);
        }
        Focus(FocusState.Programmatic);
        FocusImeInput();
    }

    private void SelectForAutomation(SourceRange range)
    {
        EnsureAutomationAvailable();
        if (!IsEnabled || _imeComposing) throw new InvalidOperationException("Finish the current input composition before changing selection.");
        MirrorPlainText(_imeInput.Text); ClearImeInput();
        var text = AutomationText();
        range = text.SafeRange(range);
        _history.BreakCoalescing();
        var first = new TextCaret(TextSurface.Body, text.Source.Version, range.Start, CaretAffinity.Downstream);
        var last = first with { Offset = range.End, Affinity = CaretAffinity.Upstream };
        _selection = new(first, last);
        _preferredCaretX = null; _inputFeedback = null;
        UpdateMarkdownReveal();
        FocusForAutomation();
        ResetCaretBlink(); InvalidateInteraction();
        RevealCaret(_renderer?.Document, _canvas);
        UpdateStatusIfReady();
    }

    private void SetAutomationValue(string value)
    {
        EnsureAutomationAvailable();
        ArgumentNullException.ThrowIfNull(value);
        if (!IsEnabled || _imeComposing) throw new InvalidOperationException("Finish the current input composition before changing text.");
        MirrorPlainText(_imeInput.Text); ClearImeInput();
        var before = _editorText.Capture();
        var first = new TextCaret(TextSurface.Body, before.Source.Version, 0, CaretAffinity.Downstream);
        var last = first with { Offset = before.Source.Length, Affinity = CaretAffinity.Upstream };
        var selection = new TextSelection(first, last);
        _history.BreakCoalescing();
        ApplyEdit(TextEditingOperations.Replace(_editorText, selection, value), before, selection, HistoryEditKind.Replace);
    }

    private (Point Origin, double Scale)? AutomationClientTransform()
    {
        if (_canvas is not { ActualWidth: > 0, ActualHeight: > 0 } canvas || XamlRoot is null) return null;
        // WinUI's native TextPattern bridge converts screen points to client physical
        // pixels, and client rectangles back to screen pixels. Do not add HWND origin.
        var relative = canvas.TransformToVisual(XamlRoot.Content).TransformPoint(new(0, 0));
        var scale = XamlRoot.RasterizationScale;
        return (new(relative.X * scale, relative.Y * scale), scale);
    }

    private int AutomationHit(Point client)
    {
        AutomationText();
        if (!double.IsFinite(client.X) || !double.IsFinite(client.Y)) throw new ArgumentOutOfRangeException(nameof(client));
        if (_automationMap is not { } map || _canvas is not { } canvas ||
            AutomationClientTransform() is not { } transform) return AutomationCaret;
        var view = new LayoutPoint((client.X - transform.Origin.X) / transform.Scale,
            (client.Y - transform.Origin.Y) / transform.Scale);
        var viewport = PresentedViewport(canvas);
        // UIA RangeFromPoint must return the nearest range even outside the text;
        // this does not change the stricter pointer hit test used by normal editing.
        if (!viewport.IsVisible) return AutomationCaret;
        view = new(Math.Clamp(view.X, DocumentViewport.Padding, canvas.ActualWidth - DocumentViewport.Padding),
            Math.Clamp(view.Y, 0, canvas.ActualHeight));
        var point = viewport.ToDocument(view, _scroll);
        if (_renderer!.Document!.TryHitTableRow(point, out var tableCaret) && tableCaret is { } cell) return cell.Offset;
        return (map is MarkdownProjectionInteractionMap projected ? projected.HitTestForEditing(point) : map.HitTest(point))?.Offset
            ?? map.HitTest(point)?.Offset ?? AutomationCaret;
    }

    private double[] AutomationRectangles(SourceRange range)
    {
        AutomationText();
        if (_automationGeometry is not { } geometry || _canvas is not { } canvas ||
            AutomationClientTransform() is not { } transform) return [];
        return geometry.Rectangles(range, PresentedViewport(canvas), _scroll)
            .SelectMany(rect => new[] { transform.Origin.X + rect.X * transform.Scale,
                transform.Origin.Y + rect.Y * transform.Scale, rect.Width * transform.Scale, rect.Height * transform.Scale })
            .ToArray();
    }

    private IReadOnlyList<SourceRange> AutomationVisibleRanges()
    {
        AutomationText();
        return _automationGeometry is { } geometry && _canvas is { } canvas ?
            geometry.VisibleRanges(PresentedViewport(canvas), _scroll) : Array.Empty<SourceRange>();
    }

    private void ScrollAutomationRange(SourceRange range, bool alignToTop)
    {
        AutomationText();
        if (_automationMap is not { } map || _canvas is not { } canvas) return;
        var offset = alignToTop ? range.Start : range.End;
        var rect = map.Resolve(new(TextSurface.Body, map.Source.Version, offset,
            alignToTop ? CaretAffinity.Downstream : CaretAffinity.Upstream));
        if (rect is not { } caret) return;
        var viewport = PresentedViewport(canvas);
        _scroll = alignToTop ? caret.Y : caret.Bottom - viewport.DocumentHeight;
        _followCaretAfterCommit = false;
        UpdateScroll(viewport); Invalidate(); UpdateStatusIfReady();
    }

    private void NotifyAccessibilityChanges()
    {
        // No peer/client means no new snapshot/navigation/geometry work on the typing path.
        if (!_disposed) _automationPeer?.NotifyChanges();
    }

    private sealed class EditorAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2, IValueProvider
    {
        private readonly EditorCanvas _editor;
        private SourceTextSnapshot _lastSource;
        private long _lastIdentity;
        private TextSelection? _lastSelection;
        private bool _lastFocus;

        public EditorAutomationPeer(EditorCanvas owner) : base(owner)
        {
            _editor = owner; _lastSource = owner._editorText.Capture().Source;
            _lastIdentity = owner._documentIdentity; _lastSelection = owner._selection;
            _lastFocus = owner.AutomationHasFocus;
        }

        protected override string GetClassNameCore() => nameof(EditorCanvas);
        protected override string GetNameCore() => "Markdown 编辑器";
        protected override string GetAutomationIdCore() => "MarkdownDocument";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        protected override bool IsKeyboardFocusableCore() => _editor.IsEnabled;
        protected override bool HasKeyboardFocusCore() => _editor.AutomationHasFocus;
        protected override void SetFocusCore() => _editor.FocusForAutomation();
        protected override object GetPatternCore(PatternInterface pattern) =>
            pattern is PatternInterface.Text or PatternInterface.Text2 or PatternInterface.Value ? this : base.GetPatternCore(pattern);
        protected override IList<AutomationPeer> GetChildrenCore() => base.GetChildrenCore()?
            .Where(peer => peer is not EditorImeAutomationPeer).ToList() ?? [];

        internal IRawElementProviderSimple Provider => ProviderFromPeer(this);
        internal EditorTextRange Range(SourceRange range) => new(this, _editor.AutomationText(), range, _editor._documentIdentity);
        public ITextRangeProvider DocumentRange => Range(_editor.AutomationText().Source.FullRange);
        public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
        public ITextRangeProvider[] GetSelection() => [Range(_editor.AutomationSelection)];
        public ITextRangeProvider[] GetVisibleRanges() => _editor.AutomationVisibleRanges().Select(range => (ITextRangeProvider)Range(range)).ToArray();
        public ITextRangeProvider RangeFromPoint(Point screenLocation) => Range(new(_editor.AutomationHit(screenLocation), 0));
        public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) =>
            throw new ArgumentException("The editor does not expose embedded child text providers.", nameof(childElement));
        public ITextRangeProvider GetCaretRange(out bool isActive)
        {
            isActive = _editor.AutomationHasFocus && _editor.CurrentEditableSelection() is not null;
            return Range(new(_editor.AutomationCaret, 0));
        }
        public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) =>
            throw new ArgumentException("No UIA annotation provider belongs to this editor.", nameof(annotationElement));
        public bool IsReadOnly => false;
        public string Value => _editor.AutomationText().Source.Text;
        public void SetValue(string value) => _editor.SetAutomationValue(value);

        internal void NotifyChanges()
        {
            var source = _editor._editorText.Capture().Source;
            var changed = !ReferenceEquals(_lastSource, source) || _lastIdentity != _editor._documentIdentity;
            var selectionChanged = _lastSelection != _editor._selection || changed;
            var focused = _editor.AutomationHasFocus;
            var oldSource = _lastSource;
            _lastSource = source; _lastIdentity = _editor._documentIdentity;
            _lastSelection = _editor._selection;
            var focusChanged = focused != _lastFocus;
            _lastFocus = focused;
            if (changed && ListenerExists(AutomationEvents.TextPatternOnTextChanged))
                RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
            if (changed && ListenerExists(AutomationEvents.PropertyChanged))
                RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldSource.Text, source.Text);
            if (selectionChanged && ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
                RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
            if (focusChanged && focused && ListenerExists(AutomationEvents.AutomationFocusChanged))
                RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
        }

        internal sealed class EditorTextRange : ITextRangeProvider
        {
            private readonly EditorAutomationPeer _peer;
            private AutomationTextDocument _text;
            private SourceRange _range;
            private readonly long _identity;

            internal EditorTextRange(EditorAutomationPeer peer, AutomationTextDocument text, SourceRange range, long identity)
            { _peer = peer; _text = text; _range = range; _identity = identity; }

            private void Refresh()
            {
                _peer._editor.EnsureAutomationAvailable();
                if (_identity != _peer._editor._documentIdentity)
                    throw new COMException("This range belongs to a replaced document.", unchecked((int)0x80040201));
                var next = _peer._editor.AutomationText();
                _range = _text.Rebase(_range, next.Source); _text = next;
            }

            private EditorTextRange Compatible(ITextRangeProvider other)
            {
                Refresh();
                if (other is not EditorTextRange range || !ReferenceEquals(_peer, range._peer))
                    throw new ArgumentException("Text ranges must belong to the same editor.", nameof(other));
                range.Refresh(); return range;
            }

            private static int Endpoint(SourceRange range, TextPatternRangeEndpoint endpoint) => endpoint switch
            {
                TextPatternRangeEndpoint.Start => range.Start,
                TextPatternRangeEndpoint.End => range.End,
                _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
            };

            private void SetEndpoint(TextPatternRangeEndpoint endpoint, int value)
            {
                _range = endpoint switch
                {
                    TextPatternRangeEndpoint.Start => new(value, Math.Max(value, _range.End) - value),
                    TextPatternRangeEndpoint.End => new(Math.Min(value, _range.Start), value - Math.Min(value, _range.Start)),
                    _ => throw new ArgumentOutOfRangeException(nameof(endpoint))
                };
            }

            private static AutomationTextUnit Unit(TextUnit unit) => unit switch
            {
                TextUnit.Character => AutomationTextUnit.Character,
                TextUnit.Format or TextUnit.Word => AutomationTextUnit.Word,
                TextUnit.Line => AutomationTextUnit.Line,
                TextUnit.Paragraph => AutomationTextUnit.Paragraph,
                TextUnit.Page or TextUnit.Document => AutomationTextUnit.Document,
                _ => throw new ArgumentOutOfRangeException(nameof(unit))
            };

            public ITextRangeProvider Clone() { Refresh(); return new EditorTextRange(_peer, _text, _range, _identity); }
            public bool Compare(ITextRangeProvider other)
            { var compatible = Compatible(other); return _range == compatible._range; }
            public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider other, TextPatternRangeEndpoint targetEndpoint)
            {
                var compatible = Compatible(other);
                return Endpoint(_range, endpoint).CompareTo(Endpoint(compatible._range, targetEndpoint));
            }
            public void ExpandToEnclosingUnit(TextUnit unit) { Refresh(); _range = _text.Enclosing(_range, Unit(unit)); }
            public string GetText(int maxLength) { Refresh(); return _text.GetText(_range, maxLength); }
            public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase)
            { Refresh(); return _text.FindText(_range, text, backward, ignoreCase) is { } found ? _peer.Range(found) : null!; }
            public int Move(TextUnit unit, int count)
            { Refresh(); var moved = _text.Move(_range, Unit(unit), count); _range = moved.Range; return moved.Moved; }
            public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
            { Refresh(); var moved = _text.MoveEndpoint(Endpoint(_range, endpoint), Unit(unit), count); SetEndpoint(endpoint, moved.Offset); return moved.Moved; }
            public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider other, TextPatternRangeEndpoint targetEndpoint)
            { var compatible = Compatible(other); SetEndpoint(endpoint, Endpoint(compatible._range, targetEndpoint)); }
            public void GetBoundingRectangles(out double[] returnValue)
            { Refresh(); returnValue = _peer._editor.AutomationRectangles(_range); }
            public IRawElementProviderSimple GetEnclosingElement() { Refresh(); return _peer.Provider; }
            public IRawElementProviderSimple[] GetChildren() { Refresh(); return []; }
            public void Select() { Refresh(); _peer._editor.SelectForAutomation(_range); }
            public void AddToSelection()
            {
                Refresh();
                if (_range.Length == 0) { Select(); return; }
                if (_peer._editor.AutomationSelection == _range) return;
                throw new InvalidOperationException("Only a single contiguous selection is supported.");
            }
            public void RemoveFromSelection()
            {
                Refresh();
                if (_range.Length == 0) { Select(); return; }
                if (_peer._editor.AutomationSelection == _range)
                { _peer._editor.SelectForAutomation(new(_range.Start, 0)); return; }
                throw new InvalidOperationException("Removing disjoint selections is unsupported.");
            }
            public void ScrollIntoView(bool alignToTop) { Refresh(); _peer._editor.ScrollAutomationRange(_range, alignToTop); }
            public object GetAttributeValue(int attributeId)
            {
                Refresh();
                if (attributeId == (int)AutomationTextAttributesEnum.IsReadOnlyAttribute) return false;
                if (attributeId == (int)AutomationTextAttributesEnum.IsActiveAttribute) return _peer._editor.AutomationHasFocus;
                // WinUI maps E_NOT_SUPPORTED to UIA's reserved NotSupported value.
                // null would be a missing value, not the required attribute sentinel.
                throw new COMException("This text attribute is not supported.", unchecked((int)0x80070032));
            }
            public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward)
            {
                Refresh();
                if (attributeId != (int)AutomationTextAttributesEnum.IsReadOnlyAttribute &&
                    attributeId != (int)AutomationTextAttributesEnum.IsActiveAttribute) return null!;
                return GetAttributeValue(attributeId).Equals(value) ? Clone() : null!;
            }
        }
    }

    // TSF still owns the real TextBox. Its automation peer delegates to the document,
    // so the focused element never exposes the scratch preedit buffer as document text.
    private sealed class EditorImeInput : TextBox
    {
        internal EditorCanvas? Editor { get; set; }
        protected override AutomationPeer OnCreateAutomationPeer() => Editor is { } editor ?
            new EditorImeAutomationPeer(this, editor.AccessibilityPeer) : base.OnCreateAutomationPeer();
    }

    private sealed class EditorImeAutomationPeer : FrameworkElementAutomationPeer
    {
        private readonly EditorAutomationPeer _document;
        internal EditorImeAutomationPeer(EditorImeInput owner, EditorAutomationPeer document) : base(owner)
        { _document = document; EventsSource = document; }
        protected override string GetClassNameCore() => nameof(EditorImeInput);
        protected override string GetNameCore() => _document.GetName();
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override bool IsContentElementCore() => false;
        protected override bool IsControlElementCore() => false;
        protected override object GetPatternCore(PatternInterface pattern) =>
            pattern is PatternInterface.Text or PatternInterface.Text2 or PatternInterface.Value ? _document : null!;
        protected override Rect GetBoundingRectangleCore() => _document.GetBoundingRectangle();
        protected override void SetFocusCore() => _document.SetFocus();
    }
}
