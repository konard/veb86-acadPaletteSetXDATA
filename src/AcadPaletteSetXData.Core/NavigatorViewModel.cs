using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// The «Навигатор» tab (issue #66): a read-only multi-column tree of the objects with XDATA of the drawing, grouped by
/// the expression of the active profile. It reads the drawing through <see cref="INavigatorSource"/> and keeps no
/// AutoCAD types. Switching profiles, sorting, expanding nodes and changing columns use the objects already read; only
/// <see cref="Refresh"/> reads the drawing.
/// </summary>
public sealed class NavigatorViewModel : ObservableObject
{
    private readonly NavigatorProfileStore? store;
    private readonly HashSet<string> expanded = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, NavigatorObject> objects = new Dictionary<string, NavigatorObject>(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> loadErrors = new List<string>();
    private INavigatorSource? source;
    private NavigatorSettings settings = new NavigatorSettings();
    private NavigatorProfile? profile;
    private NavigatorProfile? selectedProfile;
    private NavigatorTreeExpression? expression;
    private NavigatorTree? tree;
    private NavigatorRow? selectedRow;
    private SchemaSnapshot? schema;
    private string? document;
    private string? readError;
    private string status = "";
    private string? message;
    private bool selectionMessage; // the message is about the last selection of an object
    private bool isDirty;
    private bool settingsDirty;
    private bool isShown;
    private bool stale = true;
    private bool selecting;
    private bool loaded;
    private IReadOnlyList<string> warnings = Array.Empty<string>();

    /// <param name="store">The profile files; null keeps profiles in memory only (tests, the WPF smoke test).</param>
    public NavigatorViewModel(NavigatorProfileStore? store = null)
    {
        this.store = store;
        RefreshCommand = new DelegateCommand(_ => Refresh(true));
        SaveCommand = new DelegateCommand(_ => Save(), _ => IsDirty);
        NewProfileCommand = new DelegateCommand(_ => NewProfile());
        EditProfileCommand = new DelegateCommand(_ => EditProfile(), _ => profile != null);
        DeleteProfileCommand = new DelegateCommand(_ => DeleteProfile(), _ => profile != null);
        ToggleCommand = new DelegateCommand(row => { if (row is NavigatorRow item && item.HasChildren) item.IsExpanded = !item.IsExpanded; });
        ActivateCommand = new DelegateCommand(row => Activate(row as NavigatorRow ?? SelectedRow));
        SelectCommand = new DelegateCommand(row => Select(row as NavigatorRow ?? SelectedRow));
        SelectGroupCommand = new DelegateCommand(row => SelectGroup(row as NavigatorRow ?? SelectedRow),
            row => (row as NavigatorRow ?? SelectedRow)?.IsGroup == true);
        SortCommand = new DelegateCommand(column => SortBy(column as NavigatorColumnViewModel));
        ExpandAllCommand = new DelegateCommand(_ => ExpandAll(true));
        CollapseAllCommand = new DelegateCommand(_ => ExpandAll(false));
    }

    public ObservableCollection<NavigatorProfile> Profiles { get; } = new ObservableCollection<NavigatorProfile>();
    /// <summary>The columns of the active profile in its order, hidden ones included.</summary>
    public ObservableCollection<NavigatorColumnViewModel> Columns { get; } = new ObservableCollection<NavigatorColumnViewModel>();
    public IEnumerable<NavigatorColumnViewModel> VisibleColumns => Columns.Where(column => column.Visible);
    /// <summary>The visible rows of the tree (expanded groups and their children): the list a virtualizing view shows.</summary>
    public NavigatorRowList Rows { get; } = new NavigatorRowList();

    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand NewProfileCommand { get; }
    public DelegateCommand EditProfileCommand { get; }
    public DelegateCommand DeleteProfileCommand { get; }
    public DelegateCommand ToggleCommand { get; }
    public DelegateCommand ActivateCommand { get; }
    /// <summary>
    /// Issue #68: a click on an object selects it in AutoCAD, also when its row is already the selected one (the
    /// selection of AutoCAD may have been cleared since). A group selects nothing.
    /// </summary>
    public DelegateCommand SelectCommand { get; }
    /// <summary>Issue #68: the menu of a group — selects in AutoCAD every object grouped under it, at any depth.</summary>
    public DelegateCommand SelectGroupCommand { get; }
    public DelegateCommand SortCommand { get; }
    public DelegateCommand ExpandAllCommand { get; }
    public DelegateCommand CollapseAllCommand { get; }

    /// <summary>Opens the profile settings window; true when the user accepted the changes. Set by the view.</summary>
    public Func<NavigatorProfileEditor, bool>? EditProfileWindow { get; set; }
    /// <summary>A yes/no question (deleting a profile). Without it the answer is «no».</summary>
    public Func<string, bool>? Confirm { get; set; }
    /// <summary>«Save the changes?» with yes (true), no (false) and cancel (null). Without it changes are kept unsaved.</summary>
    public Func<string, bool?>? AskSave { get; set; }

    /// <summary>The columns were added, removed, shown or hidden: the view rebuilds its column headers (not the data).</summary>
    public event EventHandler? ColumnsChanged;

    public INavigatorSource? Source
    {
        get => source;
        set
        {
            if (ReferenceEquals(source, value)) return;
            if (source != null) source.Changed -= OnSourceChanged;
            source = value;
            if (source != null) source.Changed += OnSourceChanged;
            Clear();
            stale = true;
            if (isShown) Refresh(false);
        }
    }

    /// <summary>
    /// The tab is shown. A hidden navigator does not read the drawing; it remembers that the drawing changed and reads
    /// it when it is shown again.
    /// </summary>
    public bool IsShown
    {
        get => isShown;
        set
        {
            if (isShown == value) return;
            isShown = value;
            Notify(nameof(IsShown));
            if (value)
            {
                EnsureLoaded();
                if (stale) Refresh(false);
            }
        }
    }

    public NavigatorProfile? Profile => profile;

    /// <summary>The profile of the ComboBox: choosing another one switches the tree without a restart.</summary>
    public NavigatorProfile? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (value is null || ReferenceEquals(value, selectedProfile)) return;
            if (!SwitchProfile(value.Id))
            {
                // The ComboBox returns to the profile that stays active.
                Notify(nameof(SelectedProfile));
            }
        }
    }

    /// <summary>The active profile has changes that are not saved (column widths, order, visibility).</summary>
    public bool IsDirty
    {
        get => isDirty;
        private set { if (isDirty == value) return; isDirty = value; Notify(nameof(IsDirty)); SaveCommand.RaiseCanExecuteChanged(); }
    }

    public NavigatorTree? Tree => tree;
    public NavigatorSettings Settings => settings;
    public string EmptyText => settings.EmptyText;

    /// <summary>The width of the tree column, kept in <c>navigator.json</c>.</summary>
    public double TreeWidth
    {
        get => settings.TreeWidth;
        set
        {
            var width = NavigatorColumn.ClampWidth(value);
            if (Math.Abs(settings.TreeWidth - width) < 0.5) return;
            settings.TreeWidth = width;
            settingsDirty = true;
            Notify(nameof(TreeWidth));
        }
    }

    public string? SortColumn => settings.SortColumn;
    public bool SortDescending => settings.SortDescending;
    public string TreeSortGlyph => settings.SortColumn is null ? settings.SortDescending ? " ▼" : " ▲" : "";

    public NavigatorRow? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (ReferenceEquals(selectedRow, value)) return;
            selectedRow = value;
            Notify(nameof(SelectedRow));
            SelectGroupCommand.RaiseCanExecuteChanged();
            if (!selecting && value?.Node.Kind == NavigatorNodeKind.Leaf) SelectObject(value, false);
        }
    }

    /// <summary>The summary line under the tree: objects, groups, errors.</summary>
    public string Status { get => status; private set => SetProperty(ref status, value); }

    /// <summary>The last message of an action (an object not found, a profile not applied).</summary>
    public string? Message
    {
        get => message;
        private set
        {
            selectionMessage = false;
            SetProperty(ref message, value);
            Notify(nameof(Diagnostics));
            Notify(nameof(HasDiagnostics));
        }
    }

    /// <summary>Sets a message about selecting an object: a later selection or a refresh of the drawing removes it.</summary>
    private void SetSelectionMessage(string? text)
    {
        if (text is null && !selectionMessage) return; // the message of another action stays
        Message = text;
        selectionMessage = text != null;
    }

    /// <summary>Problems to show: broken profile files, unknown parameters of the profile, errors of reading.</summary>
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            var result = new List<string>();
            if (message != null) result.Add(message);
            if (readError != null) result.Add(readError);
            result.AddRange(loadErrors);
            result.AddRange(warnings);
            return result;
        }
    }

    public bool HasDiagnostics => Diagnostics.Count != 0;

    /// <summary>Reads the profiles and settings once; a missing folder gives the default profile.</summary>
    public void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        loadErrors.Clear();
        if (store != null)
        {
            settings = store.LoadSettings(out var error);
            if (error != null) loadErrors.Add("navigator.json не прочитан, используются настройки по умолчанию: " + error);
            foreach (var file in store.LoadProfiles())
            {
                if (file.Profile != null && !Profiles.Any(item => string.Equals(item.Id, file.Profile.Id, StringComparison.OrdinalIgnoreCase)))
                    Profiles.Add(file.Profile);
                else if (file.Error != null) loadErrors.Add("Профиль не загружен (файл не изменён): " + file.Path + ": " + file.Error);
            }
        }
        if (Profiles.Count == 0) Profiles.Add(NavigatorProfile.CreateDefault());
        var active = Profiles.FirstOrDefault(item => string.Equals(item.Id, settings.ActiveProfile, StringComparison.OrdinalIgnoreCase)) ?? Profiles[0];
        Apply(active.Clone());
        NotifyAll();
    }

    /// <summary>
    /// Switches to another profile without a restart: loads its file, checks the JSON, the expression and the
    /// references, builds the tree from the objects already read and applies the columns. A profile that fails keeps
    /// the previous one active.
    /// </summary>
    public bool SwitchProfile(string id)
    {
        EnsureLoaded();
        if (profile != null && string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        var listed = Profiles.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (listed is null) { Message = "Профиль «" + id + "» не найден."; return false; }
        if (!ConfirmLeave()) return false;
        // 1–2. Load the file again (it may have been edited) and check its JSON; a missing file uses the profile in memory.
        var candidate = listed.Clone();
        var file = store?.LoadProfile(listed.Id);
        if (file?.Error != null)
        {
            Message = "Профиль «" + listed.Name + "» не применён, остаётся «" + profile?.Name + "»: " + file.Error;
            return false;
        }
        if (file?.Profile != null) { candidate = file.Profile.Clone(); Profiles[Profiles.IndexOf(listed)] = file.Profile; }
        // 3. Check the expression and the columns.
        var validation = candidate.Validate(schema);
        if (!validation.IsValid)
        {
            Message = "Профиль «" + candidate.Name + "» не применён: " + string.Join("; ", validation.Errors);
            return false;
        }
        // 4–6. Build from the objects read, apply the columns, refresh the view.
        Apply(candidate);
        Message = null;
        settings.ActiveProfile = candidate.Id;
        SaveSettings();
        return true;
    }

    /// <summary>Reads the drawing (<paramref name="full"/> — every object) and rebuilds the tree, keeping expanded nodes.</summary>
    public void Refresh(bool full = true)
    {
        EnsureLoaded();
        SetSelectionMessage(null); // the drawing is read again: the object that was not found is no longer shown
        if (source is null)
        {
            stale = false;
            Rebuild();
            return;
        }
        NavigatorSnapshot snapshot;
        try { snapshot = source.Read(full); }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            snapshot = NavigatorSnapshot.Empty;
            readError = "Чертёж не прочитан: " + failure.Message;
            Load(snapshot, keepError: true);
            return;
        }
        readError = snapshot.Error;
        Load(snapshot, keepError: true);
    }

    /// <summary>Shows the objects of <paramref name="snapshot"/>; objects whose parsed values did not change keep their computed values.</summary>
    public void Load(NavigatorSnapshot snapshot, bool keepError = false)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        EnsureLoaded();
        stale = false;
        if (!keepError) readError = snapshot.Error;
        if (!string.Equals(snapshot.Document, document, StringComparison.Ordinal))
        {
            // Another drawing: nothing of the previous one is used.
            objects.Clear();
            expanded.Clear();
            document = snapshot.Document;
        }
        schema = snapshot.Schema;
        var next = new Dictionary<string, NavigatorObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Items)
        {
            if (next.ContainsKey(item.Handle)) continue;
            next[item.Handle] = objects.TryGetValue(item.Handle, out var known) && ReferenceEquals(known.Source, item.Values) && known.TypeName == (item.TypeName ?? item.Values.EntityType)
                ? known : new NavigatorObject(item.Values, item.TypeName);
        }
        objects.Clear();
        foreach (var pair in next) objects[pair.Key] = pair.Value;
        if (snapshot.Failed > 0)
            readError = (readError is null ? "" : readError + "; ") + "не прочитано объектов: " + snapshot.Failed.ToString(CultureInfo.InvariantCulture);
        Rebuild();
    }

    /// <summary>Forgets the objects (the document was closed, the source was replaced).</summary>
    public void Clear()
    {
        objects.Clear();
        expanded.Clear();
        document = null;
        schema = null;
        readError = null;
        if (profile != null) Rebuild();
        else { tree = null; Rows.Reset(Array.Empty<NavigatorRow>()); }
    }

    /// <summary>Saves the unsaved state of the navigator (on shutdown) and detaches from the drawing.</summary>
    public void Shutdown()
    {
        Source = null;
        if (settingsDirty) SaveSettings();
    }

    // ---- profiles ----

    public bool Save()
    {
        if (profile is null) return false;
        if (store != null && !store.SaveProfile(profile, out var error))
        {
            Message = "Профиль не сохранён, файл не изменён: " + error;
            return false;
        }
        Replace(profile.Clone());
        IsDirty = false;
        Message = null;
        SaveSettings();
        return true;
    }

    /// <summary>Creates a profile from the active one, edits it in the settings window and saves it.</summary>
    public bool NewProfile()
    {
        EnsureLoaded();
        if (!ConfirmLeave()) return false;
        var draft = (profile ?? NavigatorProfile.CreateDefault()).Clone();
        draft.Id = NewId();
        draft.Name = NewName();
        var editor = new NavigatorProfileEditor(draft, true, Exists, schema);
        if (EditProfileWindow is null || !EditProfileWindow(editor)) return false;
        var created = editor.ToProfile();
        if (store != null && !store.SaveProfile(created, out var error)) { Message = "Профиль не создан: " + error; return false; }
        Profiles.Add(created);
        IsDirty = false;
        Apply(created.Clone());
        settings.ActiveProfile = created.Id;
        SaveSettings();
        Message = null;
        return true;
    }

    /// <summary>Opens the settings of the active profile (name, expression, columns); accepted changes are saved.</summary>
    public bool EditProfile()
    {
        if (profile is null) return false;
        var editor = new NavigatorProfileEditor(profile.Clone(), false, Exists, schema);
        if (EditProfileWindow is null || !EditProfileWindow(editor)) return false;
        return ApplyEdited(editor.ToProfile());
    }

    /// <summary>Applies an edited profile (from the settings window) and saves it.</summary>
    public bool ApplyEdited(NavigatorProfile edited)
    {
        if (edited is null) throw new ArgumentNullException(nameof(edited));
        var validation = edited.Validate(schema);
        if (!validation.IsValid) { Message = "Профиль не применён: " + string.Join("; ", validation.Errors); return false; }
        Apply(edited.Clone());
        IsDirty = true;
        return Save();
    }

    /// <summary>Renames the active profile and saves it; the file name (id) stays.</summary>
    public bool RenameProfile(string name)
    {
        if (profile is null || string.IsNullOrWhiteSpace(name)) return false;
        var renamed = profile.Clone();
        renamed.Name = name.Trim();
        return ApplyEdited(renamed);
    }

    /// <summary>Deletes the active profile after a confirmation and switches to another one.</summary>
    public bool DeleteProfile()
    {
        if (profile is null) return false;
        if (Confirm?.Invoke("Удалить профиль навигатора «" + profile.Name + "»? Файл профиля будет удалён.") != true) return false;
        if (store != null && !store.DeleteProfile(profile.Id, out var error)) { Message = "Профиль не удалён: " + error; return false; }
        var listed = Profiles.FirstOrDefault(item => string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (listed != null) Profiles.Remove(listed);
        if (Profiles.Count == 0) Profiles.Add(NavigatorProfile.CreateDefault());
        IsDirty = false;
        Apply(Profiles[0].Clone());
        settings.ActiveProfile = Profiles[0].Id;
        SaveSettings();
        Message = null;
        return true;
    }

    private bool ConfirmLeave()
    {
        if (!IsDirty || profile is null) return true;
        var answer = AskSave?.Invoke("Профиль навигатора «" + profile.Name + "» изменён (ширина, порядок или видимость колонок). Сохранить изменения?");
        if (answer is null && AskSave != null) return false;
        if (answer == true) return Save();
        // «Нет»: the changes are dropped, the saved profile stays.
        IsDirty = false;
        return true;
    }

    private bool Exists(string id) => Profiles.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    private string NewId()
    {
        for (var i = 1; ; i++)
        {
            var id = "profile" + i.ToString(CultureInfo.InvariantCulture);
            if (!Exists(id) && (store is null || !System.IO.File.Exists(store.ProfilePath(id)))) return id;
        }
    }

    private string NewName()
    {
        for (var i = 1; ; i++)
        {
            var name = "Новый профиль" + (i == 1 ? "" : " " + i.ToString(CultureInfo.InvariantCulture));
            if (!Profiles.Any(item => item.Name == name)) return name;
        }
    }

    private void Replace(NavigatorProfile saved)
    {
        var index = Profiles.ToList().FindIndex(item => string.Equals(item.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) Profiles.Add(saved);
        else Profiles[index] = saved;
        selectedProfile = saved;
        Notify(nameof(SelectedProfile));
    }

    private void SaveSettings()
    {
        settingsDirty = false;
        if (store != null && !store.SaveSettings(settings, out var error)) Message = "Настройки навигатора не сохранены: " + error;
    }

    private void Apply(NavigatorProfile applied)
    {
        profile = applied;
        expression = applied.ParseTree();
        selectedProfile = Profiles.FirstOrDefault(item => string.Equals(item.Id, applied.Id, StringComparison.OrdinalIgnoreCase));
        foreach (var column in Columns) column.PropertyChanged -= OnColumnChanged;
        Columns.Clear();
        for (var i = 0; i < applied.Columns.Count; i++)
        {
            var column = new NavigatorColumnViewModel(this, applied.Columns[i], i);
            column.PropertyChanged += OnColumnChanged;
            Columns.Add(column);
        }
        if (settings.SortColumn != null && !Columns.Any(column => column.Id == settings.SortColumn)) settings.SortColumn = null;
        UpdateSortGlyphs();
        Rebuild();
        Notify(nameof(Profile));
        Notify(nameof(SelectedProfile));
        Notify(nameof(VisibleColumns));
        EditProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyAll()
    {
        Notify(nameof(TreeWidth));
        Notify(nameof(EmptyText));
        Notify(nameof(Diagnostics));
        Notify(nameof(HasDiagnostics));
    }

    // ---- columns ----

    private void OnColumnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NavigatorColumnViewModel.Width)) IsDirty = true;
        else if (e.PropertyName is nameof(NavigatorColumnViewModel.Visible))
        {
            IsDirty = true;
            Notify(nameof(VisibleColumns));
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// A column of the view was dragged from <paramref name="from"/> to <paramref name="to"/> (indexes among the visible
    /// columns): the order of the profile changes, the data is not rebuilt.
    /// </summary>
    public void MoveVisibleColumn(int from, int to)
    {
        if (profile is null) return;
        var visible = Columns.Where(column => column.Visible).ToList();
        if (from < 0 || from >= visible.Count || to < 0 || to >= visible.Count || from == to) return;
        var moved = visible[from];
        visible.RemoveAt(from);
        visible.Insert(to, moved);
        var order = new List<NavigatorColumnViewModel>();
        var next = 0;
        foreach (var column in Columns) order.Add(column.Visible ? visible[next++] : column);
        for (var i = 0; i < order.Count; i++)
        {
            var current = Columns.IndexOf(order[i]);
            if (current != i) Columns.Move(current, i);
        }
        profile.Columns.Clear();
        profile.Columns.AddRange(Columns.Select(column => column.Column));
        IsDirty = true;
        Notify(nameof(VisibleColumns));
    }

    /// <summary>Sorts leaves by <paramref name="column"/> (null — by name); the same column again reverses the order.</summary>
    public void SortBy(NavigatorColumnViewModel? column)
    {
        var id = column?.Id;
        if (settings.SortColumn == id) settings.SortDescending = !settings.SortDescending;
        else { settings.SortColumn = id; settings.SortDescending = false; }
        settingsDirty = true;
        UpdateSortGlyphs();
        if (tree != null) { Sort(tree); Flatten(); }
        SaveSettings();
    }

    private void UpdateSortGlyphs()
    {
        foreach (var column in Columns)
            column.SortGlyph = column.Id == settings.SortColumn ? settings.SortDescending ? " ▼" : " ▲" : "";
        Notify(nameof(SortColumn));
        Notify(nameof(SortDescending));
        Notify(nameof(TreeSortGlyph));
    }

    private void Sort(NavigatorTree target)
    {
        var column = settings.SortColumn is null ? null : Columns.FirstOrDefault(item => item.Id == settings.SortColumn);
        if (column is null) target.Sort(null, settings.SortDescending);
        else target.Sort(node => node.Object is null ? "" : node.Object.Value(column.Source).Text, settings.SortDescending);
    }

    // ---- tree ----

    private void Rebuild()
    {
        if (profile is null || expression is null) return;
        tree = NavigatorTreeBuilder.Build(objects.Values, expression, settings.EmptyText);
        Sort(tree);
        warnings = expression.UnknownReferences(schema).Select(id => "Параметр " + id + " выражения дерева не найден в схеме чертежа: его объекты в группе «" + settings.EmptyText + "».")
            .Concat(Columns.SelectMany(column => column.Source.References.Select(id => (column, id)))
                .Where(item => NavigatorProfile.Unknown(new[] { item.id }, schema).Count != 0)
                .Select(item => "Параметр " + item.id + " колонки «" + item.column.Caption + "» не найден в схеме чертежа."))
            .Distinct().ToArray();
        Flatten();
        Notify(nameof(Tree));
        Notify(nameof(Diagnostics));
        Notify(nameof(HasDiagnostics));
    }

    private void Flatten()
    {
        if (tree is null) { Rows.Reset(Array.Empty<NavigatorRow>()); return; }
        var selectedKey = selectedRow?.Node.Key;
        var rows = new List<NavigatorRow>();
        void Add(NavigatorNode node)
        {
            foreach (var child in node.Children)
            {
                rows.Add(new NavigatorRow(this, child, expanded.Contains(child.Key)));
                if (child.IsGroup && expanded.Contains(child.Key)) Add(child);
            }
        }
        Add(tree.Root);
        Rows.Reset(rows);
        RestoreSelection(selectedKey);
        UpdateStatus();
    }

    private void RestoreSelection(string? key)
    {
        selecting = true;
        try { SelectedRow = key is null ? null : Rows.FirstOrDefault(row => row.Node.Key == key); }
        finally { selecting = false; }
    }

    private void UpdateStatus()
    {
        if (tree is null) { Status = ""; return; }
        var groups = tree.Nodes.Count(node => node.Kind == NavigatorNodeKind.Group);
        var text = "Объектов: " + tree.ObjectCount.ToString(CultureInfo.InvariantCulture) + " · групп: " + groups.ToString(CultureInfo.InvariantCulture);
        if (tree.ErrorCount != 0) text += " · с ошибками: " + tree.ErrorCount.ToString(CultureInfo.InvariantCulture);
        if (document is null && source != null) text = "Нет активного чертежа";
        Status = text;
    }

    internal bool IsExpanded(NavigatorNode node) => expanded.Contains(node.Key);

    /// <summary>Expands or collapses a group: its rows are inserted into or removed from <see cref="Rows"/>.</summary>
    internal void SetExpanded(NavigatorRow row, bool value)
    {
        if (!row.HasChildren) return;
        if (value ? !expanded.Add(row.Node.Key) : !expanded.Remove(row.Node.Key)) return;
        var index = Rows.IndexOf(row);
        if (index < 0) return;
        if (value)
        {
            var rows = new List<NavigatorRow>();
            void Add(NavigatorNode node)
            {
                foreach (var child in node.Children)
                {
                    rows.Add(new NavigatorRow(this, child, expanded.Contains(child.Key)));
                    if (child.IsGroup && expanded.Contains(child.Key)) Add(child);
                }
            }
            Add(row.Node);
            Rows.InsertRange(index + 1, rows);
        }
        else
        {
            var end = index + 1;
            while (end < Rows.Count && Rows[end].Depth > row.Depth) end++;
            if (selectedRow != null && Rows.IndexOf(selectedRow) is var selected && selected > index && selected < end) RestoreSelection(row.Node.Key);
            Rows.RemoveRange(index + 1, end - index - 1);
        }
    }

    private void ExpandAll(bool value)
    {
        if (tree is null) return;
        if (value) foreach (var node in tree.Nodes.Where(node => node.IsGroup)) expanded.Add(node.Key);
        else expanded.Clear();
        Flatten();
    }

    /// <summary>Expands the groups down to <paramref name="depth"/> levels (the WPF smoke test, the first look at a tree).</summary>
    public void ExpandTo(int depth)
    {
        if (tree is null) return;
        foreach (var node in tree.Nodes.Where(node => node.IsGroup && node.Depth < depth)) expanded.Add(node.Key);
        Flatten();
    }

    // ---- AutoCAD ----

    private void Activate(NavigatorRow? row)
    {
        if (row is null) return;
        if (row.Node.Kind == NavigatorNodeKind.Leaf) SelectObject(row, true);
        else row.IsExpanded = !row.IsExpanded;
    }

    private void Select(NavigatorRow? row)
    {
        if (row?.Node.Kind == NavigatorNodeKind.Leaf) SelectObject(row, false);
    }

    /// <summary>The handles of the objects under a group (of the object itself for a leaf), in the order of the tree.</summary>
    public static IReadOnlyList<string> HandlesOf(NavigatorRow row)
    {
        if (row is null) throw new ArgumentNullException(nameof(row));
        if (row.Node.Handle is string handle) return new[] { handle };
        return row.Node.Descendants().Select(node => node.Handle).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Issue #68: every object of the group (nested groups and automatic groups included) goes to the selection of
    /// AutoCAD at once. Objects erased since the last read are skipped and reported.
    /// </summary>
    private void SelectGroup(NavigatorRow? row)
    {
        if (row is null || !row.IsGroup || source is null) return;
        var handles = HandlesOf(row);
        if (handles.Count == 0) return;
        var name = "«" + row.Node.Name + "»";
        int found;
        try { found = source.SelectAll(handles); }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            SetSelectionMessage("Объекты группы " + name + " не выбраны: " + failure.Message);
            return;
        }
        if (found >= handles.Count) { SetSelectionMessage(null); return; }
        stale = true;
        SetSelectionMessage(found == 0
            ? "Объекты группы " + name + " не найдены в чертеже (удалены?). Нажмите «Обновить»."
            : "Выбрано объектов группы " + name + ": " + found.ToString(CultureInfo.InvariantCulture) + " из " +
              handles.Count.ToString(CultureInfo.InvariantCulture) + "; остальные не найдены в чертеже (удалены?). Нажмите «Обновить».");
    }

    /// <summary>A leaf goes to the standard selection of AutoCAD by its handle; a group selects nothing.</summary>
    private void SelectObject(NavigatorRow row, bool zoom)
    {
        var handle = row.Node.Handle;
        if (source is null || handle is null) return;
        bool found;
        try { found = source.Select(handle, zoom); }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            SetSelectionMessage("Объект " + handle + " не выбран: " + failure.Message);
            return;
        }
        SetSelectionMessage(found ? null : "Объект " + handle + " не найден в чертеже (удалён?). Нажмите «Обновить».");
        if (!found) stale = true;
    }

    private void OnSourceChanged(object? sender, EventArgs e)
    {
        stale = true;
        if (isShown) Refresh(false);
    }
}

