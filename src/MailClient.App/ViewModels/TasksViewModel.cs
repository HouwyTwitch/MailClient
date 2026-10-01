using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MailClient.App.Services;
using MailClient.Core.Models;

namespace MailClient.App.ViewModels;

public sealed partial class TaskItemViewModel : ObservableObject
{
    private readonly Func<TaskItemViewModel, bool, Task> _onToggle;

    public TaskItemViewModel(TaskItem task, Func<TaskItemViewModel, bool, Task> onToggle)
    {
        Task = task;
        _onToggle = onToggle;
        _isComplete = task.IsComplete;
    }

    public TaskItem Task { get; }
    public string Subject => Task.Subject;
    public string DueText => Task.DueDate is { } d ? "Срок: " + d.ToLocalTime().ToString("d MMMM yyyy", RuText.Culture) : "";
    public bool IsOverdue => !IsComplete && Task.DueDate is { } d && d.ToLocalTime().Date < DateTime.Today;
    public string StatusText => RuText.TaskStatusText(Task.Status) + (Task.PercentComplete is > 0 and < 100 ? $" ({Task.PercentComplete}%)" : "");
    public bool IsHighImportance => Task.Importance == Importance.High;

    [ObservableProperty] private bool _isComplete;

    private bool _suppress;

    partial void OnIsCompleteChanged(bool value)
    {
        if (_suppress) return;
        _ = _onToggle(this, value);
    }

    public void Revert(bool value)
    {
        _suppress = true;
        IsComplete = value;
        _suppress = false;
    }
}

public sealed partial class TasksViewModel : ObservableObject
{
    private readonly Func<AccountSession?> _session;
    private List<TaskItemViewModel> _all = new();

    public TasksViewModel(Func<AccountSession?> session) => _session = session;

    public ObservableCollection<TaskItemViewModel> Items { get; } = new();

    [ObservableProperty] private string _newSubject = "";
    [ObservableProperty] private DateTime? _newDueDate;
    [ObservableProperty] private bool _showCompleted;
    [ObservableProperty] private TaskItemViewModel? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";

    partial void OnShowCompletedChanged(bool value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var t in _all.Where(t => ShowCompleted || !t.IsComplete)) Items.Add(t);
        var open = _all.Count(t => !t.IsComplete);
        StatusText = open == 0 ? "Все задачи выполнены" : RuText.Count(open, "незавершённая задача", "незавершённые задачи", "незавершённых задач");
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var session = _session();
        if (session == null) return;
        IsBusy = true;
        try
        {
            var tasks = await session.Provider.GetTasksAsync();
            _all = tasks.Select(t => new TaskItemViewModel(t, ToggleAsync)).ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText = "Задачи недоступны: " + RuText.Error(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleAsync(TaskItemViewModel item, bool complete)
    {
        var session = _session();
        if (session == null) return;
        try
        {
            await session.Provider.SetTaskCompleteAsync(item.Task.Id, complete);
            item.Task.Status = complete ? TaskItemStatus.Completed : TaskItemStatus.NotStarted;
            ApplyFilter();
        }
        catch (Exception ex)
        {
            item.Revert(!complete);
            Dialogs.Error(ex, "Не удалось изменить задачу");
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var session = _session();
        if (session == null || string.IsNullOrWhiteSpace(NewSubject)) return;
        try
        {
            await session.Provider.CreateTaskAsync(new TaskItem
            {
                Subject = NewSubject.Trim(),
                DueDate = NewDueDate is { } d ? new DateTimeOffset(d.Date) : null,
            });
            NewSubject = "";
            NewDueDate = null;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось создать задачу");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(TaskItemViewModel? item)
    {
        var session = _session();
        item ??= Selected;
        if (session == null || item == null) return;
        if (!Dialogs.Confirm($"Удалить задачу «{item.Subject}»?")) return;
        try
        {
            await session.Provider.DeleteItemsAsync(new[] { item.Task.Id }, permanent: false);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Dialogs.Error(ex, "Не удалось удалить задачу");
        }
    }
}
