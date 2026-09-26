using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SecureMaid.Controls;
using SecureMaid.Helpers;
using SecureMaid.Managers;
using SecureMaid.Models;

namespace SecureMaid.Views;

public partial class DashboardView : UserControl
{
    public event EventHandler? LockRequested;

    private readonly VaultManager _vault = new();
    private readonly HideManager _hide = new();
    private readonly DisguiseRegistry _disguise = new();

    // Contraseñas propias de elementos que se dejaron "visibles temporalmente" (Descamuflar
    // rapido), para no volver a pedirlas al usar "Volver a camuflar" en la misma sesion.
    // Nunca se guarda en disco: vive solo en memoria mientras la app esta abierta.
    private readonly Dictionary<string, string> _revealedCustomPasswords = new();

    // Carpeta actual dentro de la caja fuerte (null = raiz).
    private string? _currentFolderId = null;

    private bool _loadingTheme;

    public DashboardView()
    {
        InitializeComponent();
        LoadIcons();
        LoadLogoOptions();
        LoadSidebarIdentity();
        LoadThemeOptions();
        LoadChangePasswordSection();
        LoadSecurityQuestionSection();
        InitializeData();

        // Las columnas de estas listas tenian ancho fijo: al achicar la ventana no
        // habia espacio para todas y el contenido se "mochaba" (se cortaba texto,
        // por ejemplo la fecha). Con esto, una columna de cada lista (la del nombre)
        // se encoge o crece para ocupar el espacio real disponible.
        AttachAutoFillColumn(VaultList, flexibleColumnIndex: 1, minWidth: 140);
        AttachAutoFillColumn(HideList, flexibleColumnIndex: 0, minWidth: 160);
        AttachAutoFillColumn(DisguiseList, flexibleColumnIndex: 0, minWidth: 100);
    }

    /// <summary>Hace que una columna de un GridView (por ejemplo, "Nombre") se
    /// achique o crezca automaticamente para ocupar el ancho real del ListView,
    /// en vez de quedar con un ancho fijo que se corta cuando la ventana es angosta.</summary>
    private static void AttachAutoFillColumn(ListView listView, int flexibleColumnIndex, double minWidth)
    {
        void Resize()
        {
            if (listView.View is not GridView gridView || gridView.Columns.Count <= flexibleColumnIndex) return;

            double otherColumnsWidth = 0;
            for (int i = 0; i < gridView.Columns.Count; i++)
            {
                if (i != flexibleColumnIndex) otherColumnsWidth += gridView.Columns[i].ActualWidth;
            }

            // Margen para el borde de la tarjeta y una eventual barra de scroll vertical.
            double available = listView.ActualWidth - otherColumnsWidth - 28;
            gridView.Columns[flexibleColumnIndex].Width = Math.Max(minWidth, available);
        }

        listView.Loaded += (_, __) => Resize();
        listView.SizeChanged += (_, __) => Resize();
    }

    // ---------------- Barra lateral: logo y nombre ----------------

    private void LoadSidebarIdentity()
    {
        SidebarAppName.Text = PasswordManager.Settings.DisguisedAppName;
        var bmp = LogoManager.LoadCurrentLogoBitmap(130);
        if (bmp != null) SidebarLogo.Source = bmp;
    }

    // ---------------- Apariencia (tema oscuro/claro) ----------------

    private void LoadThemeOptions()
    {
        _loadingTheme = true;
        ThemeDarkOption.IsChecked = ThemeManager.Current == ThemeManager.Dark;
        ThemeLightOption.IsChecked = ThemeManager.Current == ThemeManager.Light;
        _loadingTheme = false;
    }

