using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CEAISuite.Application.AgentLoop;

namespace CEAISuite.Desktop;

public partial class SkillsManagerWindow : Window
{
    public bool SkillsChanged { get; private set; }

    private readonly List<SkillEntry> _skills = new();

    private sealed record SkillEntry(
        string Name, string Description, string Version,
        string Author, string[] Tags, string Path, bool IsBuiltIn);

    // View-model for the ListBox DataTemplate
    private sealed record SkillListItem(string Icon, string Name, string Description, int Index);

    private static readonly string UserSkillsDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CEAISuite", "skills");

    public SkillsManagerWindow()
    {
        InitializeComponent();
        LoadSkills();
    }

    // ── Skill discovery ──

    private void LoadSkills()
    {
        _skills.Clear();

        var builtInDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skills");
        DiscoverSkills(builtInDir, isBuiltIn: true);
        DiscoverSkills(UserSkillsDir, isBuiltIn: false);

        var items = new List<SkillListItem>();
        for (int i = 0; i < _skills.Count; i++)
        {
            var s = _skills[i];
            var icon = s.IsBuiltIn ? "📦" : "👤";
            var desc = s.Description.Length > 80
                ? s.Description[..77] + "…"
                : s.Description;
            items.Add(new SkillListItem(icon, s.Name, desc, i));
        }

        SkillList.ItemsSource = items;
        DetailPanel.Visibility = Visibility.Collapsed;
        UpdateButtonStates();
    }

    private void DiscoverSkills(string directory, bool isBuiltIn)
    {
        if (!Directory.Exists(directory)) return;

        foreach (var subDir in Directory.GetDirectories(directory))
        {
            var skillMd = System.IO.Path.Combine(subDir, "SKILL.md");
            if (!File.Exists(skillMd)) continue;

            var entry = ParseSkillFile(skillMd, isBuiltIn);
            if (entry is not null)
                _skills.Add(entry);
        }
    }

    // ── YAML frontmatter parser (delegates to shared SkillLoader.ParseFrontmatter) ──

    private static SkillEntry? ParseSkillFile(string skillMdPath, bool isBuiltIn)
    {
        var lines = File.ReadAllLines(skillMdPath);
        if (lines.Length < 3 || lines[0].Trim() != "---") return null;

        int endIdx = -1;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---") { endIdx = i; break; }
        }
        if (endIdx < 0) return null;

