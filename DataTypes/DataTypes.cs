using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Serialization;

namespace DinoLino.DataTypes
{
    public class Vector3
    {
        private double x;
        private double y;
        private double z;

        public double X => x;
        public double Y => y;
        public double Z => z;

        public Vector3()
        {
            x = 0;
            y = 0;
            z = 0;
        }

        public Vector3(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator ^(Vector3 a, Vector3 b) => new Vector3((a.y * b.z) - (a.z * b.y), (a.z * b.x) - (a.x * b.z), (a.x * b.y) - (a.y * b.x));

        public static Vector3 operator *(Vector3 a, double b) => new Vector3(a.x * b, a.y * b, a.z * b);
        public static Vector3 operator *(Vector3 a, Vector3 b) => new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);

        public static double operator |(Vector3 a, Vector3 b) => a.x*b.x + a.y*b.y + a.z*b.z;

        public void Normalize()
        {
            Scale(1 / Magnitude());
        }

        public double Magnitude()
        {
            return Math.Sqrt(this | this);
        }

        public void Scale(double scale)
        {
            x *= scale;
            y *= scale;
            z *= scale;
        }

        public void Scale(Vector3 b)
        {
            x *= b.x;
            y *= b.y;
            z *= b.z;
        }

        public Vector2 ToVector2()
        {
            return new Vector2(x, y);
        }

    }

    public class Vector2
    {
        private double x;
        private double y;

        public double X => x;
        public double Y => y;

        public Vector2()
        {
            x = 0;
            y = 0;
        }

        public Vector2(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        public Vector2(Point point)
        {
            x = point.X;
            y = point.Y;
        }

        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static double operator ^(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
        public static double operator |(Vector2 a, Vector2 b) => a.x * b.x + a.y * b.y;
        public static Vector2 operator *(Vector2 a, double b) => new Vector2(a.x * b, a.y * b);
        public static Vector2 operator *(Vector2 a, Vector2 b) => new Vector2(a.x * b.x, a.y * b.y);

        public static double AngleBetween(Vector2 a, Vector2 b)
        {
            //same as the windows one for now, just want to ewmove the need for a different Vector class 
            double cross = a ^ b;
            double dot = a | b;
            return Math.Atan2(cross, dot) * (180.0/Math.PI); 

        }
        public void Normalize()
        {
            Scale(1 / Magnitude());
        }

        public double Magnitude()
        {
            return Math.Sqrt(this | this);
        }

        public void Scale(double scale)
        {
            x *= scale;
            y *= scale;
        }

        public void Scale(Vector2 b)
        {
            x *= b.x;
            y *= b.y;
        }
    }
}
