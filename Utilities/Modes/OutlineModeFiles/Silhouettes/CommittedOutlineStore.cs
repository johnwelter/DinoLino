using System;
using System.Collections.Generic;
using System.Windows;

namespace DinoLino.Utilities
{
    /// <summary>
    /// One outline the user deliberately kept as a silhouette. The points are a
    /// snapshot taken at commit time, so undoing the operation, editing it in the
    /// Batch Workshop, or redrawing over it leaves this entry untouched.
    /// </summary>
    public sealed class CommittedOutline
    {
        /// <summary>Specimen the outline was drawn on, as named at commit time.</summary>
        public string SpecimenName;

        /// <summary>File stem the user chose; also what the batch export names it.</summary>
        public string Name;

        /// <summary>Canvas-space points, closure duplicate stripped.</summary>
        public List<Point> Points;
    }

    /// <summary>
    /// Session-scoped store of the outlines the user has committed as silhouettes,
    /// plus the settings remembered when the commit window is switched off. Only
    /// what lives here is written by Batch Workshop ▸ Export 2D Outlines.
    /// </summary>
    public static class CommittedOutlineStore
    {
        public const int MaxNameLength = 60;

        private static readonly List<CommittedOutline> _outlines = new List<CommittedOutline>();

        /// <summary>Committed outlines in the order they were stored.</summary>
        public static IReadOnlyList<CommittedOutline> Outlines => _outlines;

        public static int Count => _outlines.Count;

        // =====================
        // Remembered dialog settings
        // =====================
        // Set only by "Don't show window again"; the batch export keeps asking for
        // its own transformations regardless of what is remembered here.

        public static bool SuppressDialog { get; private set; }
        public static bool DefaultScaleToCommonArea { get; private set; } = false;
        public static bool DefaultAlignRotation { get; private set; } = true;
        public static int DefaultCanvasSize { get; private set; } = 512;

        /// Stores the settings the user just used, and whether the window should be
        /// skipped from now on.
        public static void RememberDefaults(OutlineExportOptions options, bool suppress)
        {
            if (options != null)
            {
                DefaultScaleToCommonArea = options.ScaleToCommonArea;
                DefaultAlignRotation = options.AlignRotation;
                DefaultCanvasSize = options.CanvasSize;
            }

            if (suppress) SuppressDialog = true;
        }

        // =====================
        // Names
        // =====================
        //
        // No two silhouettes may share a name: the export builds filenames from
        // them, so a collision would silently overwrite a file. There are two ways
        // to honour that, and which one applies depends on WHO chose the name:
        //
        //   * a name the USER typed is rejected on collision — ask NameInUse before
        //     accepting it, so they get to decide what to do about it (this is what
        //     the commit window and the gallery's rename box do);
        //   * a name this store GENERATED is made unique silently by
        //     MakeUniqueName, because there is nobody to ask.
        //
        // The rule lives here rather than in each window so the three callers can't
        // drift into three different answers.

        /// <summary>Trims a typed name and caps its length.</summary>
        public static string CleanName(string name)
        {
            name = (name ?? "").Trim();
            return name.Length <= MaxNameLength ? name : name.Substring(0, MaxNameLength);
        }

