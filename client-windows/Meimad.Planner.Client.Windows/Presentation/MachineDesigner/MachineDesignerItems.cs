using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Meimad.Planner.NcEngine.MachineDefinitions;

namespace Meimad.Planner.Client.Windows.Presentation.MachineDesigner;

internal abstract class DesignerObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after any edit, so the designer redraws and marks the draft changed.</summary>
    internal event EventHandler? Edited;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        Edited?.Invoke(this, EventArgs.Empty);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseEdited() => Edited?.Invoke(this, EventArgs.Empty);
}

/// <summary>Invariant number text of the designer's fields (a decimal point, never a comma).</summary>
internal static class DesignerNumbers
{
    internal static string Format(double? value) =>
        value is { } number && double.IsFinite(number) ? number.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

    internal static bool TryParse(string? text, out double value) =>
        double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>Blank is null; anything else must be a finite number.</summary>
    internal static bool TryParseOptional(string? text, out double? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!TryParse(text, out var number)) return false;
        value = number;
        return true;
    }
}

/// <summary>Three invariant number fields (mm or degrees).</summary>
internal sealed class DesignerVector : DesignerObservable
{
    private string x;
    private string y;
    private string z;

    internal DesignerVector(double[]? value = null)
    {
        var v = MachineVectors.IsFinite3(value) ? value! : [0, 0, 0];
        x = DesignerNumbers.Format(v[0]);
        y = DesignerNumbers.Format(v[1]);
        z = DesignerNumbers.Format(v[2]);
    }

    public string X { get => x; set => Set(ref x, value); }
    public string Y { get => y; set => Set(ref y, value); }
    public string Z { get => z; set => Set(ref z, value); }

    /// <summary>The numbers, or null (with <paramref name="problem"/>) when a field is not a number.</summary>
    internal double[]? Value(out string? problem)
    {
        problem = null;
        if (DesignerNumbers.TryParse(X, out var a) && DesignerNumbers.TryParse(Y, out var b) && DesignerNumbers.TryParse(Z, out var c))
            return [a, b, c];
        problem = "Enter three numbers with a decimal point.";
        return null;
    }

    internal double[] ValueOrZero() => Value(out _) ?? [0, 0, 0];

    public override string ToString() => $"{X}, {Y}, {Z}";
}

/// <summary>One mesh of a component (an uploaded asset, or a file path carried over until it is imported).</summary>
internal sealed class DesignerMesh : DesignerObservable
{
    private string name;
    private string color;
    private string opacity;
    private string scale;
    private bool visible;
    private bool wireframe;
    private bool collisionParticipation;
    private string? assetId;
    private string? legacyPath;

    internal DesignerMesh(MachineModel model)
    {
        assetId = model.AssetId;
        legacyPath = model.LegacyPath;
        name = model.Name ?? (model.LegacyPath is null ? model.AssetId ?? "Mesh" : Path.GetFileName(model.LegacyPath));
        Position = new DesignerVector(model.Alignment.Position);
        Rotation = new DesignerVector(model.Alignment.RotationDeg);
        scale = DesignerNumbers.Format(model.Alignment.Scale);
        color = model.Color ?? string.Empty;
        opacity = DesignerNumbers.Format(model.Opacity);
        visible = model.Visible;
        wireframe = model.Wireframe;
        collisionParticipation = model.CollisionParticipation;
        Position.Edited += (_, _) => RaiseEdited();
        Rotation.Edited += (_, _) => RaiseEdited();
    }

    public string Name { get => name; set => Set(ref name, value); }
    public DesignerVector Position { get; }
    public DesignerVector Rotation { get; }
    public string Scale { get => scale; set => Set(ref scale, value); }
    public string Color { get => color; set => Set(ref color, value); }
    public string Opacity { get => opacity; set => Set(ref opacity, value); }
    public bool Visible { get => visible; set => Set(ref visible, value); }
    public bool Wireframe { get => wireframe; set => Set(ref wireframe, value); }
    /// <summary>Kept independent of <see cref="Visible"/> and <see cref="Opacity"/>.</summary>
    public bool CollisionParticipation { get => collisionParticipation; set => Set(ref collisionParticipation, value); }

