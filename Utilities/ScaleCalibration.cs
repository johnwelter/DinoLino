using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DinoLino.Utilities
{
    // Holds the image scale calibration: how many real-world units one canvas pixel
    // represents, plus the unit label. Shared by every WorkMode so they can convert
    // pixel measurements (length, area) into real units. The image is never resized.
    public class ScaleCalibration : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private double _unitsPerPixel;
        private string _unit;

        public bool IsCalibrated => _unitsPerPixel > 0 && !string.IsNullOrEmpty(_unit);
        public string Unit => _unit;
        public double UnitsPerPixel => _unitsPerPixel;

        // Calibrate from a drawn line: its pixel length and the real length/unit entered.
        public void SetFromLine(double pixelLength, double realLength, string unit)
        {
            if (pixelLength <= 1e-6 || realLength <= 0) { Clear(); return; }
            _unitsPerPixel = realLength / pixelLength;
            _unit = unit;
            NotifyAll();
        }

        public void Clear()
        {
            _unitsPerPixel = 0;
            _unit = null;
            NotifyAll();
        }

        public double ToUnits(double pixelLength) => pixelLength * _unitsPerPixel;
        public double ToUnitsArea(double pixelArea) => pixelArea * _unitsPerPixel * _unitsPerPixel;

        public string StatusText => IsCalibrated
            ? ""
            : "Scale: not calibrated";

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(IsCalibrated));
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(UnitsPerPixel));
            OnPropertyChanged(nameof(StatusText));
        }
    }
}