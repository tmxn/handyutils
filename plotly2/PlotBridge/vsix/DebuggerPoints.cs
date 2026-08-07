using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;

namespace PlotBridge.Vsix
{
    /// <summary>
    /// Pulls a point collection out of a debugger property by reading its
    /// children's formatted value strings - no memory layouts, no type knowledge.
    /// The strings go to the server as text and are parsed there, so number
    /// extraction lives in one place (TextPoints.cs) and dimensionality falls out
    /// of how many numbers each line carries.
    ///
    /// Which children to read, in order of preference:
    ///
    /// 1. A CHART VIEW child - a synthetic node named [chart3d] or [chart2d] whose
    ///    own children render one point per line. Built with IndexListItems over a
    ///    view(rawxyz) / view(rawxy) DisplayString, giving
    ///    tab-separated numbers. Preferred because it is one extra enumeration and
    ///    the values need no interpretation. The 3d node is tried first: when the
    ///    element type has no z, an Optional 3d entry drops out on its own, so the
    ///    choice also settles dimensionality.
    ///
    /// 2. [0]..[n-1] children whose value strings contain numbers. This is the
    ///    plain case - a vector of doubles, or a struct whose default display
    ///    spells its members out.
    ///
    /// 3. [0]..[n-1] children whose value strings do NOT contain numbers, expanded
    ///    one level each. A type with an ExpandedItem-only natvis entry displays as
    ///    "{...}" - all the numbers are in the children. Correct but costs an
    ///    enumeration per element, so it is last.
    ///
    /// Range groups ([0..99], [100..199]) are walked at any of these stages;
    /// Visual Studio buckets large collections that way.
    /// </summary>
    internal static class DebuggerPoints
    {
        private static readonly Regex ElementName = new Regex(@"^\[\d+\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex GroupName = new Regex(@"^\[\d+\.\.\d+\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex HasNumber = new Regex(@"[-+]?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Most preferred first.</summary>
        private static readonly string[] ChartViewNames = { "[chart3d]", "[chart2d]", "[chart]", "[plot]" };

        private const int Batch = 2048;
        private const int MaxDepth = 8;

        internal sealed class Result
        {
            public string Text;
            public int Count;
            public string Name;
            public string TypeName;
            public long ElapsedMs;
            public string Warning;
            public string Via;
        }

        private sealed class State
        {
            public bool Truncated;
            public bool SawAnyChild;
            public string FirstChildName;
            public string FirstChildValue;
            public string Via;
            public bool DeepScanned;
        }

        /// <summary>One child from a pass-1 enumeration.</summary>
        private struct Child
        {
            public string Name;
            public string Value;
        }

        public static Result Extract(IDebugProperty3 property, int maxPoints, out string error)
        {
            error = null;
            var sw = Stopwatch.StartNew();

            var self = GetInfo(property);
            var lines = new List<string>(1024);
            var state = new State();

            var hr = Collect(property, lines, maxPoints, 0, state);
            sw.Stop();

            if (lines.Count == 0)
            {
                error = Explain(hr, state);
                return null;
            }

            var sb = new StringBuilder(lines.Count * 24);
            foreach (var line in lines) sb.Append(line).Append('\n');

            var warning = state.Truncated ? $"Stopped at {maxPoints:N0} points - the collection is larger." : null;
            if (state.DeepScanned && lines.Count > 2000)
            {
                warning = (warning == null ? "" : warning + "\n\n") +
                          "Each element had to be expanded individually because its value string " +
                          "carried no numbers, which is why this took a moment. A natvis " +
                          "DisplayString on the element type, or a [chart3d] synthetic on the " +
                          "container, would make it instant.";
            }

            return new Result
            {
                Text = sb.ToString(),
                Count = lines.Count,
                Name = self.Name,
                TypeName = self.TypeName,
                ElapsedMs = sw.ElapsedMilliseconds,
                Warning = warning,
                Via = state.Via,
            };
        }

        private static string Explain(int hr, State state)
        {
            if (hr != VSConstants.S_OK)
                return $"The debugger could not expand this variable (hr = 0x{hr:x8}).";

            if (!state.SawAnyChild)
                return "The debugger reported no children for this variable. Is it empty, or optimised away?";

            var sample = state.FirstChildValue == null
                ? ""
                : $"\n\nThe first child was {state.FirstChildName} = {Trim(state.FirstChildValue, 120)}";

            return "The variable expanded, but no numbers could be found in its elements." + sample +
                   "\n\nPlotBridge reads the value strings the debugger formats. If the element type " +
                   "displays as \"{...}\", give it a natvis DisplayString, or add a [chart3d] / " +
                   "[chart2d] synthetic node to the container.";
        }

        private static string Trim(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

        private static int Collect(IDebugProperty2 property, List<string> lines, int maxPoints, int depth, State state)
        {
            if (depth > MaxDepth) return VSConstants.S_OK;

            // Pass 1: names and values only. No DEBUGPROP_INFO_PROP, so the engine
            // does not build a property object per element.
            var elements = new List<Child>();
            var chartViews = new List<string>();
            var groups = new List<string>();
            var anyElementHasNumber = false;

            var hr = Enumerate(property, withChildProperties: false, visit: (name, value, child) =>
            {
                state.SawAnyChild = true;
                if (state.FirstChildName == null) { state.FirstChildName = name; state.FirstChildValue = value; }

                if (ElementName.IsMatch(name))
                {
                    if (elements.Count < maxPoints)
                    {
                        elements.Add(new Child { Name = name, Value = value });
                        if (!anyElementHasNumber && !string.IsNullOrEmpty(value) && HasNumber.IsMatch(value))
                            anyElementHasNumber = true;
                    }
                    else
                    {
                        state.Truncated = true;
                        return false;
                    }
                }
                else if (IsChartView(name)) chartViews.Add(name);
                else if (GroupName.IsMatch(name)) groups.Add(name);

                return true;
            });

            if (hr != VSConstants.S_OK) return hr;

            // 1 - a chart view beats everything: clean values, one extra enumeration.
            if (chartViews.Count > 0)
            {
                foreach (var wanted in ChartViewNames)
                {
                    if (!chartViews.Contains(wanted)) continue;

                    var before = lines.Count;
                    var node = FindChildProperty(property, wanted);
                    if (node == null) continue;

                    Collect(node, lines, maxPoints, depth + 1, state);
                    if (lines.Count > before)
                    {
                        if (state.Via == null) state.Via = wanted;
                        return VSConstants.S_OK;
                    }
                }
            }

            // 2 - element values already carry their numbers.
            if (anyElementHasNumber)
            {
                foreach (var e in elements)
                {
                    if (string.IsNullOrEmpty(e.Value)) continue;
                    lines.Add(e.Value);
                }
                if (state.Via == null) state.Via = "element values";
                return VSConstants.S_OK;
            }

            // 3 - range buckets.
            if (groups.Count > 0)
            {
                var props = FindChildProperties(property, n => GroupName.IsMatch(n));
                foreach (var g in props)
                {
                    Collect(g, lines, maxPoints, depth + 1, state);
                    if (state.Truncated) break;
                }
                if (lines.Count > 0)
                {
                    if (state.Via == null) state.Via = "range groups";
                    return VSConstants.S_OK;
                }
            }

            // 4 - opaque elements: expand each one and harvest its numeric children.
            if (elements.Count > 0)
            {
                state.DeepScanned = true;
                var props = FindChildProperties(property, n => ElementName.IsMatch(n));
                foreach (var element in props)
                {
                    var parts = new List<string>(3);
                    Enumerate(element, withChildProperties: false, visit: (name, value, child) =>
                    {
                        if (!string.IsNullOrEmpty(value) && HasNumber.IsMatch(value)) parts.Add(value);
                        return parts.Count < 3;
                    });

                    if (parts.Count == 0) continue;
                    lines.Add(string.Join("\t", parts.ToArray()));
                    if (lines.Count >= maxPoints) { state.Truncated = true; break; }
                }
                if (lines.Count > 0 && state.Via == null) state.Via = "expanded elements";
            }

            return VSConstants.S_OK;
        }

        private static bool IsChartView(string name)
        {
            foreach (var n in ChartViewNames)
                if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static IDebugProperty2 FindChildProperty(IDebugProperty2 property, string name)
        {
            IDebugProperty2 found = null;
            Enumerate(property, withChildProperties: true, visit: (n, v, child) =>
            {
                if (child != null && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = child;
                    return false;
                }
                return true;
            });
            return found;
        }

        private static List<IDebugProperty2> FindChildProperties(IDebugProperty2 property, Func<string, bool> nameMatches)
        {
            var found = new List<IDebugProperty2>();
            Enumerate(property, withChildProperties: true, visit: (n, v, child) =>
            {
                if (child != null && nameMatches(n)) found.Add(child);
                return true;
            });
            return found;
        }

        /// <param name="visit">Return false to stop enumerating.</param>
        private static int Enumerate(IDebugProperty2 property, bool withChildProperties,
                                     Func<string, string, IDebugProperty2, bool> visit)
        {
            var filter = Guid.Empty;
            var fields = enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NAME | enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_VALUE;
            if (withChildProperties) fields |= enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP;

            IEnumDebugPropertyInfo2 enumerator;
            try
            {
                var hr = property.EnumChildren(fields, 10, ref filter,
                    enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_NONE, null, 30000, out enumerator);
                if (hr != VSConstants.S_OK || enumerator == null)
                    return hr == VSConstants.S_OK ? VSConstants.E_FAIL : hr;
            }
            catch (Exception)
            {
                return VSConstants.E_FAIL;
            }

            var buffer = new DEBUG_PROPERTY_INFO[Batch];
            while (true)
            {
                uint fetched;
                int next;
                try { next = enumerator.Next(Batch, buffer, out fetched); }
                catch (Exception) { break; }

                if (next != VSConstants.S_OK && next != VSConstants.S_FALSE) break;
                if (fetched == 0) break;

                for (var i = 0; i < fetched; i++)
                {
                    var name = buffer[i].bstrName;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!visit(name, buffer[i].bstrValue, buffer[i].pProperty)) return VSConstants.S_OK;
                }

                if (fetched < Batch) break;
            }

            return VSConstants.S_OK;
        }

        private static (string Name, string TypeName) GetInfo(IDebugProperty3 property)
        {
            try
            {
                var info = new DEBUG_PROPERTY_INFO[1];
                var hr = property.GetPropertyInfo(
                    enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_FULLNAME |
                    enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NAME |
                    enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_TYPE,
                    10, 5000, null, 0, info);

                if (hr != VSConstants.S_OK) return ("points", null);

                var name = !string.IsNullOrEmpty(info[0].bstrFullName) ? info[0].bstrFullName : info[0].bstrName;
                return (string.IsNullOrEmpty(name) ? "points" : name, info[0].bstrType);
            }
            catch
            {
                return ("points", null);
            }
        }
    }
}