    public string? AssetId
    {
        get => assetId;
        internal set
        {
            if (Set(ref assetId, value)) { OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(IsLegacy)); }
        }
    }

    public string? LegacyPath
    {
        get => legacyPath;
        internal set
        {
            if (Set(ref legacyPath, value)) { OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(IsLegacy)); }
        }
    }

    public bool IsLegacy => assetId is null && legacyPath is not null;

    public string Source => assetId is not null ? $"Server mesh {assetId[..8]}…" : $"File path (import it): {legacyPath}";

    internal MachineModel ToModel(List<MachineSimulationIssue> problems, string path)
    {
        var position = Position.Value(out var positionProblem);
        if (positionProblem is not null) problems.Add(new(path + ".alignment.position", "not_finite", positionProblem));
        var rotation = Rotation.Value(out var rotationProblem);
        if (rotationProblem is not null) problems.Add(new(path + ".alignment.rotationDeg", "not_finite", rotationProblem));
        if (!DesignerNumbers.TryParse(Scale, out var scaleValue)) problems.Add(new(path + ".alignment.scale", "not_finite", "Enter the mesh scale (1 = millimetres, 25.4 = inches)."));
        if (!DesignerNumbers.TryParse(Opacity, out var opacityValue)) problems.Add(new(path + ".opacity", "not_finite", "Enter an opacity from 0 to 1."));
        return new MachineModel
        {
            AssetId = AssetId,
            LegacyPath = AssetId is null ? LegacyPath : null,
            Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            Alignment = new MachineTransform { Position = position ?? [0, 0, 0], RotationDeg = rotation ?? [0, 0, 0], Scale = scaleValue == 0 ? 1 : scaleValue },
            Color = string.IsNullOrWhiteSpace(Color) ? null : Color.Trim(),
            Visible = Visible,
            Opacity = opacityValue,
            Wireframe = Wireframe,
            CollisionParticipation = CollisionParticipation
        };
    }
}

/// <summary>One component of the machine tree with its optional joint, primitive and meshes.</summary>
internal sealed class DesignerNode : DesignerObservable
{
    internal static readonly IReadOnlyList<string> JointTypes = ["none", "linear", "rotary"];
    internal static readonly IReadOnlyList<string> PrimitiveShapes = ["none", "box", "cylinder"];

    private string id;
    private string label;
    private string? parent;
    private string jointType;
    private string axis;
    private string min;
    private string max;
    private bool continuous;
    private string zeroOutsideReason;
    private string rapidRate;
    private string maxFeed;
    private string jointScale;
    private bool diameter;
    private string shape;
    private string radius;
    private string height;
    private string primitiveAxis;

    internal DesignerNode(MachineNode node)
    {
        id = node.Id;
        label = node.Label ?? string.Empty;
        parent = node.Parent;
        Position = new DesignerVector(node.Position);
        var joint = node.Joint;
        jointType = joint is null ? "none" : joint.IsRotary ? "rotary" : "linear";
        axis = joint?.Axis ?? string.Empty;
        Direction = new DesignerVector(joint?.Direction ?? [0, 0, 1]);
        Point = new DesignerVector(joint?.Point ?? node.Position);
        min = DesignerNumbers.Format(joint?.Min);
        max = DesignerNumbers.Format(joint?.Max);
        continuous = joint?.Continuous ?? false;
        zeroOutsideReason = joint?.ZeroOutsideReason ?? string.Empty;
        rapidRate = DesignerNumbers.Format(joint?.RapidRate);
        maxFeed = DesignerNumbers.Format(joint?.MaxFeed);
        jointScale = DesignerNumbers.Format(joint?.Scale ?? 1);
        diameter = joint?.Diameter ?? false;
        var primitive = node.Primitive;
        shape = primitive?.Shape ?? "none";
        Size = new DesignerVector(primitive?.Size ?? [100, 100, 100]);
        radius = DesignerNumbers.Format(primitive?.Radius);
        height = DesignerNumbers.Format(primitive?.Height);
        Center = new DesignerVector(primitive?.Center);
        primitiveAxis = primitive?.Axis ?? "z";
        foreach (var vector in new[] { Position, Direction, Point, Size, Center }) vector.Edited += (_, _) => RaiseEdited();
        foreach (var model in node.Models) AddMesh(new DesignerMesh(model));
    }

