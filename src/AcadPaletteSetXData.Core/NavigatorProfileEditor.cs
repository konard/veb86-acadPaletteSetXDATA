using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// The model of the profile settings window of the navigator (issue #66): the name of the profile, the tree expression
/// (checked as it is typed) and the list of columns. Nothing is applied until <see cref="ToProfile"/> is called on OK.
/// </summary>
public sealed class NavigatorProfileEditor : ObservableObject
{
    private readonly NavigatorProfile original;
    private readonly Func<string, bool> exists;
    private readonly SchemaSnapshot? schema;
    private string id;
    private string name;
    private string expression;
    private NavigatorColumnEditor? selectedColumn;
    private NavigatorValidation validation = new NavigatorValidation(Array.Empty<string>(), Array.Empty<string>());

    public NavigatorProfileEditor(NavigatorProfile profile, bool isNew, Func<string, bool>? exists = null, SchemaSnapshot? schema = null)
    {
        original = (profile ?? throw new ArgumentNullException(nameof(profile))).Clone();
        IsNew = isNew;
        this.exists = exists ?? (_ => false);
        this.schema = schema;
        id = original.Id; name = original.Name; expression = original.TreeExpression;
        foreach (var column in original.Columns) Add(new NavigatorColumnEditor(column.Clone()));
        Columns.CollectionChanged += (_, _) => Validate();
        AddColumnCommand = new DelegateCommand(_ => AddColumn());
        RemoveColumnCommand = new DelegateCommand(_ => RemoveColumn(), _ => SelectedColumn != null);
        MoveUpCommand = new DelegateCommand(_ => Move(-1), _ => SelectedColumn != null && Columns.IndexOf(SelectedColumn) > 0);
        MoveDownCommand = new DelegateCommand(_ => Move(1), _ => SelectedColumn != null && Columns.IndexOf(SelectedColumn) < Columns.Count - 1);
        ResetExpressionCommand = new DelegateCommand(_ => Expression = NavigatorProfile.ControlExpression);
        Validate();
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Новый профиль навигатора" : "Профиль навигатора «" + original.Name + "»";
    public ObservableCollection<NavigatorColumnEditor> Columns { get; } = new ObservableCollection<NavigatorColumnEditor>();
    public DelegateCommand AddColumnCommand { get; }
    public DelegateCommand RemoveColumnCommand { get; }
    public DelegateCommand MoveUpCommand { get; }
    public DelegateCommand MoveDownCommand { get; }
    public DelegateCommand ResetExpressionCommand { get; }
    public string ControlExpression => NavigatorProfile.ControlExpression;

    /// <summary>The file name of the profile; set once, when the profile is created.</summary>
    public string Id { get => id; set { var text = value?.Trim() ?? ""; if (id == text) return; id = text; Notify(nameof(Id)); Validate(); } }
    public bool CanEditId => IsNew;
    public string Name { get => name; set { if (name == (value ?? "")) return; name = value ?? ""; Notify(nameof(Name)); Validate(); } }

    public string Expression
    {
        get => expression;
        set { if (expression == (value ?? "")) return; expression = value ?? ""; Notify(nameof(Expression)); Validate(); Notify(nameof(ExpressionInfo)); }
    }

    /// <summary>How the expression is read: the levels of groups and the name of the leaves.</summary>
    public string ExpressionInfo
    {
        get
        {
            var tree = NavigatorTreeExpression.Parse(expression);
            if (!tree.IsValid) return string.Join("\n", tree.Errors);
            var lines = new List<string>();
            foreach (var level in tree.Levels)
            {
                var role = level.IsLeaf ? "имя объекта" : "группа";
                var state = level.Enabled ? role : level.IsLeaf ? "отключено: имя объекта — тип" : "отключено";
                lines.Add((level.Index + 1).ToString(CultureInfo.InvariantCulture) + ". " + level.Text + " — " + state);
            }
            return string.Join("\n", lines);
        }
    }

    public NavigatorColumnEditor? SelectedColumn
    {
        get => selectedColumn;
        set
        {
            if (ReferenceEquals(selectedColumn, value)) return;
            selectedColumn = value;
            Notify(nameof(SelectedColumn));
            RemoveColumnCommand.RaiseCanExecuteChanged();
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
        }
    }

    public IReadOnlyList<string> Errors => validation.Errors;
    public IReadOnlyList<string> Warnings => validation.Warnings;
    public bool IsValid => validation.IsValid;
    public string Problems => string.Join("\n", validation.Errors.Concat(validation.Warnings.Select(warning => "Предупреждение: " + warning)));

    public NavigatorProfile ToProfile()
    {
        var profile = original.Clone();
        profile.Id = IsNew ? id : original.Id;
        profile.Name = name.Trim();
        profile.TreeExpression = expression.Trim();
        profile.Columns.Clear();
        profile.Columns.AddRange(Columns.Select(column => column.ToColumn()));
        return profile;
    }

    public void AddColumn()
    {
        var number = 1;
        while (Columns.Any(column => column.Id == "column" + number.ToString(CultureInfo.InvariantCulture))) number++;
        var added = new NavigatorColumnEditor(new NavigatorColumn("column" + number.ToString(CultureInfo.InvariantCulture), "Колонка " + number.ToString(CultureInfo.InvariantCulture), "NMO_Name"));
        Add(added);
        SelectedColumn = added;
    }

    private void RemoveColumn()
    {
        if (SelectedColumn is null) return;
        var index = Columns.IndexOf(SelectedColumn);
        SelectedColumn.PropertyChanged -= OnColumnChanged;
        Columns.Remove(SelectedColumn);
        SelectedColumn = Columns.Count == 0 ? null : Columns[Math.Min(index, Columns.Count - 1)];
    }

    private void Move(int offset)
    {
        if (SelectedColumn is null) return;
        var index = Columns.IndexOf(SelectedColumn);
        var target = index + offset;
        if (target < 0 || target >= Columns.Count) return;
        Columns.Move(index, target);
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
    }

    private void Add(NavigatorColumnEditor column)
    {
        column.PropertyChanged += OnColumnChanged;
        Columns.Add(column);
    }

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e) => Validate();

