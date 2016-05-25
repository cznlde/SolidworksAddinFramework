﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LanguageExt;
using LanguageExt.Parsec;
using LanguageExt.UnitsOfMeasure;
using static LanguageExt.Prelude;
using SolidworksAddinFramework.Events;
using SolidworksAddinFramework.Geometry;
using SolidworksAddinFramework.OpenGl;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using Char = LanguageExt.Parsec.Char;
using static LanguageExt.Prelude;
using static LanguageExt.Parsec.Prim;
using static LanguageExt.Parsec.Char;
using static LanguageExt.Parsec.Expr;
using static LanguageExt.Parsec.Token;

namespace SolidworksAddinFramework
{
    public static class ModelDocExtensions
    {
        public static IBody2[] GetBodiesTs(this IModelDoc2 doc, swBodyType_e type = swBodyType_e.swSolidBody,
            bool visibleOnly = false)
        {
            var part = (IPartDoc) doc;
            var objects = (object[]) part.GetBodies2((int) type, visibleOnly);
            return objects?.Cast<IBody2>().ToArray() ?? new IBody2[0];
        }

        public static IDisposable CloseDisposable(this IModelDoc2 @this)
        {
            return Disposable.Create(@this.Close);
        }

        public static void Using(this IModelDoc2 doc, ISldWorks sldWorks, Action<IModelDoc2> run)
        {
            doc.Using(m => sldWorks.CloseDoc(doc.GetTitle()), run);
        }
        public static Task Using(this IModelDoc2 doc, ISldWorks sldWorks, Func<IModelDoc2, Task> run)
        {
            return doc.Using(m => sldWorks.CloseDoc(doc.GetTitle()), run);
        }


        /// <summary>
        /// Get all reference planes from the model
        /// </summary>
        /// <param name="doc"></param>
        /// <returns></returns>
        public static IEnumerable<IRefPlane> GetPlanes(this IModelDoc2 doc)
        {
            return doc.FeatureManager
                .GetFeatures(false)
                .CastArray<IFeature>()
                .Select(f => f.GetSpecificFeature2() as IRefPlane);
        }

        public static IObservable<IReadOnlyList<object>> SelectionObservable(this IModelDoc2 modelDoc, 
            Func<swSelectType_e, int, bool> filter = null)
        {
            var sm = modelDoc
                .SelectionManager
                .DirectCast<ISelectionMgr>();

            filter = filter ?? ((type,mark)=> true);
            return modelDoc
                .UserSelectionPostNotifyObservable()
                .Select(e => sm.GetSelectedObjects(filter));
        }

        public static IDisposable PushSelections(this IModelDoc2 doc, object model)
        {
            var selectionManager = (ISelectionMgr)doc.SelectionManager;
            var revert = selectionManager.DeselectAllUndoable();

            var selections = SelectionDataExtensions.GetSelectionsFromModel(model);

            var selectionMgr = (ISelectionMgr) doc.SelectionManager;
            selections
                .GroupBy(p => p.Mark)
                .Select(p => new { Mark = p.Key, Objects = p.SelectMany(selectionData => selectionData.GetObjects(doc)).ToArray() })
                .Where(p => p.Objects.Length > 0)
                .ForEach(o =>
                {
                    var selectData = selectionMgr.CreateSelectData();
                    selectData.Mark = o.Mark;

                    var count = doc.Extension.MultiSelect2(ComWangling.ObjectArrayToDispatchWrapper(o.Objects), true, selectData);
                });

            return revert;
        }

        public static IEnumerable<object> GetSelectedObjectsFromModel(this IModelDoc2 doc, object model)
        {
            return SelectionDataExtensions.GetSelectionsFromModel(model)
                .SelectMany(data => data.GetObjects(doc));
        }

        public static Tuple<object[], int[], IView[]> GetMacroFeatureDataSelectionInfo(this IModelDoc2 doc, object model)
        {
            var view = (IView) (doc as IDrawingDoc)?.GetFirstView();

            var selections = SelectionDataExtensions.GetSelectionsFromModel(model).ToList();
            var selectedObjects = selections.SelectMany(s => s.GetObjects(doc)).ToArray();
            var marks = selections.SelectMany(s => Enumerable.Repeat(s.Mark, s.ObjectIds.Count)).ToArray();
            var views = selections.SelectMany(s => Enumerable.Repeat(view, s.ObjectIds.Count)).ToArray();
            return Tuple(selectedObjects, marks, views);
        }