        public static bool NameInUse(string name)
        {
            foreach (var outline in _outlines)
                if (string.Equals(outline.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// The given name if it is free, otherwise the same name with "_2", "_3", …
        /// appended until it is. A blank name comes back blank: what an empty name
        /// means is the caller's decision, not the store's.
        public static string MakeUniqueName(string name)
        {
            string clean = CleanName(name);
            if (clean.Length == 0 || !NameInUse(clean)) return clean;

            for (int n = 2; ; n++)
            {
                string candidate = WithSuffix(clean, "_" + n);
                if (!NameInUse(candidate)) return candidate;
            }
        }

        /// Suggested name: the specimen's name, an underscore, and the number this
        /// silhouette is for that specimen. Bumped past any name already taken, so
        /// the numbering stays flat rather than growing a "_3_2" tail.
        public static string SuggestName(string specimenName)
        {
            string root = string.IsNullOrWhiteSpace(specimenName) ? "specimen" : specimenName.Trim();

            int n = CountFor(specimenName) + 1;
            string candidate = WithSuffix(root, "_" + n);

            while (NameInUse(candidate))
                candidate = WithSuffix(root, "_" + (++n));

            return candidate;
        }

        // Appends a suffix, trimming the STEM rather than the suffix when the result
        // would run past the length cap. Clipping the suffix off instead would make
        // every candidate for a maximum-length name come back identical, and the
        // search loops above would never find a free name.
        private static string WithSuffix(string stem, string suffix)
        {
            int room = MaxNameLength - suffix.Length;
            if (room < 1) return CleanName(suffix);

            stem = (stem ?? "").Trim();
            if (stem.Length > room) stem = stem.Substring(0, room).TrimEnd();

            return stem + suffix;
        }

        // =====================
        // Store
        // =====================

        /// <summary>How many silhouettes are stored for one specimen.</summary>
        public static int CountFor(string specimenName)
        {
            int n = 0;
            foreach (var outline in _outlines)
                if (string.Equals(outline.SpecimenName, specimenName, StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>Every silhouette stored for one specimen, in store order.</summary>
        public static List<CommittedOutline> OutlinesFor(string specimenName)
        {
            var found = new List<CommittedOutline>();

            foreach (var outline in _outlines)
                if (string.Equals(outline.SpecimenName, specimenName, StringComparison.Ordinal))
                    found.Add(outline);

            return found;
        }

        /// Stores a copy of the outline under a free name. Returns the entry, or null
        /// when the outline is too small to be a shape. A caller offering the user a
        /// name box should reject collisions with NameInUse first: reaching here with
        /// one means it is quietly numbered off instead.
        public static CommittedOutline Add(string specimenName, string name, IEnumerable<Point> points)
        {
            if (points == null) return null;

            var copy = new List<Point>(points);
            PolylineGeometry.StripClosureDuplicate(copy);
            if (copy.Count < 3) return null;

            string clean = CleanName(name);
            if (clean.Length == 0) clean = SuggestName(specimenName);

            var entry = new CommittedOutline
            {
                SpecimenName = specimenName ?? "",
                Name = MakeUniqueName(clean),
                Points = copy
            };

            _outlines.Add(entry);
            return entry;
        }

        /// Renames a stored outline. Returns false when the name is blank or already
        /// taken — a typed name is refused rather than numbered off, so the user can
        /// see the clash and choose something else.
        public static bool Rename(CommittedOutline outline, string newName)
        {
            if (outline == null) return false;

            string clean = CleanName(newName);
            if (clean.Length == 0) return false;

            // Re-entering the same name, or only its casing, is not a collision.
            if (string.Equals(clean, outline.Name, StringComparison.OrdinalIgnoreCase))
            {
                outline.Name = clean;
                return true;
            }

            foreach (var other in _outlines)
            {
                if (ReferenceEquals(other, outline)) continue;
                if (string.Equals(other.Name, clean, StringComparison.OrdinalIgnoreCase)) return false;
            }

            outline.Name = clean;
            return true;
        }

        /// Adds a copy directly after the original: the same shape under the next free
        /// letter. Returns the copy, or null when the outline is not in the store.
        public static CommittedOutline Duplicate(CommittedOutline outline)
        {
            if (outline == null) return null;

            int at = _outlines.IndexOf(outline);
            if (at < 0) return null;

            var copy = new CommittedOutline
            {
                SpecimenName = outline.SpecimenName,
                Name = DuplicateNameFor(outline.Name),
                Points = new List<Point>(outline.Points)
            };

            _outlines.Insert(at + 1, copy);
            return copy;
        }

        /// The name a duplicate takes: the original's with " B" appended, or the next
        /// free letter after that. A name already ending in one of those letters
        /// continues its original's run rather than starting a nested one, so
        /// duplicating "Specimen 1_1 B" gives "Specimen 1_1 C".
        private static string DuplicateNameFor(string name)
        {
            string root = StripDuplicateSuffix(name);

            // The run this name belongs to, then — once its letters are used up — a
            // fresh run off the whole name.
            string candidate = FirstFreeLetter(root)
                            ?? (root == name ? null : FirstFreeLetter(name));

            if (candidate != null) return candidate;

            // Past the alphabet twice over. From here the names only have to stay
            // distinct.
            for (int n = 2; ; n++)
            {
                candidate = WithSuffix(name, " B" + n);
                if (!NameInUse(candidate)) return candidate;
            }
        }

        // First of "<root> B" … "<root> Z" nothing is using, or null when all are taken.
        private static string FirstFreeLetter(string root)
        {
            for (char suffix = 'B'; suffix <= 'Z'; suffix++)
            {
                string candidate = WithSuffix(root, " " + suffix);
                if (!NameInUse(candidate)) return candidate;
            }

            return null;
        }

        // Drops a trailing " B" … " Z", the suffix duplication itself adds. " A" is
        // left alone: nothing here produces one, so it belongs to whoever typed it.
        private static string StripDuplicateSuffix(string name)
        {
            if (name == null || name.Length < 3) return name;

            char last = name[name.Length - 1];
            if (name[name.Length - 2] != ' ' || last < 'B' || last > 'Z') return name;

            return name.Substring(0, name.Length - 2);
        }

        public static bool Remove(CommittedOutline outline) => _outlines.Remove(outline);

        /// <summary>Drops every silhouette stored for one specimen.</summary>
        public static void RemoveFor(string specimenName) =>
            _outlines.RemoveAll(o => string.Equals(o.SpecimenName, specimenName, StringComparison.Ordinal));

        /// <summary>Empties the store. Called alongside the other session resets.</summary>
        public static void Clear() => _outlines.Clear();
    }
}