    private void Validate()
    {
        var current = ToProfile();
        var result = current.Validate(schema);
        var errors = result.Errors.ToList();
        if (IsNew && NavigatorProfile.IsValidId(id) && exists(id)) errors.Add("профиль с id «" + id + "» уже есть");
        validation = new NavigatorValidation(errors, result.Warnings);
        Notify(nameof(Errors));
        Notify(nameof(Warnings));
        Notify(nameof(IsValid));
        Notify(nameof(Problems));
    }
}

/// <summary>A column in the profile settings window.</summary>
public sealed class NavigatorColumnEditor : ObservableObject
{
    private readonly NavigatorColumn column;

    public NavigatorColumnEditor(NavigatorColumn column) { this.column = column ?? throw new ArgumentNullException(nameof(column)); }

    public string Id { get => column.Id; set { if (column.Id == value) return; column.Id = value?.Trim() ?? ""; Notify(nameof(Id)); } }
    public string Caption { get => column.Caption; set { if (column.Caption == value) return; column.Caption = value ?? ""; Notify(nameof(Caption)); } }
    public string Value { get => column.Value; set { if (column.Value == value) return; column.Value = value ?? ""; Notify(nameof(Value)); Notify(nameof(SourceError)); } }
    public double Width { get => column.Width; set { if (column.Width.Equals(value)) return; column.Width = value; Notify(nameof(Width)); } }
    public bool Visible { get => column.Visible; set { if (column.Visible == value) return; column.Visible = value; Notify(nameof(Visible)); } }
    /// <summary>The problem of <see cref="Value"/>, shown next to it.</summary>
    public string? SourceError => NavigatorValueSource.Parse(column.Value).Error;

    public NavigatorColumn ToColumn() => column.Clone();
}
