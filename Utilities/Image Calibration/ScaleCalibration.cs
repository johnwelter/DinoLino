using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DinoLino.Utilities
{
    /// <summary>
    /// One specimen's pixel-to-unit conversion. Immutable, and constructible only
    /// from a usable measurement, so a set state always carries a valid ratio.
    /// </summary>
    public readonly struct ScaleState : IEquatable<ScaleState>
    {
        /// <summary>Shortest calibration line, in image pixels, worth trusting.</summary>
        public const double MinimumLinePixels = 2.0;

        public bool IsSet { get; }

        /// Real-world units in one image pixel. Image pixels rather than canvas
        /// pixels because the canvas coordinate space depends on window size and on
        /// the dimensions of whichever image is displayed.
        public double UnitsPerImagePixel { get; }

        public string Unit { get; }

        private ScaleState(double unitsPerImagePixel, string unit)
        {
            IsSet = true;
            UnitsPerImagePixel = unitsPerImagePixel;
            Unit = unit;
        }

        /// <summary>An uncalibrated specimen, which is also the default value.</summary>
        public static ScaleState None => default;

        /// The calibration a measured line describes, or None when the line is too
        /// short or the entered length is not positive.
        public static ScaleState FromLine(double imagePixelLength, double realLength, string unit)
        {
            if (imagePixelLength < MinimumLinePixels) return None;
            if (realLength <= 0 || double.IsNaN(realLength) || double.IsInfinity(realLength)) return None;
            if (string.IsNullOrEmpty(unit)) return None;

            return new ScaleState(realLength / imagePixelLength, unit);
        }

        public bool Equals(ScaleState other) =>
            IsSet == other.IsSet
            && UnitsPerImagePixel == other.UnitsPerImagePixel
            && Unit == other.Unit;

        public override bool Equals(object obj) => obj is ScaleState s && Equals(s);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = IsSet.GetHashCode();
                hash = (hash * 397) ^ UnitsPerImagePixel.GetHashCode();
                hash = (hash * 397) ^ (Unit?.GetHashCode() ?? 0);
                return hash;
            }
        }

        public static bool operator ==(ScaleState a, ScaleState b) => a.Equals(b);
        public static bool operator !=(ScaleState a, ScaleState b) => !a.Equals(b);
    }

    /// <summary>
    /// The loaded specimen's scale calibration, and the conversions every mode uses
    /// to turn measurements into real-world units.
    /// </summary>
    public class ScaleCalibration : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private ScaleState _state = ScaleState.None;
        private Specimen _owner;

        // How many canvas pixels one image pixel currently occupies on screen. The
        // host keeps this current as the workspace is laid out and resized.
        private double _canvasPerImagePixel = 1.0;

        // =====================
        // Calibration state
        // =====================

        /// <summary>True when a valid pixel-to-unit conversion has been set.</summary>
        public bool IsCalibrated => _state.IsSet;

        /// <summary>Unit label chosen by the user, such as mm or µm.</summary>
        public string Unit => _state.Unit;

        /// <summary>Real-world units represented by one canvas pixel right now.</summary>
        public double UnitsPerPixel =>
            _state.IsSet ? _state.UnitsPerImagePixel / _canvasPerImagePixel : 0;

        /// The live calibration. Assigning writes through to the bound specimen, so
        /// what is displayed and what is stored cannot drift apart.
        public ScaleState State
        {
            get => _state;
            set
            {
                if (_state == value) return;
                _state = value;
                if (_owner != null) _owner.Calibration = value;
                NotifyAll();
            }
        }

        /// Makes a specimen's stored calibration the live one and the destination for
        /// every later change. Pass null when no specimen is loaded.
        public void BindTo(Specimen specimen)
        {
            _owner = specimen;
            _state = specimen?.Calibration ?? ScaleState.None;
            NotifyAll();
        }

        /// Records how large the image is being drawn, so canvas measurements can be
        /// converted whatever the window size. Returns true when the ratio moved,
        /// which is the host's cue to refresh anything showing a scaled value.
        public bool UpdateViewScale(double canvasPixelsPerImagePixel)
        {
            if (canvasPixelsPerImagePixel <= 0
                || double.IsNaN(canvasPixelsPerImagePixel)
                || double.IsInfinity(canvasPixelsPerImagePixel))
                return false;

            if (canvasPixelsPerImagePixel == _canvasPerImagePixel) return false;

            _canvasPerImagePixel = canvasPixelsPerImagePixel;
            OnPropertyChanged(nameof(UnitsPerPixel));
            return true;
        }

        /// <summary>Sets the calibration from a line measured in canvas pixels.</summary>
        public void SetFromLine(double canvasPixelLength, double realLength, string unit)
            => State = ScaleState.FromLine(canvasPixelLength / _canvasPerImagePixel, realLength, unit);

        public void Clear() => State = ScaleState.None;

        // =====================
        // Canvas to image space
        // =====================

        /// Converts a canvas-space length into image pixels, the unit every stored
        /// measurement uses. Image pixels do not move when the window is resized, so
        /// a value stored this way yields the same real-world number whenever it is
        /// read.
        public double CanvasToImageLength(double canvasLength) => canvasLength / _canvasPerImagePixel;

        /// <summary>Converts a canvas-space area into square image pixels.</summary>
        public double CanvasToImageArea(double canvasArea) =>
            canvasArea / (_canvasPerImagePixel * _canvasPerImagePixel);

        // =====================
        // Conversion helpers
        // =====================

        /// <summary>Converts a canvas-space length into calibrated units.</summary>
        public double ToUnits(double canvasLength) => canvasLength * UnitsPerPixel;

        /// <summary>Converts a canvas-space area into calibrated square units.</summary>
        public double ToUnitsArea(double canvasArea)
        {
            double perPixel = UnitsPerPixel;
            return canvasArea * perPixel * perPixel;
        }

        /// Converts a length already measured in image pixels, skipping the view
        /// ratio. Preferred for anything stored and converted later.
        public double ToUnitsFromImage(double imagePixelLength) =>
            imagePixelLength * _state.UnitsPerImagePixel;

        /// <summary>Converts an area already measured in image pixels.</summary>
        public double ToUnitsAreaFromImage(double imagePixelArea) =>
            imagePixelArea * _state.UnitsPerImagePixel * _state.UnitsPerImagePixel;

        public string StatusText => IsCalibrated ? "" : "Scale: not calibrated";

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsCalibrated));
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(UnitsPerPixel));
            OnPropertyChanged(nameof(StatusText));
        }
    }
}