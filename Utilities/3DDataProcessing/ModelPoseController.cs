using System;
using System.Windows.Media.Media3D;

namespace DinoLino
{
    /// <summary>
    /// Controls the pose of the 3D model overlay.
    /// </summary>
    /// <remarks>
    /// Rotations are applied in the fixed camera/world frame, so each nudge behaves the same
    /// no matter how the model is already oriented.
    /// </remarks>
    public sealed class ModelPoseController
    {
        /// <summary>
        /// Axes used for incremental rotation commands.
        /// </summary>
        public enum Axis { Pitch, Yaw, Roll }

        // Camera-frame axes used by the preset views and nudge controls.
        private static readonly Vector3D XAxis = new Vector3D(1, 0, 0);
        private static readonly Vector3D YAxis = new Vector3D(0, 1, 0);
        private static readonly Vector3D ZAxis = new Vector3D(0, 0, 1);

        private readonly QuaternionRotation3D _rotation;

        public ModelPoseController(QuaternionRotation3D rotation)
        {
            _rotation = rotation ?? throw new ArgumentNullException(nameof(rotation));
        }

        /// <summary>
        /// Gets or sets the current model orientation.
        /// </summary>
        public Quaternion Orientation
        {
            get { return _rotation.Quaternion; }
            set { _rotation.Quaternion = value; }
        }

        // =====================
        // Incremental rotation
        // =====================

        /// <summary>
        /// Rotates the model by a fixed angle around a camera-frame axis.
        /// </summary>
        public void Nudge(Axis axis, double degrees)
        {
            if (degrees == 0) return;

            Vector3D v = axis == Axis.Pitch ? XAxis : (axis == Axis.Yaw ? YAxis : ZAxis);
            var delta = new Quaternion(v, degrees);

            // Pre-multiply so the rotation is applied in world space.
            Orientation = delta * _rotation.Quaternion;
        }

        // =====================
        // Preset views
        // =====================

        /// <summary>
        /// Restores the default front-facing view.
        /// </summary>
        public void ViewFront() { Orientation = Quaternion.Identity; }

        /// <summary>
        /// Rotates to the opposite side of the model.
        /// </summary>
        public void ViewBack() { Orientation = new Quaternion(YAxis, 180); }

        /// <summary>
        /// Rotates to the model's right side.
        /// </summary>
        public void ViewRight() { Orientation = new Quaternion(YAxis, -90); }

        /// <summary>
        /// Rotates to the model's left side.
        /// </summary>
        public void ViewLeft() { Orientation = new Quaternion(YAxis, 90); }

        /// <summary>
        /// Rotates to the top-down view.
        /// </summary>
        public void ViewTop() { Orientation = new Quaternion(XAxis, 90); }

        /// <summary>
        /// Rotates to the bottom-up view.
        /// </summary>
        public void ViewBottom() { Orientation = new Quaternion(XAxis, -90); }
    }
}