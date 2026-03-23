using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CEAISuite.Desktop.ViewModels;

/// <summary>
/// ViewModel for the AI Operator panel. Owns chat messages, chat history,
/// attachments, streaming state, model selection, and all non-UI-specific
/// AI interaction logic extracted from MainWindow code-behind.
/// </summary>
public partial class AiOperatorViewModel : ObservableObject
{
    private readonly AiOperatorService _aiOperatorService;
    private readonly AppSettingsService _appSettingsService;
    private readonly IProcessContext _processContext;
    private readonly AddressTableService _addressTableService;
    private readonly IDialogService _dialogService;
    private readonly IOutputLog _outputLog;

    private CancellationTokenSource? _streamingCts;
    private bool _suppressChatSwitch;
    private List<ChatHistoryDisplayItem> _allChatItems = new();

    // Pending approval events so "Allow All" / "Deny All" can resolve them
    private readonly List<AgentStreamEvent.ApprovalRequested> _pendingApprovals = new();

    // Tools the user has approved for this session (skip future prompts)
    private readonly HashSet<string> _sessionTrustedTools = new(StringComparer.OrdinalIgnoreCase);

    public AiOperatorViewModel(
        AiOperatorService aiOperatorService,
        AppSettingsService appSettingsService,
        IProcessContext processContext,
        AddressTableService addressTableService,
        IDialogService dialogService,
        IOutputLog outputLog)
    {
        _aiOperatorService = aiOperatorService;
        _appSettingsService = appSettingsService;
        _processContext = processContext;
        _addressTableService = addressTableService;
        _dialogService = dialogService;
        _outputLog = outputLog;

        _selectedModel = appSettingsService.Settings.Model;
    }

    // ── Observable Properties ──

    [ObservableProperty]
    private ObservableCollection<AiChatDisplayItem> _chatMessages = new();

    [ObservableProperty]
    private ObservableCollection<ChatHistoryDisplayItem> _chatHistory = new();

    [ObservableProperty]
    private ObservableCollection<AttachmentChip> _attachments = new();

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _chatTitle = "New Chat";

    [ObservableProperty]
    private string _selectedModel;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isChatHistoryVisible;

    [ObservableProperty]
    private string _chatSearchText = "";

    // ── Events for MainWindow to subscribe (UI-specific actions) ──

    /// <summary>Raised when the chat display needs to scroll to bottom.</summary>
    public event Action? ScrollToBottomRequested;

    /// <summary>Raised when the chat list items have been refreshed and need a visual refresh.</summary>
    public event Action? ChatDisplayRefreshed;

    /// <summary>Raised when an approval card needs to be shown (WPF-specific UI).</summary>
    public event Action<AgentStreamEvent.ApprovalRequested>? ApprovalCardRequested;

    /// <summary>Raised when the streaming text delta updates the display items in-place.</summary>
    public event Action? StreamingTextUpdated;

    // ── Public Service Accessors (for MainWindow thin wrappers) ──

    public AiOperatorService AiOperatorService => _aiOperatorService;
    public IReadOnlyList<AgentStreamEvent.ApprovalRequested> PendingApprovals => _pendingApprovals;
    public HashSet<string> SessionTrustedTools => _sessionTrustedTools;

    // ── Commands ──

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        // If already streaming, cancel instead of sending
        if (IsStreaming)
        {
            _streamingCts?.Cancel();
            return;
        }

        var inputText = InputText?.Trim();

        // Build message: attachments prepended as context blocks
        var parts = new List<string>();
        foreach (var att in Attachments)
        {
            parts.Add($"<context label=\"{att.Label}\">\n{att.FullText}\n</context>");
        }
        if (!string.IsNullOrEmpty(inputText))
            parts.Add(inputText);

        var message = string.Join("\n\n", parts);
        if (string.IsNullOrEmpty(message)) return;

        InputText = "";
        Attachments.Clear();

        // Show user message immediately before starting AI work
        _aiOperatorService.AddUserMessageToHistory(message);
        RefreshChatDisplay();

