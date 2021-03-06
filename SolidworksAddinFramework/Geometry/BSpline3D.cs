using System;
using System.Diagnostics;
using System.Linq;
using System.DoubleNumerics;
using JetBrains.Annotations;
using SolidWorks.Interop.sldworks;
using Weingartner.WeinCad.Interfaces;
using Weingartner.WeinCad.Interfaces.Math;

namespace SolidworksAddinFramework.Geometry
{
    public class BSpline3D : BSpline<Vector4>
    {
        public BSpline3D([NotNull] Vector4[] controlPoints, [NotNull] double[] knotVectorU, int order, bool isClosed, bool isRational) : base(controlPoints, knotVectorU, order, isClosed, isRational)
        {
            if(!IsRational)
            {
                Debug.Assert(controlPoints.All(c => Math.Abs(c.W - 1.0) < 1e-9));
            }
        }
        public ICurve ToCurve()
        {
            var propsDouble = PropsDouble;
            var knots = KnotVectorU;
            var ctrlPtCoords = ControlPoints.SelectMany(p => p.ToDoubles()).ToArray();
            return (ICurve) SwAddinBase.Active.Modeler.CreateBsplineCurve( propsDouble, knots, ctrlPtCoords);
        }

        public double[] ToDoubles(Vector4 t)
        {
            return t.ToDoubles();
        }

        public override int Dimension => IsRational ? 4 : 3;
    }
}