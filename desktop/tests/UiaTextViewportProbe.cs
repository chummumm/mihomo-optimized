// Test-only UIA3 client. These COM declarations follow the Windows SDK's
// UIAutomationClient.idl, including the order of every preceding vtable slot.
// https://learn.microsoft.com/windows/win32/api/uiautomationclient/
// Do not substitute System.Windows.Automation text ranges here: that managed
// UIA2 client calls the deprecated TextRange_* C exports, which failed in CI.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

public static class NativeTextViewportProbe
{
    public static volatile string Stage = "read_text_uia3_start";

    public static Dictionary<string, object> Read(int processId, string automationId)
    {
        Dictionary<string, object> result = null;
        Exception failure = null;
        Thread worker = new Thread(delegate()
        {
            bool initialized = false;
            try
            {
                Stage = "read_text_uia3_initialize";
                Check(CoInitializeEx(IntPtr.Zero, 0)); // COINIT_MULTITHREADED
                initialized = true;
                using (ComScope scope = new ComScope())
                    result = ReadOnMta(processId, automationId, scope);
            }
            catch (Exception exception) { failure = exception; }
            finally { if (initialized) CoUninitialize(); }
        });
        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        // The existing outer helper-process timeout remains the sole deadline.
        // No RCW, range or exception-producing COM call crosses apartments.
        worker.Join();
        if (failure != null) throw failure;
        return result;
    }

