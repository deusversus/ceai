using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CEAISuite.Application;
using CEAISuite.Desktop.Models;
using CEAISuite.Desktop.Services;
using CEAISuite.Engine.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CEAISuite.Desktop.ViewModels;

/// <summary>
/// ViewModel for the AI Operator panel. Owns chat messages, chat history,
/// attachments, streaming state, model selection, and all non-UI-specific
/// AI interaction logic extracted from MainWindow code-behind.
/// </summary>
public partial class AiOperatorViewModel : ObservableObject, IDisposable
{
    private readonly AiOperatorService _aiOperatorService;
    private readonly AppSettingsService _appSettingsService;
    private readonly IProcessContext _processContext;
    private readonly AddressTableService _addressTableService;
    private readonly IDialogService _dialogService;
    private readonly IOutputLog _outputLog;
    private readonly IDispatcherService _dispatcher;
    private readonly IThemeService _themeService;
    private readonly IClipboardService _clipboard;
    private readonly ILogger<AiOperatorViewModel> _logger;

    private static readonly string[] GeminiModels = ["gemini-3.1-flash-lite-preview", "gemini-3-flash-preview", "gemini-3.1-pro-preview"];
    private static readonly string[] AnthropicModels = ["claude-sonnet-4-6", "claude-opus-4-6", "claude-haiku-4-5"];
    private static readonly string[] OpenAiModels = ["gpt-5.4", "gpt-4.1", "o3", "o4-mini", "gpt-4o", "gpt-4o-mini"];

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
        IOutputLog outputLog,
        IDispatcherService dispatcher,
        IThemeService themeService,
        IClipboardService clipboard,
        ILogger<AiOperatorViewModel> logger)
    {
        _logger = logger;
        _aiOperatorService = aiOperatorService;
        _appSettingsService = appSettingsService;
        _processContext = processContext;
        _clipboard = clipboard;
        _addressTableService = addressTableService;
        _dialogService = dialogService;
        _outputLog = outputLog;
        _dispatcher = dispatcher;
        _themeService = themeService;

        _selectedModel = appSettingsService.Settings.Model;
        _permissionModeDisplay = appSettingsService.Settings.PermissionMode ?? "Normal";
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
    private string _permissionModeDisplay = "Normal";

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isChatHistoryVisible;

    [ObservableProperty]
    private ObservableCollection<ModelOption> _availableModels = new();

    [ObservableProperty]
    private string _chatSearchText = "";

    /// <summary>Structured content blocks for the currently-streaming response (OpenCode-style).</summary>
    [ObservableProperty]
    private ObservableCollection<ChatContentBlock> _streamingBlocks = new();

    /// <summary>Whether the streaming blocks area is visible (active response in progress).</summary>
    [ObservableProperty]
    private bool _isStreamingBlocksVisible;

    /// <summary>Session cost estimate displayed in the status bar.</summary>
    [ObservableProperty]
    private string _budgetStatus = "";

    /// <summary>Memory entry count for status display.</summary>
    [ObservableProperty]
    private string _memoryStatus = "";

    /// <summary>MCP server connection status.</summary>
    [ObservableProperty]
    private string _mcpStatus = "";

    /// <summary>Prompt cache hit rate status.</summary>
    [ObservableProperty]
    private string _cacheStatus = "";

    // ── Events for MainWindow to subscribe (UI-specific actions) ──

    /// <summary>Raised when the chat display needs to scroll to bottom.</summary>
    public event Action? ScrollToBottomRequested;

    /// <summary>Raised when the chat list items have been refreshed and need a visual refresh.</summary>
    public event Action? ChatDisplayRefreshed;

    /// <summary>Raised when a streaming content block is added or updated.</summary>
    public event Action? StreamingBlocksUpdated;

    /// <summary>Raised when an approval card needs to be shown (WPF-specific UI).</summary>
    public event Action<AgentStreamEvent.ApprovalRequested>? ApprovalCardRequested;

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

        // Separate text and image attachments
        var textParts = new List<string>();
        var imageAttachments = new List<(byte[] Data, string MediaType)>();

        foreach (var att in Attachments)
        {
            if (att.IsImage && att.ImageData is not null && att.MediaType is not null)
                imageAttachments.Add((att.ImageData, att.MediaType));
            else
                textParts.Add($"<context label=\"{att.Label}\">\n{att.FullText}\n</context>");
        }
        if (!string.IsNullOrEmpty(inputText))
            textParts.Add(inputText);

        var message = string.Join("\n\n", textParts);
        if (string.IsNullOrEmpty(message) && imageAttachments.Count == 0) return;

        InputText = "";
        Attachments.Clear();

        // Show user message and add to history (with images if present)
        if (imageAttachments.Count > 0)
        {
            _aiOperatorService.AddUserMessageWithImages(message, imageAttachments);
        }
        else
        {
            _aiOperatorService.AddUserMessageToHistory(message);
        }
        RefreshChatDisplay();

        _streamingCts = new CancellationTokenSource();
        IsStreaming = true;

        try
        {
            if (_appSettingsService.Settings.UseStreaming)
            {
                var reader = _aiOperatorService.SendMessageStreamingAsync(message, _streamingCts.Token);

                // Initialize streaming blocks display
                _dispatcher.Invoke(() =>
                {
                    StreamingBlocks.Clear();
                    IsStreamingBlocksVisible = true;
                    ScrollToBottomRequested?.Invoke();
                });

                TextContentBlock? currentTextBlock = null;

                await foreach (var evt in reader.ReadAllAsync(_streamingCts.Token))
                {
                    switch (evt)
                    {
                        case AgentStreamEvent.TextDelta delta:
                            _dispatcher.Invoke(() =>
                            {
                                if (currentTextBlock is null)
                                {
                                    currentTextBlock = new TextContentBlock
                                    {
                                        RoleLabel = "AI Operator",
                                        Timestamp = DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture),
                                        Background = FindThemeBrush("ChatAiBubble")
                                    };
                                    StreamingBlocks.Add(currentTextBlock);
                                }
                                currentTextBlock.Content += delta.Text;
                                StreamingBlocksUpdated?.Invoke();
                                ScrollToBottomRequested?.Invoke();
                            });
                            break;

                        case AgentStreamEvent.ToolCallStarted tool:
                            _dispatcher.Invoke(() =>
                            {
                                currentTextBlock = null; // Next text starts a new block
                                var block = new ToolCallBlock
                                {
                                    ToolName = tool.ToolName,
                                    Arguments = tool.Arguments,
                                    Timestamp = DateTime.Now.ToString("h:mm:ss", CultureInfo.InvariantCulture),
                                    Status = "running"
                                };
                                StreamingBlocks.Add(block);
                                StatusText = $"Tool: {tool.ToolName}";
                                StreamingBlocksUpdated?.Invoke();
                                ScrollToBottomRequested?.Invoke();
                            });
                            break;

                        case AgentStreamEvent.ToolCallCompleted completed:
                            _dispatcher.Invoke(() =>
                            {
                                // Find the matching running tool block (search backwards)
                                for (int i = StreamingBlocks.Count - 1; i >= 0; i--)
                                {
                                    if (StreamingBlocks[i] is ToolCallBlock tb && tb.Status == "running")
                                    {
                                        tb.Status = "completed";
                                        tb.Result = completed.Result;
                                        break;
                                    }
                                }
                                StreamingBlocksUpdated?.Invoke();
                            });
                            break;

                        case AgentStreamEvent.ApprovalRequested approval:
                            await _dispatcher.InvokeAsync(() =>
                            {
                                currentTextBlock = null;
                                var block = new ApprovalBlock
                                {
                                    ToolName = approval.ToolName,
                                    Arguments = approval.Arguments,
                                    Timestamp = DateTime.Now.ToString("h:mm:ss", CultureInfo.InvariantCulture),
                                };
                                block.Resolve = approved =>
                                {
                                    block.Status = approved ? "approved" : "denied";
                                    ResolveApproval(approval, approved);
                                };
                                StreamingBlocks.Add(block);
                                _pendingApprovals.Add(approval);
                                StatusText = $"⚠ Awaiting approval: {approval.ToolName}";
                                StreamingBlocksUpdated?.Invoke();
                                ScrollToBottomRequested?.Invoke();
                            });
                            break;

                        case AgentStreamEvent.Error err:
                            _dispatcher.Invoke(() =>
                            {
                                currentTextBlock = null;
                                StreamingBlocks.Add(new TextContentBlock
                                {
                                    RoleLabel = "Error",
                                    Content = err.Message,
                                    Timestamp = DateTime.Now.ToString("h:mm tt", CultureInfo.InvariantCulture),
                                    Background = FindThemeBrush("ChatAiBubble")
                                });
                                StreamingBlocksUpdated?.Invoke();
                            });
                            break;
                    }
                }

                // Convert streaming blocks to a flat history item
                _dispatcher.Invoke(() =>
                {
                    var fullText = string.Join("\n\n", StreamingBlocks
                        .OfType<TextContentBlock>()
                        .Select(b => b.Content)
                        .Where(c => !string.IsNullOrWhiteSpace(c)));
                    // Tool calls get appended as compact summaries
                    var toolSummaries = StreamingBlocks
                        .OfType<ToolCallBlock>()
                        .Select(b => $"[{b.Icon} {b.ToolName}: {b.Status}]");
                    var combined = string.IsNullOrWhiteSpace(fullText)
                        ? string.Join(" ", toolSummaries)
                        : fullText;

                    IsStreamingBlocksVisible = false;
                    // The history item is added by RefreshChatDisplay below
                });
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

            if (StatusText.StartsWith("Thinking", StringComparison.Ordinal) || StatusText.StartsWith("Tool:", StringComparison.Ordinal))
                StatusText = _aiOperatorService.IsConfigured ? "Ready" : "Not configured — open Settings to add API key";
            RefreshChatDisplay();
            RefreshChatSwitcher();
            RefreshStatusIndicators();
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
        try { _clipboard.SetText(item.Content); }
        catch (Exception ex) { _outputLog.Append("AiOperator", "Debug", $"Clipboard copy failed: {ex.Message}"); }
    }

    [RelayCommand]
    private void ExportChat()
    {
        var markdown = _aiOperatorService.ExportChatToMarkdown();
        if (string.IsNullOrWhiteSpace(markdown) || markdown.Split('\n').Length <= 5)
        {
            _dialogService.ShowInfo("Export Chat", "No messages to export.");
            return;
        }

        var defaultName = $"{_aiOperatorService.CurrentChatTitle.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd}";
        var path = _dialogService.ShowSaveFileDialog("Markdown (*.md)|*.md|Text (*.txt)|*.txt", defaultName);
        if (path is not null)
        {
            File.WriteAllText(path, markdown);
            StatusText = $"Exported to {Path.GetFileName(path)}";
        }
    }

    [RelayCommand]
    private void NewChat()
    {
        _aiOperatorService.NewChat();
        PermissionModeDisplay = _aiOperatorService.CurrentPermissionMode;
        RefreshChatDisplay();
        RefreshChatSwitcher();
    }

    [RelayCommand]
    private void DeleteChat(ChatHistoryDisplayItem? selected)
    {
        if (selected is null) return;

        if (!_dialogService.Confirm("Delete Chat", $"Delete chat \"{selected.Title}\"?"))
            return;

        _aiOperatorService.DeleteChat(selected.Id);
        RefreshChatDisplay();
        RefreshChatSwitcher();
    }

    [RelayCommand]
    private void ToggleChatHistory()
    {
        // IsChatHistoryVisible is already toggled by the ToggleButton's two-way
        // IsChecked binding before this command fires — don't toggle again.
        if (IsChatHistoryVisible) RefreshChatSwitcher();
    }

    [RelayCommand]
    private async Task SwitchChat(ChatHistoryDisplayItem? selected)
    {
        if (_suppressChatSwitch) return;
        if (selected is not null && selected.Id != _aiOperatorService.CurrentChatId)
        {
            await _aiOperatorService.SwitchChatAsync(selected.Id);
            PermissionModeDisplay = _aiOperatorService.CurrentPermissionMode;
            RefreshChatDisplay();
            RefreshChatSwitcher();
        }
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    private static string? GetMediaType(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null
    };

    [RelayCommand]
    private void AttachContext()
    {
        var files = _dialogService.ShowOpenFilesDialog(
            "All supported|*.txt;*.md;*.cs;*.json;*.xml;*.log;*.csv;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" +
            "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" +
            "Text files|*.txt;*.md;*.cs;*.json;*.xml;*.log;*.csv|" +
            "All files|*.*");
        if (files is null) return;

        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                var ext = info.Extension;

                if (ImageExtensions.Contains(ext))
                {
                    // Image file — binary read, 5MB limit
                    if (info.Length > 5_000_000)
                    {
                        _dialogService.ShowWarning("Image Too Large",
                            $"{info.Name} is too large ({info.Length / 1024}KB). Max 5MB for images.");
                        continue;
                    }
                    var bytes = File.ReadAllBytes(file);
                    var mediaType = GetMediaType(ext) ?? "image/png";
                    AddImageAttachment(Path.GetFileName(file), bytes, mediaType);
                }
                else
                {
                    // Text file — UTF-8 read, 100KB limit
                    if (info.Length > 100_000)
                    {
                        _dialogService.ShowWarning("File Too Large",
                            $"{info.Name} is too large ({info.Length / 1024}KB). Max 100KB for text files.");
                        continue;
                    }
                    var content = File.ReadAllText(file);
                    AddAttachment(Path.GetFileName(file), content);
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError("Error", $"Failed to read {file}: {ex.Message}");
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
    private void SelectPermissionMode(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return;

        _appSettingsService.Settings.PermissionMode = mode;
        _appSettingsService.Save();
        PermissionModeDisplay = mode;

        // Update the live permission engine if the operator service has one
        _aiOperatorService.SetPermissionMode(mode);
    }

    [RelayCommand]
    private async Task SelectModelAsync(ModelOption? option)
    {
        if (option is null || option.IsHeader) return;

        _appSettingsService.Settings.Model = option.ModelId;
        SelectedModel = option.ModelId;

        // Switch provider if the selected model belongs to a different provider
        var currentProvider = (_appSettingsService.Settings.Provider ?? "openai").ToLowerInvariant();
        if (!option.Provider.Equals(currentProvider, StringComparison.OrdinalIgnoreCase))
        {
            _appSettingsService.Settings.Provider = option.Provider;
        }

        _appSettingsService.Save();

        try
        {
            var newClient = await ChatClientFactory.CreateAsync(_appSettingsService.Settings, option.Provider, option.ModelId);
            _aiOperatorService.Reconfigure(newClient);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model switch failed");
        }
    }

    // ── Public Methods ──

    /// <summary>Add a text attachment chip (used by drag-drop and paste handlers in MainWindow).</summary>
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

    /// <summary>Add an image attachment chip from file bytes.</summary>
    public void AddImageAttachment(string label, byte[] data, string mediaType)
    {
        var sizeKb = data.Length / 1024;
        Attachments.Add(new AttachmentChip
        {
            Label = label,
            Preview = $"Image ({sizeKb}KB)",
            FullText = $"[Image: {label}, {sizeKb}KB, {mediaType}]",
            ImageData = data,
            MediaType = mediaType
        });
    }

    /// <summary>Add an image from raw PNG bytes (used by clipboard paste and programmatic injection).</summary>
    public void AddImageFromBytes(string label, byte[] pngData)
    {
        AddImageAttachment(label, pngData, "image/png");
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
            Timestamp = msg.Timestamp.ToLocalTime().ToString("h:mm tt", CultureInfo.InvariantCulture),
            Background = msg.Role == "user" ? userBrush : aiBrush,
            ImageData = msg.ImageDataList?.FirstOrDefault()
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
        var chats = AiOperatorService.ListChats();
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
        _ = _aiOperatorService.SwitchChatAsync(selected.Id).ContinueWith(t =>
        {
            if (t.Exception != null) System.Diagnostics.Trace.TraceWarning($"SwitchChat failed: {t.Exception}");
        }, TaskContinuationOptions.OnlyOnFaulted);
        PermissionModeDisplay = _aiOperatorService.CurrentPermissionMode;
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

    [RelayCommand]
    private async Task RefreshAvailableModelsAsync()
    {
        var models = await GetAvailableModelsAsync();
        AvailableModels = new ObservableCollection<ModelOption>(models);
    }

    /// <summary>Fetch available models for the model selector popup, grouped by provider.</summary>
    public async Task<List<ModelOption>> GetAvailableModelsAsync()
    {
        var settings = _appSettingsService.Settings;
        var models = new List<ModelOption>();

        // OpenAI
        if (!string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            models.Add(new ModelOption("openai", "", "\u2500\u2500 OpenAI \u2500\u2500", IsHeader: true));
            foreach (var m in OpenAiModels)
                models.Add(new ModelOption("openai", m, m));
        }

        // Anthropic
        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
        {
            models.Add(new ModelOption("anthropic", "", "\u2500\u2500 Anthropic \u2500\u2500", IsHeader: true));
            foreach (var m in AnthropicModels)
                models.Add(new ModelOption("anthropic", m, m));
        }

        // Gemini
        if (!string.IsNullOrWhiteSpace(settings.GeminiApiKey))
        {
            models.Add(new ModelOption("gemini", "", "\u2500\u2500 Google Gemini \u2500\u2500", IsHeader: true));
            foreach (var m in GeminiModels)
                models.Add(new ModelOption("gemini", m, m));
        }

        // GitHub Copilot
        if (!string.IsNullOrWhiteSpace(settings.GitHubToken))
        {
            models.Add(new ModelOption("copilot", "", "\u2500\u2500 GitHub Copilot \u2500\u2500", IsHeader: true));
            try
            {
                var copilotModels = await ChatClientFactory.CopilotService.FetchModelsAsync(settings.GitHubToken);
                foreach (var m in copilotModels)
                    models.Add(new ModelOption("copilot", m.Id, $"{m.Name} [{m.GetRate()}]"));
            }
            catch (Exception ex)
            {
                _outputLog.Append("AiOperator", "Debug", $"Failed to fetch Copilot models: {ex.Message}");
                // Fallback defaults — rates resolved from known table
                var fallbacks = new[] { ("gpt-4o", "GPT-4o"), ("claude-sonnet-4-6", "Claude Sonnet 4.6") };
                foreach (var (id, name) in fallbacks)
                {
                    var info = new CopilotModelInfo(id, name, "", "");
                    models.Add(new ModelOption("copilot", id, $"{name} [{info.GetRate()}]"));
                }
            }
        }

        // OpenAI-Compatible
        if (!string.IsNullOrWhiteSpace(settings.CompatibleApiKey ?? settings.OpenAiApiKey) && !string.IsNullOrWhiteSpace(settings.CustomEndpoint))
        {
            models.Add(new ModelOption("openai-compatible", "", "\u2500\u2500 Compatible \u2500\u2500", IsHeader: true));
            if (!string.IsNullOrWhiteSpace(settings.Model))
                models.Add(new ModelOption("openai-compatible", settings.Model, settings.Model));
        }

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

    /// <summary>Update budget, memory, and MCP status indicators.</summary>
    public void RefreshStatusIndicators()
    {
        var budget = _aiOperatorService.TokenBudget;
        if (budget.TotalRequests > 0)
        {
            BudgetStatus = $"~${budget.EstimatedCostUsd:F4} ({budget.TotalInputTokens:#,0}↑ {budget.TotalOutputTokens:#,0}↓)";
        }

        var memory = _aiOperatorService.Memory;
        if (memory is not null)
        {
            MemoryStatus = memory.Entries.Count > 0
                ? $"{memory.Entries.Count} memories"
                : "";
        }

        McpStatus = _aiOperatorService.GetMcpStatus();

        var cache = _aiOperatorService.PromptCacheOptimizer;
        if (cache.LastTotalSections > 0)
        {
            var hitRate = cache.LastCacheHits / (decimal)cache.LastTotalSections * 100;
            CacheStatus = $"Cache: {cache.LastCacheHits}/{cache.LastTotalSections} ({hitRate:F0}%)";
        }
    }

    public void Dispose()
    {
        _streamingCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    internal Brush FindThemeBrush(string key) => _themeService.FindBrush(key);

    private static string FormatTimeAgo(DateTimeOffset dt)
    {
        var diff = DateTimeOffset.UtcNow - dt;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return dt.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    }
}
