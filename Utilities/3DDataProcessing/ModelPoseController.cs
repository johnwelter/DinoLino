using System;
using System.Windows.Media.Media3D;

namespace DinoLino
{
    /// Precise orientation control for the 3D pose overlay. Wraps the live
    /// QuaternionRotation3D that drives the on-screen model, so every method here updates
    /// the view immediately (the transform is in the visual tree and not frozen).
    ///
    /// All incremental rotations compose in the FIXED camera/world frame
    /// (extrinsic: delta * current). Combining active rotations q v q* gives
    /// (delta * current) = "apply current, then delta in world space", so the on-screen
    /// axes stay put no matter how the model is currently turned — the behavior users
    /// expect from CAD/mesh-viewer rotate buttons. (For local/body-frame rotation you'd
    /// instead post-multiply, current * delta; that's deliberately not used here.)
    ///
    /// Preset/camera assumption: the pose camera looks along -Z with +Y up and +X to the
    /// right. "Front" is the model's native orientation (identity); the others are
    /// canonical 90/180 turns from it. A specimen's true anatomical "front" depends on how
    /// the file was authored, so treat presets as starting points and fine-tune from there.
    public sealed class ModelPoseController
    {
        public enum Axis { Pitch, Yaw, Roll } // camera-frame X, Y, Z

        private static readonly Vector3D XAxis = new Vector3D(1, 0, 0);
        private static readonly Vector3D YAxis = new Vector3D(0, 1, 0);
        private static readonly Vector3D ZAxis = new Vector3D(0, 0, 1);

        private readonly QuaternionRotation3D _rotation;

        public ModelPoseController(QuaternionRotation3D rotation)
        {
            _rotation = rotation ?? throw new ArgumentNullException(nameof(rotation));
        }

        public Quaternion Orientation
        {
            get { return _rotation.Quaternion; }
            set { _rotation.Quaternion = value; }
        }

        // Rotate by an exact number of degrees about a camera-frame axis, in the fixed
        // world frame so the control feels identical regardless of current orientation.
        // WPF's Quaternion(Vector3D, double) takes the angle in DEGREES.
        public void Nudge(Axis axis, double degrees)
        {
            if (degrees == 0) return;
            Vector3D v = axis == Axis.Pitch ? XAxis : (axis == Axis.Yaw ? YAxis : ZAxis);
            var delta = new Quaternion(v, degrees);
            Orientation = delta * _rotation.Quaternion; // extrinsic: delta applied in world frame
        }

        // Absolute canonical views (see class remarks for the camera assumption).
        public void ViewFront() { Orientation = Quaternion.Identity; }
        public void ViewBack() { Orientation = new Quaternion(YAxis, 180); }
        public void ViewRight() { Orientation = new Quaternion(YAxis, -90); }
        public void ViewLeft() { Orientation = new Quaternion(YAxis, 90); }
        public void ViewTop() { Orientation = new Quaternion(XAxis, 90); }
        public void ViewBottom() { Orientation = new Quaternion(XAxis, -90); }
    }
}