        _streamingCts = new CancellationTokenSource();
        IsStreaming = true;

        try
        {
            if (_appSettingsService.Settings.UseStreaming)
            {
                var reader = _aiOperatorService.SendMessageStreamingAsync(message, _streamingCts.Token);

                // Add a placeholder for the streaming response
                var streamItem = new AiChatDisplayItem
                {
                    RoleLabel = "AI Operator",
                    Content = "",
                    Timestamp = DateTime.Now.ToString("h:mm tt"),
                    Background = FindThemeBrush("ChatAiBubble")
                };

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ChatMessages.Add(streamItem);
                    StreamingTextUpdated?.Invoke();
                    ScrollToBottomRequested?.Invoke();
                });

                await foreach (var evt in reader.ReadAllAsync(_streamingCts.Token))
                {
                    switch (evt)
                    {
                        case AgentStreamEvent.TextDelta delta:
                            streamItem.Content += delta.Text;
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                StreamingTextUpdated?.Invoke();
                                ScrollToBottomRequested?.Invoke();
                            });
                            break;

                        case AgentStreamEvent.ToolCallStarted tool:
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                StatusText = $"Tool: {tool.ToolName}");
                            break;

                        case AgentStreamEvent.ApprovalRequested approval:
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                HandleApprovalRequest(approval);
                            });
                            break;

                        case AgentStreamEvent.Error err:
                            streamItem.Content = err.Message;
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                StreamingTextUpdated?.Invoke());
                            break;
                    }
                }
            }
            else
            {
                // Non-streaming: wait for full response
                StatusText = "Thinking...";
                await Task.Run(() => _aiOperatorService.SendMessageAsync(message), _streamingCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped";
        }
        catch (Exception ex)
        {
            StatusText = $"AI error: {ex.Message}";
        }
        finally
        {
            IsStreaming = false;
            _streamingCts?.Dispose();
            _streamingCts = null;

            if (StatusText.StartsWith("Thinking") || StatusText.StartsWith("Tool:"))
                StatusText = _aiOperatorService.IsConfigured ? "Ready" : "Not configured — open Settings to add API key";
            RefreshChatDisplay();
            RefreshChatSwitcher();
        }
    }

    [RelayCommand]
    private void ClearChat()
    {
        _aiOperatorService.ClearHistory();
        RefreshChatDisplay();
    }

    [RelayCommand]
    private void CopyMessage(AiChatDisplayItem? item)
    {
        if (item is null) return;
        try { Clipboard.SetText(item.Content); }
        catch { /* clipboard locked */ }
    }

    [RelayCommand]
    private void ExportChat()
    {
        var markdown = _aiOperatorService.ExportChatToMarkdown();
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Split('\n').Length <= 5)
        {
            MessageBox.Show("No messages to export.", "Export Chat", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Chat History",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            DefaultExt = ".md",
            FileName = $"{_aiOperatorService.CurrentChatTitle.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd}"
        };

        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, markdown);
            StatusText = $"Exported to {Path.GetFileName(dlg.FileName)}";
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        _aiOperatorService.NewChat();
        RefreshChatDisplay();
        RefreshChatSwitcher();
    }

    [RelayCommand]
    private void DeleteChat(ChatHistoryDisplayItem? selected)
    {
        if (selected is null) return;

        var result = MessageBox.Show(
            $"Delete chat \"{selected.Title}\"?",
            "Delete Chat", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _aiOperatorService.DeleteChat(selected.Id);
            RefreshChatDisplay();
            RefreshChatSwitcher();
        }
    }

    [RelayCommand]
    private void ToggleChatHistory()
    {
        IsChatHistoryVisible = !IsChatHistoryVisible;
        if (IsChatHistoryVisible) RefreshChatSwitcher();
    }

    [RelayCommand]
    private void SwitchChat(ChatHistoryDisplayItem? selected)
    {
        if (_suppressChatSwitch) return;
        if (selected is not null && selected.Id != _aiOperatorService.CurrentChatId)
        {
            _aiOperatorService.SwitchChat(selected.Id);
            RefreshChatDisplay();
            RefreshChatSwitcher();
        }
    }

    [RelayCommand]
    private void AttachContext()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach Context File",
            Filter = "Text files|*.txt;*.md;*.cs;*.json;*.xml;*.log;*.csv|All files|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 100_000)
                    {
                        MessageBox.Show($"{info.Name} is too large ({info.Length / 1024}KB). Max 100KB.",
                            "File Too Large", MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }
                    var content = File.ReadAllText(file);
                    AddAttachment(Path.GetFileName(file), content);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to read {file}: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentChip? chip)
    {
        if (chip is null) return;
        var toRemove = Attachments.FirstOrDefault(a => a.Id == chip.Id);
        if (toRemove is not null)
            Attachments.Remove(toRemove);
    }

    [RelayCommand]
    private async Task SelectModelAsync(string? model)
    {
        if (string.IsNullOrEmpty(model)) return;

        _appSettingsService.Settings.Model = model;
        _appSettingsService.Save();
        SelectedModel = model;

        try
        {
            var newClient = ChatClientFactory.Create(_appSettingsService.Settings);
            _aiOperatorService.Reconfigure(newClient);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Model switch failed: {ex.Message}");
        }
    }

    // ── Public Methods ──

    /// <summary>Add an attachment chip (used by drag-drop and paste handlers in MainWindow).</summary>
    public void AddAttachment(string label, string fullText)
    {
        var lines = fullText.Split('\n');
        var preview = lines[0].Trim();
        if (preview.Length > 60) preview = preview[..57] + "...";
        if (lines.Length > 1) preview += $" (+{lines.Length - 1} lines)";

        Attachments.Add(new AttachmentChip
        {
            Label = label,
            Preview = preview,
            FullText = fullText
        });
    }

    /// <summary>Rebuild the chat display items from the service's display history.</summary>
    public void RefreshChatDisplay()
    {
        var userBrush = FindThemeBrush("ChatUserBubble");
        var aiBrush = FindThemeBrush("ChatAiBubble");

        var items = _aiOperatorService.DisplayHistory.Select(msg => new AiChatDisplayItem
        {
            RoleLabel = msg.Role == "user" ? "You" : "AI Operator",
            Content = msg.Content,
            Timestamp = msg.Timestamp.ToLocalTime().ToString("h:mm tt"),
            Background = msg.Role == "user" ? userBrush : aiBrush
        }).ToList();

        ChatMessages.Clear();
        foreach (var item in items)
            ChatMessages.Add(item);

        ChatTitle = _aiOperatorService.CurrentChatTitle;
        ChatDisplayRefreshed?.Invoke();
        ScrollToBottomRequested?.Invoke();
    }

    /// <summary>Rebuild the chat history switcher list.</summary>
    public void RefreshChatSwitcher()
    {
        _suppressChatSwitch = true;
        var chats = _aiOperatorService.ListChats();
        var currentId = _aiOperatorService.CurrentChatId;

        _allChatItems = chats.Select(c => new ChatHistoryDisplayItem
        {
            Id = c.Id,
            Title = c.Title,
            TimeAgo = FormatTimeAgo(c.UpdatedAt),
            Preview = c.Messages.LastOrDefault(m => m.Role == "assistant")?.Content is string last
                ? (last.Length > 80 ? last[..80] + "\u2026" : last)
                : c.Messages.Count > 0 ? $"{c.Messages.Count} messages" : "Empty",
            IsCurrent = c.Id == currentId
        }).ToList();

        // Insert current unsaved chat at top if not already listed
        if (!_allChatItems.Any(c => c.Id == currentId))
        {
            _allChatItems.Insert(0, new ChatHistoryDisplayItem
            {
                Id = currentId,
                Title = _aiOperatorService.CurrentChatTitle,
                TimeAgo = "now",
                Preview = _aiOperatorService.DisplayHistory.Count > 0
                    ? $"{_aiOperatorService.DisplayHistory.Count} messages"
                    : "Active chat",
                IsCurrent = true
            });
        }

        ChatHistory.Clear();
        foreach (var item in _allChatItems)
            ChatHistory.Add(item);

        _suppressChatSwitch = false;
    }

    /// <summary>Open a chat from the history list (context menu).</summary>
    public void OpenChat(ChatHistoryDisplayItem? selected)
    {
        if (selected is null) return;
        _aiOperatorService.SwitchChat(selected.Id);
        RefreshChatDisplay();
        RefreshChatSwitcher();
    }

    /// <summary>Rename a chat. Returns true if renamed.</summary>
    public void RenameChat(ChatHistoryDisplayItem selected, string newTitle)
    {
        _aiOperatorService.RenameChat(selected.Id, newTitle);
        RefreshChatDisplay();
        RefreshChatSwitcher();
    }

    /// <summary>Filter chat history by search text.</summary>
    public void FilterChatHistory(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            ChatHistory.Clear();
            foreach (var item in _allChatItems)
                ChatHistory.Add(item);
            return;
        }

        var filtered = _allChatItems
            .Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.Preview.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ChatHistory.Clear();
        foreach (var item in filtered)
            ChatHistory.Add(item);
    }

    /// <summary>Fetch available models for the model selector popup.</summary>
    public async Task<List<string>> GetAvailableModelsAsync()
    {
        var currentModel = _appSettingsService.Settings.Model;

        List<string> models = new();
        if (_appSettingsService.Settings.Provider.Equals("copilot", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_appSettingsService.Settings.GitHubToken))
        {
            try
            {
                var copilotModels = await ChatClientFactory.CopilotService.FetchModelsAsync(_appSettingsService.Settings.GitHubToken);
                models.AddRange(copilotModels.Select(m => m.Id));
            }
            catch { }
        }

        if (models.Count == 0)
        {
            models.AddRange(new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "gpt-5.4", "claude-sonnet-4", "claude-sonnet-4.5", "o4-mini" });
        }

        if (!models.Contains(currentModel))
            models.Insert(0, currentModel);

        return models;
    }

    /// <summary>Handle an approval request: auto-approve if trusted, otherwise raise event for UI.</summary>
    public void HandleApprovalRequest(AgentStreamEvent.ApprovalRequested approval)
    {
        // Auto-approve tools the user already trusted this session
        if (_sessionTrustedTools.Contains(approval.ToolName))
        {
            StatusText = $"\u2713 Auto-approved: {approval.ToolName}";
            approval.Resolve(true);
            return;
        }

        _pendingApprovals.Add(approval);
        StatusText = $"\u26a0 Awaiting approval: {approval.ToolName} ({_pendingApprovals.Count} pending)";

        // Raise event for MainWindow to build the WPF approval card
        ApprovalCardRequested?.Invoke(approval);
    }

    /// <summary>Resolve a single approval request.</summary>
    public void ResolveApproval(AgentStreamEvent.ApprovalRequested approval, bool approved)
    {
        _pendingApprovals.Remove(approval);
        StatusText = approved
            ? $"Executing {approval.ToolName}..."
            : $"Denied: {approval.ToolName}";
        approval.Resolve(approved);
    }

    /// <summary>Resolve all pending approval requests.</summary>
    public void ResolveAllPending(bool approved)
    {
        foreach (var pending in _pendingApprovals.ToList())
        {
            if (approved) _sessionTrustedTools.Add(pending.ToolName);
            pending.Resolve(approved);
        }
        _pendingApprovals.Clear();

        StatusText = approved
            ? "Executing approved tools..."
            : "Denied all pending tools";
    }

    // ── Helpers ──

    internal static Brush FindThemeBrush(string key) =>
        System.Windows.Application.Current.FindResource(key) as Brush ?? Brushes.Transparent;

    private static string FormatTimeAgo(DateTimeOffset dt)
    {
        var diff = DateTimeOffset.UtcNow - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return dt.ToLocalTime().ToString("MMM d");
    }
}
