using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Meimad.CimatronExporter;

/// <summary>
/// Calls Cimatron API members through the interface vtables that Cimatron's own interop assemblies
/// (<c>interop.CimAppAccess.dll</c>, <c>interop.CimNcAPI.dll</c>, … in the Program folder) declare, instead of IDispatch.
/// IDispatch on Cimatron's objects needs Cimatron's type libraries registered (Cimatron 2026.0 at Meimad, 2026-10-12:
/// <c>GetApplication</c> failed with TYPE_E_LIBNOTREGISTERED), and some members exist only on the vtable interfaces
/// (<c>INcModel.PostByUcsToAllNcProcedures</c> is not on the dispatch <c>_INcModel</c>). A member is found by name and
/// argument count on an interface the object implements (QueryInterface for COM objects); trailing out parameters
/// are filled in, a string is converted to an enum parameter by name. Pure dispinterfaces are skipped: they would go
/// through IDispatch again.
/// </summary>
internal sealed class CimatronBinder
{
    private readonly Type[] interfaces;

    internal CimatronBinder(IEnumerable<Type> interfaces) =>
        this.interfaces = interfaces.Where(Usable).Distinct().ToArray();

    internal int InterfaceCount => interfaces.Length;

    /// <summary>The interface types of the <c>interop.*.dll</c> assemblies in <paramref name="programFolder"/>.</summary>
    internal static CimatronBinder FromProgramFolder(string? programFolder, ICollection<string>? problems = null)
    {
        var types = new List<Type>();
        if (programFolder is null || !Directory.Exists(programFolder)) return new CimatronBinder(types);
        foreach (var file in Directory.EnumerateFiles(programFolder, "interop.*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                types.AddRange(Assembly.LoadFrom(file).GetTypes().Where(type => type.IsInterface));
            }
            catch (ReflectionTypeLoadException exception)
            {
                types.AddRange(exception.Types.Where(type => type is { IsInterface: true })!);
                problems?.Add($"{Path.GetFileName(file)}: some types could not be loaded ({exception.LoaderExceptions.FirstOrDefault()?.Message})");
            }
            catch (Exception exception) when (exception is IOException or BadImageFormatException or FileLoadException)
            {
                problems?.Add($"{Path.GetFileName(file)}: {exception.Message}");
            }
        }

        return new CimatronBinder(types);
    }

    private static bool Usable(Type type)
    {
        if (!type.IsInterface || type.IsGenericType) return false;
        if (type.GetCustomAttribute<CoClassAttribute>() is not null) return false;
        return type.GetCustomAttribute<InterfaceTypeAttribute>()?.Value != ComInterfaceType.InterfaceIsIDispatch;
    }

    /// <summary>Whether <paramref name="target"/> has <paramref name="member"/> on one of the known vtable interfaces.</summary>
    internal bool Has(object target, string member) => Candidates(target, member, null).Any();

    /// <summary>
    /// Calls <paramref name="member"/> (a method, or a property getter when there are no arguments). Returns false when
    /// no known interface of the object has it; out parameter values are in <paramref name="outputs"/> afterwards.
    /// </summary>
    internal bool TryInvoke(object target, string member, object?[] arguments, out object? result, out object?[] outputs)
    {
        foreach (var method in Candidates(target, member, arguments.Length))
        {
            var parameters = method.GetParameters();
            var values = new object?[parameters.Length];
            for (var index = 0; index < parameters.Length; index++)
            {
                values[index] = index < arguments.Length
                    ? Convert(arguments[index], parameters[index].ParameterType)
                    : null;
            }

            result = method.Invoke(target, values);
            outputs = parameters.Select((parameter, index) => parameter.ParameterType.IsByRef ? values[index] : null).ToArray();
            return true;
        }

        result = null;
        outputs = [];
        return false;
    }

    private IEnumerable<MethodInfo> Candidates(object target, string member, int? argumentCount)
    {
        foreach (var type in interfaces)
        {
            var methods = type.GetMethods()
                .Where(method => string.Equals(method.Name, member, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(method.Name, "get_" + member, StringComparison.OrdinalIgnoreCase))
                .Where(method => argumentCount is null || Fits(method, argumentCount.Value))
                .OrderBy(method => method.GetParameters().Length)
                .ToArray();
            if (methods.Length == 0 || !Implements(target, type)) continue;
            foreach (var method in methods) yield return method;
        }
    }

    // All given arguments map to parameters, and every parameter left over is an out parameter.
    private static bool Fits(MethodInfo method, int argumentCount)
    {
        var parameters = method.GetParameters();
        return parameters.Length >= argumentCount && parameters.Skip(argumentCount).All(parameter => parameter.IsOut || parameter.ParameterType.IsByRef);
    }

    private static bool Implements(object target, Type type)
    {
        if (!OperatingSystem.IsWindows() || !Marshal.IsComObject(target)) return type.IsInstanceOfType(target);
        var unknown = Marshal.GetIUnknownForObject(target);
        try
        {
            var iid = type.GUID;
            if (Marshal.QueryInterface(unknown, in iid, out var pointer) != 0) return false;
            Marshal.Release(pointer);
            return true;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static object? Convert(object? value, Type parameterType)
    {
        var type = parameterType.IsByRef ? parameterType.GetElementType()! : parameterType;
        if (value is null || type.IsInstanceOfType(value)) return value;
        if (type.IsEnum)
        {
            if (value is string text)
            {
                return Enum.TryParse(type, text, ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new CimatronApiException("cimatron_call_failed",
                        $"'{text}' is not a {type.Name} value ({string.Join(", ", Enum.GetNames(type))}).");
            }

            return Enum.ToObject(type, value);
        }

        return value is IConvertible && typeof(IConvertible).IsAssignableFrom(type)
            ? System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture)
            : value;
    }
}
