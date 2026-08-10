using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace realm_manager;

// Data Model
class ServerEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    public override string ToString() => $"{Name}  ->  {Value}";
}

class ExpansionData
{
    [JsonPropertyName("wtfPath")]
    public string WtfPath { get; set; } = "";

    [JsonPropertyName("servers")]
    public List<ServerEntry> Servers { get; set; } = new();
}

class AppConfig
{
    [JsonPropertyName("selectedExpansion")]
    public string SelectedExpansion { get; set; } = "WOTLK";

    [JsonPropertyName("expansions")]
    public Dictionary<string, ExpansionData> Expansions { get; set; } = new();

    public static readonly string[] ExpansionNames = { "Classic", "TBC", "WOTLK", "Cataclysm", "MOP", "WOD", "Legion", "BFA" };

    // Mapping expansion to patch number for easier format identification
    public static int GetExpansionNumber(string expansion) => expansion switch
    {
        "Classic" => 1,
        "TBC" => 2,
        "WOTLK" => 3,
        "Cataclysm" => 4,
        "MOP" => 5,
        "WOD" => 6,
        "Legion" => 7,
        "BFA" => 8,
        _ => 3
    };

    // Default .wtf paths per expansion
    static string DefaultPath(string expansion) =>
        GetExpansionNumber(expansion) >= 5
            ? @".\WTF\Config.wtf"
            : @".\Data\enUS\realmlist.wtf";

    // Guarantees both expansions exist with non-null members.
    public void EnsureDefaults()
    {
        foreach (var name in ExpansionNames)
        {
            if (!Expansions.TryGetValue(name, out var data) || data == null)
            {
                data = new ExpansionData { WtfPath = DefaultPath(name) };
                Expansions[name] = data;
            }
            data.Servers ??= new List<ServerEntry>();
            if (string.IsNullOrWhiteSpace(data.WtfPath))
                data.WtfPath = DefaultPath(name);
        }

        if (!ExpansionNames.Contains(SelectedExpansion))
            SelectedExpansion = "WOTLK";
    }
}

// Realmlist file writer single line surgical edit (for patches >= 5)

static class RealmlistWriter
{

    // Formats the one line for the given expansion.
    public static string FormatLine(string expansion, string value) =>
        AppConfig.GetExpansionNumber(expansion) >= 5
            ? $"SET portal \"{value}\""
            : $"set realmlist {value}";

    // Case insensitive test of whether a line is the realmlist/portal line.
    static bool IsTargetLine(string line, string expansion)
    {
        string t = line.TrimStart();
        return AppConfig.GetExpansionNumber(expansion) >= 5
            ? t.StartsWith("set portal", StringComparison.OrdinalIgnoreCase)
            : t.StartsWith("set realmlist", StringComparison.OrdinalIgnoreCase);
    }

    // Replaces/appends only the realmlist/portal line
    public static void Apply(string path, string expansion, string value)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("The .wtf file path is empty.");

        string formatted = FormatLine(expansion, value);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Missing or empty file: just write the single line
        if (!File.Exists(path))
        {
            File.WriteAllText(path, formatted + Environment.NewLine);
            return;
        }

        string content = File.ReadAllText(path);
        if (content.Length == 0)
        {
            File.WriteAllText(path, formatted + Environment.NewLine);
            return;
        }

        string nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines = content.Split(new[] { nl }, StringSplitOptions.None).ToList();

        int idx = lines.FindIndex(l => IsTargetLine(l, expansion));
        if (idx >= 0)
        {
            lines[idx] = formatted;
        }
        else if (lines.Count > 0 && lines[^1].Length == 0)
        {
            // File ends with a trailing newline, insert before to avoid gap
            lines.Insert(lines.Count - 1, formatted);
        }
        else
        {
            lines.Add(formatted);
        }

        File.WriteAllText(path, string.Join(nl, lines));
    }

    // Reads back the current active realmlist/portal line for display.
    public static string ReadCurrent(string path, string expansion)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "(file not found)";
            foreach (var line in File.ReadLines(path))
                if (IsTargetLine(line, expansion))
                    return line.Trim();
            return "(no realmlist line yet)";
        }
        catch
        {
            return "(unreadable)";
        }
    }
}

// Main window

class MainForm : Form
{
    static readonly string version = "v1.0.1";
    static readonly string website = "https://blaststar.net";

    static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "wrm-config.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    readonly AppConfig _config;
    string _currentExpansion;