        /// <summary>
        /// Doesn't work when intersecting with wire bodies. 
        /// </summary>
        /// <param name="modelDoc"></param>
        /// <param name="ray"></param>
        /// <param name="bodies"></param>
        /// <param name="hitRadius"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static List<RayIntersection> GetRayIntersections(this IModelDoc2 modelDoc, PointDirection3 ray, IBody2[] bodies, double hitRadius, double offset)
        {
            var icount = modelDoc.RayIntersections
                (BodiesIn: bodies
                , BasePointsIn: ray.Point.ToDoubles()
                , VectorsIn: ray.Direction.ToDoubles()
                , Options: (int) (swRayPtsOpts_e.swRayPtsOptsENTRY_EXIT | swRayPtsOpts_e.swRayPtsOptsNORMALS |
                                  swRayPtsOpts_e.swRayPtsOptsTOPOLS | swRayPtsOpts_e.swRayPtsOptsUNBLOCK)
                , HitRadius: hitRadius
                , Offset: offset);
            var result = modelDoc.GetRayIntersectionsPoints().CastArray<double>();

            const int fields = 9;
            return Enumerable.Range(0, icount)
                .Select(i =>
                {
                    var baseOffset = i * fields;

                    var bodyIndex = result[baseOffset + 0];
                    var rayIndex = result[baseOffset + 1];
                    var intersectionType = result[baseOffset + 2];
                    var x = result[baseOffset + 3];
                    var y = result[baseOffset + 4];
                    var z = result[baseOffset + 5];
                    var nx = result[baseOffset + 6];
                    var ny = result[baseOffset + 7];
                    var nz = result[baseOffset + 8];

                    return new RayIntersection(
                        bodies[(int)bodyIndex],
                        (int)rayIndex,
                        (swRayPtsResults_e)intersectionType,
                        new [] { x, y, z }.ToVector3(),
                        new[] { nx, ny, nz }.ToVector3()
                        );
                }).ToList();
        }

        public class RayIntersection
        {
            public RayIntersection(IBody2 body, int rayIndex, swRayPtsResults_e intersectionType, Vector3 hitPoint, Vector3 normals)
            {
                Body = body;
                RayIndex = rayIndex;
                IntersectionType = intersectionType;
                HitPoint = hitPoint;
                Normals = normals;
            }

            public IBody2 Body { get; }
            public int RayIndex { get; }
            public swRayPtsResults_e IntersectionType { get; }
            public Vector3 HitPoint { get; }
            public Vector3 Normals { get; }

        }

        /// <summary>
        /// From a given X,Y screen coordinate return the model
        /// coordinates and the direction of looking.
        /// </summary>
        /// <param name="doc"></param>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        public static PointDirection3 ScreenToView(this IModelDoc2 doc, int x, int y)
        {
            var math = SwAddinBase.Active.Math;
            var view = doc.ActiveView.DirectCast<IModelView>();
            var t = view.Transform.Inverse().DirectCast<MathTransform>();

            var eye = math.Point(new[] {x, y, 0.0});

            var look = math.ZAxis().DirectCast<MathVector>();

            eye = eye.MultiplyTransformTs(t);
            look = look.MultiplyTransformTs(t);

            return new PointDirection3(Vector3Extensions.ToVector3(eye), look.ToVector3().Unit());
        }

        public static Vector2 ViewToScreen(this IModelDoc2 doc, Vector3 point)
        {
            var math = SwAddinBase.Active.Math;
            var view = doc.ActiveView.DirectCast<IModelView>();
            var t = view.Transform.DirectCast<MathTransform>();
            var mathPoint = point.ToSwMathPoint(math);
            mathPoint = mathPoint.MultiplyTransformTs(t);
            var v3 = mathPoint.ToVector3();
            return new Vector2(v3.X, v3.Y);
        }


        public enum EquationsDimensionType
        {
            Length,
            Angle
        }

