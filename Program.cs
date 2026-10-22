using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutoLootPlusFilterCompanion;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        if (Process.GetProcessesByName("enshrouded").Length > 0)
        {
            MessageBox.Show(
                "Enshrouded läuft bereits.\n\nDer Auto Loot Plus Filter Companion wird aus Sicherheitsgründen nicht gestartet.",
                "Auto Loot Plus Filter Companion",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        Application.Run(new MainForm());
    }
}

public sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private string? _gameRoot;
    private readonly System.Windows.Forms.Timer _processWatch = new() { Interval = 1500 };
    private bool _lastGameRunning;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MainForm()
    {
        Text = "Auto Loot Plus - Filter Companion";
        Width = 1440;
        Height = 900;
        MinimumSize = new Size(1120, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 19, 24);

        Controls.Add(_web);
        Shown += async (_, _) => await InitAsync();

        _processWatch.Tick += async (_, _) =>
        {
            var running = Process.GetProcessesByName("enshrouded").Length > 0;
            if (running == _lastGameRunning) return;

            _lastGameRunning = running;
            await PostAsync(new
            {
                type = "gameProcess",
                running,
                message = running
                    ? "Enshrouded läuft - Speichern ist gesperrt."
                    : "Enshrouded wurde beendet - Speichern ist wieder möglich."
            });
        };
    }

    private async Task InitAsync()
    {
        await _web.EnsureCoreWebView2Async();
        _web.CoreWebView2.Settings.AreDevToolsEnabled = true;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _web.CoreWebView2.WebMessageReceived += OnWebMessage;

        var ui = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
        _gameRoot = AutoLootService.FindGameRoot();
        _lastGameRunning = Process.GetProcessesByName("enshrouded").Length > 0;
        _processWatch.Start();
        _web.Source = new Uri(ui);
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "ready":
                    await SendStateAsync();
                    break;

                case "chooseFolder":
                    using (var dlg = new FolderBrowserDialog { Description = "Enshrouded-Spielordner auswählen" })
                    {
                        if (!string.IsNullOrWhiteSpace(_gameRoot))
                            dlg.SelectedPath = _gameRoot;

                        if (dlg.ShowDialog(this) == DialogResult.OK)
                        {
                            if (!AutoLootService.IsValidRoot(dlg.SelectedPath))
                            {
                                MessageBox.Show("In diesem Ordner wurde Auto Loot Plus nicht gefunden.",
                                    "Ungültiger Ordner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                return;
                            }
                            _gameRoot = dlg.SelectedPath;
                            await SendStateAsync();
                        }
                    }
                    break;

                case "reload":
                    await SendStateAsync();
                    break;

                case "openUrl":
                {
                    var url = root.GetProperty("url").GetString();
                    if (!string.IsNullOrWhiteSpace(url) &&
                        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                    }
                    break;
                }

                case "revealRoot":
                    if (!string.IsNullOrWhiteSpace(_gameRoot) && Directory.Exists(_gameRoot))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\\\"{_gameRoot}\\\"")
                        {
                            UseShellExecute = true
                        });
                    }
                    break;

                case "getSlots":
                    await PostAsync(new
                    {
                        type = "slots",
                        slots = AutoLootService.ReadSlots()
                    });
                    break;

                case "saveSlot":
                {
                    var index = root.GetProperty("index").GetInt32();
                    var name = root.GetProperty("name").GetString() ?? $"Slot {index}";
                    var slotItems = root.GetProperty("selected").EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Cast<string>()
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(x => x, StringComparer.Ordinal)
                        .ToArray();

                    AutoLootService.SaveSlot(index, name, slotItems);
                    await PostAsync(new { type = "slots", slots = AutoLootService.ReadSlots() });
                    break;
                }

                case "renameSlot":
                {
                    var index = root.GetProperty("index").GetInt32();
                    var name = root.GetProperty("name").GetString() ?? $"Slot {index}";
                    AutoLootService.RenameSlot(index, name);
                    await PostAsync(new { type = "slots", slots = AutoLootService.ReadSlots() });
                    break;
                }

                case "clearSlot":
                {
                    var index = root.GetProperty("index").GetInt32();
                    AutoLootService.ClearSlot(index);
                    await PostAsync(new { type = "slots", slots = AutoLootService.ReadSlots() });
                    break;
                }

                case "save":
                    if (Process.GetProcessesByName("enshrouded").Length > 0)
                    {
                        await PostAsync(new { type = "saveResult", ok = false, message = "Enshrouded läuft. Speichern wurde blockiert." });
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(_gameRoot))
                    {
                        await PostAsync(new { type = "saveResult", ok = false, message = "Kein Enshrouded-Ordner geladen." });
                        return;
                    }

                    var selected = root.GetProperty("selected").EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Cast<string>()
                        .ToArray();

                    var validItems = AutoLootService.ReadItems(_gameRoot)
                        .Select(x => x.Internal)
                        .ToHashSet(StringComparer.Ordinal);

                    var unknown = selected.Where(x => !validItems.Contains(x)).ToArray();
                    var validSelected = selected.Where(validItems.Contains).ToArray();

                    var backup = AutoLootService.SaveIgnoreOnly(_gameRoot, validSelected);
                    await PostAsync(new
                    {
                        type = "saveResult",
                        ok = true,
                        message = unknown.Length == 0
                            ? "Filter gespeichert."
                            : $"Filter gespeichert. {unknown.Length} nicht mehr vorhandene Item(s) wurden verworfen.",
                        backup,
                        removedUnknown = unknown.Length
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            await PostAsync(new { type = "error", message = ex.Message });
        }
    }

    private async Task SendStateAsync()
    {
        if (string.IsNullOrWhiteSpace(_gameRoot) || !AutoLootService.IsValidRoot(_gameRoot))
        {
            await PostAsync(new { type = "state", found = false });
            return;
        }

        try
        {
            var items = AutoLootService.ReadItems(_gameRoot);
            var cfg = AutoLootService.ReadConfig(_gameRoot);

            var valid = items.Select(x => x.Internal).ToHashSet(StringComparer.Ordinal);
            var selected = cfg.Exact.Where(valid.Contains).ToArray();
            var staleExact = cfg.Exact.Where(x => !valid.Contains(x)).ToArray();

            await PostAsync(new
            {
                type = "state",
                found = true,
                gameRoot = _gameRoot,
                items,
                selected,
                preservedRules = cfg.Preserved,
                preservedRuleCount = cfg.Preserved.Length,
                itemCount = items.Length,
                staleExactCount = staleExact.Length,
                staleExact,
                gameRunning = Process.GetProcessesByName("enshrouded").Length > 0,
                configWritable = AutoLootService.CanWriteConfig(_gameRoot)
            });
        }
        catch (Exception ex)
        {
            await PostAsync(new
            {
                type = "state",
                found = false,
                gameRoot = _gameRoot,
                error = ex.Message
            });
        }
    }

    private Task PostAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _web.CoreWebView2.PostWebMessageAsJson(json);
        return Task.CompletedTask;
    }
}

