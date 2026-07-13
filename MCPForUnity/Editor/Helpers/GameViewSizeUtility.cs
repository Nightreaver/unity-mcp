using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace MCPForUnity.Editor.Helpers
{
    /// <summary>
    /// Selects the Unity Game View resolution/aspect — the presets in the Game View toolbar
    /// dropdown ("Full HD (1920x1080)", "16:9 Aspect", "4K UHD (3840x2160)", …) — via internal
    /// editor reflection, so screenshots can be captured at a chosen size.
    ///
    /// This reaches into internal UnityEditor types (GameView / GameViewSizes / GameViewSizeGroup
    /// / GameViewSize), which are stable in shape but not public, so all the reflection lives here
    /// in one place per the compat-shim policy. Reflection handles are resolved once and cached.
    /// </summary>
    public static class GameViewSizeUtility
    {
        private static readonly Assembly EditorAsm = typeof(UnityEditor.Editor).Assembly;
        private static readonly Type TGameView = EditorAsm.GetType("UnityEditor.GameView");
        private static readonly Type TGameViewSizes = EditorAsm.GetType("UnityEditor.GameViewSizes");
        private static readonly Type TGameViewSizeGroup = EditorAsm.GetType("UnityEditor.GameViewSizeGroup");
        private static readonly Type TGameViewSize = EditorAsm.GetType("UnityEditor.GameViewSize");

        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public readonly struct SizeInfo
        {
            public readonly int Index;
            public readonly int Width;
            public readonly int Height;
            public readonly string DisplayText;

            public SizeInfo(int index, int width, int height, string displayText)
            {
                Index = index;
                Width = width;
                Height = height;
                DisplayText = displayText;
            }
        }

        /// <summary>True when the internal Game View size API is reachable on this Unity version.</summary>
        public static bool Available =>
            TGameView != null && TGameViewSizes != null && TGameViewSizeGroup != null && TGameViewSize != null;

        /// <summary>Enumerates the sizes in the currently active group (mirrors the toolbar dropdown).</summary>
        public static List<SizeInfo> ListSizes()
        {
            var list = new List<SizeInfo>();
            if (!Available) return list;

            object group = GetCurrentGroup();
            if (group == null) return list;

            int total = (int)TGameViewSizeGroup.GetMethod("GetTotalCount").Invoke(group, null);
            var getSize = TGameViewSizeGroup.GetMethod("GetGameViewSize");
            for (int i = 0; i < total; i++)
            {
                object size = getSize.Invoke(group, new object[] { i });
                if (size == null) continue;
                int w = GetMemberInt(size, "width");
                int h = GetMemberInt(size, "height");
                string display = GetMemberString(size, "displayText")
                    ?? GetMemberString(size, "baseText")
                    ?? $"Size {i}";
                list.Add(new SizeInfo(i, w, h, display));
            }
            return list;
        }

        /// <summary>
        /// Resolves a requested size against the active group. Accepts the exact display text
        /// ("Full HD (1920x1080)"), a "WIDTHxHEIGHT" resolution ("1920x1080"), or a case-insensitive
        /// substring ("Full HD", "16:9", "4K"). Returns false with the available names on no match.
        /// </summary>
        public static bool TryResolve(string request, out SizeInfo match, out List<SizeInfo> available, out string error)
        {
            match = default;
            available = ListSizes();
            error = null;

            if (!Available)
            {
                error = "Game View size API is not available on this Unity version.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(request))
            {
                error = "game_view_size is empty.";
                return false;
            }
            if (available.Count == 0)
            {
                error = "No Game View sizes are available.";
                return false;
            }

            string q = request.Trim();

            // 1) exact display text
            foreach (var s in available)
                if (string.Equals(s.DisplayText, q, StringComparison.OrdinalIgnoreCase)) { match = s; return true; }

            // 2) WIDTHxHEIGHT resolution
            var res = ParseResolution(q);
            if (res.HasValue)
                foreach (var s in available)
                    if (s.Width == res.Value.w && s.Height == res.Value.h) { match = s; return true; }

            // 3) substring
            foreach (var s in available)
                if (s.DisplayText.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) { match = s; return true; }

            error = $"Game View size '{request}' not found. Available: {string.Join(", ", available.Select(a => a.DisplayText))}.";
            return false;
        }

        /// <summary>
        /// Selects the size at <paramref name="index"/> on the Game View window and returns the prior
        /// index (for an optional restore). Setting the property triggers the view to resize.
        /// </summary>
        public static bool TrySelect(int index, out int previousIndex, out string error)
        {
            previousIndex = -1;
            error = null;
            if (!Available) { error = "Game View size API not available."; return false; }

            EditorWindow gv = GetOrOpenGameView();
            if (gv == null) { error = "Could not obtain a Game View window."; return false; }

            var member = SelectedIndexMember();
            if (member == null) { error = "Game View 'selectedSizeIndex' member not found."; return false; }

            previousIndex = GetSelectedIndex(gv, member);
            SetSelectedIndex(gv, member, index);
            gv.Repaint();
            return true;
        }

        /// <summary>Restores a previously captured selected-size index (no-op on invalid input).</summary>
        public static void Restore(int previousIndex)
        {
            if (!Available || previousIndex < 0) return;
            EditorWindow gv = GetOrOpenGameView();
            if (gv == null) return;
            var member = SelectedIndexMember();
            if (member == null) return;
            SetSelectedIndex(gv, member, previousIndex);
            gv.Repaint();
        }

        // ---- internals ----

        private static object _sizesInstance;

        private static object GetSizesInstance()
        {
            if (_sizesInstance != null) return _sizesInstance;
            // GameViewSizes : ScriptableSingleton<GameViewSizes>; static 'instance' lives on the base.
            var instanceProp = TGameViewSizes.BaseType?.GetProperty(
                "instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _sizesInstance = instanceProp?.GetValue(null);
            return _sizesInstance;
        }

        private static object GetCurrentGroup()
        {
            object sizes = GetSizesInstance();
            if (sizes == null) return null;
            object groupType = TGameViewSizes.GetProperty("currentGroupType")?.GetValue(sizes);
            if (groupType == null) return null;
            return TGameViewSizes.GetMethod("GetGroup")?.Invoke(sizes, new[] { groupType });
        }

        private static EditorWindow GetOrOpenGameView()
        {
            // Reuse an existing Game View so we don't steal focus; open one only if none exist.
            // Fully qualified: unqualified 'Resources' collides with the MCPForUnity.Editor.Resources namespace.
            var existing = UnityEngine.Resources.FindObjectsOfTypeAll(TGameView);
            if (existing != null && existing.Length > 0) return existing[0] as EditorWindow;
            return EditorWindow.GetWindow(TGameView, false, "Game", false);
        }

        private static MemberInfo SelectedIndexMember()
        {
            // Prefer the property (its setter triggers the resize); fall back to the field.
            return (MemberInfo)TGameView.GetProperty("selectedSizeIndex", Members)
                ?? TGameView.GetField("selectedSizeIndex", Members)
                ?? TGameView.GetField("m_SelectedSizeIndex", Members);
        }

        private static int GetSelectedIndex(object gv, MemberInfo m)
        {
            if (m is PropertyInfo p) return (int)p.GetValue(gv);
            if (m is FieldInfo f) return (int)f.GetValue(gv);
            return -1;
        }

        private static void SetSelectedIndex(object gv, MemberInfo m, int value)
        {
            if (m is PropertyInfo p) p.SetValue(gv, value);
            else if (m is FieldInfo f) f.SetValue(gv, value);
        }

        private static int GetMemberInt(object obj, string name)
        {
            var t = obj.GetType();
            var p = t.GetProperty(name, Members);
            if (p != null && p.PropertyType == typeof(int)) return (int)p.GetValue(obj);
            var f = t.GetField(name, Members);
            if (f != null && f.FieldType == typeof(int)) return (int)f.GetValue(obj);
            return 0;
        }

        private static string GetMemberString(object obj, string name)
        {
            var t = obj.GetType();
            var p = t.GetProperty(name, Members);
            if (p != null && p.PropertyType == typeof(string)) return (string)p.GetValue(obj);
            var f = t.GetField(name, Members);
            if (f != null && f.FieldType == typeof(string)) return (string)f.GetValue(obj);
            return null;
        }

        private static (int w, int h)? ParseResolution(string s)
        {
            var m = Regex.Match(s, @"(\d{2,5})\s*[xX×]\s*(\d{2,5})");
            if (!m.Success) return null;
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }
    }
}