/// <summary>A column of the active profile in the view: its width and visibility are edited by the header.</summary>
public sealed class NavigatorColumnViewModel : ObservableObject
{
    private readonly NavigatorViewModel owner;
    private string sortGlyph = "";

    internal NavigatorColumnViewModel(NavigatorViewModel owner, NavigatorColumn column, int slot)
    {
        this.owner = owner; Column = column; Slot = slot;
        Source = NavigatorValueSource.Parse(column.Value);
    }

    public NavigatorColumn Column { get; }
    /// <summary>The index of the cell of rows: it does not change when the column is moved.</summary>
    public int Slot { get; }
    public NavigatorValueSource Source { get; }
    public string Id => Column.Id;
    public string Caption => Column.Caption;
    public string Header => Caption + sortGlyph;
    public NavigatorViewModel Owner => owner;

    public double Width
    {
        get => Column.Width;
        set
        {
            var width = NavigatorColumn.ClampWidth(value);
            if (Math.Abs(Column.Width - width) < 0.5) return;
            Column.Width = width;
            Notify(nameof(Width));
        }
    }

    public bool Visible
    {
        get => Column.Visible;
        set
        {
            if (Column.Visible == value) return;
            Column.Visible = value;
            Notify(nameof(Visible));
        }
    }

    public string SortGlyph
    {
        get => sortGlyph;
        internal set { if (sortGlyph == value) return; sortGlyph = value; Notify(nameof(SortGlyph)); Notify(nameof(Header)); }
    }

