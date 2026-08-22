using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DinoLino.Utilities
{
    /// <summary>
    /// The preferences a user sets from the View menu, and the small text file they
    /// are kept in between sessions.
    /// </summary>
    /// <remarks>
    /// Only choices about how the program itself looks belong here. Anything that
    /// describes a specimen — its scale, its alignment, its measurements — is session
    /// data and is deliberately left out: opening the program must never offer a
    /// calibration that was measured against an image nobody has loaded.
    ///
    /// Every property starts at the value the XAML gives the control it drives, so a
    /// missing or unreadable file leaves the program looking exactly as it does on a
    /// machine that has never run it.
    /// </remarks>
    public class UserSettings
    {
        #region Preferences

        public bool SeeTips { get; set; } = true;
        public bool SeePreviousOperations { get; set; } = false;
        public bool SeeOperationCount { get; set; } = true;
        public bool SeeImageAxes { get; set; } = false;
        public bool SeeNavigationWindow { get; set; } = false;
        public bool SeeBatchWorkshop { get; set; } = true;
        public bool SeeDirectory { get; set; } = true;
        public bool SeeRex { get; set; } = true;

        /// Tag of the checked View ▸ Line Color radio button. Null until the user
        /// picks a color, which leaves every work mode on its own default.
        public string LineColor { get; set; }

        public string FontFamily { get; set; } = "Arial";
        public double FontSize { get; set; } = 14;

        // A size outside this range leaves the control panel unreadable or the
        // buttons unusable, so a hand-edited or damaged file cannot ask for one.
        // Match these to the range the font dialog offers.
        private const double MinFontSize = 10;
        private const double MaxFontSize = 50;

        #endregion

        #region File location

        private const string FolderName = "DinoLino";
        private const string FileName = "settings.txt";

        /// Roaming application data, so preferences follow the user rather than the
        /// machine they were set on.
        public static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            FolderName,
            FileName);

        #endregion

        #region Reading

        /// Reads the stored preferences, falling back to the default for anything the
        /// file does not name or names badly. A file that cannot be read at all is
        /// treated as no file: preferences are a convenience, and losing them must
        /// never stop the program from starting.
        public static UserSettings Load()
        {
            var settings = new UserSettings();

            Dictionary<string, string> values;
            try
            {
                if (!File.Exists(FilePath)) return settings;
                values = ReadPairs(File.ReadAllLines(FilePath));
            }
            catch (IOException) { return settings; }
            catch (UnauthorizedAccessException) { return settings; }

            settings.SeeTips = ReadBool(values, "SeeTips", settings.SeeTips);
            settings.SeePreviousOperations = ReadBool(values, "SeePreviousOperations", settings.SeePreviousOperations);
            settings.SeeOperationCount = ReadBool(values, "SeeOperationCount", settings.SeeOperationCount);
            settings.SeeImageAxes = ReadBool(values, "SeeImageAxes", settings.SeeImageAxes);
            settings.SeeNavigationWindow = ReadBool(values, "SeeNavigationWindow", settings.SeeNavigationWindow);
            settings.SeeBatchWorkshop = ReadBool(values, "SeeBatchWorkshop", settings.SeeBatchWorkshop);
            settings.SeeDirectory = ReadBool(values, "SeeDirectory", settings.SeeDirectory);
            settings.SeeRex = ReadBool(values, "SeeRex", settings.SeeRex);

            settings.LineColor = ReadString(values, "LineColor", settings.LineColor);
            settings.FontFamily = ReadString(values, "FontFamily", settings.FontFamily);
            settings.FontSize = ReadFontSize(values, settings.FontSize);

            return settings;
        }

        // One "key=value" per line. Blank lines and lines opening with # are skipped,
        // so the file stays readable and editable by hand.
        private static Dictionary<string, string> ReadPairs(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;

                // A key needs at least one character before the separator, so a line
                // opening with '=' is malformed rather than an empty key.
                int split = trimmed.IndexOf('=');
                if (split <= 0) continue;

                values[trimmed.Substring(0, split).Trim()] = trimmed.Substring(split + 1).Trim();
            }

            return values;
        }

        private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback) =>
            values.TryGetValue(key, out string text) && bool.TryParse(text, out bool parsed)
                ? parsed
                : fallback;

        private static string ReadString(Dictionary<string, string> values, string key, string fallback) =>
            values.TryGetValue(key, out string text) && text.Length > 0
                ? text
                : fallback;

        // Invariant culture both ways, so a file written on one machine reads the
        // same on a machine whose decimal separator differs.
        private static double ReadFontSize(Dictionary<string, string> values, double fallback)
        {
            if (!values.TryGetValue("FontSize", out string text)) return fallback;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double size))
                return fallback;

            if (size < MinFontSize || size > MaxFontSize) return fallback;

            return size;
        }

        #endregion

        #region Writing

        /// Writes the preferences, creating the folder on first use. Failures are
        /// swallowed for the same reason Load tolerates a bad file: a preference that
        /// cannot be stored is not worth interrupting someone who is closing the
        /// program.
        public void Save()
        {
            try
            {
                string folder = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                File.WriteAllText(FilePath, Serialize(), new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string Serialize()
        {
            var text = new StringBuilder();
            text.AppendLine("# DinoLino preferences. Delete this file to return to the defaults.");

            Write(text, "SeeTips", SeeTips);
            Write(text, "SeePreviousOperations", SeePreviousOperations);
            Write(text, "SeeOperationCount", SeeOperationCount);
            Write(text, "SeeImageAxes", SeeImageAxes);
            Write(text, "SeeNavigationWindow", SeeNavigationWindow);
            Write(text, "SeeBatchWorkshop", SeeBatchWorkshop);
            Write(text, "SeeDirectory", SeeDirectory);
            Write(text, "SeeRex", SeeRex);

            Write(text, "LineColor", LineColor);
            Write(text, "FontFamily", FontFamily);
            Write(text, "FontSize", FontSize);

            return text.ToString();
        }

        private static void Write(StringBuilder text, string key, bool value) =>
            text.AppendLine($"{key}={(value ? "true" : "false")}");

        private static void Write(StringBuilder text, string key, double value) =>
            text.AppendLine($"{key}={value.ToString(CultureInfo.InvariantCulture)}");

        // An unset value is left out of the file rather than written blank, so reading
        // it back gives the default rather than an empty name.
        private static void Write(StringBuilder text, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                text.AppendLine($"{key}={value.Trim()}");
        }

        #endregion
    }
}