    public string Id { get => id; set { if (Set(ref id, value.Trim())) OnPropertyChanged(nameof(Title)); } }
    public string Label { get => label; set { if (Set(ref label, value)) OnPropertyChanged(nameof(Title)); } }
    public string? Parent { get => parent; set => Set(ref parent, string.IsNullOrWhiteSpace(value) ? null : value); }
    public DesignerVector Position { get; }

    public string JointType
    {
        get => jointType;
        set
        {
            if (!Set(ref jointType, value)) return;
            OnPropertyChanged(nameof(HasJoint));
            OnPropertyChanged(nameof(IsRotary));
            OnPropertyChanged(nameof(Title));
        }
    }

    public bool HasJoint => jointType is "linear" or "rotary";
    public bool IsRotary => jointType == "rotary";
    public string Axis { get => axis; set { if (Set(ref axis, value.Trim().ToUpperInvariant())) OnPropertyChanged(nameof(Title)); } }
    public DesignerVector Direction { get; }
    /// <summary>A point on the rotation line, in the parent's frame (rotary joints only).</summary>
    public DesignerVector Point { get; }
    public string Min { get => min; set => Set(ref min, value); }
    public string Max { get => max; set => Set(ref max, value); }
    public bool Continuous { get => continuous; set => Set(ref continuous, value); }
    public string ZeroOutsideReason { get => zeroOutsideReason; set => Set(ref zeroOutsideReason, value); }
    public string RapidRate { get => rapidRate; set => Set(ref rapidRate, value); }
    public string MaxFeed { get => maxFeed; set => Set(ref maxFeed, value); }
    public string JointScale { get => jointScale; set => Set(ref jointScale, value); }
    public bool Diameter { get => diameter; set => Set(ref diameter, value); }

    public string Shape { get => shape; set { if (Set(ref shape, value)) { OnPropertyChanged(nameof(IsBox)); OnPropertyChanged(nameof(IsCylinder)); } } }
    public bool IsBox => shape == "box";
    public bool IsCylinder => shape == "cylinder";
    public DesignerVector Size { get; }
    public string Radius { get => radius; set => Set(ref radius, value); }
    public string Height { get => height; set => Set(ref height, value); }
    public DesignerVector Center { get; }
    public string PrimitiveAxis { get => primitiveAxis; set => Set(ref primitiveAxis, value); }

    public ObservableCollection<DesignerMesh> Meshes { get; } = [];

    /// <summary>Children in the tree (rebuilt by the designer when parents change).</summary>
    public ObservableCollection<DesignerNode> Children { get; } = [];