    public override string ToString() => Caption;
}

/// <summary>A visible row of the tree: a group or an object.</summary>
public sealed class NavigatorRow : ObservableObject
{
    public const double IndentStep = 14;
    private readonly NavigatorViewModel owner;
    private bool isExpanded;

    internal NavigatorRow(NavigatorViewModel owner, NavigatorNode node, bool isExpanded)
    {
        this.owner = owner; Node = node; this.isExpanded = isExpanded;
        Cells = new NavigatorCells(owner, node);
    }

    public NavigatorNode Node { get; }
    public int Depth => Node.Depth;
    public double Indent => Depth * IndentStep;
    public string Caption => Node.Caption;
    public bool IsGroup => Node.IsGroup;
    public bool IsLeaf => !Node.IsGroup;
    public bool IsAutoGroup => Node.Kind == NavigatorNodeKind.AutoGroup;
    public bool IsError => Node.IsError;
    public bool IsEmptyValue => Node.IsEmptyValue;
    public bool HasChildren => Node.Children.Count != 0;
    public string Glyph => !HasChildren ? "" : isExpanded ? "▾" : "▸";
    /// <summary>The values of the columns by <see cref="NavigatorColumnViewModel.Slot"/>; computed when shown.</summary>
    public NavigatorCells Cells { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value || !HasChildren) return;
            isExpanded = value;
            Notify(nameof(IsExpanded));
            Notify(nameof(Glyph));
            owner.SetExpanded(this, value);
        }
    }

    /// <summary>The tooltip: the handle and type of an object, the errors of its values.</summary>
    public string ToolTip
    {
        get
        {
            var lines = new List<string>();
            if (Node.Object is NavigatorObject target)
            {
                lines.Add(target.TypeName + ", Handle " + target.Handle);
                foreach (var column in owner.Columns)
                {
                    var value = target.Value(column.Source);
                    if (value.IsError) lines.Add(column.Caption + ": " + value.Error);
                }
            }
            else
            {
                lines.Add(Node.LeafCount.ToString(CultureInfo.InvariantCulture) + " объект(ов)");
                lines.Add("Правая кнопка — выделить все устройства группы");
            }
            if (Node.Error != null) lines.Add(Node.Error);
            return string.Join("\n", lines);
        }
    }

    public override string ToString() => new string(' ', Depth * 2) + Caption;
}

