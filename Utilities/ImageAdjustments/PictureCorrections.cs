using System;

namespace DinoLino.Utilities
{
    /// <summary>
    /// One specimen's picture corrections, in the percentages the adjustment dialog
    /// works in. Immutable, and clamped on the way in, so a stored value can never be
    /// one the sliders could not have produced.
    /// </summary>
    public readonly struct CorrectionState : IEquatable<CorrectionState>
    {
        /// <summary>Largest correction the sliders offer, in either direction.</summary>
        public const double Limit = 100.0;

        public double Contrast { get; }
        public double Brightness { get; }
        public double Saturation { get; }

        public CorrectionState(double contrast, double brightness, double saturation)
        {
            Contrast = Clamp(contrast);
            Brightness = Clamp(brightness);
            Saturation = Clamp(saturation);
        }

        /// <summary>An uncorrected specimen, which is also the default value.</summary>
        public static CorrectionState None => default;

        /// <summary>True when at least one correction would change the image.</summary>
        public bool IsSet => Contrast != 0 || Brightness != 0 || Saturation != 0;

        private static double Clamp(double value)
        {
            if (double.IsNaN(value)) return 0;
            return Math.Max(-Limit, Math.Min(Limit, value));
        }

        public bool Equals(CorrectionState other) =>
            Contrast == other.Contrast
            && Brightness == other.Brightness
            && Saturation == other.Saturation;

        public override bool Equals(object obj) => obj is CorrectionState c && Equals(c);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Contrast.GetHashCode();
                hash = (hash * 397) ^ Brightness.GetHashCode();
                hash = (hash * 397) ^ Saturation.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(CorrectionState a, CorrectionState b) => a.Equals(b);
        public static bool operator !=(CorrectionState a, CorrectionState b) => !a.Equals(b);
    }

    /// <summary>
    /// The loaded specimen's picture corrections. Corrections belong to the specimen
    /// rather than to the session, so a new image starts uncorrected and a specimen
    /// returned to brings its own sliders back with it.
    /// </summary>
    public class PictureCorrections
    {
        private CorrectionState _state = CorrectionState.None;
        private Specimen _owner;

        public double Contrast => _state.Contrast;
        public double Brightness => _state.Brightness;
        public double Saturation => _state.Saturation;

        /// <summary>True when at least one slider sits away from zero.</summary>
        public bool IsSet => _state.IsSet;

        /// The live corrections. Assigning writes through to the bound specimen, so
        /// what is on screen and what is stored cannot drift apart.
        public CorrectionState State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                if (_owner != null) _owner.Corrections = value;
            }
        }

        /// Makes a specimen's stored corrections the live ones and the destination for
        /// every later change. Pass null when no specimen is loaded.
        public void BindTo(Specimen specimen)
        {
            _owner = specimen;
            _state = specimen?.Corrections ?? CorrectionState.None;
        }

        /// <summary>Records the values the adjustment dialog is showing.</summary>
        public void Set(double contrast, double brightness, double saturation)
            => State = new CorrectionState(contrast, brightness, saturation);

        public void Clear() => State = CorrectionState.None;
    }
}
