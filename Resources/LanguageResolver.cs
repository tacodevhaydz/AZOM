using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MozaPlugin.Resources
{
    /// <summary>
    /// Resolves the effective UI culture for the plugin. Reads SimHub's own
    /// language choice from <c>&lt;install&gt;/PluginsData/GlobalSimhubSettings.json</c>
    /// (<c>Culture</c> field) so the plugin tracks the host application's
    /// language without a redundant picker of its own. Falls back to the OS UI
    /// culture, then English.
    ///
    /// Called once from <c>MozaPlugin.Init</c> and again from
    /// <c>MozaPlugin.GetWPFSettingsControl</c> (because the WPF UI thread
    /// predates plugin Init and would otherwise keep its own
    /// <c>CurrentUICulture</c>).
    /// </summary>
    internal static class LanguageResolver
    {
        // Locales we ship a Strings.<lang>.resx satellite for. The neutral
        // ("en") resources live in the main assembly; the rest are satellites
        // under <install>/<culture>/MozaPlugin.resources.dll. Adding a new
        // language means: drop Strings.<lang>.resx in Resources/, append the
        // culture code here, add an EmbeddedResource entry in MozaPlugin.csproj.
        private static readonly string[] SupportedCultures = { "en", "es", "fr", "ru" };

        public static CultureInfo Resolve()
        {
            // 1) Honour SimHub's UI language. Users expect their language pick
            //    in SimHub Settings to apply to every plugin's pane, not just
            //    SimHub's own surface.
            var simhubCulture = TryReadSimHubCulture();
            if (!string.IsNullOrWhiteSpace(simhubCulture))
            {
                var matched = WalkChainForSupported(simhubCulture!);
                if (matched != null) return matched;
            }

            // 2) Fall back to the OS UI culture so a German-locale machine with
            //    SimHub set to "en" still gets a sensible default if/when we
            //    add German resources.
            var osMatched = WalkChainForSupported(CultureInfo.CurrentUICulture.Name);
            if (osMatched != null) return osMatched;

            return new CultureInfo("en");
        }

        /// <summary>
        /// Reads the <c>Culture</c> field out of SimHub's global settings JSON.
        /// Returns null if the file is missing, unreadable, or the field is absent.
        /// </summary>
        private static string? TryReadSimHubCulture()
        {
            try
            {
                // Plugin DLLs load from the SimHub install dir, so
                // AppDomain.BaseDirectory points at it. The settings file lives
                // under PluginsData/.
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                        "PluginsData", "GlobalSimhubSettings.json");
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                // Lightweight extraction so we don't drag Newtonsoft into a path
                // that runs before the rest of plugin initialisation.
                var m = Regex.Match(json, "\"Culture\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Walks the parent chain of <paramref name="name"/> looking for a
        /// match against <see cref="SupportedCultures"/>. So "es-MX" resolves
        /// to "es"; "ja-JP" returns null (we don't ship Japanese).
        /// </summary>
        private static CultureInfo? WalkChainForSupported(string name)
        {
            CultureInfo? c;
            try { c = new CultureInfo(name); }
            catch (CultureNotFoundException) { return null; }

            while (c != null && !string.IsNullOrEmpty(c.Name))
            {
                if (SupportedCultures.Any(s => string.Equals(s, c.Name, StringComparison.OrdinalIgnoreCase)))
                    return new CultureInfo(c.Name);
                if (c.Equals(CultureInfo.InvariantCulture)) break;
                c = c.Parent;
            }
            return null;
        }
    }
}