public static class AutoLootService
{
    public sealed record ItemDto(
        string Id, string Internal, string En, string De,
        string GameCategory, string F8Group, string Guid, string Stack,
        string Group, string Category, string Subcategory, string Tier);

    public sealed record ConfigState(
        string Text, bool HasBom, string NewLine,
        string[] Exact, string[] Preserved, bool HadIgnoreLine);

    public sealed record FilterSlotDto(int Index, string Name, string[] Items);


    public static bool IsValidRoot(string root)
    {
        var mod = Path.Combine(root, "mods", "auto-loot-plus");
        return File.Exists(Path.Combine(mod, "items.txt")) &&
               File.Exists(Path.Combine(mod, "config.txt"));
    }

    public static bool CanWriteConfig(string root)
    {
        try
        {
            var path = Path.Combine(root, "mods", "auto-loot-plus", "config.txt");
            if (!File.Exists(path)) return false;

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0) return false;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return fs.CanWrite;
        }
        catch
        {
            return false;
        }
    }

    public static string? FindGameRoot()
    {
        var candidates = new List<string>
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var registrySteam = TryGetSteamPathFromRegistry();
        if (!string.IsNullOrWhiteSpace(registrySteam))
            steamRoots.Add(registrySteam);

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam"));
            steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
            steamRoots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
        }

        foreach (var steamRoot in steamRoots.Where(Directory.Exists))
        {
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Enshrouded"));

            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;

            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            {
                var libraryRoot = m.Groups[1].Value.Replace(@"\\", @"\");
                candidates.Add(Path.Combine(libraryRoot, "steamapps", "common", "Enshrouded"));
            }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(IsValidRoot);
    }

    private static string? TryGetSteamPathFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch
        {
            return null;
        }
    }

    public static ItemDto[] ReadItems(string root)
    {
        var path = Path.Combine(root, "mods", "auto-loot-plus", "items.txt");
        var result = new List<ItemDto>();

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var p = line.Split('\t');
            if (p.Length < 9 || p[4] != "1") continue;

            var internalName = p[1];
            var (group, category, subcategory) = Classify(internalName, p[5], p[6]);
            var tierMatch = Regex.Match(internalName, @"(?:^|_)T([1-7])(?:_|$)", RegexOptions.IgnoreCase);
            var tier = tierMatch.Success ? "T" + tierMatch.Groups[1].Value : "Ohne Tier";

            result.Add(new ItemDto(
                p[0], p[1], StripMarkup(p[2]), StripMarkup(p[3]),
                p[5], p[6], p[7], p[8],
                group, category, subcategory, tier));
        }

        if (result.Count == 0)
            throw new InvalidDataException("items.txt enthält keine gültigen lootbaren Items.");

        return result.ToArray();
    }

    public static ConfigState ReadConfig(string root)
    {
        var path = Path.Combine(root, "mods", "auto-loot-plus", "config.txt");
        var bytes = File.ReadAllBytes(path);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var m = Regex.Match(text, @"^ignoreItems=(.*)$", RegexOptions.Multiline);
        var raw = m.Success ? m.Groups[1].Value.TrimEnd('\r') : "";

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var preserved = new List<string>();

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith('=') && token.Length > 1) exact.Add(token[1..]);
            else preserved.Add(token);
        }

        return new ConfigState(text, bom, nl, exact.ToArray(), preserved.ToArray(), m.Success);
    }

    public static string SaveIgnoreOnly(string root, IEnumerable<string> internals)
    {
        var path = Path.Combine(root, "mods", "auto-loot-plus", "config.txt");
        var state = ReadConfig(root);

        var tokens = state.Preserved
            .Concat(internals.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).Select(x => "=" + x))
            .ToArray();

        var replacement = "ignoreItems=" + string.Join(",", tokens);
        string output;

        if (state.HadIgnoreLine)
            output = Regex.Replace(state.Text, @"^ignoreItems=.*$", replacement, RegexOptions.Multiline);
        else
            output = state.Text + (state.Text.EndsWith(state.NewLine) ? "" : state.NewLine) + replacement + state.NewLine;

        if (!CanWriteConfig(root))
            throw new IOException("config.txt ist nicht beschreibbar oder wird von einem anderen Prozess gesperrt.");

        var backup = path + ".companion.bak";
        File.Copy(path, backup, true);

        var enc = new UTF8Encoding(state.HasBom);
        var temp = path + ".companion.tmp";

        try
        {
            File.WriteAllText(temp, output, enc);
            File.Copy(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return backup;
    }

    private static string SlotsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutoLootPlusFilterCompanion",
            "filter-slots.json");

    public static FilterSlotDto[] ReadSlots()
    {
        var defaults = Enumerable.Range(1, 5)
            .Select(i => new FilterSlotDto(i, $"Slot {i}", Array.Empty<string>()))
            .ToArray();

        try
        {
            if (!File.Exists(SlotsPath))
                return defaults;

            var json = File.ReadAllText(SlotsPath, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<FilterSlotDto[]>(json) ?? Array.Empty<FilterSlotDto>();

            var byIndex = loaded.ToDictionary(x => x.Index);
            return Enumerable.Range(1, 5)
                .Select(i => byIndex.TryGetValue(i, out var s)
                    ? new FilterSlotDto(i,
                        string.IsNullOrWhiteSpace(s.Name) ? $"Slot {i}" : s.Name.Trim(),
                        s.Items?.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()
                            ?? Array.Empty<string>())
                    : defaults[i - 1])
                .ToArray();
        }
        catch
        {
            return defaults;
        }
    }

    private static void WriteSlots(FilterSlotDto[] slots)
    {
        var dir = Path.GetDirectoryName(SlotsPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(slots, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(SlotsPath, json, new UTF8Encoding(false));
    }

    public static void SaveSlot(int index, string name, string[] items)
    {
        if (index < 1 || index > 5)
            throw new ArgumentOutOfRangeException(nameof(index));

        var slots = ReadSlots();
        slots[index - 1] = new FilterSlotDto(
            index,
            string.IsNullOrWhiteSpace(name) ? $"Slot {index}" : name.Trim(),
            items ?? Array.Empty<string>());
        WriteSlots(slots);
    }

    public static void RenameSlot(int index, string name)
    {
        if (index < 1 || index > 5)
            throw new ArgumentOutOfRangeException(nameof(index));

        var slots = ReadSlots();
        var current = slots[index - 1];
        slots[index - 1] = current with
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Slot {index}" : name.Trim()
        };
        WriteSlots(slots);
    }

    public static void ClearSlot(int index)
    {
        if (index < 1 || index > 5)
            throw new ArgumentOutOfRangeException(nameof(index));

        var slots = ReadSlots();
        slots[index - 1] = new FilterSlotDto(index, $"Slot {index}", Array.Empty<string>());
        WriteSlots(slots);
    }

    private static string StripMarkup(string s) =>
        Regex.Replace(s ?? "", "<[^>]+>", "").Trim();

    private static (string group, string category, string subcategory) Classify(string internalName, string gameCategory, string f8)
    {
        var n = internalName.ToLowerInvariant();
        string cat, sub;

        if (f8 == "Materials")
        {
            cat = "Materialien";
            if (Regex.IsMatch(n, "bone|gland|critter|fur|feather|tooth|teeth|claw|horn|scale|shell|insect|animal|monster|bat|spider"))
                sub = "Knochen & Monsterteile";
            else if (Regex.IsMatch(n, "ore|metal|iron|copper|tin|bronze|coal|salt|sulfur|sulphur|flint|stone|gem|crystal|amber|obsidian|lapis|mineral"))
                sub = "Erze & Mineralien";
            else if (Regex.IsMatch(n, "wood|log|branch|twig|bark|charcoal|plank"))
                sub = "Holz & Holzprodukte";
            else if (Regex.IsMatch(n, "plant|flower|herb|fiber|mushroom|berry|seed|reed|straw|wheat|moss|root|leaf|leaves"))
                sub = "Pflanzen & Kräuter";
            else
                sub = "Verschiedenes Material";
        }
        else if (f8 == "Ammunition") { cat = "Munition"; sub = "Pfeile & Munition"; }
        else if (f8 == "Food") { cat = "Nahrung"; sub = "Nahrung & Zutaten"; }
        else if (f8 is "Potions" or "Bait") { cat = "Verbrauchsgegenstände"; sub = f8 == "Potions" ? "Tränke" : "Köder"; }
        else if (f8 is "Armor" or "Vanity" or "Shields" or "Rings")
        {
            cat = "Rüstungen";
            sub = f8 switch { "Armor" => "Rüstung", "Vanity" => "Vanity", "Shields" => "Schilde", _ => "Ringe" };
        }
        else if (f8 is "Weapons" or "WeaponGem") { cat = "Waffen"; sub = f8 == "Weapons" ? "Waffen" : "Waffenedelsteine"; }
        else if (f8 == "Terrain") { cat = "Bauteile & Möbel"; sub = "Terrain & Baublöcke"; }
        else if (f8 is "Collectibles" or "Keys" or "ColorPalette")
        {
            cat = "Quest- & Spezialitems";
            sub = f8 switch { "Collectibles" => "Sammelstücke", "Keys" => "Schlüssel", _ => "Farbpaletten" };
        }
        else if (f8 == "Tools") { cat = "Werkzeuge"; sub = "Werkzeuge"; }
        else if (f8 == "Instrument") { cat = "Verschiedenes"; sub = "Instrumente"; }
        else { cat = "Verschiedenes"; sub = string.IsNullOrWhiteSpace(f8) ? gameCategory : f8; }

        var group = cat is "Materialien" or "Munition" or "Nahrung" or "Verbrauchsgegenstände"
            ? "Ressourcen & Verbrauch"
            : cat is "Rüstungen" or "Waffen" ? "Ausrüstung" : "Verschiedenes";

        return (group, cat, sub);
    }
}