    private static Dictionary<string, object> ReadOnMta(int processId,
        string automationId, ComScope scope)
    {
        Stage = "read_text_uia3_create";
        IUIAutomation automation = scope.Own((IUIAutomation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))));
        Stage = "read_text_uia3_root";
        IUIAutomationElement root;
        Check(automation.GetRootElement(out root));
        scope.Own(root);
        IUIAutomationCondition processCondition, idCondition;
        Stage = "read_text_uia3_process_condition";
        Check(automation.CreatePropertyCondition(30002, processId, out processCondition));
        scope.Own(processCondition);
        Stage = "read_text_uia3_id_condition";
        Check(automation.CreatePropertyCondition(30011, automationId, out idCondition));
        scope.Own(idCondition);
        Stage = "read_text_uia3_windows";
        IUIAutomationElementArray windows;
        Check(root.FindAll(2, processCondition, out windows)); // TreeScope_Children
        scope.Own(windows);
        int windowCount;
        Check(windows.get_Length(out windowCount));
        IUIAutomationElement element = null;
        for (int index = 0; index < windowCount; index++)
        {
            Stage = "read_text_uia3_find_element";
            IUIAutomationElement window, candidate;
            Check(windows.GetElement(index, out window));
            scope.Own(window);
            Check(window.FindFirst(7, idCondition, out candidate)); // TreeScope_Subtree
            scope.Own(candidate);
            if (candidate == null) continue;
            if ((int)Property(candidate, 30002) != processId ||
                !string.Equals((string)Property(candidate, 30011), automationId, StringComparison.Ordinal))
                throw new InvalidOperationException("UIA element identity mismatch.");
            element = candidate;
            break;
        }
        if (element == null) return new Dictionary<string, object> { { "found", false } };

        Stage = "read_text_uia3_properties";
        bool enabled = (bool)Property(element, 30010);
        bool offscreen = (bool)Property(element, 30022);
        bool focused = (bool)Property(element, 30008);
        string name = (string)Property(element, 30005);
        if ((int)Property(element, 30003) != 50004) // UIA_EditControlTypeId
            throw new InvalidOperationException("UIA editor control type mismatch.");

        Stage = "read_text_uia3_value_pattern";
        IUIAutomationValuePattern valuePattern = Pattern<IUIAutomationValuePattern>(element, 10002, scope);
        string value;
        bool readOnly;
        Stage = "read_text_uia3_value";
        Check(valuePattern.get_CurrentValue(out value));
        value = value ?? string.Empty;
        Check(valuePattern.get_CurrentIsReadOnly(out readOnly));

        Stage = "read_text_uia3_scroll_pattern";
        IUIAutomationScrollPattern scroll = Pattern<IUIAutomationScrollPattern>(element, 10004, scope);
        double horizontalPercent, verticalPercent, horizontalViewSize, verticalViewSize;
        bool horizontallyScrollable, verticallyScrollable;
        Stage = "read_text_uia3_scroll";
        Check(scroll.get_CurrentHorizontalScrollPercent(out horizontalPercent));
        Check(scroll.get_CurrentVerticalScrollPercent(out verticalPercent));
        Check(scroll.get_CurrentHorizontalViewSize(out horizontalViewSize));
        Check(scroll.get_CurrentVerticalViewSize(out verticalViewSize));
        Check(scroll.get_CurrentHorizontallyScrollable(out horizontallyScrollable));
        Check(scroll.get_CurrentVerticallyScrollable(out verticallyScrollable));

        Stage = "read_text_uia3_pattern";
        IUIAutomationTextPattern pattern = Pattern<IUIAutomationTextPattern>(element, 10014, scope);
        Dictionary<string, object> viewport = ReadViewport(pattern, value, scope);
        Stage = "read_text_uia3_value_after";
        string valueAfter;
        Check(valuePattern.get_CurrentValue(out valueAfter));
        valueAfter = valueAfter ?? string.Empty;
        viewport["documentMatchesValue"] = (bool)viewport["documentMatchesValue"] &&
            string.Equals(value, valueAfter, StringComparison.Ordinal);
        Stage = "read_text_uia3_complete";
        return new Dictionary<string, object> {
            { "found", true }, { "enabled", enabled }, { "offscreen", offscreen },
            { "focused", focused }, { "name", name }, { "controlType", "ControlType.Edit" },
            { "value", value }, { "readOnly", readOnly },
            { "scroll", new Dictionary<string, object> {
                { "horizontalScrollable", horizontallyScrollable }, { "horizontalPercent", horizontalPercent },
                { "horizontalViewSize", horizontalViewSize }, { "verticalScrollable", verticallyScrollable },
                { "verticalPercent", verticalPercent }, { "verticalViewSize", verticalViewSize } } },
            { "textViewport", viewport }
        };
    }

    private static Dictionary<string, object> ReadViewport(IUIAutomationTextPattern pattern,
        string value, ComScope scope)
    {
        Stage = "read_text_uia3_document";
        IUIAutomationTextRange document;
        Check(pattern.get_DocumentRange(out document));
        scope.Own(document);
        string documentText = Text(document, "read_text_uia3_document_text");
        int length = documentText.Length;
        bool documentValid;
        int[] documentComparisons;
        int[] documentOffsets = ReadBounds(document, document, documentText,
            "read_text_uia3_document", scope, out documentValid, out documentComparisons);
        documentValid &= length > 0 && documentOffsets[0] == 0 && documentOffsets[1] == length &&
            documentComparisons[2] < 0;
        bool matchesValue = string.Equals(documentText.Replace("\r\n", "\n"),
            value.Replace("\r\n", "\n"), StringComparison.Ordinal);

        Stage = "read_text_uia3_visible_ranges";
        IUIAutomationTextRangeArray visible;
        Check(pattern.GetVisibleRanges(out visible));
        scope.Own(visible);
        int visibleCount;
        Check(visible.get_Length(out visibleCount));
        bool startsDocument = false, endsDocument = false, visibleValid = visibleCount > 0;
        List<int[]> visibleOffsets = new List<int[]>();
        List<int[]> visibleComparisons = new List<int[]>();
        for (int index = 0; index < visibleCount; index++)
        {
            Stage = "read_text_uia3_visible_element";
            IUIAutomationTextRange range;
            Check(visible.GetElement(index, out range));
            scope.Own(range);
            bool valid;
            int[] comparisons;
            int[] offsets = ReadBounds(range, document, documentText,
                "read_text_uia3_visible", scope, out valid, out comparisons);
            visibleValid &= valid;
            startsDocument |= valid && comparisons[0] == 0 && offsets[0] == 0;
            endsDocument |= valid && comparisons[1] == 0 && offsets[1] == length;
            visibleOffsets.Add(offsets);
            visibleComparisons.Add(comparisons);
        }

        Stage = "read_text_uia3_selection";
        IUIAutomationTextRangeArray selection;
        Check(pattern.GetSelection(out selection));
        scope.Own(selection);
        int selectionCount;
        Check(selection.get_Length(out selectionCount));
        bool caretAtStart = false, caretAtEnd = false, selectionValid = selectionCount == 1;
        List<int[]> selectionOffsets = new List<int[]>();
        List<int[]> selectionComparisons = new List<int[]>();
        for (int index = 0; index < selectionCount; index++)
        {
            Stage = "read_text_uia3_selection_element";
            IUIAutomationTextRange range;
            Check(selection.GetElement(index, out range));
            scope.Own(range);
            bool valid;
            int[] comparisons;
            int[] offsets = ReadBounds(range, document, documentText,
                "read_text_uia3_caret", scope, out valid, out comparisons);
            selectionValid &= valid;
            selectionOffsets.Add(offsets);
            selectionComparisons.Add(comparisons);
            bool collapsed = valid && comparisons[2] == 0 && offsets[2] == 0 && offsets[0] == offsets[1];
            if (selectionCount == 1 && collapsed)
            {
                caretAtStart = comparisons[0] == 0 && offsets[0] == 0;
                caretAtEnd = comparisons[1] == 0 && offsets[1] == length;
            }
        }
        return new Dictionary<string, object> {
            { "backend", "uia3-com" }, { "apartment", "MTA" },
            { "documentTextLength", length }, { "documentOffsets", documentOffsets },
            { "documentStartVsEnd", documentComparisons[2] },
            { "documentRangeValid", documentValid }, { "documentMatchesValue", matchesValue },
            { "ranges", visibleCount }, { "visibleOffsets", visibleOffsets },
            { "visibleComparisons", visibleComparisons }, { "visibleRangesValid", visibleValid },
            { "selectionOffsets", selectionOffsets }, { "selectionComparisons", selectionComparisons },
            { "selectionRangesValid", selectionValid },
            { "startsDocument", startsDocument }, { "endsDocument", endsDocument },
            { "caretAtStart", caretAtStart }, { "caretAtEnd", caretAtEnd }
        };
    }

    private static int[] ReadBounds(IUIAutomationTextRange range, IUIAutomationTextRange document,
        string documentText, string stage, ComScope scope, out bool valid, out int[] comparisons)
    {
        string text = Text(range, stage + "_text");
        int startComparison, endComparison, spanComparison;
        Stage = stage + "_compare_start";
        Check(range.CompareEndpoints(0, document, 0, out startComparison));
        Stage = stage + "_compare_end";
        Check(range.CompareEndpoints(1, document, 1, out endComparison));
        Stage = stage + "_compare_span";
        Check(range.CompareEndpoints(0, range, 1, out spanComparison));
        comparisons = new int[] { startComparison, endComparison, spanComparison };

        // Independently derive UTF-16 offsets from clones of this exact range.
        // No text searching, guessed positions, selection or scrolling occurs.
        IUIAutomationTextRange prefix, suffix;
        Stage = stage + "_prefix_clone";
        Check(range.Clone(out prefix));
        scope.Own(prefix);
        int moved;
        Stage = stage + "_prefix_extend";
        Check(prefix.MoveEndpointByUnit(0, 6, -1, out moved)); // Start, Document
        string prefixText = Text(prefix, stage + "_prefix_text");
        Stage = stage + "_suffix_clone";
        Check(range.Clone(out suffix));
        scope.Own(suffix);
        Stage = stage + "_suffix_extend";
        Check(suffix.MoveEndpointByUnit(1, 6, 1, out moved)); // End, Document
        string suffixText = Text(suffix, stage + "_suffix_text");

        int start = documentText.Length - suffixText.Length;
        int end = prefixText.Length;
        valid = 0 <= start && start <= end && end <= documentText.Length &&
            documentText.StartsWith(prefixText, StringComparison.Ordinal) &&
            documentText.EndsWith(suffixText, StringComparison.Ordinal) &&
            end - start == text.Length &&
            string.Equals(text, documentText.Substring(start, end - start), StringComparison.Ordinal) &&
            Math.Sign(startComparison) == Math.Sign(start) &&
            Math.Sign(endComparison) == Math.Sign(end - documentText.Length) &&
            Math.Sign(spanComparison) == Math.Sign(start - end);
        return new int[] { start, end, text.Length };
    }

    private static object Property(IUIAutomationElement element, int propertyId)
    {
        object value;
        Check(element.GetCurrentPropertyValue(propertyId, out value));
        return value;
    }

    private static T Pattern<T>(IUIAutomationElement element, int patternId, ComScope scope) where T : class
    {
        object pattern;
        Check(element.GetCurrentPattern(patternId, out pattern));
        scope.Own(pattern);
        if (pattern == null) throw new InvalidOperationException("Required UIA pattern is unavailable.");
        return (T)pattern;
    }

    private static string Text(IUIAutomationTextRange range, string stage)
    {
        Stage = stage;
        string text;
        Check(range.GetText(-1, out text));
        // A successful COM call may represent an empty string with a null
        // BSTR. The independent length/span checks reject an empty document.
        return text ?? string.Empty;
    }

    private static void Check(int result)
    {
        // Preserve the actual HRESULT; ignore unrelated thread IErrorInfo.
        if (result < 0) Marshal.ThrowExceptionForHR(result, new IntPtr(-1));
    }

    private sealed class ComScope : IDisposable
    {
        private readonly List<object> owned = new List<object>();
        public T Own<T>(T value) where T : class
        {
            if (value != null)
            {
                foreach (object existing in owned)
                    if (object.ReferenceEquals(existing, value)) return value;
                owned.Add(value);
            }
            return value;
        }
        public void Dispose()
        {
            for (int index = owned.Count - 1; index >= 0; index--)
                Marshal.FinalReleaseComObject(owned[index]);
            owned.Clear();
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrency);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    // Only unused cache/walker interfaces are opaque pointers. Every used
    // interface is typed, and all native HRESULTs remain explicit.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IUIAutomationElement first, IUIAutomationElement second, [MarshalAs(UnmanagedType.Bool)] out bool same);
        [PreserveSig] int CompareRuntimeIds([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] first, [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] second, [MarshalAs(UnmanagedType.Bool)] out bool same);
        [PreserveSig] int GetRootElement(out IUIAutomationElement root);
        [PreserveSig] int ElementFromHandle(IntPtr window, out IUIAutomationElement element);
        [PreserveSig] int ElementFromPoint(Point point, out IUIAutomationElement element);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
        [PreserveSig] int GetRootElementBuildCache(IntPtr request, out IUIAutomationElement element);
        [PreserveSig] int ElementFromHandleBuildCache(IntPtr window, IntPtr request, out IUIAutomationElement element);
        [PreserveSig] int ElementFromPointBuildCache(Point point, IntPtr request, out IUIAutomationElement element);
        [PreserveSig] int GetFocusedElementBuildCache(IntPtr request, out IUIAutomationElement element);
        [PreserveSig] int CreateTreeWalker(IUIAutomationCondition condition, out IntPtr walker);
        [PreserveSig] int get_ControlViewWalker(out IntPtr walker);
        [PreserveSig] int get_ContentViewWalker(out IntPtr walker);
        [PreserveSig] int get_RawViewWalker(out IntPtr walker);
        [PreserveSig] int get_RawViewCondition(out IUIAutomationCondition condition);
        [PreserveSig] int get_ControlViewCondition(out IUIAutomationCondition condition);
        [PreserveSig] int get_ContentViewCondition(out IUIAutomationCondition condition);
        [PreserveSig] int CreateCacheRequest(out IntPtr request);
        [PreserveSig] int CreateTrueCondition(out IUIAutomationCondition condition);
        [PreserveSig] int CreateFalseCondition(out IUIAutomationCondition condition);
        [PreserveSig] int CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition condition);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] out int[] runtimeId);
        [PreserveSig] int FindFirst(int scope, IUIAutomationCondition condition, out IUIAutomationElement found);
        [PreserveSig] int FindAll(int scope, IUIAutomationCondition condition, out IUIAutomationElementArray found);
        [PreserveSig] int FindFirstBuildCache(int scope, IUIAutomationCondition condition, IntPtr request, out IUIAutomationElement found);
        [PreserveSig] int FindAllBuildCache(int scope, IUIAutomationCondition condition, IntPtr request, out IUIAutomationElementArray found);
        [PreserveSig] int BuildUpdatedCache(IntPtr request, out IUIAutomationElement element);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefault, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCachedPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefault, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid iid, out IntPtr pattern);
        [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid iid, out IntPtr pattern);
        [PreserveSig] int GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object pattern);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        [PreserveSig] int get_Length(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationElement element);
    }

    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        [PreserveSig] int SetValue([MarshalAs(UnmanagedType.BStr)] string value);
        [PreserveSig] int get_CurrentValue([MarshalAs(UnmanagedType.BStr)] out string value);
        [PreserveSig] int get_CurrentIsReadOnly([MarshalAs(UnmanagedType.Bool)] out bool readOnly);
        [PreserveSig] int get_CachedValue([MarshalAs(UnmanagedType.BStr)] out string value);
        [PreserveSig] int get_CachedIsReadOnly([MarshalAs(UnmanagedType.Bool)] out bool readOnly);
    }

    [ComImport, Guid("88f4d42a-e881-459d-a77c-73bbbb7e02dc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationScrollPattern
    {
        [PreserveSig] int Scroll(int horizontalAmount, int verticalAmount);
        [PreserveSig] int SetScrollPercent(double horizontalPercent, double verticalPercent);
        [PreserveSig] int get_CurrentHorizontalScrollPercent(out double value);
        [PreserveSig] int get_CurrentVerticalScrollPercent(out double value);
        [PreserveSig] int get_CurrentHorizontalViewSize(out double value);
        [PreserveSig] int get_CurrentVerticalViewSize(out double value);
        [PreserveSig] int get_CurrentHorizontallyScrollable([MarshalAs(UnmanagedType.Bool)] out bool value);
        [PreserveSig] int get_CurrentVerticallyScrollable([MarshalAs(UnmanagedType.Bool)] out bool value);
        [PreserveSig] int get_CachedHorizontalScrollPercent(out double value);
        [PreserveSig] int get_CachedVerticalScrollPercent(out double value);
        [PreserveSig] int get_CachedHorizontalViewSize(out double value);
        [PreserveSig] int get_CachedVerticalViewSize(out double value);
        [PreserveSig] int get_CachedHorizontallyScrollable([MarshalAs(UnmanagedType.Bool)] out bool value);
        [PreserveSig] int get_CachedVerticallyScrollable([MarshalAs(UnmanagedType.Bool)] out bool value);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        [PreserveSig] int RangeFromPoint(Point point, out IUIAutomationTextRange range);
        [PreserveSig] int RangeFromChild(IUIAutomationElement child, out IUIAutomationTextRange range);
        [PreserveSig] int GetSelection(out IUIAutomationTextRangeArray ranges);
        [PreserveSig] int GetVisibleRanges(out IUIAutomationTextRangeArray ranges);
        [PreserveSig] int get_DocumentRange(out IUIAutomationTextRange range);
        [PreserveSig] int get_SupportedTextSelection(out int value);
    }

    [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        [PreserveSig] int get_Length(out int length);
        [PreserveSig] int GetElement(int index, out IUIAutomationTextRange element);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        [PreserveSig] int Clone(out IUIAutomationTextRange range);
        [PreserveSig] int Compare(IUIAutomationTextRange range, [MarshalAs(UnmanagedType.Bool)] out bool same);
        [PreserveSig] int CompareEndpoints(int sourceEndpoint, IUIAutomationTextRange range, int targetEndpoint, out int comparison);
        [PreserveSig] int ExpandToEnclosingUnit(int unit);
        [PreserveSig] int FindAttribute(int attributeId, [MarshalAs(UnmanagedType.Struct)] object value, [MarshalAs(UnmanagedType.Bool)] bool backward, out IUIAutomationTextRange range);
        [PreserveSig] int FindText([MarshalAs(UnmanagedType.BStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool backward, [MarshalAs(UnmanagedType.Bool)] bool ignoreCase, out IUIAutomationTextRange range);
        [PreserveSig] int GetAttributeValue(int attributeId, [MarshalAs(UnmanagedType.Struct)] out object value);
        [PreserveSig] int GetBoundingRectangles([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)] out double[] rectangles);
        [PreserveSig] int GetEnclosingElement(out IUIAutomationElement element);
        [PreserveSig] int GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string text);
        [PreserveSig] int Move(int unit, int count, out int moved);
        [PreserveSig] int MoveEndpointByUnit(int endpoint, int unit, int count, out int moved);
        [PreserveSig] int MoveEndpointByRange(int sourceEndpoint, IUIAutomationTextRange range, int targetEndpoint);
        [PreserveSig] int Select();
        [PreserveSig] int AddToSelection();
        [PreserveSig] int RemoveFromSelection();
        [PreserveSig] int ScrollIntoView([MarshalAs(UnmanagedType.Bool)] bool alignToTop);
        [PreserveSig] int GetChildren(out IUIAutomationElementArray children);
    }
}