    public string Title
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Label) ? Id : Label;
            return HasJoint && !string.IsNullOrEmpty(Axis) ? $"{name}  [{Axis}]" : name;
        }
    }

    private bool isSelected;
    private bool isExpanded = true;

    /// <summary>Tree state only; not part of the definition.</summary>
    public bool IsSelected
    {
        get => isSelected;
        set { if (isSelected != value) { isSelected = value; OnPropertyChanged(); } }
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set { if (isExpanded != value) { isExpanded = value; OnPropertyChanged(); } }
    }

    internal void AddMesh(DesignerMesh mesh)
    {
        mesh.Edited += (_, _) => RaiseEdited();
        Meshes.Add(mesh);
        RaiseEdited();
    }

    internal void RemoveMesh(DesignerMesh mesh)
    {
        if (Meshes.Remove(mesh)) RaiseEdited();
    }

    internal MachineNode ToNode(List<MachineSimulationIssue> problems, string path)
    {
        var position = Position.Value(out var problem);
        if (problem is not null) problems.Add(new(path + ".position", "not_finite", problem));
        MachineJoint? joint = null;
        if (HasJoint)
        {
            var direction = Direction.Value(out var directionProblem);
            if (directionProblem is not null) problems.Add(new(path + ".joint.direction", "not_finite", directionProblem));
            double[]? point = null;
            if (IsRotary)
            {
                point = Point.Value(out var pointProblem);
                if (pointProblem is not null) problems.Add(new(path + ".joint.point", "not_finite", pointProblem));
            }
            double? Optional(string text, string field)
            {
                if (DesignerNumbers.TryParseOptional(text, out var value)) return value;
                problems.Add(new($"{path}.joint.{field}", "not_finite", $"{field} must be a number or empty."));
                return null;
            }
            var scaleValue = Optional(JointScale, "scale") ?? 1;
            joint = new MachineJoint
            {
                Axis = Axis,
                Type = JointType,
                Direction = direction ?? [0, 0, 0],
                Point = IsRotary ? point ?? [0, 0, 0] : null,
                Min = IsRotary && Continuous ? null : Optional(Min, "min"),
                Max = IsRotary && Continuous ? null : Optional(Max, "max"),
                Continuous = IsRotary && Continuous,
                ZeroOutsideReason = string.IsNullOrWhiteSpace(ZeroOutsideReason) ? null : ZeroOutsideReason.Trim(),
                RapidRate = Optional(RapidRate, "rapidRate"),
                MaxFeed = Optional(MaxFeed, "maxFeed"),
                Scale = scaleValue,
                Diameter = Diameter
            };
        }
        MachinePrimitive? primitive = null;
        if (Shape is "box" or "cylinder")
        {
            var center = Center.Value(out var centerProblem);
            if (centerProblem is not null) problems.Add(new(path + ".primitive.center", "not_finite", centerProblem));
            primitive = new MachinePrimitive
            {
                Shape = Shape,
                Size = IsBox ? Size.Value(out _) : null,
                Radius = IsCylinder && DesignerNumbers.TryParse(Radius, out var r) ? r : null,
                Height = IsCylinder && DesignerNumbers.TryParse(Height, out var h) ? h : null,
                Center = center ?? [0, 0, 0],
                Axis = IsCylinder ? PrimitiveAxis : null
            };
        }
        return new MachineNode
        {
            Id = Id,
            Parent = Parent,
            Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            Position = position ?? [0, 0, 0],
            Joint = joint,
            Primitive = primitive,
            Models = Meshes.Select((mesh, index) => mesh.ToModel(problems, $"{path}.models[{index}]")).ToArray()
        };
    }
}

/// <summary>A jog slider for one joint: the physical axis reading (mm or degrees) within its travel.</summary>
internal sealed class DesignerJog : DesignerObservable
{
    private double value;

    internal DesignerJog(string axis, bool rotary, double? min, double? max)
    {
        Axis = axis;
        Unit = rotary ? "°" : "mm";
        Minimum = min ?? (rotary ? -360 : -1000);
        Maximum = max ?? (rotary ? 360 : 1000);
        if (Minimum > Maximum) (Minimum, Maximum) = (Maximum, Minimum);
        value = Math.Clamp(0, Minimum, Maximum);
    }

    public string Axis { get; }
    public string Unit { get; }
    public double Minimum { get; }
    public double Maximum { get; }

    public double Value
    {
        get => value;
        set { if (Set(ref this.value, Math.Round(value, 3))) OnPropertyChanged(nameof(Text)); }
    }

    public string Text => $"{Axis} {Value.ToString("0.###", CultureInfo.InvariantCulture)} {Unit}";
}
