using System;
using System.Linq;
using System.Reflection;

var assembly = typeof(OCCSharp.TopoDS_Shape).Assembly;
foreach (var type in assembly.GetTypes().Where(type =>
             type.Name.Contains("STEPControl_Reader")
             || type.Name.Contains("BRepMesh_IncrementalMesh")
             || type.Name.Contains("TopExp_Explorer")
             || type.Name.Contains("BRep_Tool")
             || type.Name.Contains("Poly_Triangulation")
             || type.Name.Contains("TopLoc_Location")
             || type.Name.Contains("TopoDS_Face")
             || type.Name.Contains("Poly_Triangle")
             || type.Name.Contains("XSControl_Reader")
             || type.Name.Contains("BRepPrimAPI_MakeBox")
             || type.Name.Contains("STEPControl_Writer")
             || type.Name is "TopAbs_ShapeEnum" or "TopAbs_Orientation" or "IFSelect_ReturnStatus"
             || type.Name is "TopoDS" or "TopoDS_Shape" or "gp_Pnt" or "gp_Trsf" or "Message_ProgressRange"))
{
    Console.WriteLine($"TYPE {type.FullName}");
    if (type.IsEnum) Console.WriteLine($"  VALUES {string.Join(", ", Enum.GetNames(type))}");
    foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  {member}");
}