        /// <summary>
        /// Set a global variable as you would find in the equation manager.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="doc"></param>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public static bool SetGlobal(this IModelDoc2 doc, string name, double value)
        {
            var swEqnMgr = doc.GetEquationMgr();
            for (int i = 0; i < swEqnMgr.GetCount(); i++)
            {
                if (
                    (from q in TrySet(swEqnMgr.Equation[i], name, value)
                        let _ = swEqnMgr.Equation[i] = q
                        select Unit.Default).IsSome)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get all the globals found in the equation manager into a dictionary.
        /// </summary>
        /// <param name="doc"></param>
        /// <returns></returns>
        public static Dictionary<string, string> GetGlobals(this IModelDoc2 doc )
        {
            var swEqnMgr = doc.GetEquationMgr();
            return Enumerable.Range(0, swEqnMgr.GetCount()).Select
                (i =>
                {
                    var str = swEqnMgr.Equation[i];
                    var nv = str.Split('=').Select(s => s.Trim('"', ' ')).ToList();
                    var kvp = new
                    {
                        name = nv[0],
                        value = nv[1]
                    };

                    return kvp;

                }).ToDictionary(v => v.name, v => v.value);
        }

        public static Option<string> GetGlobal(this IModelDoc2 doc, string name)
        {
            var globals = doc.GetGlobals();
            if (globals.ContainsKey((name)))
                return globals[name];
            return None;
        }

        /// <summary>
        /// Try to set the equation with the form " $name = $value ". If the 
        /// name does not equal $name then the tryset will fail.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="eq"></param>
        /// <param name="name"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        private static Option<string> TrySet<T>(string eq, string name, T value)
        {
            var vsplit = eq.Split('=').Select(sub=>sub.Trim()).ToList();
            Option<string> r = None;
            if (vsplit[0] == $@"""{name}""")
            {
                r = Some(eq.Replace(vsplit[1], value.ToString()));
            }
            return r;
        }


        static Parser<T> DeOpt<T>(Parser<Option<T>> p)
            => from i in p from r in i.Match(result,()=> failure<T>($"Could not parse {typeof(T).Name}")) select r;

        static Parser<T> DeOpt<T>(Option<T> p) => p.Match(result, () => failure<T>($"Could not parse {typeof(T).Name}"));


        public static ParserResult<double> TryParseDouble(string value)
        {

            var intLiteral = DeOpt(asInteger(many1(digit)));

            var floatLiteral = from i0 in intLiteral
                from p in ch('.')
                from i1 in intLiteral
                from r in DeOpt(parseDouble($"{i0}.{i1}"))
                select r;

            var expFloatLiteral = from fl in floatLiteral
                from e in choice(ch('e'), ch('E'))
                from d in intLiteral
                from r in DeOpt(parseDouble($"{fl}E{d}"))
                select r;

            var f = choice
                    ( attempt(expFloatLiteral) 
                    , attempt(floatLiteral)
                    , from i in intLiteral select (double) i
                    );

            return f.Parse(value);

        }

        /// <summary>
        /// Solidworks floating point parser for doubles as used in the equation manager.
        /// </summary>
        public static Parser<double> SwDoubleParser
        {
            get
            {
                var optSign = optionOrElse("", from x in oneOf("+-") select x.ToString());

                return from si in optSign
                    from nu in asString(many(digit))
                    from frac in optionOrElse("",
                        from pt in ch('.') 
                        from fr in asString(many(digit))
                        from ex in optionOrElse("",
                            from e in oneOf("eE")
                            from s in optSign
                            from n in asString(many1(digit))
                            select $"{e}{s}{n}"
                            )
                        select $"{pt}{fr}{ex}")
                    let all = $"{si}{nu}{frac}"
                    let opt = parseDouble(all)
                    from res in opt.Match(
                        result,
                        () => failure<double>("Invalid floating point value")
                        )
                    select res;
            }
        }

        public static Parser<SwEq> SwEwParser
        {
            get
            {
                var nameParser = from a in letter
                                 from b in asString(many(alphaNum))
                                 select a + b;

                var idParser = nameParser.doubleQuoted().label("Id Parser").skipWhite();
                var valueParser = SwDoubleParser.label("Value parser").skipWhite();
                var eq = ch('=').skipWhite();
                var unitsParser = asString(many1(letter)).label("Unit parser").skipWhite();

                return from id in idParser.skip(eq)
                       from val in valueParser
                       from units in unitsParser
                       select new SwEq(id, val, units);

            }
        }
    }

    public static class ParserExt
    {
    }

    public class SwEq
    {
        public string Id { get; }
        public double Val { get; }
        public string Units { get; }

        public SwEq(string id, double val, string units )
        {
            Id = id;
            Val = val;
            switch (units)
            {
                case "cm": // centimeters
                    Val = val.Centimetres().Metres;
                    break;
                case "ft": // feet
                    Val = val.Feet().Metres;
                    break;
                case "in": // inches
                    Val = val.Inches().Metres;
                    break;
                case "m":  // meters
                    Val = val.Metres().Metres;
                    break;
                case "uin":// micro inches
                    Val = (val/1e9).Inches().Metres;
                    break;
                case "um": // micro meteres
                    Val = val.Micrometres().Metres;
                    break;
                case "mil": // thousanth of an inch
                    Val = (val/1e6).Inches().Metres;
                    break;
                case "mm": // millimeteres
                    Val = val.Millimetres().Metres;
                    break;
                case "nm": // nanometers
                    Val = val.Nanometres().Metres;
                    break;
                case "deg": // degrees
                    Val = val*Math.PI/180;
                    break;
                case "rad": // radians
                    Val = val;
                    break;
                case "undefined":
                default:
                    throw new Exception($"Not supported {units}");
                    
            }
            Units = units;
        }

        public override string ToString() => $@"""{Id}""={ValUnits}";

        public string ValUnits
        {
            get
            {

                var scaled = 0.0;
                switch (Units)
                {
                    case "cm": // centimeters
                        scaled = Val.Metres().Centimetres;
                        break;
                    case "ft": // feet
                        scaled = Val.Metres().Feet;
                        break;
                    case "in": // inches
                        scaled = Val.Metres().Inches;
                        break;
                    case "m":  // meters
                        scaled = Val.Metres().Metres;
                        break;
                    case "uin":// micro inches
                        scaled = Val.Metres().Inches * 1e9;
                        break;
                    case "um": // micro meteres
                        scaled = Val.Metres().Micrometres;
                        break;
                    case "mil": // thousanth of an inch
                        scaled = Val.Metres().Inches * 1e6;
                        break;
                    case "mm": // millimeteres
                        scaled = Val.Metres().Millimetres;
                        break;
                    case "nm": // nanometers
                        scaled = Val.Metres().Nanometres;
                        break;
                    case "deg": // degrees
                        scaled = Val * 180 / Math.PI;
                        break;
                    case "rad": // radians
                        scaled = Val;
                        break;
                    default:
                        throw new Exception($"Not supported {Units}");

                }

                return $"{scaled}{Units}";
            }

        }
    }
}