/// <summary>The cells of a row: groups show their name only, so their cells are empty.</summary>
public sealed class NavigatorCells
{
    private readonly NavigatorViewModel owner;
    private readonly NavigatorNode node;

    internal NavigatorCells(NavigatorViewModel owner, NavigatorNode node) { this.owner = owner; this.node = node; }

    public NavigatorValue this[int slot]
    {
        get
        {
            if (node.Object is null) return NavigatorValue.Empty;
            var column = owner.Columns.FirstOrDefault(item => item.Slot == slot);
            return column is null ? NavigatorValue.Empty : node.Object.Value(column.Source);
        }
    }
}

/// <summary>
/// The rows of the view. WPF does not take range notifications, so big changes are one <c>Reset</c> and the
/// insertion of a few children are single notifications.
/// </summary>
public sealed class NavigatorRowList : ObservableCollection<NavigatorRow>
{
    private const int SingleNotifications = 64;

    public void Reset(IEnumerable<NavigatorRow> rows)
    {
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void InsertRange(int index, IReadOnlyList<NavigatorRow> rows)
    {
        if (rows.Count <= SingleNotifications) { for (var i = 0; i < rows.Count; i++) Insert(index + i, rows[i]); return; }
        for (var i = 0; i < rows.Count; i++) Items.Insert(index + i, rows[i]);
        Notify();
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= SingleNotifications) { for (var i = 0; i < count; i++) RemoveAt(index); return; }
        for (var i = 0; i < count; i++) Items.RemoveAt(index);
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
