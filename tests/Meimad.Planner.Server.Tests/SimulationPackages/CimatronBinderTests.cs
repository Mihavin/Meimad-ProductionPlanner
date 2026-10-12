using System.Runtime.InteropServices;
using Meimad.CimatronExporter;

namespace Meimad.Planner.Server.Tests.SimulationPackages;

/// <summary>
/// The exporter's vtable binder (Cimatron 2026.0 at Meimad refused IDispatch calls with TYPE_E_LIBNOTREGISTERED):
/// members found by name and argument count, trailing out parameters filled in, strings turned into enum values,
/// property getters, IDispatch-only interfaces skipped, and a real COM call through QueryInterface and the vtable.
/// </summary>
public sealed class CimatronBinderTests
{
    public enum UcsKind
    {
        Model = 0,
        Procedure = 1
    }

    public interface INcModelLike
    {
        void ExportStock(int procedureId, UcsKind ucsType, string path, out bool created);

        string Title { get; }
    }

    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IDispatchOnly
    {
        string Title { get; }
    }

    private sealed class Model : INcModelLike, IDispatchOnly
    {
        public (int Procedure, UcsKind Ucs, string Path)? Exported { get; private set; }

        public void ExportStock(int procedureId, UcsKind ucsType, string path, out bool created)
        {
            Exported = (procedureId, ucsType, path);
            created = true;
        }

        public string Title => "OP010.elt";
    }

    [Fact]
    public void A_member_is_called_by_name_with_out_parameters_filled_and_strings_turned_into_enums()
    {
        var binder = new CimatronBinder([typeof(INcModelLike), typeof(IDispatchOnly)]);
        var model = new Model();

        Assert.Equal(1, binder.InterfaceCount);
        Assert.True(binder.Has(model, "exportstock"));
        Assert.False(binder.Has(model, "PostByUcsToAllNcProcedures"));
        Assert.True(binder.TryInvoke(model, "ExportStock", [17, "procedure", @"C:\stock.stl"], out _, out var outputs));
        Assert.Equal((17, UcsKind.Procedure, @"C:\stock.stl"), model.Exported);
        Assert.Equal(true, outputs[3]);
        Assert.True(binder.TryInvoke(model, "Title", [], out var title, out _));
        Assert.Equal("OP010.elt", title);
        Assert.False(binder.TryInvoke(model, "ExportStock", [17], out _, out _)); // the UCS type and path are not out parameters
        var error = Assert.Throws<CimatronApiException>(() => binder.TryInvoke(model, "ExportStock", [17, "world", "x"], out _, out _));
        Assert.Contains("Model, Procedure", error.Message, StringComparison.Ordinal);
        Assert.False(binder.TryInvoke(new object(), "Title", [], out _, out _));
    }

    // IDispatch's own vtable, declared as a plain IUnknown interface: the binder must QueryInterface the COM object and
    // call the slot directly, as it does with Cimatron's interop interfaces.
    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDispatchVtable
    {
        void GetTypeInfoCount(out uint count);
    }

    [Fact]
    public void A_COM_object_is_called_through_its_vtable_after_QueryInterface()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dll = Path.Combine(Environment.SystemDirectory, "scrrun.dll");
        if (!File.Exists(dll)) return;
        var fileSystem = CimatronTypeLibrary.CreateFromDll(dll, dll, "FileSystemObject");
        var binder = new CimatronBinder([typeof(IDispatchVtable)]);

        Assert.True(binder.TryInvoke(fileSystem, "GetTypeInfoCount", [], out _, out var outputs));
        Assert.Equal(1u, outputs[0]);
        Assert.Contains(CimatronTypeLibrary.Describe(dll), line => line.Trim() == "ForReading = 1"); // enum values are listed
        Assert.Equal("registered", CimatronTypeLibrary.RegistrationStatus(dll));
    }
}