        var fm = SkillLoader.ParseFrontmatter(lines, 1, endIdx);
        var dir = System.IO.Path.GetDirectoryName(skillMdPath) ?? skillMdPath;
        return new SkillEntry(fm.Name, fm.Description, fm.Version ?? "", fm.Author ?? "",
            fm.Tags is { Count: > 0 } ? fm.Tags.ToArray() : [], dir, isBuiltIn);
    }

    // ── Selection handling ──

    private void SkillList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SkillList.SelectedItem is not SkillListItem item || item.Index < 0 || item.Index >= _skills.Count)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            UpdateButtonStates();
            return;
        }

        var skill = _skills[item.Index];

        DetailName.Text = skill.Name;
        DetailType.Text = skill.IsBuiltIn ? "📦 Built-in Skill" : "👤 User Skill";
        DetailDescription.Text = skill.Description;
        DetailVersion.Text = string.IsNullOrEmpty(skill.Version) ? "—" : skill.Version;
        DetailAuthor.Text = string.IsNullOrEmpty(skill.Author) ? "—" : skill.Author;
        DetailPath.Text = skill.Path;
        DetailTags.Text = skill.Tags.Length > 0 ? string.Join(", ", skill.Tags) : "—";

        DetailPanel.Visibility = Visibility.Visible;
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        var isUserSkill = GetSelectedSkill() is { IsBuiltIn: false };
        EditButton.IsEnabled = isUserSkill;
        DeleteButton.IsEnabled = isUserSkill;
    }

    private SkillEntry? GetSelectedSkill()
    {
        if (SkillList.SelectedItem is SkillListItem item && item.Index >= 0 && item.Index < _skills.Count)
            return _skills[item.Index];
        return null;
    }

    // ── Button handlers ──

    private void NewSkill_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewSkillDialog();
        dialog.Owner = this;
        if (dialog.ShowDialog() != true) return;

        var skillName = dialog.SkillName;
        var skillDesc = dialog.SkillDescription;
        var skillTags = dialog.SkillTags;

        var skillDir = System.IO.Path.Combine(UserSkillsDir, skillName);
        if (Directory.Exists(skillDir))
        {
            MessageBox.Show($"A skill named '{skillName}' already exists in the user skills folder.",
                "Duplicate Skill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(skillDir);

        var titleCase = string.Join(' ',
            skillName.Split('-').Select(w =>
                w.Length > 0 ? char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..] : w));

        var tagsYaml = skillTags.Length > 0
            ? string.Join("\n", skillTags.Select(t => $"  - {t}"))
            : "  - custom";

        var content = $"""
            ---
            name: {skillName}
            description: >
              {skillDesc}
            version: "1.0.0"
            author: "User"
            tags:
            {tagsYaml}
            triggers:
              - {skillName}
            ---

            # {titleCase}

            ## When to Use
            Describe when the AI agent should load this skill.

            ## Instructions
            Write instructions for the AI agent here. Be specific about:
            - What workflows or procedures to follow
            - What tools to use and in what order
            - Common patterns and pitfalls
            - Examples of correct behavior

            ## Quick Reference
            Add any quick-reference tables or cheat sheets here.
            """;

        // Remove leading indentation from raw string literal
        var lines = content.Split('\n');
        var trimmed = string.Join("\n", lines.Select(l => l.Length >= 12 ? l[12..] : l.TrimStart()));

        File.WriteAllText(System.IO.Path.Combine(skillDir, "SKILL.md"), trimmed);

        SkillsChanged = true;
        LoadSkills();
    }

    private void EditSkill_Click(object sender, RoutedEventArgs e)
    {
        var skill = GetSelectedSkill();
        if (skill is null || skill.IsBuiltIn) return;

        var skillMd = System.IO.Path.Combine(skill.Path, "SKILL.md");
        if (!File.Exists(skillMd)) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = skillMd,
            UseShellExecute = true
        });

        SkillsChanged = true;
    }

    private void DeleteSkill_Click(object sender, RoutedEventArgs e)
    {
        var skill = GetSelectedSkill();
        if (skill is null || skill.IsBuiltIn) return;

        var result = MessageBox.Show(
            $"Delete skill '{skill.Name}' and all its files?\n\nThis cannot be undone.",
            "Delete Skill", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            Directory.Delete(skill.Path, recursive: true);
            SkillsChanged = true;
            LoadSkills();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete skill: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var skill = GetSelectedSkill();
        var dir = skill?.Path ?? UserSkillsDir;
        Directory.CreateDirectory(dir);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

// ── Inline dialog for creating new skills ──

public partial class NewSkillDialog : Window
{
    public string SkillName { get; private set; } = "";
    public string SkillDescription { get; private set; } = "";
    public string[] SkillTags { get; private set; } = [];

    public NewSkillDialog()
    {
        Title = "New Skill";
        Width = 420;
        Height = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)FindResource("WindowBackground");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int row = 0;

        var nameLabel = new TextBlock
        {
            Text = "Skill Name (kebab-case):",
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryForeground"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(nameLabel, row++);
        grid.Children.Add(nameLabel);

        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(nameBox, row++);
        grid.Children.Add(nameBox);

        var descLabel = new TextBlock
        {
            Text = "Description:",
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryForeground"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(descLabel, row++);
        grid.Children.Add(descLabel);

        var descBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 60,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(descBox, row++);
        grid.Children.Add(descBox);

        var tagsLabel = new TextBlock
        {
            Text = "Tags (comma-separated, optional):",
            Foreground = (System.Windows.Media.Brush)FindResource("PrimaryForeground"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(tagsLabel, row++);
        grid.Children.Add(tagsLabel);

        var tagsBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(tagsBox, row++);
        grid.Children.Add(tagsBox);

        // spacer
        row++;

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetRow(buttonPanel, row);
        grid.Children.Add(buttonPanel);

        var createBtn = new Button
        {
            Content = "Create",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        createBtn.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var desc = descBox.Text.Trim();

            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Skill name is required.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Enforce kebab-case
            if (name != System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9-]", ""))
            {
                MessageBox.Show("Skill name must be kebab-case (lowercase letters, numbers, hyphens only).",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(desc))
            {
                MessageBox.Show("Description is required.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SkillName = name;
            SkillDescription = desc;
            SkillTags = tagsBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length > 0)
                .ToArray();

            DialogResult = true;
        };
        buttonPanel.Children.Add(createBtn);

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(14, 6, 14, 6),
            IsCancel = true
        };
        buttonPanel.Children.Add(cancelBtn);

        Content = grid;
    }
}