    ComboBox _expansionCombo = null!;
    TextBox _pathBox = null!;
    Label _currentLine = null!;
    ListBox _serverList = null!;
    TextBox _nameBox = null!;
    TextBox _valueBox = null!;
    Button _addBtn = null!;
    Button _removeBtn = null!;
    Button _applyBtn = null!;
    Label _status = null!;
    LinkLabel _footer = null!;

    bool _switching; // guards the combo/path events during programmatic changes

    public MainForm()
    {
        _config = LoadConfig();
        _currentExpansion = _config.SelectedExpansion;
        BuildUi();
        LoadExpansionIntoUi(_currentExpansion);
    }

    // UI construction

    void BuildUi()
    {
        Text = $"WoW Realmlist Manager {version}";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 500);

        int margin = 12;
        int width = ClientSize.Width - margin * 2;

        var expansionLabel = new Label
        {
            Text = "Expansion",
            Location = new Point(margin, margin),
            AutoSize = true,
        };
        _expansionCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(margin, expansionLabel.Bottom + 2),
            Width = width,
        };
        _expansionCombo.Items.AddRange(AppConfig.ExpansionNames);
        _expansionCombo.SelectedItem = _currentExpansion;
        _expansionCombo.SelectedIndexChanged += OnExpansionChanged;

        var pathLabel = new Label
        {
            Text = ".wtf file path",
            Location = new Point(margin, _expansionCombo.Bottom + 10),
            AutoSize = true,
        };
        _pathBox = new TextBox
        {
            Location = new Point(margin, pathLabel.Bottom + 2),
            Width = width,
        };
        _pathBox.Leave += (_, _) => SaveCurrentPathIntoConfig();

        _currentLine = new Label
        {
            Location = new Point(margin, _pathBox.Bottom + 6),
            Width = width,
            AutoEllipsis = true,
            ForeColor = Color.DimGray,
        };

        var serversLabel = new Label
        {
            Text = "Saved servers",
            Location = new Point(margin, _currentLine.Bottom + 8),
            AutoSize = true,
        };
        _serverList = new ListBox
        {
            Location = new Point(margin, serversLabel.Bottom + 2),
            Width = width,
            Height = 150,
        };

        // Add area: name + value + Add button
        var nameLabel = new Label
        {
            Text = "Name",
            Location = new Point(margin, _serverList.Bottom + 10),
            AutoSize = true,
        };
        var valueLabel = new Label
        {
            Text = "IP / Domain",
            Location = new Point(margin + 150, _serverList.Bottom + 10),
            AutoSize = true,
        };
        _nameBox = new TextBox
        {
            Location = new Point(margin, nameLabel.Bottom + 2),
            Width = 140,
        };
        _valueBox = new TextBox
        {
            Location = new Point(margin + 150, valueLabel.Bottom + 2),
            Width = 160,
        };
        _addBtn = new Button
        {
            Text = "Add",
            Location = new Point(margin + 320, _nameBox.Top - 1),
            Width = width - 320,
        };
        _addBtn.Click += OnAdd;

        // Action row: Remove + Set Active
        _removeBtn = new Button
        {
            Text = "Remove Selected",
            Location = new Point(margin, _nameBox.Bottom + 12),
            Width = 150,
        };
        _removeBtn.Click += OnRemove;

        _applyBtn = new Button
        {
            Text = "Set Active",
            Location = new Point(margin + width - 150, _nameBox.Bottom + 12),
            Width = 150,
            Height = _removeBtn.Height,
        };
        _applyBtn.Font = new Font(Font, FontStyle.Bold);
        _applyBtn.Click += OnApply;

        _status = new Label
        {
            Location = new Point(margin, _applyBtn.Bottom + 12),
            Width = width,
            AutoEllipsis = true,
            Text = "Ready.",
        };

        _footer = new LinkLabel
        {
            Location = new Point(margin, _status.Bottom + 8),
            Width = width,
            AutoEllipsis = true,
            ForeColor = Color.Black,
            Text = "Created by: Blaststar",
            LinkArea = new LinkArea(12, 9),
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _footer.LinkClicked += OnLinkClick;

        Controls.AddRange(new Control[]
        {
            expansionLabel, _expansionCombo,
            pathLabel, _pathBox, _currentLine,
            serversLabel, _serverList,
            nameLabel, valueLabel, _nameBox, _valueBox, _addBtn,
            _removeBtn, _applyBtn, _status, _footer,
        });

        FormClosing += (_, _) => { SaveCurrentPathIntoConfig(); SaveConfig(); };
    }

    // Expansion/list handling 

    ExpansionData Current => _config.Expansions[_currentExpansion];

    void OnExpansionChanged(object? sender, EventArgs e)
    {
        if (_switching) return;
        SaveCurrentPathIntoConfig(); // keep edits to the old path
        _currentExpansion = (string)_expansionCombo.SelectedItem!;
        _config.SelectedExpansion = _currentExpansion;
        SaveConfig();
        LoadExpansionIntoUi(_currentExpansion);
    }

    void LoadExpansionIntoUi(string expansion)
    {
        _switching = true;
        _currentExpansion = expansion;
        _expansionCombo.SelectedItem = expansion;
        _pathBox.Text = Current.WtfPath;
        RefreshServerList();
        RefreshCurrentLine();
        SetStatus($"{expansion} format: {RealmlistWriter.FormatLine(expansion, "IP_DOMAIN")}", Color.DimGray);
        _switching = false;
    }

    void RefreshServerList()
    {
        _serverList.BeginUpdate();
        _serverList.Items.Clear();
        foreach (var s in Current.Servers)
            _serverList.Items.Add(s);
        _serverList.EndUpdate();
    }

    void RefreshCurrentLine()
    {
        string cur = RealmlistWriter.ReadCurrent(_pathBox.Text.Trim(), _currentExpansion);
        _currentLine.Text = "Active: " + cur;
    }

    void SaveCurrentPathIntoConfig()
    {
        if (_config.Expansions.TryGetValue(_currentExpansion, out var data))
            data.WtfPath = _pathBox.Text.Trim();
    }

    // Link Handler
    private void OnLinkClick(object? sender, LinkLabelLinkClickedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = website,
            UseShellExecute = true
        });
    }

    // Button handlers

    void OnAdd(object? sender, EventArgs e)
    {
        string name = _nameBox.Text.Trim();
        string value = _valueBox.Text.Trim();

        if (name.Length == 0 || value.Length == 0)
        {
            SetStatus("Name and IP/Domain cannot be empty.", Color.Firebrick);
            return;
        }
        if (Current.Servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"A server named '{name}' already exists.", Color.Firebrick);
            return;
        }

        Current.Servers.Add(new ServerEntry { Name = name, Value = value });
        SaveConfig();
        RefreshServerList();
        _serverList.SelectedIndex = _serverList.Items.Count - 1;
        _nameBox.Clear();
        _valueBox.Clear();
        _nameBox.Focus();
        SetStatus($"Added '{name}' ({value}).", Color.ForestGreen);
    }

    void OnRemove(object? sender, EventArgs e)
    {
        int idx = _serverList.SelectedIndex;
        if (idx < 0)
        {
            SetStatus("Select a server to remove.", Color.Firebrick);
            return;
        }

        string name = Current.Servers[idx].Name;
        if (MessageBox.Show($"Remove '{name}'?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        Current.Servers.RemoveAt(idx);
        SaveConfig();
        RefreshServerList();
        SetStatus($"Removed '{name}'.", Color.ForestGreen);
    }

    void OnApply(object? sender, EventArgs e)
    {
        int idx = _serverList.SelectedIndex;
        if (idx < 0)
        {
            SetStatus("Select a server to set active.", Color.Firebrick);
            return;
        }

        var entry = Current.Servers[idx];
        string path = _pathBox.Text.Trim();
        SaveCurrentPathIntoConfig();
        SaveConfig();

        try
        {
            RealmlistWriter.Apply(path, _currentExpansion, entry.Value);
            RefreshCurrentLine();
            SetStatus($"Active realm set to '{entry.Name}' ({entry.Value}).", Color.ForestGreen);
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message, Color.Firebrick);
        }
    }

    void SetStatus(string text, Color color)
    {
        _status.ForeColor = color;
        _status.Text = text;
    }

    // Persistence

    static AppConfig LoadConfig()
    {
        AppConfig config;
        try
        {
            if (File.Exists(ConfigPath))
            {
                string raw = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<AppConfig>(raw) ?? new AppConfig();
            }
            else
            {
                config = new AppConfig();
            }
        }
        catch
        {
            config = new AppConfig();
        }

        config.EnsureDefaults();
        return config;
    }

    void SaveConfig()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, JsonOpts));
        }
        catch (Exception ex)
        {
            SetStatus("Could not save settings: " + ex.Message, Color.Firebrick);
        }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new MainForm());
    }
}
