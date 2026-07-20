using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DinoLino.Utilities
{
    /// <summary>
    /// Stores the current scale calibration for the active image.
    /// </summary>
    public class ScaleCalibration : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // =====================
        // Calibration state
        // =====================

        private double _unitsPerPixel;
        private string _unit;

        /// <summary>
        /// True when a valid pixel-to-unit conversion has been set.
        /// </summary>
        public bool IsCalibrated => _unitsPerPixel > 0 && !string.IsNullOrEmpty(_unit);

        /// <summary>
        /// Unit label entered by the user, such as mm or in.
        /// </summary>
        public string Unit => _unit;

        /// <summary>
        /// Real-world units represented by one canvas pixel.
        /// </summary>
        public double UnitsPerPixel => _unitsPerPixel;

        // =====================
        // Calibration updates
        // =====================

        /// <summary>
        /// Sets the calibration from a measured line and its real-world length.
        /// </summary>
        public void SetFromLine(double pixelLength, double realLength, string unit)
        {
            if (pixelLength <= 1e-6 || realLength <= 0)
            {
                Clear();
                return;
            }

            _unitsPerPixel = realLength / pixelLength;
            _unit = unit;
            NotifyAll();
        }

        /// <summary>
        /// Clears the current calibration.
        /// </summary>
        public void Clear()
        {
            _unitsPerPixel = 0;
            _unit = null;
            NotifyAll();
        }

        // =====================
        // Conversion helpers
        // =====================

        /// <summary>
        /// Converts a pixel length into calibrated units.
        /// </summary>
        public double ToUnits(double pixelLength) => pixelLength * _unitsPerPixel;

        /// <summary>
        /// Converts a pixel area into calibrated square units.
        /// </summary>
        public double ToUnitsArea(double pixelArea) => pixelArea * _unitsPerPixel * _unitsPerPixel;

        /// <summary>
        /// Status text shown when no calibration is available.
        /// </summary>
        public string StatusText => IsCalibrated ? "" : "Scale: not calibrated";

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(IsCalibrated));
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(UnitsPerPixel));
            OnPropertyChanged(nameof(StatusText));
        }
    }
}