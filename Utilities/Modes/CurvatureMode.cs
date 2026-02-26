using DinoLino.DataTypes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Shapes;

namespace DinoLino.Utilities.Modes
{
    internal class CurvatureMode : WorkMode
    {
        public int CurrentPointIndex = 0;
        public Line CurrentUILine = null;

        public Vector2 PointA;
        public Vector2 PointB;
        public Vector2 Midpoint;
        public Vector2 Orthoganal;
        public Vector2 PointC;
        public Vector2 Intersection;

        public Vector2 ACMid;
        public Vector2 BCMid;

        public double AngleResult;
        public double RatioResult;




    }



}