    private void OnThemeOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingTheme) return;
        string theme = ReferenceEquals(sender, ThemeLightOption) ? ThemeManager.Light : ThemeManager.Dark;
        ThemeManager.Apply(theme);
    }

    // El mouse pasando sobre un TextBox/PasswordBox/ComboBox ya no frena el
    // scroll del panel (ver Helpers/ScrollBehavior.cs para el detalle).
    private void OnPanelScroll(object sender, MouseWheelEventArgs e) => ScrollBehavior.RedirectToScrollViewer(sender, e);

    // ---------------- Inicializacion ----------------

    private void LoadIcons()
    {
        foreach (var (label, source) in DisguiseManager.SuggestedIcons)
        {
            DisguiseIconCombo.Items.Add(new ComboBoxItem { Content = label, Tag = source });
            ShortcutIconCombo.Items.Add(new ComboBoxItem { Content = label, Tag = source });
        }
        DisguiseIconCombo.SelectedIndex = 0;
        ShortcutIconCombo.SelectedIndex = 0;
        AppNameBox.Text = PasswordManager.Settings.DisguisedAppName;
        DecoyCheck.IsChecked = PasswordManager.Settings.DecoyModeEnabled;
    }

    private void LoadLogoOptions()
    {
        var current = PasswordManager.Settings.LogoFileName;
        var options = new List<LogoOptionVM>();
        foreach (var fileName in LogoManager.GetAvailableLogos())
        {
            BitmapImage? preview = null;
            try
            {
                preview = new BitmapImage();
                preview.BeginInit();
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.DecodePixelWidth = 96;
                preview.UriSource = new Uri(LogoManager.GetLogoPath(fileName), UriKind.Absolute);
                preview.EndInit();
                preview.Freeze();
            }
            catch { /* si un logo no carga, simplemente no se muestra su miniatura */ }

            options.Add(new LogoOptionVM
            {
                FileName = fileName,
                Label = Path.GetFileNameWithoutExtension(fileName),
                Preview = preview,
                IsSelected = string.Equals(fileName, current, StringComparison.OrdinalIgnoreCase)
            });
        }
        LogoList.ItemsSource = options;
    }

    private bool EnsurePassword()
    {
        if (!string.IsNullOrEmpty(Session.CurrentPassword)) return true;
        AppMessageBox.Show("Tu sesion expiro. Vuelve a iniciar sesion.", "SecureMaid",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        LockRequested?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private void InitializeData()
    {
        if (!EnsurePassword()) return;
        string pwd = Session.CurrentPassword!;

        if (!_vault.LoadIndex(pwd) || !_hide.LoadIndex(pwd) || !_disguise.LoadIndex(pwd))
        {
            AppMessageBox.Show("No se pudo desbloquear tus datos con esta contraseña.", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            LockRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        ReloadVaultListView();
        ReloadHideListView();
        ReloadDisguiseListView();
    }

    // ---------------- Caja fuerte: listado, carpetas y miniaturas ----------------

    private void ReloadVaultListView()
    {
        var rows = _vault.GetChildren(_currentFolderId).Select(item => new VaultRowVM(item)).ToList();
        VaultList.ItemsSource = rows;

        UpdateBreadcrumb();

        // Genera miniaturas para las imagenes en segundo plano, sin bloquear la UI.
        string? pwd = Session.CurrentPassword;
        if (pwd == null) return;
        foreach (var row in rows.Where(r => r.Item.Kind == ItemKind.Image))
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                byte[]? bytes = _vault.DecryptToBytes(row.Item, pwd);
                if (bytes == null) return;
                try
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(bytes))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.DecodePixelWidth = 88;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                    Dispatcher.Invoke(() => row.Thumbnail = bmp);
                }
                catch { /* imagen corrupta o formato no soportado: se queda con el icono generico */ }
            });
        }
    }

    private void UpdateBreadcrumb()
    {
        var chain = new List<string>();
        string? id = _currentFolderId;
        while (id != null)
        {
            var folder = _vault.FindById(id);
            if (folder == null) break;
            chain.Insert(0, folder.OriginalName);
            id = folder.ParentFolderId;
        }
        chain.Insert(0, "Caja fuerte");
        VaultBreadcrumb.Text = string.Join("  ›  ", chain);
        VaultBackButton.Visibility = _currentFolderId == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnVaultGoUp(object sender, RoutedEventArgs e)
    {
        var current = _currentFolderId != null ? _vault.FindById(_currentFolderId) : null;
        _currentFolderId = current?.ParentFolderId;
        ReloadVaultListView();
    }

    private void OnCreateVaultFolder(object sender, RoutedEventArgs e)
    {
        if (!EnsurePassword()) return;
        string? name = InputDialog.Show(Window.GetWindow(this), "Nueva carpeta", "Nombre de la carpeta:");
        if (name == null) return;

        _vault.CreateFolder(name, Session.CurrentPassword!, _currentFolderId);
        ReloadVaultListView();
        ExplorerRefresh.RefreshAll();
    }

    private void OnVaultItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (VaultList.SelectedItem is not VaultRowVM row) return;

        if (row.Item.Kind == ItemKind.Folder)
        {
            _currentFolderId = row.Item.Id;
            ReloadVaultListView();
            return;
        }

        if (!EnsurePassword()) return;
        string pwd = Session.CurrentPassword!;

        if (row.Item.Kind == ItemKind.Image || row.Item.Kind == ItemKind.Video)
        {
            // Todas las imagenes/videos de esta misma carpeta, en el mismo orden
            // que se ven en la lista, para poder pasar de uno a otro con las flechas.
            var navigable = _vault.GetChildren(_currentFolderId)
                .Where(i => i.Kind == ItemKind.Image || i.Kind == ItemKind.Video)
                .ToList();
            int startIndex = navigable.FindIndex(i => i.Id == row.Item.Id);
            if (startIndex < 0) startIndex = 0;

            new MediaPreviewWindow(_vault, pwd, navigable, startIndex) { Owner = Window.GetWindow(this) }.Show();
        }
        else
        {
            AppMessageBox.Show("Este tipo de archivo no tiene vista previa. Usa 'Extraer' para abrirlo con su programa habitual.",
                "Sin vista previa", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ---------------- Navegacion del menu lateral ----------------

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (PanelVault == null) return; // durante inicializacion de XAML

        PanelVault.Visibility = NavVault.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelHide.Visibility = NavHide.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelDisguise.Visibility = NavDisguise.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelRecent.Visibility = NavRecent.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavVault.IsChecked == true) ReloadVaultListView();
        if (NavHide.IsChecked == true) ReloadHideListView();
        if (NavDisguise.IsChecked == true) ReloadDisguiseListView();
    }

    private void OnLockClick(object sender, RoutedEventArgs e)
    {
        Session.Clear();
        LockRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- Caja fuerte: agregar / extraer / eliminar ----------------

    private void OnAddToVault(object sender, RoutedEventArgs e)
    {
        if (!EnsurePassword()) return;
        var dlg = new OpenFileDialog { Title = "Selecciona el archivo a proteger", Multiselect = true };
        if (dlg.ShowDialog() != true) return;

        foreach (var file in dlg.FileNames)
        {
            try { _vault.AddFile(file, Session.CurrentPassword!, parentFolderId: _currentFolderId); }
            catch (Exception ex) { AppMessageBox.Show($"No se pudo agregar '{Path.GetFileName(file)}': {ex.Message}"); }
        }
        ReloadVaultListView();
        ExplorerRefresh.RefreshAll();
        AppMessageBox.Show("Archivo(s) cifrados y guardados en la caja fuerte. El original se borro de su ubicacion.",
            "Listo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Extrae los archivos y/o carpetas seleccionados de la caja fuerte.
    /// Si hay carpetas seleccionadas, extrae todo su contenido recursivamente
    /// recreando la misma estructura de subcarpetas. Al terminar, pregunta si se
    /// quiere quitar de la caja fuerte lo ya extraido (mover) o dejar la copia
    /// cifrada adentro (copiar), que es lo que hacia antes siempre.</summary>
    private void OnExtractFromVault(object sender, RoutedEventArgs e)
    {
        if (!EnsurePassword()) return;
        var selected = VaultList.SelectedItems.Cast<VaultRowVM>().Select(r => r.Item).ToList();

        if (selected.Count == 0)
        {
            AppMessageBox.Show("Selecciona uno o mas archivos o carpetas de la lista primero.");
            return;
        }

        // Junta los archivos a extraer: los sueltos tal cual, y todo el contenido
        // (recursivo) de las carpetas seleccionadas, con su ruta relativa para
        // recrear la misma estructura de subcarpetas afuera.
        var toExtract = new List<(VaultItem Item, string RelativePath)>();
        foreach (var item in selected)
        {
            if (item.Kind == ItemKind.Folder)
                toExtract.AddRange(_vault.GetAllDescendantFiles(item)
                    .Select(f => (f.File, Path.Combine(item.OriginalName, f.RelativePath))));
            else
                toExtract.Add((item, item.OriginalName));
        }

        if (toExtract.Count == 0)
        {
            AppMessageBox.Show("La seleccion no tiene archivos para extraer (las carpetas seleccionadas estan vacias).");
            return;
        }

        var choice = AppMessageBox.Show(
            "¿Que hacer con los archivos despues de extraerlos?\n\n" +
            "Sí: se quitan de la caja fuerte (mover).\n" +
            "No: se deja una copia cifrada en la caja fuerte (copiar).",
            "Extraer", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        bool removeAfterExtract = choice == MessageBoxResult.Yes;

        // Un solo archivo suelto (sin carpetas de por medio): se puede elegir nombre y destino exactos.
        if (toExtract.Count == 1 && selected.Count == 1 && selected[0].Kind != ItemKind.Folder)
        {
            var item = toExtract[0].Item;
            var saveDlg = new SaveFileDialog { FileName = item.OriginalName, Title = "Guardar archivo extraido" };
            if (saveDlg.ShowDialog() != true) return;

            bool ok = _vault.ExtractFile(item, Session.CurrentPassword!, saveDlg.FileName);
            if (ok && removeAfterExtract) _vault.RemoveFile(item, Session.CurrentPassword!);
            if (ok) ExplorerRefresh.RefreshAll();
            ReloadVaultListView();
            AppMessageBox.Show(ok ? "Archivo extraido correctamente." : "No se pudo descifrar (contraseña incorrecta o archivo dañado).",
                "Extraer", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            return;
        }

        var folderDlg = new OpenFolderDialog { Title = "Selecciona la carpeta donde guardar los archivos extraidos" };
        if (folderDlg.ShowDialog() != true) return;

        int okCount = 0;
        var extractedItems = new List<VaultItem>();
        foreach (var (item, relativePath) in toExtract)
        {
            string destination = Path.Combine(folderDlg.FolderName, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            destination = MakeUniquePath(destination);
            if (_vault.ExtractFile(item, Session.CurrentPassword!, destination))
            {
                okCount++;
                extractedItems.Add(item);
            }
        }

        if (removeAfterExtract)
        {
            foreach (var item in extractedItems)
                _vault.RemoveFile(item, Session.CurrentPassword!);

            // Las carpetas seleccionadas (y sus subcarpetas) que quedaron vacias
            // porque todo su contenido se extrajo y se quito, tambien se limpian.
            foreach (var folder in selected.Where(i => i.Kind == ItemKind.Folder))
                _vault.PruneEmptyFolder(folder, Session.CurrentPassword!);
        }

        if (okCount > 0) ExplorerRefresh.RefreshAll();
        ReloadVaultListView();
        AppMessageBox.Show($"{okCount} de {toExtract.Count} archivo(s) extraidos correctamente.",
            "Extraer", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int i = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            i++;
        } while (File.Exists(candidate));
        return candidate;
    }

    private void OnDeleteFromVault(object sender, RoutedEventArgs e)
    {
        if (!EnsurePassword()) return;
        var selected = VaultList.SelectedItems.Cast<VaultRowVM>().Select(r => r.Item).ToList();
        if (selected.Count == 0) return;

        string message = selected.Count == 1
            ? $"¿Eliminar '{selected[0].OriginalName}' de forma permanente? Esto no se puede deshacer."
            : $"¿Eliminar {selected.Count} elementos seleccionados de forma permanente? Esto no se puede deshacer.";

        var confirm = AppMessageBox.Show(message, "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var item in selected)
            _vault.RemoveFile(item, Session.CurrentPassword!);

        ReloadVaultListView();
        ExplorerRefresh.RefreshAll();
    }

    // ---------------- Ocultar ----------------

    private void ReloadHideListView()
    {
        HideList.ItemsSource = null;
        HideList.ItemsSource = _hide.Items;
    }

    private void OnHideFolder(object sender, RoutedEventArgs e)
    {
        if (!EnsurePassword()) return;
        var dlg = new OpenFolderDialog { Title = "Selecciona la carpeta a ocultar" };
        if (dlg.ShowDialog() != true) return;

        bool invisible = ChkHideInvisible.IsChecked == true;
        try
        {
            if (invisible)
                _hide.HideInvisible(dlg.FolderName, Session.CurrentPassword!);
            else
                _hide.HideByMoving(dlg.FolderName, Session.CurrentPassword!);

            ReloadHideListView();
            ExplorerRefresh.RefreshAll();
            AppMessageBox.Show(
                invisible
                    ? "Carpeta ocultada de forma parcial: sigue en su sitio, pero sin nombre ni icono."
                    : "Carpeta oculta. Ya no aparecera en el Explorador, galerias ni busquedas.",
                "Listo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show("No se pudo ocultar: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnHideFile(object sender, RoutedEventArgs e)
    {
        if (!EnsurePassword()) return;
        var dlg = new OpenFileDialog { Title = "Selecciona el archivo a ocultar", Multiselect = true };
        if (dlg.ShowDialog() != true) return;

        bool invisible = ChkHideInvisible.IsChecked == true;
        foreach (var file in dlg.FileNames)
        {
            try
            {
                if (invisible)
                    _hide.HideInvisible(file, Session.CurrentPassword!);
                else
                    _hide.HideByMoving(file, Session.CurrentPassword!);
            }
            catch (Exception ex) { AppMessageBox.Show($"No se pudo ocultar '{Path.GetFileName(file)}': {ex.Message}"); }
        }
        ReloadHideListView();
        ExplorerRefresh.RefreshAll();
    }

    private void OnUnhide(object sender, RoutedEventArgs e)
    {
        if (!EnsurePassword()) return;
        if (HideList.SelectedItem is not HiddenItem item) return;

        try
        {
            _hide.Unhide(item, Session.CurrentPassword!);
            ReloadHideListView();
            ExplorerRefresh.RefreshAll();
        }
        catch (Exception ex)
        {
            AppMessageBox.Show("No se pudo restaurar: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenHidden(object sender, RoutedEventArgs e)
    {
        if (HideList.SelectedItem is not HiddenItem item) return;

        try { _hide.Open(item); }
        catch (Exception ex)
        {
            AppMessageBox.Show("No se pudo abrir: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------- Camuflar ----------------

    private void ReloadDisguiseListView()
    {
        DisguiseList.ItemsSource = null;
        DisguiseList.ItemsSource = _disguise.Items;
        DisguiseEmptyText.Visibility = _disguise.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPickDisguiseFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Selecciona la carpeta a camuflar" };
        if (dlg.ShowDialog() == true)
        {
            DisguisePathBox.Text = dlg.FolderName;
            DisguiseNameBox.Text = "";
            UpdateDisguiseTargetUi();
        }
    }

    private void OnPickDisguiseFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Selecciona el archivo a camuflar" };
        if (dlg.ShowDialog() == true)
        {
            DisguisePathBox.Text = dlg.FileName;
            DisguiseNameBox.Text = "";
            UpdateDisguiseTargetUi();
        }
    }

    // Los archivos no pueden tener un icono propio (Windows lo toma de la extension),
    // asi que el selector de icono solo aplica a carpetas.
    private void UpdateDisguiseTargetUi()
    {
        bool isFile = File.Exists(DisguisePathBox.Text);
        DisguiseIconCombo.IsEnabled = !isFile;
        DisguiseFileIconHint.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Arrastrar y soltar (Caja fuerte / Ocultar / Camuflar) ----------------
    //
    // Los tres paneles aceptan que se arrastren archivos o carpetas directo desde el
    // Explorador de Windows, en vez de tener que pasar siempre por el dialogo de
    // seleccion. Reutilizan exactamente la misma logica que ya usan los botones
    // "+ Agregar archivo" / "+ Ocultar..." / "Elegir...", asi que el resultado es
    // identico sea cual sea el camino que uses.

    private static string[] GetDroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? (string[])e.Data.GetData(DataFormats.FileDrop)! : Array.Empty<string>();

    // Comun a los tres: mientras el mouse esta arrastrando algo por encima, muestra el
    // cursor de "copiar" solo si lo que se arrastra son archivos reales del Explorador.
    private void OnDragOverShowFileCopy(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ---- Caja fuerte ----

    private void OnVaultDragEnter(object sender, DragEventArgs e)
    {
        OnDragOverShowFileCopy(sender, e);
        VaultDropHint.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVaultDragLeave(object sender, DragEventArgs e) => VaultDropHint.Visibility = Visibility.Collapsed;

    private void OnVaultDrop(object sender, DragEventArgs e)
    {
        VaultDropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (!EnsurePassword()) return;

        var paths = GetDroppedPaths(e);
        if (paths.Length == 0) return;

        int errors = 0;
        foreach (var path in paths)
        {
            try { AddPathToVaultRecursive(path, _currentFolderId); }
            catch (Exception ex)
            {
                errors++;
                AppMessageBox.Show($"No se pudo agregar '{Path.GetFileName(path)}': {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        ReloadVaultListView();
        ExplorerRefresh.RefreshAll();
        if (errors == 0)
        {
            AppMessageBox.Show(
                paths.Length == 1 ? "Elemento agregado a la caja fuerte." : $"{paths.Length} elementos agregados a la caja fuerte.",
                "Listo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // Si es un archivo lo cifra y agrega directo; si es una carpeta, crea la carpeta
    // dentro de la caja fuerte y agrega su contenido recursivamente (igual que si
    // hicieras "Nueva carpeta" y luego fueras arrastrando cada archivo a mano).
    private void AddPathToVaultRecursive(string path, string? parentFolderId)
    {
        if (Directory.Exists(path))
        {
            string folderName = new DirectoryInfo(path).Name;
            var folder = _vault.CreateFolder(folderName, Session.CurrentPassword!, parentFolderId);
            foreach (var entry in Directory.GetFileSystemEntries(path))
                AddPathToVaultRecursive(entry, folder.Id);
        }
        else if (File.Exists(path))
        {
            _vault.AddFile(path, Session.CurrentPassword!, parentFolderId: parentFolderId);
        }
    }

    // ---- Ocultar ----

    private void OnHideDragEnter(object sender, DragEventArgs e)
    {
        OnDragOverShowFileCopy(sender, e);
        HideDropHint.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHideDragLeave(object sender, DragEventArgs e) => HideDropHint.Visibility = Visibility.Collapsed;

    private void OnHideDrop(object sender, DragEventArgs e)
    {
        HideDropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (!EnsurePassword()) return;

        var paths = GetDroppedPaths(e);
        if (paths.Length == 0) return;

        bool invisible = ChkHideInvisible.IsChecked == true;
        int errors = 0;
        foreach (var path in paths)
        {
            try
            {
                if (invisible) _hide.HideInvisible(path, Session.CurrentPassword!);
                else _hide.HideByMoving(path, Session.CurrentPassword!);
            }
            catch (Exception ex)
            {
                errors++;
                AppMessageBox.Show($"No se pudo ocultar '{Path.GetFileName(path)}': {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        ReloadHideListView();
        ExplorerRefresh.RefreshAll();
        if (errors == 0)
        {
            AppMessageBox.Show(
                invisible ? "Ocultado de forma parcial." : "Oculto. Ya no aparecera en el Explorador, galerias ni busquedas.",
                "Listo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ---- Camuflar ----

    private void OnDisguiseDragEnter(object sender, DragEventArgs e)
    {
        OnDragOverShowFileCopy(sender, e);
        DisguiseDropHint.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDisguiseDragLeave(object sender, DragEventArgs e) => DisguiseDropHint.Visibility = Visibility.Collapsed;

    private void OnDisguiseDrop(object sender, DragEventArgs e)
    {
        DisguiseDropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;

        var paths = GetDroppedPaths(e);
        if (paths.Length == 0) return;

        if (paths.Length > 1)
        {
            AppMessageBox.Show("Solo se puede camuflar un elemento a la vez: se tomo el primero que soltaste.",
                "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        DisguisePathBox.Text = paths[0];
        DisguiseNameBox.Text = "";
        UpdateDisguiseTargetUi();
    }

    private void OnDisguiseProtectToggled(object sender, RoutedEventArgs e)
    {
        if (DisguiseProtectPanel == null) return; // durante inicializacion de XAML
        bool on = DisguiseProtectCheck.IsChecked == true;
        DisguiseProtectPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) ResetDisguiseProtectionFields();
    }

    private void OnDisguisePwdModeChanged(object sender, RoutedEventArgs e)
    {
        if (DisguisePwdCustomPanel == null) return; // durante inicializacion de XAML
        bool custom = DisguisePwdCustomOption.IsChecked == true;
        bool master = DisguisePwdMasterOption.IsChecked == true;
        DisguisePwdCustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        DisguisePwdMasterHint.Visibility = master ? Visibility.Visible : Visibility.Collapsed;
        if (custom) DisguisePwdBox.FocusInput();
    }

    private void ResetDisguiseProtectionFields()
    {
        DisguisePwdMasterOption.IsChecked = false;
        DisguisePwdCustomOption.IsChecked = false;
        DisguisePwdBox.Clear();
        DisguisePwdBox2.Clear();
        DisguisePwdMasterHint.Visibility = Visibility.Collapsed;
        DisguisePwdCustomPanel.Visibility = Visibility.Collapsed;
    }

    private void OnApplyDisguise(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DisguisePathBox.Text))
        {
            AppMessageBox.Show("Primero elige una carpeta o un archivo.");
            return;
        }
        if (string.IsNullOrWhiteSpace(DisguiseNameBox.Text))
        {
            AppMessageBox.Show("Escribe el nuevo nombre visible.");
            return;
        }
        if (!EnsurePassword()) return;

        string target = DisguisePathBox.Text;
        bool isFile = File.Exists(target);
        string newName = DisguiseNameBox.Text.Trim();

        string? nameError = DisguiseRegistry.ValidateName(newName);
        if (nameError != null)
        {
            AppMessageBox.Show(nameError);
            return;
        }

        string iconSource = "";
        if (!isFile)
        {
            iconSource = ((ComboBoxItem)DisguiseIconCombo.SelectedItem).Tag.ToString()!;
            iconSource = Environment.ExpandEnvironmentVariables(iconSource);
        }

        // Proteccion opcional: si la activa, debe elegir explicitamente que contraseña usar.
        var protection = DisguiseProtection.None;
        string? customPassword = null;
        if (DisguiseProtectCheck.IsChecked == true)
        {
            if (DisguisePwdMasterOption.IsChecked == true)
            {
                protection = DisguiseProtection.MasterPassword;
            }
            else if (DisguisePwdCustomOption.IsChecked == true)
            {
                string p1 = DisguisePwdBox.Password;
                string p2 = DisguisePwdBox2.Password;
                if (p1.Length < 6)
                {
                    AppMessageBox.Show("La contraseña debe tener al menos 6 caracteres.");
                    return;
                }
                if (p1 != p2)
                {
                    AppMessageBox.Show("Las contraseñas no coinciden.");
                    return;
                }
                protection = DisguiseProtection.CustomPassword;
                customPassword = p1;
            }
            else
            {
                AppMessageBox.Show("Elige que contraseña quieres usar: la contraseña general de la app o una nueva e independiente para este elemento.");
                return;
            }

            string warning = $"Vas a proteger '{newName}' con contraseña: su contenido se cifrara y no se podra abrir hasta que lo descamufles con la contraseña correcta.";
            if (protection == DisguiseProtection.CustomPassword)
                warning += "\n\nEsta contraseña no se puede recuperar si la olvidas.";
            warning += "\n\n¿Continuar?";

            var confirm = AppMessageBox.Show(warning, "Proteger con contraseña", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        Exception? error = null;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _disguise.Camouflage(target, newName, iconSource, protection, customPassword, Session.CurrentPassword!);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        ReloadDisguiseListView();
        ExplorerRefresh.RefreshAll();

        if (error != null)
        {
            AppMessageBox.Show("No se pudo camuflar: " + error.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string done = isFile ? $"Archivo camuflado como '{newName}'" : $"Carpeta camuflada como '{newName}'";
        done += protection == DisguiseProtection.None ? "." : " y protegida con contraseña.";
        if (_disguise.LastWarning != null) done += "\n\n" + _disguise.LastWarning;
        AppMessageBox.Show(done, "Listo", MessageBoxButton.OK, MessageBoxImage.Information);

        // El elemento ya esta en la lista de abajo: se limpia el formulario para el siguiente.
        DisguisePathBox.Text = "";
        DisguiseNameBox.Text = "";
        DisguiseProtectCheck.IsChecked = false; // tambien reinicia las opciones de contraseña
        UpdateDisguiseTargetUi();
    }

    // ---------------- Descamuflar ----------------

    private void OnUndisguise(object sender, RoutedEventArgs e)
    {
        if (DisguiseList.SelectedItem is not DisguisedItem item)
        {
            AppMessageBox.Show("Selecciona primero un elemento de la lista.");
            return;
        }
        UndisguiseItem(item);
    }

    private void UndisguiseItem(DisguisedItem item)
    {
        if (!EnsurePassword()) return;

        string? customPassword = null;
        string? retryError = null;

        while (true)
        {
            // Contraseña propia: se pide cada vez. La general ya se introdujo al abrir la app.
            if (item.Protection == DisguiseProtection.CustomPassword)
            {
                customPassword = PasswordPromptDialog.Show(Window.GetWindow(this), "Descamuflar",
                    $"Escribe la contraseña de '{item.DisplayName}':", retryError);
                if (customPassword == null) return; // cancelo
            }

            var result = UndisguiseResult.Success;
            Exception? failure = null;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                result = _disguise.Uncamouflage(item, Session.CurrentPassword!, customPassword);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (failure != null)
            {
                HandleUndisguiseFailure(item, failure);
                ReloadDisguiseListView();
                return;
            }

            if (result == UndisguiseResult.WrongPassword)
            {
                if (item.Protection == DisguiseProtection.CustomPassword)
                {
                    retryError = "Contraseña incorrecta. Inténtalo de nuevo.";
                    continue;
                }
                AppMessageBox.Show("No se pudo desbloquear este elemento con la contraseña general actual.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            break;
        }

        _revealedCustomPasswords.Remove(item.Id);
        ReloadDisguiseListView();
        ExplorerRefresh.RefreshAll();
        string done = $"'{item.DisplayName}' vuelve a llamarse '{item.OriginalName}' y ya no esta camuflado.";
        if (_disguise.LastWarning != null) done += "\n\n" + _disguise.LastWarning;
        AppMessageBox.Show(done, "Listo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void HandleUndisguiseFailure(DisguisedItem item, Exception ex)
    {
        if (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            string text = $"No se encontro '{item.DisplayName}' en su ubicacion:\n{item.CurrentPath}\n\n" +
                          "Pudo haberse movido, renombrado o borrado fuera de la app. " +
                          "¿Quieres quitarlo de esta lista? (No se modifica ningun archivo.)";
            if (item.Protection != DisguiseProtection.None)
                text += "\n\nOjo: si esta protegido y sigue existiendo en otro lugar, no podras restaurarlo despues de quitarlo.";

            var answer = AppMessageBox.Show(text, "No encontrado", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                try { _disguise.Forget(item, Session.CurrentPassword!); _revealedCustomPasswords.Remove(item.Id); }
                catch (Exception forgetEx)
                {
                    AppMessageBox.Show("No se pudo quitar de la lista: " + forgetEx.Message, "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            return;
        }

        AppMessageBox.Show("No se pudo descamuflar: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Descamuflar rapido: descifra y muestra el contenido con su nombre original,
    /// pero SIN sacar el elemento de la lista, para poder volver a camuflarlo despues con la
    /// misma configuracion usando "Volver a camuflar".</summary>
    private void OnRevealQuick(object sender, RoutedEventArgs e)
    {
        if (DisguiseList.SelectedItem is not DisguisedItem item)
        {
            AppMessageBox.Show("Selecciona primero un elemento de la lista.");
            return;
        }
        if (item.IsRevealed)
        {
            AppMessageBox.Show(
                $"'{item.DisplayName}' ya esta visible temporalmente. Revisalo en el Explorador y, cuando termines, usa 'Volver a camuflar'.");
            return;
        }
        if (!EnsurePassword()) return;

        string? customPassword = null;
        string? retryError = null;

        while (true)
        {
            if (item.Protection == DisguiseProtection.CustomPassword)
            {
                customPassword = PasswordPromptDialog.Show(Window.GetWindow(this), "Descamuflar rápido",
                    $"Escribe la contraseña de '{item.DisplayName}':", retryError);
                if (customPassword == null) return; // cancelo
            }

            var result = UndisguiseResult.Success;
            Exception? failure = null;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                result = _disguise.RevealTemporarily(item, Session.CurrentPassword!, customPassword);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (failure != null)
            {
                HandleUndisguiseFailure(item, failure);
                ReloadDisguiseListView();
                return;
            }

            if (result == UndisguiseResult.WrongPassword)
            {
                if (item.Protection == DisguiseProtection.CustomPassword)
                {
                    retryError = "Contraseña incorrecta. Inténtalo de nuevo.";
                    continue;
                }
                AppMessageBox.Show("No se pudo desbloquear este elemento con la contraseña general actual.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            break;
        }

        // Se recuerda la contraseña propia en memoria para no volver a pedirla al re-camuflar.
        if (item.Protection == DisguiseProtection.CustomPassword && customPassword != null)
            _revealedCustomPasswords[item.Id] = customPassword;

        ReloadDisguiseListView();
        ExplorerRefresh.RefreshAll();
        AppMessageBox.Show(
            $"'{item.DisplayName}' quedó visible como '{item.OriginalName}'. Ábrelo en el Explorador y revisa lo que necesites.\n\n" +
            "Cuando termines, selecciónalo de nuevo aquí y pulsa 'Volver a camuflar' para dejarlo protegido tal como estaba.",
            "Listo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Vuelve a camuflar un elemento dejado visible con "Descamuflar rapido", con
    /// exactamente el mismo nombre, icono y proteccion que tenia. No repite el formulario.</summary>
    private void OnReCamouflage(object sender, RoutedEventArgs e)
    {
        if (DisguiseList.SelectedItem is not DisguisedItem item)
        {
            AppMessageBox.Show("Selecciona primero un elemento de la lista.");
            return;
        }
        if (!item.IsRevealed)
        {
            AppMessageBox.Show(
                "Este elemento no está visible temporalmente. Usa 'Descamuflar rápido' primero si quieres revisarlo y luego volver a camuflarlo.");
            return;
        }
        if (!EnsurePassword()) return;

        string? customPassword = null;
        if (item.Protection == DisguiseProtection.CustomPassword)
        {
            if (!_revealedCustomPasswords.TryGetValue(item.Id, out customPassword))
            {
                customPassword = PasswordPromptDialog.Show(Window.GetWindow(this), "Volver a camuflar",
                    $"Escribe otra vez la contraseña de '{item.DisplayName}' para volver a protegerlo:", null);
                if (customPassword == null) return; // cancelo
            }
        }

        Exception? error = null;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _disguise.ReCamouflage(item, Session.CurrentPassword!, customPassword);
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        ReloadDisguiseListView();
        ExplorerRefresh.RefreshAll();

        if (error != null)
        {
            AppMessageBox.Show("No se pudo volver a camuflar: " + error.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _revealedCustomPasswords.Remove(item.Id);

        string done = $"'{item.DisplayName}' vuelve a estar camuflado, con la misma protección de antes.";
        if (_disguise.LastWarning != null) done += "\n\n" + _disguise.LastWarning;
        AppMessageBox.Show(done, "Listo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Para carpetas camufladas ANTES de que existiera el registro: no hay datos guardados
    /// de su nombre original, asi que se elige la carpeta y se escribe el nombre que debe tener.</summary>
    private void OnUndisguiseLegacyFolder(object sender, RoutedEventArgs e)
    {
        if (!EnsurePassword()) return;

        var dlg = new OpenFolderDialog { Title = "Selecciona la carpeta camuflada" };
        if (dlg.ShowDialog() != true) return;

        string folder = Path.TrimEndingDirectorySeparator(dlg.FolderName);

        // Si en realidad si esta registrada, se usa el flujo normal (recupera el nombre solo).
        var registered = _disguise.FindByPath(folder);
        if (registered != null)
        {
            UndisguiseItem(registered);
            return;
        }

        string? name = InputDialog.Show(Window.GetWindow(this), "Descamuflar carpeta",
            "Nombre con el que quieres que se llame la carpeta:", Path.GetFileName(folder));
        if (name == null) return;

        string? nameError = DisguiseRegistry.ValidateName(name);
        if (nameError != null)
        {
            AppMessageBox.Show(nameError);
            return;
        }

        string? parent = Path.GetDirectoryName(folder);
        if (parent != null)
        {
            string newPath = Path.Combine(parent, name);
            if (!string.Equals(folder, newPath, StringComparison.OrdinalIgnoreCase) &&
                (Directory.Exists(newPath) || File.Exists(newPath)))
            {
                AppMessageBox.Show($"Ya existe un elemento llamado '{name}' en esa ubicacion.");
                return;
            }
        }

        try
        {
            DisguiseManager.RemoveDisguise(folder, name);
            AppMessageBox.Show($"La carpeta ya no esta camuflada y se llama '{name}'.", "Listo",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppMessageBox.Show("No se pudo descamuflar: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------- Recientes ----------------

    private void OnClearRecentShortcuts(object sender, RoutedEventArgs e)
    {
        RecentCleaner.ClearAllRecentShortcuts();
        RecentStatus.Text = "Accesos de 'Reciente' eliminados.";
    }

    private void OnClearJumpLists(object sender, RoutedEventArgs e)
    {
        RecentCleaner.ClearAllJumpLists();
        RecentStatus.Text = "Jump Lists eliminadas.";
    }

    // ---------------- Ajustes: cambiar contraseña ----------------

    private void LoadChangePasswordSection()
    {
        if (PasswordManager.HasSecurityQuestion())
        {
            ChangePasswordAnswerPanel.Visibility = Visibility.Visible;
            ChangePasswordQuestionText.Text = "Para mantener tu recuperación funcionando, confirma tu respuesta a: "
                + PasswordManager.Settings.SecurityQuestion;
        }
        else
        {
            ChangePasswordAnswerPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void OnChangePassword(object sender, RoutedEventArgs e)
    {
        ChangePasswordError.Visibility = Visibility.Collapsed;
        ChangePasswordStatus.Text = "";

        string oldPwd = OldPasswordBox.Password;
        string newPwd = NewPasswordBoxSettings.Password;
        string newPwd2 = NewPasswordBoxSettings2.Password;

        if (string.IsNullOrEmpty(oldPwd))
        {
            ShowChangePasswordError("Escribe tu contraseña actual.");
            return;
        }
        if (newPwd.Length < 6)
        {
            ShowChangePasswordError("La nueva contraseña debe tener al menos 6 caracteres.");
            return;
        }
        if (newPwd != newPwd2)
        {
            ShowChangePasswordError("Las contraseñas nuevas no coinciden.");
            return;
        }
        if (newPwd == oldPwd)
        {
            ShowChangePasswordError("La nueva contraseña debe ser diferente a la actual.");
            return;
        }

        string? answer = PasswordManager.HasSecurityQuestion() ? ChangePasswordAnswerBox.Text : null;
        if (PasswordManager.HasSecurityQuestion() && string.IsNullOrWhiteSpace(answer))
        {
            ShowChangePasswordError("Confirma también la respuesta a tu pregunta de seguridad.");
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        PasswordChangeService.ChangeResult result;
        try
        {
            result = PasswordChangeService.ChangePassword(oldPwd, newPwd, answer);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (!result.Success)
        {
            ShowChangePasswordError(result.Error ?? "No se pudo cambiar la contraseña.");
            return;
        }

        OldPasswordBox.Clear();
        NewPasswordBoxSettings.Clear();
        NewPasswordBoxSettings2.Clear();
        ChangePasswordAnswerBox.Clear();
        ChangePasswordStatus.Text = "Contraseña actualizada correctamente.";

        // La contrasena de la sesion ya quedo actualizada; se vuelve a cargar
        // el indice de la caja fuerte y de ocultos (ya re-cifrados) para
        // seguir trabajando con normalidad sin tener que volver a iniciar sesion.
        InitializeData();
    }

    private void ShowChangePasswordError(string msg)
    {
        ChangePasswordError.Text = msg;
        ChangePasswordError.Visibility = Visibility.Visible;
    }

    // ---------------- Ajustes: pregunta de seguridad ----------------
    // Antes, la pregunta de seguridad solo se podia crear al momento de crear
    // la contraseña por primera vez. Quien ya tenia la app instalada nunca
    // llegaba a configurarla, asi que el enlace "¿Olvidaste tu contraseña?"
    // del login nunca le aparecia. Esta seccion permite crearla o cambiarla
    // en cualquier momento (usando la contraseña ya vigente en la sesion).

    private void LoadSecurityQuestionSection()
    {
        bool has = PasswordManager.HasSecurityQuestion();
        SecurityQuestionMissingText.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        SecurityQuestionCurrentText.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has)
            SecurityQuestionCurrentText.Text = "Pregunta actual: " + PasswordManager.Settings.SecurityQuestion;
        SecurityQuestionToggleButton.Content = has ? "Cambiar pregunta de seguridad" : "Configurar pregunta de seguridad";
        SecurityQuestionForm.Visibility = Visibility.Collapsed;
    }

    private void OnToggleSecurityQuestionForm(object sender, RoutedEventArgs e)
    {
        SecurityQuestionForm.Visibility = SecurityQuestionForm.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        SecurityQuestionError.Visibility = Visibility.Collapsed;
        SecurityQuestionAnswerBox.Clear();
    }

    private void OnCancelSecurityQuestionForm(object sender, RoutedEventArgs e)
    {
        SecurityQuestionForm.Visibility = Visibility.Collapsed;
    }

    private void OnSecurityQuestionComboChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SecurityQuestionCustomBox == null) return; // durante inicializacion de XAML
        bool isCustom = SecurityQuestionCombo.SelectedItem is ComboBoxItem item &&
                        string.Equals(item.Content?.ToString(), "Otra (la escribo yo)");
        SecurityQuestionCustomBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSaveSecurityQuestion(object sender, RoutedEventArgs e)
    {
        SecurityQuestionError.Visibility = Visibility.Collapsed;

        if (!EnsurePassword()) return;

        string? question = null;
        if (SecurityQuestionCombo.SelectedItem is ComboBoxItem item)
        {
            string label = item.Content?.ToString() ?? "";
            question = label == "Otra (la escribo yo)" ? SecurityQuestionCustomBox.Text.Trim() : label;
        }
        if (string.IsNullOrWhiteSpace(question))
        {
            SecurityQuestionError.Text = "Escribe tu pregunta de seguridad personalizada.";
            SecurityQuestionError.Visibility = Visibility.Visible;
            return;
        }
        if (SecurityQuestionAnswerBox.Text.Trim().Length < PasswordManager.MinSecurityAnswerLength)
        {
            SecurityQuestionError.Text =
                $"La respuesta debe tener al menos {PasswordManager.MinSecurityAnswerLength} caracteres. " +
                "Evita algo obvio o público: cualquiera que la sepa puede restablecer tu contraseña.";
            SecurityQuestionError.Visibility = Visibility.Visible;
            return;
        }

        PasswordManager.SetSecurityQuestion(question, SecurityQuestionAnswerBox.Text, Session.CurrentPassword!);

        // El aviso de "confirma tu respuesta" al cambiar la contraseña depende
        // de si ya hay pregunta configurada, asi que se vuelve a cargar.
        LoadChangePasswordSection();
        LoadSecurityQuestionSection();
        SettingsStatus.Text = "Pregunta de seguridad guardada correctamente.";
    }

    // ---------------- Ajustes ----------------

    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        PasswordManager.Settings.DisguisedAppName = string.IsNullOrWhiteSpace(AppNameBox.Text)
            ? "SecureMaid" : AppNameBox.Text.Trim();
        PasswordManager.Settings.DecoyModeEnabled = DecoyCheck.IsChecked == true;
        PasswordManager.Save();
        SidebarAppName.Text = PasswordManager.Settings.DisguisedAppName;
        SettingsStatus.Text = "Ajustes guardados. El nuevo nombre/modo señuelo aplica la proxima vez que abras la app.";
    }

    private void OnLogoOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string fileName) return;

        PasswordManager.Settings.LogoFileName = fileName;
        PasswordManager.Save();
        LoadLogoOptions();
        LoadSidebarIdentity();

        if (Window.GetWindow(this) is MainWindow mw) mw.ApplyLogo();
        SettingsStatus.Text = $"Logo de la app actualizado a '{Path.GetFileNameWithoutExtension(fileName)}'.";
    }

    private void OnCreateShortcut(object sender, RoutedEventArgs e)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string iconSource = Environment.ExpandEnvironmentVariables(
                ((ComboBoxItem)ShortcutIconCombo.SelectedItem).Tag.ToString()!);
            string name = string.IsNullOrWhiteSpace(AppNameBox.Text) ? "SecureMaid" : AppNameBox.Text.Trim();

            AppDisguiseManager.CreateDisguisedShortcut(desktop, name, exePath, iconSource);
            ExplorerRefresh.RefreshAll();
            SettingsStatus.Text = $"Acceso directo '{name}' creado en el Escritorio.";
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = "No se pudo crear el acceso directo: " + ex.Message;
        }
    }
}

/// <summary>Envuelve un VaultItem para mostrarlo en la lista: nombre/tipo/tamaño
/// legibles, icono de respaldo por tipo, y miniatura cargada de forma perezosa
/// (solo para imagenes) sin bloquear la interfaz.</summary>
public class VaultRowVM : INotifyPropertyChanged
{
    public VaultItem Item { get; }

    public VaultRowVM(VaultItem item) => Item = item;

    public string DisplayName => Item.Kind == ItemKind.Folder ? "📁 " + Item.OriginalName : Item.OriginalName;

    public string KindDisplay => Item.Kind switch
    {
        ItemKind.Folder => "Carpeta",
        ItemKind.Image => "Imagen",
        ItemKind.Video => "Video",
        ItemKind.Document => "Documento",
        _ => "Otro"
    };

    public string SizeDisplay
    {
        get
        {
            if (Item.Kind == ItemKind.Folder) return "—";
            double bytes = Item.SizeBytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int u = 0;
            while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
            return $"{bytes:0.#} {units[u]}";
        }
    }

    public string DateDisplay => Item.DateAdded.ToString("dd/MM/yyyy HH:mm");

    public string FallbackIcon => Item.Kind switch
    {
        ItemKind.Folder => "📁",
        ItemKind.Image => "🖼",
        ItemKind.Video => "🎬",
        ItemKind.Document => "📄",
        _ => "📦"
    };

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasThumbnail)); }
    }

    public bool HasThumbnail => _thumbnail != null;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Una opcion de logo en el selector de Ajustes.</summary>
public class LogoOptionVM
{
    public string FileName { get; set; } = "";
    public string Label { get; set; } = "";
    public ImageSource? Preview { get; set; }
    public bool IsSelected { get; set; }
    public Brush HighlightBrush => IsSelected
        ? (Brush)(Application.Current.TryFindResource("AccentSoft") ?? new SolidColorBrush(Color.FromArgb(0x33, 0x7C, 0x5C, 0xFF)))
        : Brushes.Transparent;
    public Thickness BorderThickness => new(IsSelected ? 1.5 : 0);
}
